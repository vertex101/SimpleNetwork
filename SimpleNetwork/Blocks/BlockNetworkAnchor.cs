using SimpleNetwork.Blocks.Entities;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SimpleNetwork.Blocks;

/// <summary>
/// Network Anchor: part of the network like a cable (connects on every side), and keeps the
/// chunks its network covers loaded, so importers, exporters and wireless terminals keep working
/// while nobody is nearby. The work is done by BlockEntityNetworkAnchor and ChunkAnchorManager.
/// </summary>
public class BlockNetworkAnchor : Block, INetworkBlock
{
    public bool ConnectsOnFace(BlockFacing face) => true;

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        if (world.Side == EnumAppSide.Server && byPlayer is IServerPlayer player)
        {
            var modSystem = api.ModLoader.GetModSystem<SimpleNetworkModSystem>();
            int max = modSystem.Config.MaxAnchorsPerPlayer;
            if (max > 0 && modSystem.ChunkAnchors != null && modSystem.ChunkAnchors.CountOwnedBy(player.PlayerUID) >= max)
            {
                player.SendMessage(GlobalConstants.GeneralChatGroup, L.GetL(player.LanguageCode, "msg-anchor-limit", max), EnumChatType.Notification);
                failureCode = "__ignore__";
                return false;
            }
        }

        if (!base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref failureCode)) return false;

        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityNetworkAnchor anchor)
        {
            anchor.SetOwner(byPlayer);
        }
        return true;
    }

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
}
