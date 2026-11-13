using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SimpleNetwork.Blocks;

/// <summary>
/// Right-clicking the terminal opens the network GUI. The server scans the network
/// and sends the contents to the player, whose client then opens the dialog.
///
/// The terminal faces the player when placed (variant "side" = the direction the player
/// was looking, so the screen is on the opposite face). It only connects to the network
/// through its cable ports: top, bottom and back.
/// </summary>
public class BlockNetworkTerminal : Block, INetworkBlock
{
    private BlockFacing? backFace;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        // "-north" means the player looked north while placing, so the screen faces south and the back is north.
        backFace = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;
    }

    public bool ConnectsOnFace(BlockFacing face) =>
        face == BlockFacing.UP || face == BlockFacing.DOWN || face == backFace;

    public override void OnBlockPlaced(IWorldAccessor world, BlockPos blockPos, ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(world, blockPos, byItemStack);
        SimpleNetworkModSystem.OnNetworkChanged(world);
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        base.OnBlockRemoved(world, pos);
        SimpleNetworkModSystem.OnNetworkChanged(world);
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        // Only the server has the real inventories. The client still returns true
        // so the game knows the click was handled and forwards it to the server.
        if (world.Side == EnumAppSide.Server && byPlayer is IServerPlayer serverPlayer)
        {
            world.Api.ModLoader.GetModSystem<SimpleNetworkModSystem>().TerminalServer?.Open(serverPlayer, blockSel.Position);
        }

        return true;
    }
}
