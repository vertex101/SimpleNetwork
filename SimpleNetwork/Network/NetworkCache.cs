using System;
using System.Collections.Generic;
using SimpleNetwork.Blocks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SimpleNetwork.Network;

/// <summary>
/// Remembers network layouts so terminals, importers and exporters don't walk every cable each
/// time they run (several times a second across a busy server).
///
/// A remembered layout is thrown away when:
///  - anything that could change a network is placed or broken (a network block, or storage),
///  - the config is reloaded (the storage allow-list may have changed),
///  - a remembered container turns out to be gone (caught when the layout is used),
///  - or it is older than <see cref="MaxAgeMs"/>, as a safety net for changes no event reports.
/// Permissions and priorities/filters are never cached; they're checked on every use.
/// </summary>
public class NetworkCache
{
    private const long MaxAgeMs = 30_000;

    private readonly ICoreServerAPI sapi;
    private readonly SimpleNetworkModSystem modSystem;

    private class Entry
    {
        public NetworkTopology Topology = null!;
        public long CreatedMs;
    }

    /// <summary>Every network block maps to its network's layout (all members share one entry).</summary>
    private readonly Dictionary<BlockPos, Entry> byNetworkBlock = new();

    public NetworkCache(ICoreServerAPI sapi, SimpleNetworkModSystem modSystem)
    {
        this.sapi = sapi;
        this.modSystem = modSystem;

        sapi.Event.DidPlaceBlock += (player, oldBlockId, blockSel, stack) =>
        {
            if (IsRelevant(sapi.World.GetBlock(oldBlockId)) || IsRelevant(sapi.World.BlockAccessor.GetBlock(blockSel.Position))) Invalidate();
        };
        sapi.Event.DidBreakBlock += (player, oldBlockId, blockSel) =>
        {
            if (IsRelevant(sapi.World.GetBlock(oldBlockId))) Invalidate();
        };
    }

    private bool IsRelevant(Block? block) =>
        block != null && (block is INetworkBlock || modSystem.StorageFilter.IsStorage(block));

    /// <summary>Forget every remembered layout. Cheap; the next use of each network walks it again.</summary>
    public void Invalidate() => byNetworkBlock.Clear();

    /// <summary>Like NetworkScanner.Scan, but reuses the remembered layout when it's still valid.</summary>
    public NetworkScanResult Scan(BlockPos start, System.Func<BlockPos, bool>? canUseContainer)
    {
        var accessor = sapi.World.BlockAccessor;
        long now = sapi.World.ElapsedMilliseconds;

        // With the configurator switched off, saved priorities and filters are simply ignored.
        var settings = modSystem.Config.EnableConfigurator ? modSystem.StorageSettings : null;

        if (byNetworkBlock.TryGetValue(start, out var entry) && now - entry.CreatedMs < MaxAgeMs)
        {
            var cached = NetworkScanner.BuildResult(accessor, entry.Topology, modSystem.StorageFilter, canUseContainer, settings);
            if (cached != null) return cached;
            Invalidate(); // something on the network vanished without an event; start over
        }

        var topology = NetworkScanner.ScanTopology(accessor, start, modSystem.StorageFilter, modSystem.Config.MaxNetworkBlocks);
        var newEntry = new Entry { Topology = topology, CreatedMs = now };
        foreach (var pos in topology.NetworkBlocks) byNetworkBlock[pos] = newEntry;

        return NetworkScanner.BuildResult(accessor, topology, modSystem.StorageFilter, canUseContainer, settings)
               ?? new NetworkScanResult();
    }
}
