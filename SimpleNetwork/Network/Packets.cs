using System.Collections.Generic;
using ProtoBuf;

namespace SimpleNetwork.Network;

// Messages exchanged between client and server over the "simplenetwork" channel.
// ProtoBuf turns these classes into bytes and back. Note: an empty list arrives as null.

/// <summary>Server → client: what is stored in the network, or a request to close the terminal.</summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class TerminalContentsPacket
{
    /// <summary>True when the player just opened the terminal (client should open the dialog).</summary>
    public bool Open;

    /// <summary>True when the server ended the session (player walked away, terminal broken, ...).</summary>
    public bool Close;

    /// <summary>One serialized ItemStack per item type; its StackSize is the network-wide total.</summary>
    public List<byte[]>? Stacks;

    public int ContainerCount;
    public bool HitSizeLimit;
    public int MaxNetworkBlocks;

    /// <summary>Storage space: slots holding something / all slots, across the usable containers.</summary>
    public int UsedSlots;
    public int TotalSlots;

    /// <summary>Containers on the network the player may not use (someone else's land claim or lock).</summary>
    public int DeniedContainers;
}

public enum TerminalAction
{
    /// <summary>Take items of the given type out of the network.</summary>
    Take,

    /// <summary>Put the items the player holds on the mouse cursor into the network.</summary>
    Insert,

    /// <summary>Clear the crafting grid and fill it with one craft's worth of a recipe's ingredients.</summary>
    FillRecipe
}

/// <summary>Client → server: the player clicked in the terminal grid.</summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class TerminalActionPacket
{
    public TerminalAction Action;

    /// <summary>For Take: the serialized ItemStack identifying which item type to take.</summary>
    public byte[]? Stack;

    public int Quantity;

    /// <summary>For Take: true = straight into the player's inventory, false = onto the mouse cursor.</summary>
    public bool ToInventory;

    /// <summary>For FillRecipe: index into the world's grid recipe list (same order on client and server).</summary>
    public int RecipeIndex;

    /// <summary>For FillRecipe: the recipe's output code, so the server can double-check the index.</summary>
    public string? RecipeOutputCode;
}

/// <summary>
/// Both directions: a container's priority and filter. Server → client opens or refreshes the
/// settings window; client → server is a change made in that window.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class StorageSettingsPacket
{
    public int X, Y, Z, Dimension;
    public int Priority;

    /// <summary>Serialized filter item stacks (empty = accepts anything).</summary>
    public List<byte[]>? Filters;

    /// <summary>Server → client: open the window (as opposed to just refreshing it).</summary>
    public bool Open;

    /// <summary>Server → client: name of the container, shown in the window title.</summary>
    public string? ContainerName;
}

/// <summary>
/// Server → client: the storage allow-list from the server config, so the client can draw
/// cable arms towards storage blocks exactly the way the server will connect them.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class StorageConfigPacket
{
    public List<string>? StorageBlockEntityClasses;
    public List<string>? StorageBlockCodes;
    public List<string>? ExcludedBlockCodes;
}

/// <summary>Client → server: the player closed the terminal dialog.</summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class TerminalClosedPacket
{
    public bool Closed = true;
}
