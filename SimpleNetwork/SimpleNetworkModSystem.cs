using System;
using System.Collections.Generic;
using System.Linq;
using SimpleNetwork.Blocks;
using SimpleNetwork.Blocks.Entities;
using SimpleNetwork.Config;
using SimpleNetwork.Items;
using SimpleNetwork.Network;
using SimpleNetwork.Terminal;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SimpleNetwork;

/// <summary>
/// Entry point of the mod. The game creates one instance of this on the client
/// and one on the server, and calls Start() on both.
/// </summary>
public class SimpleNetworkModSystem : ModSystem
{
    public const string ConfigFileName = "simplenetwork.json";
    public const string ChannelName = "simplenetwork";

    private ICoreServerAPI? sapi;
    private IServerNetworkChannel? serverChannel;

    /// <summary>Server-side terminal logic (null on the client).</summary>
    public TerminalServer? TerminalServer { get; private set; }

    /// <summary>Client-side terminal logic (null on the server).</summary>
    public TerminalClient? TerminalClient { get; private set; }

    /// <summary>Permission checks for containers (server only).</summary>
    public AccessRules? Access { get; private set; }

    /// <summary>Per-container priority and filter, saved in the world (server only).</summary>
    public StorageSettingsRegistry? StorageSettings { get; private set; }

    /// <summary>Handles the container settings window on the server.</summary>
    public StorageSettingsServer? StorageSettingsServer { get; private set; }

    /// <summary>Remembered network layouts (server only).</summary>
    public NetworkCache? NetworkCache { get; private set; }

    /// <summary>Called by network blocks when they're placed or removed (by anything, not just players).</summary>
    public static void OnNetworkChanged(IWorldAccessor world)
    {
        if (world.Side == EnumAppSide.Server)
        {
            world.Api.ModLoader.GetModSystem<SimpleNetworkModSystem>()?.NetworkCache?.Invalidate();
        }
    }

    /// <summary>Current settings. Only loaded on the server, which is where networks are scanned.</summary>
    public SimpleNetworkConfig Config { get; private set; } = SimpleNetworkConfig.CreateDefault();

    /// <summary>
    /// Which blocks count as storage. On the server it comes from the config file; on the client
    /// it is sent by the server (used to draw cable arms towards storage).
    /// </summary>
    public StorageFilter StorageFilter { get; private set; } = new(SimpleNetworkConfig.CreateDefault());

    public override void Start(ICoreAPI api)
    {
        // These names are what the "class" field in the blocktype JSON files refers to.
        api.RegisterBlockClass("SimpleNetwork.Cable", typeof(BlockNetworkCable));
        api.RegisterBlockClass("SimpleNetwork.Terminal", typeof(BlockNetworkTerminal));
        api.RegisterBlockClass("SimpleNetwork.ChiseledCable", typeof(BlockChiseledCable));
        api.RegisterItemClass("SimpleNetwork.WirelessTerminal", typeof(ItemWirelessTerminal));
        api.RegisterBlockClass("SimpleNetwork.Port", typeof(BlockNetworkPort));
        api.RegisterBlockEntityClass("SimpleNetwork.Port", typeof(BlockEntityNetworkPort));
        api.RegisterItemClass("SimpleNetwork.Configurator", typeof(ItemNetworkConfigurator));
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        Access = new AccessRules(api);
        StorageSettings = new StorageSettingsRegistry(api);
        NetworkCache = new NetworkCache(api, this);
        LoadConfig();

        // Message types must be registered in the same order on client and server.
        serverChannel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<TerminalContentsPacket>()
            .RegisterMessageType<TerminalActionPacket>()
            .RegisterMessageType<TerminalClosedPacket>()
            .RegisterMessageType<StorageConfigPacket>()
            .RegisterMessageType<StorageSettingsPacket>();
        TerminalServer = new TerminalServer(api, this, serverChannel);
        StorageSettingsServer = new StorageSettingsServer(api, this, serverChannel);

        api.Event.PlayerNowPlaying += player => serverChannel.SendPacket(BuildStorageConfigPacket(), player);

        // Recipes are loaded by now, and players can't have joined yet (they receive the recipe list
        // when they join), so this is the moment to take out recipes of disabled features.
        api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, RemoveDisabledRecipes);

        // Descriptions use the server's language; command replies use the language of whoever ran the command.
        api.ChatCommands.Create("simplenetwork")
            .WithAlias("stn")
            .WithDescription(L.Get("cmd-desc"))
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("reload")
                .WithDescription(L.Get("cmd-reload-desc"))
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args => LoadConfig()
                    ? TextCommandResult.Success(L.GetL(args.LanguageCode, "cmd-reload-success"))
                    : TextCommandResult.Error(L.GetL(args.LanguageCode, "cmd-reload-failed")))
            .EndSubCommand()
            .BeginSubCommand("check")
                .WithDescription(L.Get("cmd-check-desc"))
                .RequiresPlayer()
                .HandleWith(args => CheckLookedAtBlock(args.Caller.Player, args.LanguageCode))
            .EndSubCommand();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        var channel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<TerminalContentsPacket>()
            .RegisterMessageType<TerminalActionPacket>()
            .RegisterMessageType<TerminalClosedPacket>()
            .RegisterMessageType<StorageConfigPacket>()
            .RegisterMessageType<StorageSettingsPacket>();
        TerminalClient = new TerminalClient(api, channel);
        new StorageSettingsClient(api, channel);

        channel.SetMessageHandler<StorageConfigPacket>(packet =>
        {
            // The client's copy of the server's settings: enough to draw cables and to know which
            // features are switched off.
            Config = new SimpleNetworkConfig
            {
                StorageBlockEntityClasses = packet.StorageBlockEntityClasses ?? new(),
                StorageBlockCodes = packet.StorageBlockCodes ?? new(),
                ExcludedBlockCodes = packet.ExcludedBlockCodes ?? new(),
                EnableWirelessTerminal = packet.EnableWirelessTerminal,
                EnableImportExport = packet.EnableImportExport,
                EnableConfigurator = packet.EnableConfigurator
            };
            StorageFilter = new StorageFilter(Config);
        });
    }

    /// <summary>
    /// Loads the config file, creating it with defaults if it doesn't exist.
    /// A broken file is never overwritten, so the player's edits aren't lost.
    /// </summary>
    private bool LoadConfig()
    {
        if (sapi == null) return false;

        SimpleNetworkConfig? loaded;
        try
        {
            loaded = sapi.LoadModConfig<SimpleNetworkConfig>(ConfigFileName);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("Failed to read {0}, keeping the previous settings: {1}", ConfigFileName, e.Message);
            return false;
        }

        if (loaded == null)
        {
            loaded = SimpleNetworkConfig.CreateDefault();
            Mod.Logger.Notification("Created default {0}", ConfigFileName);
        }

        // Save it back so settings added in newer versions of the mod show up in the file.
        sapi.StoreModConfig(loaded, ConfigFileName);

        Config = loaded;
        StorageFilter = new StorageFilter(loaded);
        NetworkCache?.Invalidate(); // the allow-list or size limit may have changed

        // Let connected players' cables pick up the new allow-list (null during server startup).
        serverChannel?.BroadcastPacket(BuildStorageConfigPacket());
        return true;
    }

    private StorageConfigPacket BuildStorageConfigPacket() => new()
    {
        StorageBlockEntityClasses = Config.StorageBlockEntityClasses,
        StorageBlockCodes = Config.StorageBlockCodes,
        ExcludedBlockCodes = Config.ExcludedBlockCodes,
        EnableWirelessTerminal = Config.EnableWirelessTerminal,
        EnableImportExport = Config.EnableImportExport,
        EnableConfigurator = Config.EnableConfigurator
    };

    /// <summary>Takes the crafting recipes of switched-off features out of the game (server start only).</summary>
    private void RemoveDisabledRecipes()
    {
        if (sapi == null) return;

        var disabledCodes = new List<string>();
        if (!Config.EnableWirelessTerminal) disabledCodes.Add("wirelessterminal");
        if (!Config.EnableImportExport) disabledCodes.AddRange(new[] { "importer", "exporter" });
        if (!Config.EnableConfigurator) disabledCodes.Add("configurator");
        if (disabledCodes.Count == 0) return;

        // Output codes look like "simplenetwork:wirelessterminal" or "simplenetwork:importer-north".
        int removed = sapi.World.GridRecipes.RemoveAll(recipe =>
        {
            var code = recipe.Output?.Code;
            return code != null && code.Domain == L.Domain
                   && disabledCodes.Any(c => code.Path == c || code.Path.StartsWith(c + "-"));
        });
        Mod.Logger.Notification("Disabled features: {0} (removed {1} recipe(s))", string.Join(", ", disabledCodes), removed);
    }

    private TextCommandResult CheckLookedAtBlock(IPlayer player, string? languageCode)
    {
        var sel = player.CurrentBlockSelection;
        if (sel == null)
        {
            return TextCommandResult.Error(L.GetL(languageCode, "cmd-check-noblock"));
        }

        var block = sapi!.World.BlockAccessor.GetBlock(sel.Position);
        var verdict = L.GetL(languageCode, StorageFilter.IsStorage(block) ? "cmd-check-is-storage" : "cmd-check-not-storage");
        return TextCommandResult.Success(L.GetL(languageCode, "cmd-check-result",
            block.Code, block.EntityClass ?? L.GetL(languageCode, "cmd-check-none"), verdict));
    }
}
