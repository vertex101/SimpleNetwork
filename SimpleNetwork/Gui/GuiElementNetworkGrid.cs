using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SimpleNetwork.Gui;

/// <summary>
/// A scrollable grid of item stacks. Unlike the game's slot grid this is not tied to a
/// real inventory: it only draws what the server reported and reports clicks back.
/// Scrolling moves by whole rows, so the slot backgrounds can be drawn once and stay put.
/// </summary>
public class GuiElementNetworkGrid : GuiElement
{
    public static readonly double UnscaledSlotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
    public static readonly double UnscaledSlotPadding = GuiElementItemSlotGridBase.unscaledSlotPadding;
    public static double UnscaledCellStep => UnscaledSlotSize + UnscaledSlotPadding;

    private const int MaxCachedCountTextures = 512;

    /// <summary>Depth for item models. 3D items have thickness, so they reach well in front of this.</summary>
    private const float ItemZ = 90;

    /// <summary>The game draws its own stack numbers at the item depth + 100, in front of even thick models.</summary>
    private const float CountTextZ = ItemZ + 100;
    private static readonly int DimmedColor = ColorUtil.ToRgba(255, 90, 90, 90);

    private readonly int columns;
    private readonly int visibleRows;
    private readonly CairoFont countFont;
    private readonly Dictionary<string, LoadedTexture> countTextures = new();
    private List<NetworkEntry> entries = new();
    private int firstRow;
    private int hoverCell = -1;
    private ItemSlot? tooltipSlot;

    /// <summary>Called when a cell is clicked. Entry is null for an empty cell.</summary>
    public Action<NetworkEntry?, EnumMouseButton>? OnCellClicked;

    /// <summary>Called when the grid scrolls by mouse wheel, so the scrollbar can follow.</summary>
    public Action<int>? OnScrolledByWheel;

    /// <summary>Called when the hovered entry changes (null = nothing hovered).</summary>
    public Action<NetworkEntry?>? OnHoverChanged;

    /// <summary>Recipe mode: decides whether a recipe looks craftable from the network (dims it if not).</summary>
    public System.Func<NetworkEntry, bool>? CraftableCheck;

    public GuiElementNetworkGrid(ICoreClientAPI capi, ElementBounds bounds, int columns, int visibleRows) : base(capi, bounds)
    {
        this.columns = columns;
        this.visibleRows = visibleRows;
        countFont = CairoFont.WhiteDetailText()
            .WithFontSize((float)GuiStyle.DetailFontSize)
            .WithStroke(new double[] { 0, 0, 0, 1 }, 2);
    }

    public int TotalRows => Math.Max(1, (entries.Count + columns - 1) / columns);

    public int MaxFirstRow => Math.Max(0, TotalRows - visibleRows);

    public int FirstRow
    {
        get => firstRow;
        set
        {
            int clamped = GameMath.Clamp(value, 0, MaxFirstRow);
            if (clamped == firstRow) return;
            firstRow = clamped;
            RefreshHover();
        }
    }

    public void SetEntries(List<NetworkEntry> newEntries)
    {
        entries = newEntries;
        firstRow = GameMath.Clamp(firstRow, 0, MaxFirstRow);
        RefreshHover();
    }

    /// <summary>Formats big numbers compactly: 999, 1.2k, 12k, 123k, 1.2M, 12M (suffixes come from the language file).</summary>
    public static string FormatCount(int count)
    {
        if (count < 1000) return count.ToString();
        if (count < 10_000) return SimpleNetwork.L.Get("gui-count-thousands", TrimZero((count / 100) / 10.0));
        if (count < 1_000_000) return SimpleNetwork.L.Get("gui-count-thousands", count / 1000);
        if (count < 10_000_000) return SimpleNetwork.L.Get("gui-count-millions", TrimZero((count / 100_000) / 10.0));
        return SimpleNetwork.L.Get("gui-count-millions", count / 1_000_000);
    }

    private static string TrimZero(double value) => value.ToString(value % 1 == 0 ? "0" : "0.0");

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        double size = scaled(UnscaledSlotSize);
        double step = scaled(UnscaledCellStep);

        // Static slot backgrounds, drawn once into the dialog's background texture.
        for (int row = 0; row < visibleRows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                double x = Bounds.drawX + col * step;
                double y = Bounds.drawY + row * step;

                RoundRectangle(ctx, x, y, size, size, 2);
                ctx.SetSourceRGBA(0, 0, 0, 0.3);
                ctx.Fill();
                EmbossRoundRectangleElement(ctx, x, y, size, size, true);
            }
        }
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        double size = scaled(UnscaledSlotSize);
        double step = scaled(UnscaledCellStep);
        float itemSize = (float)scaled(GuiElementPassiveItemSlot.unscaledItemSize);

        for (int cell = 0; cell < columns * visibleRows; cell++)
        {
            double x = Bounds.renderX + (cell % columns) * step;
            double y = Bounds.renderY + (cell / columns) * step;

            if (cell == hoverCell)
            {
                api.Render.RenderRectangle((float)x, (float)y, 80, (float)size, (float)size, ColorUtil.ToRgba(48, 255, 255, 255));
            }

            var entry = EntryAtCell(cell);
            if (entry == null) continue;

            if (entry.Recipe != null && entry.Craftable == null && CraftableCheck != null)
            {
                entry.Craftable = CraftableCheck(entry);
            }
            int color = entry.Craftable == false ? DimmedColor : ColorUtil.WhiteArgb;

            // We draw the count ourselves (compact format), so the game's own number is turned off.
            api.Render.RenderItemstackToGui(entry.Slot, x + size / 2, y + size / 2, ItemZ, itemSize, color, showStackSize: false);

            if (entry.Stack.StackSize > 1)
            {
                var text = GetCountTexture(FormatCount(entry.Stack.StackSize));
                api.Render.Render2DLoadedTexture(text, (float)(x + size - text.Width - scaled(3)), (float)(y + size - text.Height), CountTextZ);
            }
        }
    }

    private LoadedTexture GetCountTexture(string text)
    {
        if (countTextures.TryGetValue(text, out var texture)) return texture;

        if (countTextures.Count >= MaxCachedCountTextures) ClearCountTextures();
        texture = api.Gui.TextTexture.GenTextTexture(text, countFont);
        countTextures[text] = texture;
        return texture;
    }

    private void ClearCountTextures()
    {
        foreach (var texture in countTextures.Values) texture.Dispose();
        countTextures.Clear();
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        int cell = CellAt(args.X, args.Y);
        if (cell == hoverCell) return;

        hoverCell = cell;
        ClearTooltip();

        var entry = EntryAtCell(cell);
        if (entry != null)
        {
            tooltipSlot = entry.Slot;
            api.Input.TriggerOnMouseEnterSlot(tooltipSlot);
        }
        OnHoverChanged?.Invoke(entry);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        int cell = CellAt(args.X, args.Y);
        if (cell < 0) return;

        OnCellClicked?.Invoke(EntryAtCell(cell), args.Button);
        args.Handled = true;
    }

    public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
    {
        if (args.IsHandled || !Bounds.PointInside(api.Input.MouseX, api.Input.MouseY)) return;

        int before = FirstRow;
        FirstRow -= Math.Sign(args.deltaPrecise);
        if (FirstRow != before) OnScrolledByWheel?.Invoke(FirstRow);
        args.SetHandled(true);
    }

    /// <summary>Hides the item tooltip, e.g. when the dialog closes.</summary>
    public void ClearTooltip()
    {
        if (tooltipSlot == null) return;
        api.Input.TriggerOnMouseLeaveSlot(tooltipSlot);
        tooltipSlot = null;
    }

    /// <summary>The entries under the mouse changed (scrolled or refreshed): re-show tooltip and hover info.</summary>
    private void RefreshHover()
    {
        ClearTooltip();
        var entry = EntryAtCell(hoverCell);
        if (entry != null)
        {
            tooltipSlot = entry.Slot;
            api.Input.TriggerOnMouseEnterSlot(tooltipSlot);
        }
        OnHoverChanged?.Invoke(entry);
    }

    /// <summary>Forgets the hovered cell, e.g. when the dialog closes.</summary>
    public void ResetHover()
    {
        ClearTooltip();
        hoverCell = -1;
    }

    public override void Dispose()
    {
        ClearCountTextures();
        base.Dispose();
    }

    private NetworkEntry? EntryAtCell(int cell)
    {
        if (cell < 0) return null;
        int index = firstRow * columns + cell;
        return index < entries.Count ? entries[index] : null;
    }

    /// <summary>Which visible cell the screen position is over, or -1.</summary>
    private int CellAt(int mouseX, int mouseY)
    {
        double step = scaled(UnscaledCellStep);
        double relX = mouseX - Bounds.absX;
        double relY = mouseY - Bounds.absY;
        if (relX < 0 || relY < 0) return -1;

        int col = (int)(relX / step);
        int row = (int)(relY / step);
        if (col >= columns || row >= visibleRows) return -1;

        // Ignore the padding gap between slots.
        if (relX - col * step > scaled(UnscaledSlotSize) || relY - row * step > scaled(UnscaledSlotSize)) return -1;

        return row * columns + col;
    }
}
