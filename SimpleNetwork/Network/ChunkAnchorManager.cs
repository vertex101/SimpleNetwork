using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProtoBuf;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace SimpleNetwork.Network;

/// <summary>
/// Keeps the chunk columns of networks with a Network Anchor loaded, so importers, exporters
/// and wireless terminals keep working while no player is nearby (server only).
///
/// How chunks are kept loaded: the game's API puts a chunk column on the server's "force-loaded"
/// list. That list isn't saved, so anchors (and the columns they hold) are saved with the world
/// and requested again on every server start.
///
/// How chunks are released: NOT with UnloadChunkColumn, which drops a chunk from memory without
/// saving it. Instead the column is taken off the force-loaded list, and the game's normal
/// unloading takes over: it saves the chunk and only unloads it when no player is near.
/// Columns that were already force-loaded before an anchor asked (e.g. the spawn area) are never
/// taken off the list, so nothing else's chunk loading is disturbed.
/// </summary>
public class ChunkAnchorManager
{
    private const string SaveKey = "simplenetwork-anchors";

    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SaveData
    {
        public List<SavedAnchor>? Anchors;
    }

    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SavedAnchor
    {
        public int X, Y, Z, Dimension;
        public string? OwnerUid;
        public List<long>? Columns;
    }

    private class Anchor
    {
        public BlockPos Pos = null!;
        public string? OwnerUid;

        /// <summary>The chunk columns this anchor wants loaded (map chunk indexes).</summary>
        public HashSet<long> Columns = new();

        /// <summary>Whether this anchor's columns are currently requested (false while chunk loading is off).</summary>
        public bool Active;
    }

    private readonly ICoreServerAPI sapi;
    private readonly Dictionary<BlockPos, Anchor> anchors = new();
    private readonly Dictionary<long, int> refCounts = new();

    /// <summary>Columns put on the force-loaded list by us (so they may be taken off again).</summary>
    private readonly HashSet<long> addedByUs = new();

    private bool enabled;

    // The server's force-loaded list and its remove method are internal to the game, so they are
    // looked up by name. If a game update renames them, releasing chunks is skipped (they then
    // stay loaded until the next restart) and a warning is logged once.
    private readonly object server;
    private readonly FieldInfo? forceLoadedField;
    private readonly MethodInfo? removeForceLoadedMethod;

    public ChunkAnchorManager(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
        server = sapi.World;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        for (var type = server.GetType(); type != null && (forceLoadedField == null || removeForceLoadedMethod == null); type = type.BaseType)
        {
            forceLoadedField ??= type.GetField("forceLoadedChunkColumns", flags);
            removeForceLoadedMethod ??= type.GetMethod("RemoveChunkColumnFromForceLoadedList", flags, null, new[] { typeof(long) }, null);
        }
        if (forceLoadedField == null || removeForceLoadedMethod == null)
        {
            sapi.Logger.Warning("[simplenetwork] Could not find the server's force-loaded chunk list; Network Anchors will not release chunks until the server restarts.");
        }

        sapi.Event.SaveGameLoaded += Load;
        sapi.Event.GameWorldSave += Save;

        // Forget anchors whose block is gone without us noticing (e.g. removed by a world edit),
        // so they can't keep chunks loaded forever. Checked regularly; the first check comes
        // shortly after start, once the requested chunks have had time to load.
        sapi.Event.RegisterGameTickListener(_ => RemoveMissingAnchors(), 5 * 60 * 1000, 30 * 1000);
    }

    private void RemoveMissingAnchors()
    {
        foreach (var anchor in anchors.Values.ToList())
        {
            // Only judge anchors whose chunk is actually loaded; otherwise we can't see the block.
            if (sapi.World.BlockAccessor.GetChunkAtBlockPos(anchor.Pos) == null) continue;
            if (sapi.World.BlockAccessor.GetBlock(anchor.Pos) is not Blocks.BlockNetworkAnchor)
            {
                RemoveAnchor(anchor.Pos);
            }
        }
    }

    // ---------------------------------------------------------------- used by anchors

    /// <summary>Registers a newly placed anchor (so per-player limits count it straight away).</summary>
    public void Register(BlockPos pos, string? ownerUid)
    {
        if (!anchors.ContainsKey(pos)) anchors[pos.Copy()] = new Anchor { Pos = pos.Copy(), OwnerUid = ownerUid, Active = enabled };
    }

    public int CountOwnedBy(string ownerUid) => anchors.Values.Count(a => a.OwnerUid == ownerUid);

    public bool Enabled => enabled;

    /// <summary>Sets which columns an anchor wants loaded, requesting new ones and releasing old ones.</summary>
    public void UpdateAnchor(BlockPos pos, string? ownerUid, HashSet<long> wanted)
    {
        Register(pos, ownerUid);
        var anchor = anchors[pos];
        anchor.OwnerUid ??= ownerUid;

        if (anchor.Active)
        {
            foreach (long column in wanted.Except(anchor.Columns)) Acquire(column);
            foreach (long column in anchor.Columns.Except(wanted)) Release(column);
        }
        anchor.Columns = new HashSet<long>(wanted);
    }

    /// <summary>The anchor block is gone: release everything it held.</summary>
    public void RemoveAnchor(BlockPos pos)
    {
        if (!anchors.Remove(pos, out var anchor)) return;
        if (anchor.Active)
        {
            foreach (long column in anchor.Columns) Release(column);
        }
    }

    /// <summary>Turns chunk loading on or off for all anchors (server start and /stn reload).</summary>
    public void SetEnabled(bool on)
    {
        enabled = on;
        foreach (var anchor in anchors.Values)
        {
            if (on && !anchor.Active)
            {
                anchor.Active = true;
                foreach (long column in anchor.Columns) Acquire(column);
            }
            else if (!on && anchor.Active)
            {
                anchor.Active = false;
                foreach (long column in anchor.Columns) Release(column);
            }
        }
    }

    // ---------------------------------------------------------------- force-loading

    private void Acquire(long column)
    {
        refCounts.TryGetValue(column, out int count);
        refCounts[column] = count + 1;
        if (count > 0) return; // already held by another anchor

        if (!IsForceLoaded(column)) addedByUs.Add(column);
        var pos = sapi.WorldManager.MapChunkPosFromChunkIndex2D(column);
        sapi.WorldManager.LoadChunkColumnPriority(pos.X, pos.Y, new ChunkLoadOptions { KeepLoaded = true });
    }

    private void Release(long column)
    {
        if (!refCounts.TryGetValue(column, out int count)) return;
        if (count > 1)
        {
            refCounts[column] = count - 1;
            return;
        }

        refCounts.Remove(column);
        if (addedByUs.Remove(column)) RemoveFromForceLoaded(column);
    }

    private bool IsForceLoaded(long column) =>
        forceLoadedField?.GetValue(server) is HashSet<long> set && set.Contains(column);

    private void RemoveFromForceLoaded(long column)
    {
        try
        {
            removeForceLoadedMethod?.Invoke(server, new object[] { column });
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[simplenetwork] Could not release a chunk column: {0}", e.Message);
        }
    }

    // ---------------------------------------------------------------- saving

    private void Load()
    {
        anchors.Clear();
        var data = sapi.WorldManager.SaveGame.GetData(SaveKey);
        if (data == null) return;

        foreach (var saved in SerializerUtil.Deserialize<SaveData>(data).Anchors ?? new List<SavedAnchor>())
        {
            var pos = new BlockPos(saved.X, saved.Y, saved.Z).SetDimension(saved.Dimension);
            anchors[pos] = new Anchor
            {
                Pos = pos,
                OwnerUid = saved.OwnerUid,
                Columns = new HashSet<long>(saved.Columns ?? new List<long>()),
                Active = false // requested when chunk loading is switched on at server start
            };
        }
    }

    private void Save()
    {
        var data = new SaveData
        {
            Anchors = anchors.Values.Select(a => new SavedAnchor
            {
                X = a.Pos.X, Y = a.Pos.Y, Z = a.Pos.Z, Dimension = a.Pos.dimension,
                OwnerUid = a.OwnerUid,
                Columns = a.Columns.ToList()
            }).ToList()
        };
        sapi.WorldManager.SaveGame.StoreData(SaveKey, SerializerUtil.Serialize(data));
    }
}
