using System;
using System.Collections.Generic;
using System.Linq;
using SimpleNetwork.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SimpleNetwork.Gui;

/// <summary>
/// Settings window for one storage container: priority (- / +) and up to 9 filter items.
/// Click a filter slot while holding an item to add it; click a filter item with an empty
/// cursor to remove it. Every change is sent to the server straight away.
/// </summary>
public class GuiDialogStorageSettings : GuiDialog
{
    private readonly Action<StorageSettingsPacket> sendToServer;
    private readonly GuiElementNetworkGrid filterGrid;
    private StorageSettingsPacket current = new();

    public GuiDialogStorageSettings(ICoreClientAPI capi, string containerName, Action<StorageSettingsPacket> sendToServer) : base(capi)
    {
        this.sendToServer = sendToServer;

        double step = GuiElementNetworkGrid.UnscaledCellStep;
        double width = ContainerSettings.MaxFilters * step - GuiElementNetworkGrid.UnscaledSlotPadding;
        double y = GuiStyle.TitleBarHeight + 10;

        var priorityLabelBounds = ElementBounds.Fixed(0, y + 5, 90, 25);
        var minusBounds = ElementBounds.Fixed(95, y, 30, 30);
        var valueBounds = ElementBounds.Fixed(130, y + 5, 40, 25);
        var plusBounds = ElementBounds.Fixed(175, y, 30, 30);
        var priorityHintBounds = ElementBounds.Fixed(215, y + 7, width - 215, 25);

        var filterLabelBounds = ElementBounds.Fixed(0, y + 45, width, 25);
        var gridBounds = ElementBounds.Fixed(0, y + 72, width, GuiElementNetworkGrid.UnscaledSlotSize);
        var filterHintBounds = ElementBounds.Fixed(0, y + 72 + GuiElementNetworkGrid.UnscaledSlotSize + 8, width, 40);

        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(priorityLabelBounds, minusBounds, valueBounds, plusBounds, priorityHintBounds,
            filterLabelBounds, gridBounds, filterHintBounds);

        filterGrid = new GuiElementNetworkGrid(capi, gridBounds, ContainerSettings.MaxFilters, 1)
        {
            OnCellClicked = OnFilterClicked
        };

        SingleComposer = capi.Gui.CreateCompo("simplenetwork-storagesettings", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle))
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(L.Get("gui-storage-title", containerName), () => TryClose())
            .BeginChildElements(bgBounds)
                .AddStaticText(L.Get("gui-storage-priority"), CairoFont.WhiteSmallText(), priorityLabelBounds)
                .AddSmallButton("-", () => ChangePriority(-1), minusBounds)
                .AddDynamicText("0", CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center), valueBounds, "priority")
                .AddSmallButton("+", () => ChangePriority(1), plusBounds)
                .AddStaticText(L.Get("gui-storage-priority-hint"), CairoFont.WhiteDetailText(), priorityHintBounds)
                .AddStaticText(L.Get("gui-storage-filter"), CairoFont.WhiteSmallText(), filterLabelBounds)
                .AddInteractiveElement(filterGrid, "filters")
                .AddStaticText(L.Get("gui-storage-filter-hint"), CairoFont.WhiteDetailText(), filterHintBounds)
            .EndChildElements()
            .Compose();
    }

    public override string ToggleKeyCombinationCode => null!;

    /// <summary>Shows settings sent by the server (always the saved truth).</summary>
    public void SetData(StorageSettingsPacket packet)
    {
        current = packet;
        SingleComposer.GetDynamicText("priority").SetNewText(packet.Priority.ToString());

        var entries = new List<NetworkEntry>();
        foreach (var bytes in packet.Filters ?? new List<byte[]>())
        {
            var stack = new ItemStack(bytes);
            if (stack.ResolveBlockOrItem(capi.World)) entries.Add(new NetworkEntry(bytes, stack));
        }
        filterGrid.SetEntries(entries);
    }

    public bool IsFor(StorageSettingsPacket packet) =>
        packet.X == current.X && packet.Y == current.Y && packet.Z == current.Z && packet.Dimension == current.Dimension;

    private bool ChangePriority(int delta)
    {
        int priority = GameMath.Clamp(current.Priority + delta, ContainerSettings.MinPriority, ContainerSettings.MaxPriority);
        if (priority != current.Priority) Send(priority, current.Filters ?? new List<byte[]>());
        return true;
    }

    private void OnFilterClicked(NetworkEntry? entry, EnumMouseButton button)
    {
        var filters = current.Filters?.ToList() ?? new List<byte[]>();
        var mouseSlot = capi.World.Player.InventoryManager.MouseItemSlot;

        if (!mouseSlot.Empty)
        {
            // Add the held item type (the item stays on your cursor; this is only a filter).
            if (filters.Count >= ContainerSettings.MaxFilters) return;
            var filterStack = mouseSlot.Itemstack.Clone();
            filterStack.StackSize = 1;
            filters.Add(filterStack.ToBytes());
        }
        else if (entry?.Raw != null)
        {
            // Remove the clicked filter.
            filters = filters.Where(f => !f.SequenceEqual(entry.Raw)).ToList();
        }
        else
        {
            return;
        }

        Send(current.Priority, filters);
    }

    private void Send(int priority, List<byte[]> filters)
    {
        sendToServer(new StorageSettingsPacket
        {
            X = current.X, Y = current.Y, Z = current.Z, Dimension = current.Dimension,
            Priority = priority,
            Filters = filters
        });
    }

    public override void OnGuiClosed()
    {
        filterGrid.ResetHover();
        base.OnGuiClosed();
    }
}
