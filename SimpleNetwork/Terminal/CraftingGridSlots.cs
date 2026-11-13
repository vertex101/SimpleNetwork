using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SimpleNetwork.Terminal;

/// <summary>
/// Helpers for the player's own crafting grid inventory (the same 3x3 grid as in the E inventory).
/// It holds the 9 input slots plus one output slot; we find the output slot by its type
/// rather than assuming an index, in case the layout ever changes.
/// </summary>
public static class CraftingGridSlots
{
    public static IInventory? Get(IPlayer player) =>
        player.InventoryManager.GetOwnInventory(GlobalConstants.craftingInvClassName);

    public static int OutputIndex(IInventory inv)
    {
        for (int i = 0; i < inv.Count; i++)
        {
            if (inv[i] is ItemSlotOutput) return i;
        }
        return inv.Count - 1;
    }

    public static int[] InputIndices(IInventory inv)
    {
        int output = OutputIndex(inv);
        return Enumerable.Range(0, inv.Count).Where(i => i != output).ToArray();
    }
}
