using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SimpleNetwork.Terminal;

/// <summary>
/// A hidden inventory that exists while a terminal is open, so that shift-clicking an item
/// in your hotbar/backpack sends it to the network, the same way it would go into an open chest.
///
/// The game's shift-click picks the open inventory that rates itself the best fit, so this one
/// rates itself very highly, but only for items coming from the player's hotbar or backpack
/// (crafting output and the crafting grid keep their normal behaviour). The server empties it
/// into the network straight away, so it never holds anything for long.
/// </summary>
public class InventoryNetworkIntake : InventoryGeneric
{
    public const string IntakeClassName = "simplenetworkintake";

    /// <summary>Several slots so quick repeated shift-clicks don't run out of room before the server empties it.</summary>
    private const int SlotCount = 16;

    /// <summary>Higher than any normal inventory, so the network wins while the terminal is open.</summary>
    private const float IntakeSuitability = 50f;

    public InventoryNetworkIntake(string playerUid, ICoreAPI api)
        : base(SlotCount, IntakeClassName, playerUid, api, null!)
    {
    }

    public override WeightedSlot GetBestSuitedSlot(ItemSlot sourceSlot, ItemStackMoveOperation? op = null, List<ItemSlot>? skipSlots = null)
    {
        string? sourceClass = sourceSlot.Inventory?.ClassName;
        if (sourceClass != GlobalConstants.hotBarInvClassName && sourceClass != GlobalConstants.backpackInvClassName)
        {
            return new WeightedSlot(); // no slot: let the game pick another destination
        }

        return base.GetBestSuitedSlot(sourceSlot, op, skipSlots);
    }

    public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge) => IntakeSuitability;
}
