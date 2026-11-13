using SimpleNetwork.Blocks.Entities;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SimpleNetwork.Blocks;

/// <summary>
/// Network Importer / Exporter. Placed against a block, its front faces that block
/// (variant "side" = direction of the front). It is a piece of cable (green or orange):
/// it draws arms towards connected cables like a cable does, plus a fixed arm and connector
/// plate on its front, and connects to the network on every side except the front.
/// The work is done by BlockEntityNetworkPort.
///
/// Right-click with an item: set the filter. Sneak + right-click with an empty hand: clear it.
/// </summary>
public class BlockNetworkPort : BlockNetworkCable
{
    /// <summary>The side facing the block it imports from / exports to.</summary>
    public BlockFacing Front { get; private set; } = BlockFacing.NORTH;

    /// <summary>True for the exporter, false for the importer (set by "portMode" in the block JSON).</summary>
    public bool IsExporter { get; private set; }

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        Front = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;
        IsExporter = Attributes?["portMode"].AsString() == "export";
    }

    // The front faces the block being imported from / exported to, never the network.
    // (Placing/removing still updates the network cache via BlockNetworkCable.)
    public override bool ConnectsOnFace(BlockFacing face) => face != Front;

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;

        // We're placed against the clicked block, so the front points back at it.
        var front = blockSel.Face.Opposite;
        var oriented = world.BlockAccessor.GetBlock(CodeWithVariant("side", front.Code));
        if (oriented == null) return false;

        oriented.DoPlaceBlock(world, byPlayer, blockSel, itemstack);

        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityNetworkPort port)
        {
            port.SetOwner(byPlayer);
        }
        return true;
    }

    // Always drop / pick the same variant, so importers stack in the inventory whatever way they faced.
    private ItemStack DefaultStack(IWorldAccessor world) => new(world.GetBlock(CodeWithVariant("side", "north")));

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1) =>
        new[] { DefaultStack(world) };

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos) => DefaultStack(world);

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.Side != EnumAppSide.Server || byPlayer is not IServerPlayer player) return true;
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityNetworkPort port) return true;

        var held = player.InventoryManager.ActiveHotbarSlot;
        if (held != null && !held.Empty)
        {
            port.SetFilter(held.Itemstack);
            Tell(player, "msg-port-filter-set", held.Itemstack.GetName());
        }
        else if (player.Entity.Controls.ShiftKey)
        {
            port.SetFilter(null);
            Tell(player, "msg-port-filter-cleared");
        }
        return true;
    }

    private static void Tell(IServerPlayer player, string key, params object[] args) =>
        player.SendMessage(GlobalConstants.GeneralChatGroup, L.GetL(player.LanguageCode, key, args), EnumChatType.Notification);
}
