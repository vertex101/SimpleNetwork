using System.Collections.Generic;

namespace SimpleNetwork.Config;

/// <summary>
/// Server-side settings, saved as VintagestoryData/ModConfig/simplenetwork.json.
/// Lists start empty on purpose: the JSON loader appends to existing lists, so
/// pre-filled defaults here would be duplicated. Defaults live in CreateDefault().
/// </summary>
public class SimpleNetworkConfig
{
    /// <summary>
    /// Block entity class names that count as storage, e.g. "GenericTypedContainer"
    /// (chests, trunks, baskets, storage vessels). Matching is case-insensitive.
    /// Easiest way to support a modded storage family that shares one class.
    /// </summary>
    public List<string> StorageBlockEntityClasses { get; set; } = new();

    /// <summary>
    /// Block codes that count as storage. Supports wildcards, e.g. "game:crate*"
    /// or "somemod:barrel-*". Codes without a "domain:" prefix default to "game:".
    /// </summary>
    public List<string> StorageBlockCodes { get; set; } = new();

    /// <summary>Block codes (wildcards allowed) that never count as storage, even if listed above.</summary>
    public List<string> ExcludedBlockCodes { get; set; } = new();

    /// <summary>Maximum cables + terminals scanned per network, to protect server performance.</summary>
    public int MaxNetworkBlocks { get; set; } = 4096;

    /// <summary>How far (in blocks) a wireless terminal reaches its linked terminal. 0 = unlimited (same dimension).</summary>
    public int WirelessRange { get; set; } = 64;

    /// <summary>How often importers and exporters move items, in milliseconds (minimum 100).</summary>
    public int PortIntervalMs { get; set; } = 1000;

    /// <summary>How many items an importer or exporter moves each time.</summary>
    public int PortTransferAmount { get; set; } = 8;

    public static SimpleNetworkConfig CreateDefault() => new()
    {
        StorageBlockEntityClasses = new()
        {
            "GenericTypedContainer", // chest, trunk, reed basket, storage vessels
            "LabeledChest",
            "Crate"
        },
        StorageBlockCodes = new(),
        ExcludedBlockCodes = new()
    };
}
