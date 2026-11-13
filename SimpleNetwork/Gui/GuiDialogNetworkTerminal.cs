using System;
using System.Collections.Generic;
using System.Linq;
using SimpleNetwork.Network;
using SimpleNetwork.Terminal;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SimpleNetwork.Gui;

/// <summary>
/// The terminal window: search box, sort dropdown, view dropdown (Items / Recipes / Craftable),
/// scrollable item grid, status line and a crafting grid. Sort and view are remembered between sessions.
///
/// Items mode, empty hand:      Left click = take a stack, Right click = take one,
///                              Shift + Left click = take a stack straight into your inventory.
/// Holding an item (any mode):  Left click = put it all in, Right click = put one in.
/// Recipes mode:                Left click a recipe = fill the crafting grid with its ingredients.
/// Your inventory:              Shift + click an item in your hotbar/backpack to send it to the network.
/// Crafting output:             Click = craft onto cursor,
///                              Shift + click = keep crafting into your inventory until it's full
///                              or the network runs out of ingredients.
/// </summary>
public class GuiDialogNetworkTerminal : GuiDialog
{
    private const int Columns = 9;
    private const int VisibleRows = 6;

    private static readonly string[] SortValues = { TerminalClient.SortByAmount, TerminalClient.SortByName, TerminalClient.SortByMod };
    private static readonly string[] ViewValues = { TerminalClient.ViewItems, TerminalClient.ViewRecipes, TerminalClient.ViewCraftable };

    // Display names come from the language file, so they're looked up when the dialog is built.
    private static string[] SortNames => new[] { L.Get("gui-sort-amount"), L.Get("gui-sort-name"), L.Get("gui-sort-mod") };
    private static string[] ViewNames => new[] { L.Get("gui-view-items"), L.Get("gui-view-recipes"), L.Get("gui-view-craftable") };

    private readonly TerminalClient client;
    private readonly GuiElementNetworkGrid grid;
    private readonly IInventory? craftingGrid;
    private InventoryNetworkIntake? intake;

    private List<NetworkEntry> networkEntries = new();
    /// <summary>Network entries grouped by item/block id, for fast ingredient lookups.</summary>
    private Dictionary<(EnumItemClass, int), List<NetworkEntry>> networkEntriesById = new();
    private List<NetworkEntry>? recipeEntries;
    private string searchLower = "";
    private string statusText = "";
    private TerminalContentsPacket lastPacket = new();
    private bool closingFromServer;

    public GuiDialogNetworkTerminal(ICoreClientAPI capi, TerminalClient client) : base(capi)
    {
        this.client = client;

        double step = GuiElementNetworkGrid.UnscaledCellStep;
        double gridWidth = Columns * step - GuiElementNetworkGrid.UnscaledSlotPadding;
        double gridHeight = VisibleRows * step - GuiElementNetworkGrid.UnscaledSlotPadding;

        double searchY = GuiStyle.TitleBarHeight + 5;
        double gridY = GuiStyle.TitleBarHeight + 45;

        // Top row: [search .......] [sort ▼] [view ▼]
        const double sortWidth = 110, viewWidth = 110, gap = 8;
        double searchWidth = gridWidth - sortWidth - viewWidth - 2 * gap;
        var searchBounds = ElementBounds.Fixed(0, searchY, searchWidth, 30);
        var sortBounds = ElementBounds.Fixed(searchWidth + gap, searchY, sortWidth, 30);
        var viewBounds = ElementBounds.Fixed(searchWidth + sortWidth + 2 * gap, searchY, viewWidth, 30);

        var gridBounds = ElementBounds.Fixed(0, gridY, gridWidth, gridHeight);
        var scrollbarBounds = ElementStdBounds.VerticalScrollbar(gridBounds);
        var statusBounds = ElementBounds.Fixed(0, gridY + gridHeight + 8, gridWidth, 20);

        // Crafting area to the right of the scrollbar: 3x3 grid, then the output slot level with its middle row.
        double craftX = gridWidth + 45;
        var craftLabelBounds = ElementBounds.Fixed(craftX, searchY + 5, 3 * step, 25);
        var craftGridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, craftX, gridY, 3, 3);
        var craftOutputBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, craftX + 3 * step + 25, gridY + step, 1, 1);

        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(searchBounds, sortBounds, viewBounds, gridBounds, scrollbarBounds, statusBounds,
            craftLabelBounds, craftGridBounds, craftOutputBounds);

        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        // The player's own crafting grid (shared with the E inventory). The game's slot grid
        // handles all clicking, recipe matching and crafting; the server refills it after crafts.
        craftingGrid = CraftingGridSlots.Get(capi.World.Player);

        grid = new GuiElementNetworkGrid(capi, gridBounds, Columns, VisibleRows)
        {
            OnCellClicked = OnCellClicked,
            OnScrolledByWheel = row => SingleComposer.GetScrollbar("scrollbar").CurrentYPosition = row,
            OnHoverChanged = OnHoverChanged,
            CraftableCheck = IsRecipeCraftable
        };

        var composer = capi.Gui.CreateCompo("simplenetwork-terminal", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(L.Get("gui-terminal-title"), () => TryClose())
            .BeginChildElements(bgBounds)
                .AddTextInput(searchBounds, OnSearchChanged, CairoFont.WhiteSmallText(), "search")
                .AddDropDown(SortValues, SortNames, Math.Max(0, Array.IndexOf(SortValues, client.SortMode)), OnSortChanged, sortBounds, "sort")
                .AddDropDown(ViewValues, ViewNames, Math.Max(0, Array.IndexOf(ViewValues, client.ViewMode)), OnViewChanged, viewBounds, "view")
                .AddInteractiveElement(grid, "grid")
                .AddVerticalScrollbar(OnScrollbarMoved, scrollbarBounds, "scrollbar")
                .AddDynamicText("", CairoFont.WhiteDetailText(), statusBounds, "status");

        if (craftingGrid != null)
        {
            composer
                .AddStaticText(L.Get("gui-crafting"), CairoFont.WhiteSmallText(), craftLabelBounds)
                .AddItemSlotGrid(craftingGrid, SendInventoryPacket, 3, CraftingGridSlots.InputIndices(craftingGrid), craftGridBounds, "craftgrid")
                .AddItemSlotGrid(craftingGrid, SendInventoryPacket, 1, new[] { CraftingGridSlots.OutputIndex(craftingGrid) }, craftOutputBounds, "craftoutput");
        }

        // Don't focus the search box, so keys like E still open the player's inventory.
        SingleComposer = composer.EndChildElements().Compose(focusFirstElement: false);

        SingleComposer.GetTextInput("search").SetPlaceHolderText(L.Get("gui-search-placeholder"));
    }

    // The terminal is opened by right-clicking the block, not by a hotkey.
    public override string ToggleKeyCombinationCode => null!;

    // ---------------------------------------------------------------- contents

    public void SetContents(TerminalContentsPacket packet)
    {
        var entries = new List<NetworkEntry>();
        foreach (var raw in packet.Stacks ?? new List<byte[]>())
        {
            try
            {
                var stack = new ItemStack(raw);
                if (stack.ResolveBlockOrItem(capi.World)) entries.Add(new NetworkEntry(raw, stack));
            }
            catch (Exception)
            {
                // Skip anything we can't decode (e.g. an item from a mod the client doesn't have).
            }
        }

        networkEntries = entries;
        networkEntriesById = entries
            .GroupBy(e => (e.Stack.Class, e.Stack.Id))
            .ToDictionary(g => g.Key, g => g.ToList());
        lastPacket = packet;

        // What's craftable depends on the contents, so let recipes re-check when next shown.
        if (recipeEntries != null)
        {
            foreach (var entry in recipeEntries) entry.Craftable = null;
        }

        ApplyFilter();
    }

    /// <summary>Builds the recipe list once, from all grid recipes the game knows.</summary>
    private List<NetworkEntry> GetRecipeEntries()
    {
        if (recipeEntries != null) return recipeEntries;

        recipeEntries = new List<NetworkEntry>();
        var recipes = capi.World.GridRecipes;
        for (int i = 0; i < recipes.Count; i++)
        {
            var output = recipes[i].Output?.ResolvedItemStack;
            if (output?.Collectible == null) continue;
            recipeEntries.Add(new NetworkEntry(i, recipes[i], output.Clone()));
        }
        return recipeEntries;
    }

    /// <summary>
    /// Can the network supply one craft of this recipe? Ingredients that appear in several slots
    /// are added up, and (like filling the grid) each ingredient must be covered by a single kind
    /// of item, e.g. 4 planks of one wood type rather than 2 + 2.
    /// </summary>
    private bool IsRecipeCraftable(NetworkEntry entry)
    {
        if (entry.Recipe == null) return true;
        try
        {
            var needs = new Dictionary<string, (CraftingRecipeIngredient Ingredient, int Quantity)>();
            foreach (var ingredient in entry.Recipe.ResolvedIngredients ?? Array.Empty<CraftingRecipeIngredient>())
            {
                if (ingredient == null) continue;
                string key = ingredient.Code?.ToString() ?? "";
                int quantity = Math.Max(1, ingredient.Quantity);
                needs[key] = needs.TryGetValue(key, out var existing)
                    ? (existing.Ingredient, existing.Quantity + quantity)
                    : (ingredient, quantity);
            }

            foreach (var (ingredient, quantity) in needs.Values)
            {
                if (!CandidatesFor(ingredient).Any(e => e.Stack.StackSize >= quantity && ingredient.SatisfiesAsIngredient(e.Stack, checkStackSize: false)))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception)
        {
            return true; // never let an odd modded recipe break the GUI
        }
    }

    /// <summary>Network entries that could match the ingredient: a quick id lookup for exact ingredients, otherwise all.</summary>
    private IEnumerable<NetworkEntry> CandidatesFor(CraftingRecipeIngredient ingredient)
    {
        var exact = ingredient.ResolvedItemStack;
        if (ingredient.MatchingType == EnumRecipeMatchType.Exact && exact != null)
        {
            return networkEntriesById.TryGetValue((exact.Class, exact.Id), out var list) ? list : Enumerable.Empty<NetworkEntry>();
        }
        return networkEntries;
    }

    private void ApplyFilter()
    {
        IEnumerable<NetworkEntry> source = client.RecipeMode ? GetRecipeEntries() : networkEntries;
        if (searchLower.Length > 0)
        {
            source = source.Where(e => e.NameLower.Contains(searchLower));
        }
        if (client.ViewMode == TerminalClient.ViewCraftable)
        {
            source = source.Where(e => e.Craftable ??= IsRecipeCraftable(e));
        }

        grid.SetEntries(Sort(source).ToList());

        // SetHeights fires the scroll callback, which could move the grid; keep the current row.
        int row = grid.FirstRow;
        var scrollbar = SingleComposer.GetScrollbar("scrollbar");
        scrollbar.SetHeights(VisibleRows, Math.Max(VisibleRows, grid.TotalRows));
        grid.FirstRow = row;
        scrollbar.CurrentYPosition = grid.FirstRow;

        var p = lastPacket;
        statusText = client.RecipeMode
            ? L.Get("gui-status-recipes")
            : L.Get("gui-status-items", networkEntries.Count, p.UsedSlots, p.TotalSlots, p.ContainerCount);
        if (p.DeniedContainers > 0) statusText += L.Get("gui-status-locked", p.DeniedContainers);
        if (p.HitSizeLimit) statusText += L.Get("gui-status-limit", p.MaxNetworkBlocks);
        SingleComposer.GetDynamicText("status").SetNewText(statusText);
    }

    private IEnumerable<NetworkEntry> Sort(IEnumerable<NetworkEntry> entries)
    {
        // "Amount" makes no sense for recipes (it would be the output quantity), so they sort by name instead.
        string mode = client.RecipeMode && client.SortMode == TerminalClient.SortByAmount ? TerminalClient.SortByName : client.SortMode;

        return mode switch
        {
            TerminalClient.SortByName => entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            // Vanilla ("game") first, then other mods alphabetically.
            TerminalClient.SortByMod => entries
                .OrderBy(e => e.Domain == "game" ? 0 : 1)
                .ThenBy(e => e.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
            _ => entries
                .OrderByDescending(e => e.Stack.StackSize)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    // ---------------------------------------------------------------- open / close

    public void CloseFromServer()
    {
        closingFromServer = true;
        TryClose();
        closingFromServer = false;
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        var inventoryManager = capi.World.Player.InventoryManager;

        // The server only accepts slot clicks in inventories the player has "opened".
        if (craftingGrid != null)
        {
            SendInventoryPacket(inventoryManager.OpenInventory(craftingGrid));
        }

        // Client copy of the hidden intake, so shift-clicking hotbar/backpack items targets the network.
        // The server opened its own copy when the session started, so no packet is sent for it.
        intake = new InventoryNetworkIntake(capi.World.Player.PlayerUID, capi);
        inventoryManager.OpenInventory(intake);
    }

    public override void OnGuiClosed()
    {
        grid.ResetHover();
        // Our close message goes first, so the server returns leftover ingredients to the network
        // before the game's own close handling sees the grid.
        if (!closingFromServer) client.SendClosed();
        if (craftingGrid != null)
        {
            SendInventoryPacket(capi.World.Player.InventoryManager.CloseInventory(craftingGrid));
        }
        if (intake != null)
        {
            capi.World.Player.InventoryManager.CloseInventory(intake);
            intake = null;
        }
        base.OnGuiClosed();
    }

    private void SendInventoryPacket(object packet)
    {
        if (packet != null) capi.Network.SendPacketClient(packet);
    }

    // ---------------------------------------------------------------- input

    private void OnSearchChanged(string text)
    {
        searchLower = (text ?? "").Trim().ToLowerInvariant();
        ApplyFilter();
    }

    private void OnSortChanged(string code, bool selected)
    {
        client.SortMode = code;
        ApplyFilter();
    }

    private void OnViewChanged(string code, bool selected)
    {
        client.ViewMode = code;
        grid.FirstRow = 0;
        ApplyFilter();
    }

    private void OnScrollbarMoved(float value)
    {
        grid.FirstRow = (int)Math.Round(value);
    }

    private void OnHoverChanged(NetworkEntry? entry)
    {
        string text = entry == null
            ? statusText
            : entry.Recipe != null
                ? L.Get("gui-hover-recipe", entry.Name)
                : L.Get("gui-hover-item", entry.Name, entry.Stack.StackSize.ToString("N0"));
        SingleComposer.GetDynamicText("status").SetNewText(text);
    }

    private void OnCellClicked(NetworkEntry? entry, EnumMouseButton button)
    {
        if (button != EnumMouseButton.Left && button != EnumMouseButton.Right) return;

        var mouseSlot = capi.World.Player.InventoryManager.MouseItemSlot;
        bool rightClick = button == EnumMouseButton.Right;

        // Holding something: put it into the network, whatever mode we're in.
        if (!mouseSlot.Empty)
        {
            client.SendInsert(rightClick ? 1 : mouseSlot.StackSize);
            return;
        }

        if (entry == null) return;

        if (entry.Recipe != null)
        {
            if (!rightClick) client.SendFillRecipe(entry.RecipeIndex, entry.Stack.Collectible.Code.ToString());
            return;
        }

        if (entry.Raw == null) return;
        bool shift = capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ShiftLeft]
                     || capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ShiftRight];
        int quantity = rightClick ? 1 : entry.Stack.Collectible.MaxStackSize;
        client.SendTake(entry.Raw, quantity, toInventory: shift && !rightClick);
    }
}
