namespace SimpleNetwork.Config;

/// <summary>
/// Per-player terminal preferences, saved on the player's own computer as
/// VintagestoryData/ModConfig/simplenetwork-client.json so they survive restarts.
/// </summary>
public class SimpleNetworkClientSettings
{
    /// <summary>"amount", "name" or "mod".</summary>
    public string SortMode { get; set; } = "amount";

    /// <summary>"items", "recipes" or "craftable".</summary>
    public string ViewMode { get; set; } = "items";
}
