using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace SimpleNetwork.Blocks.Entities;

/// <summary>
/// The working part of a Network Anchor. On the server it regularly works out which chunk columns
/// its network covers (cables, terminals, importers/exporters and connected storage) and asks the
/// ChunkAnchorManager to keep them loaded, nearest first, up to the configured limit.
/// </summary>
public class BlockEntityNetworkAnchor : BlockEntity
{
    private const int RefreshIntervalMs = 10_000;

    public string? OwnerUid { get; private set; }
    public string? OwnerName { get; private set; }

    /// <summary>How many chunk columns are being kept loaded (shown when looking at the block).</summary>
    public int LoadedColumns { get; private set; }

    /// <summary>True when the network covers more columns than the limit allows.</summary>
    public bool LimitReached { get; private set; }

    private SimpleNetworkModSystem? modSystem;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        modSystem = api.ModLoader.GetModSystem<SimpleNetworkModSystem>();

        if (api.Side == EnumAppSide.Server)
        {
            RegisterGameTickListener(_ => Refresh(), RefreshIntervalMs, api.World.Rand.Next(RefreshIntervalMs));
            RegisterDelayedCallback(_ => Refresh(), 500); // don't wait 10 seconds after placing
        }
    }

    public void SetOwner(IPlayer player)
    {
        OwnerUid = player.PlayerUID;
        OwnerName = player.PlayerName;
        if (Api?.Side == EnumAppSide.Server) modSystem?.ChunkAnchors?.Register(Pos, OwnerUid);
        MarkDirty();
    }

    private void Refresh()
    {
        var manager = modSystem?.ChunkAnchors;
        if (manager == null || modSystem?.NetworkCache == null || Api is not ICoreServerAPI sapi) return;

        var wanted = new HashSet<long>();
        bool limited = false;

        if (Pos.dimension == 0) // only the normal world has chunk columns to keep loaded
        {
            int chunkSize = sapi.WorldManager.ChunkSize;
            int ownX = Pos.X / chunkSize, ownZ = Pos.Z / chunkSize;

            var scan = modSystem.NetworkCache.Scan(Pos, null);
            var columns = scan.NetworkBlocks
                .Concat(scan.Entries.Select(e => e.Pos))
                .Select(p => (X: p.X / chunkSize, Z: p.Z / chunkSize))
                .Distinct()
                .OrderBy(c => Math.Max(Math.Abs(c.X - ownX), Math.Abs(c.Z - ownZ))) // nearest first; own column first
                .ToList();

            int max = modSystem.Config.MaxChunksPerAnchor;
            if (max > 0 && columns.Count > max)
            {
                limited = true;
                columns = columns.Take(max).ToList();
            }

            foreach (var (x, z) in columns) wanted.Add(sapi.WorldManager.MapChunkIndex2D(x, z));
        }

        manager.UpdateAnchor(Pos, OwnerUid, wanted);

        int loaded = manager.Enabled ? wanted.Count : 0;
        if (loaded != LoadedColumns || limited != LimitReached)
        {
            LoadedColumns = loaded;
            LimitReached = limited;
            MarkDirty(); // updates the info shown to players looking at it
        }
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        if (Api?.Side == EnumAppSide.Server) modSystem?.ChunkAnchors?.RemoveAnchor(Pos);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (OwnerUid != null) tree.SetString("ownerUid", OwnerUid);
        if (OwnerName != null) tree.SetString("ownerName", OwnerName);
        tree.SetInt("loadedColumns", LoadedColumns);
        tree.SetBool("limitReached", LimitReached);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        OwnerUid = tree.GetString("ownerUid");
        OwnerName = tree.GetString("ownerName");
        LoadedColumns = tree.GetInt("loadedColumns");
        LimitReached = tree.GetBool("limitReached");
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);

        if (modSystem != null && !modSystem.Config.EnableChunkLoading)
        {
            dsc.AppendLine(L.Get("anchor-disabled"));
        }
        else
        {
            dsc.AppendLine(L.Get("anchor-loaded", LoadedColumns));
            if (LimitReached) dsc.AppendLine(L.Get("anchor-limit-reached", modSystem?.Config.MaxChunksPerAnchor ?? 0));
        }
        if (OwnerName != null) dsc.AppendLine(L.Get("port-owner", OwnerName));
    }
}
