using System;
using System.Collections.Generic;
using System.Linq;
using SimpleNetwork.Config;
using Vintagestory.API.Common;

namespace SimpleNetwork.Network;

/// <summary>
/// Decides which blocks the network treats as storage, based on the config allow-list.
/// </summary>
public class StorageFilter
{
    private readonly HashSet<string> entityClasses;
    private readonly AssetLocation[] blockCodes;
    private readonly AssetLocation[] excludedCodes;

    public StorageFilter(SimpleNetworkConfig config)
    {
        entityClasses = new HashSet<string>(
            (config.StorageBlockEntityClasses ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);
        blockCodes = ToLocations(config.StorageBlockCodes);
        excludedCodes = ToLocations(config.ExcludedBlockCodes);
    }

    public bool IsStorage(Block block)
    {
        if (block?.Code == null) return false;
        if (excludedCodes.Length > 0 && block.WildCardMatch(excludedCodes)) return false;
        if (block.EntityClass != null && entityClasses.Contains(block.EntityClass)) return true;
        return blockCodes.Length > 0 && block.WildCardMatch(blockCodes);
    }

    private static AssetLocation[] ToLocations(List<string>? codes) =>
        (codes ?? new())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => new AssetLocation(s.Trim()))
            .ToArray();
}
