using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SimpleNetwork.Blocks;

/// <summary>
/// A network cable hidden inside a chiseled block. It IS a regular chiseled block (the game's
/// own BlockChisel and its block entity do all the drawing, collisions and chisel editing), that
/// also carries the network in every direction.
///
/// Made by right-clicking a cable with a chiseled block (see BlockNetworkCable). Breaking it gives
/// back both the chiseled block and the cable.
/// </summary>
public class BlockChiseledCable : BlockChisel, INetworkBlock
{
    public const string BlockCode = "chiseledcable";

    /// <summary>Positions being broken by a player right now; their cable is already among the normal drops.</summary>
    private readonly HashSet<BlockPos> breakingByPlayer = new();

    public bool ConnectsOnFace(BlockFacing face) => true;

    /// <summary>The cable inside, as an item.</summary>
    private ItemStack CableStack(IWorldAccessor world) => new(world.GetBlock(new AssetLocation(L.Domain, "cable")));

    /// <summary>
    /// The game's code stores the chiseled shape on an item of *this* block. Players should get a
    /// normal chiseled block instead, so the shape is moved onto one.
    /// </summary>
    private static ItemStack? AsPlainChiseledBlock(IWorldAccessor world, ItemStack? stack)
    {
        if (stack == null) return null;
        var vanilla = world.GetBlock(new AssetLocation("game", "chiseledblock"));
        if (vanilla == null) return stack;
        return new ItemStack(vanilla, stack.StackSize) { Attributes = stack.Attributes.Clone() };
    }

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos) =>
        AsPlainChiseledBlock(world, base.OnPickBlock(world, pos))!;

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        var drops = base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier)
            .Select(d => d.Block == this ? AsPlainChiseledBlock(world, d)! : d)
            .ToList();
        drops.Add(CableStack(world));
        return drops.ToArray();
    }

    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        var key = pos.Copy();
        breakingByPlayer.Add(key);
        try
        {
            base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
        }
        finally
        {
            breakingByPlayer.Remove(key);
        }
    }

    public override void OnBlockPlaced(IWorldAccessor world, BlockPos blockPos, ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(world, blockPos, byItemStack);
        SimpleNetworkModSystem.OnNetworkChanged(world);
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos)
    {
        // Removed some other way than a player breaking it (chiseled away completely, an explosion,
        // ...): still give the cable back, so it's never lost.
        if (world.Side == EnumAppSide.Server && !breakingByPlayer.Contains(pos))
        {
            world.SpawnItemEntity(CableStack(world), pos.ToVec3d().Add(0.5, 0.5, 0.5));
        }

        base.OnBlockRemoved(world, pos);
        SimpleNetworkModSystem.OnNetworkChanged(world);
    }

    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        var sb = new StringBuilder(base.GetPlacedBlockInfo(world, pos, forPlayer));
        if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
        sb.AppendLine(L.Get("chiseledcable-info"));
        return sb.ToString();
    }
}
