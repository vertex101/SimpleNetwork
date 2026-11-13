using Vintagestory.API.Config;

namespace SimpleNetwork;

/// <summary>
/// Shortcut for this mod's translations. All player-facing text lives in
/// assets/simplenetwork/lang/*.json; keys there are written without the
/// "simplenetwork:" prefix, which the game adds automatically.
/// </summary>
public static class L
{
    public const string Domain = "simplenetwork";

    /// <summary>Text in the local language (the player's, on the client; the server's, on the server).</summary>
    public static string Get(string key, params object[] args) => Lang.Get($"{Domain}:{key}", args);

    /// <summary>Text in a specific language, e.g. a player's own language for a server-side chat message.</summary>
    public static string GetL(string? languageCode, string key, params object[] args) =>
        string.IsNullOrEmpty(languageCode) ? Get(key, args) : Lang.GetL(languageCode, $"{Domain}:{key}", args);
}
