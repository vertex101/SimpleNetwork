using System;
using SimpleNetwork.Config;
using SimpleNetwork.Gui;
using SimpleNetwork.Network;
using Vintagestory.API.Client;

namespace SimpleNetwork.Terminal;

/// <summary>
/// Client side of the terminal: receives contents from the server, owns the
/// dialog, sends the player's clicks back to the server, and remembers the
/// player's terminal preferences.
/// </summary>
public class TerminalClient
{
    public const string SortByAmount = "amount";
    public const string SortByName = "name";
    public const string SortByMod = "mod";

    public const string ViewItems = "items";
    public const string ViewRecipes = "recipes";
    public const string ViewCraftable = "craftable";

    private const string SettingsFileName = "simplenetwork-client.json";

    private readonly ICoreClientAPI capi;
    private readonly IClientNetworkChannel channel;
    private readonly SimpleNetworkClientSettings settings;
    private GuiDialogNetworkTerminal? dialog;

    public TerminalClient(ICoreClientAPI capi, IClientNetworkChannel channel)
    {
        this.capi = capi;
        this.channel = channel;
        settings = LoadSettings();
        channel.SetMessageHandler<TerminalContentsPacket>(OnContentsPacket);
    }

    public string SortMode
    {
        get => settings.SortMode;
        set { settings.SortMode = value; SaveSettings(); }
    }

    public string ViewMode
    {
        get => settings.ViewMode;
        set { settings.ViewMode = value; SaveSettings(); }
    }

    /// <summary>True in both recipe views ("all recipes" and "craftable only").</summary>
    public bool RecipeMode => ViewMode == ViewRecipes || ViewMode == ViewCraftable;

    private SimpleNetworkClientSettings LoadSettings()
    {
        try
        {
            return capi.LoadModConfig<SimpleNetworkClientSettings>(SettingsFileName) ?? new SimpleNetworkClientSettings();
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[simplenetwork] Could not read {0}, using defaults: {1}", SettingsFileName, e.Message);
            return new SimpleNetworkClientSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            capi.StoreModConfig(settings, SettingsFileName);
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[simplenetwork] Could not save {0}: {1}", SettingsFileName, e.Message);
        }
    }

    private void OnContentsPacket(TerminalContentsPacket packet)
    {
        if (packet.Close)
        {
            dialog?.CloseFromServer();
            return;
        }

        dialog ??= new GuiDialogNetworkTerminal(capi, this);
        dialog.SetContents(packet);

        if (packet.Open && !dialog.IsOpened())
        {
            dialog.TryOpen();
        }
    }

    public void SendTake(byte[] stack, int quantity, bool toInventory) =>
        channel.SendPacket(new TerminalActionPacket
        {
            Action = TerminalAction.Take,
            Stack = stack,
            Quantity = quantity,
            ToInventory = toInventory
        });

    public void SendInsert(int quantity) =>
        channel.SendPacket(new TerminalActionPacket { Action = TerminalAction.Insert, Quantity = quantity });

    public void SendFillRecipe(int recipeIndex, string outputCode) =>
        channel.SendPacket(new TerminalActionPacket
        {
            Action = TerminalAction.FillRecipe,
            RecipeIndex = recipeIndex,
            RecipeOutputCode = outputCode
        });

    public void SendClosed() => channel.SendPacket(new TerminalClosedPacket());
}
