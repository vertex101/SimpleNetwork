using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SimpleNetwork.Blocks;

/// <summary>
/// Connects terminals and storage containers. Being an <see cref="INetworkBlock"/> is what
/// makes the network scanner pass through it.
///
/// Visually, the cable is a small core with an "arm" towards every neighbouring cable,
/// terminal or storage block. The arms are added while the game builds the chunk's mesh
/// (OnJsonTesselation), so no block entity is needed.
/// </summary>
public class BlockNetworkCable : Block, INetworkBlock
{
    // Order matches the game's tile sides: north, east, south, west, up, down.
    private static readonly string[] ArmElementNames = { "ArmNorth", "ArmEast", "ArmSouth", "ArmWest", "ArmUp", "ArmDown" };

    /// <summary>
    /// Offsets to the neighbour in the chunk-tesselation block array, which is a 34x34x34 cube
    /// (the 32x32x32 chunk plus a 1-block border) laid out as (y * 34 + z) * 34 + x.
    /// </summary>
    private const int ExtChunkSize = 34;
    private static readonly int[] NeighbourIndexOffsets =
    {
        -ExtChunkSize,                  // north (-z)
        1,                              // east  (+x)
        ExtChunkSize,                   // south (+z)
        -1,                             // west  (-x)
        ExtChunkSize * ExtChunkSize,    // up    (+y)
        -ExtChunkSize * ExtChunkSize    // down  (-y)
    };

    private ICoreClientAPI? capi;
    private SimpleNetworkModSystem? modSystem;
    private MeshData?[]? armMeshes;
    private readonly object meshLock = new();

    /// <summary>Finished meshes per combination of connected sides (6 bits = 64 combinations).</summary>
    private readonly ConcurrentDictionary<int, MeshData> meshByConnections = new();

    /// <summary>Cables connect in every direction (importers/exporters override this).</summary>
    public virtual bool ConnectsOnFace(BlockFacing face) => true;

    // Any change to a cable changes the network's layout.
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

    /// <summary>
    /// Right-clicking a cable while holding a chiseled block hides the cable inside that chiseled
    /// block (it becomes a BlockChiseledCable). Anything else in hand behaves as normal.
    /// </summary>
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        var held = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (held?.Itemstack?.Block is not BlockMicroBlock || held.Itemstack.Block is BlockChiseledCable)
        {
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        if (world.Side == EnumAppSide.Server) CoverWith(world, byPlayer, blockSel.Position, held);
        return true; // also on the client, so the chiseled block isn't placed against the cable instead
    }

    private static void CoverWith(IWorldAccessor world, IPlayer byPlayer, BlockPos pos, ItemSlot held)
    {
        if (world.Claims.TestAccess(byPlayer, pos, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted)
        {
            (byPlayer as IServerPlayer)?.SendMessage(GlobalConstants.GeneralChatGroup,
                L.GetL((byPlayer as IServerPlayer)?.LanguageCode, "msg-cover-noaccess"), EnumChatType.Notification);
            return;
        }

        var covered = world.GetBlock(new AssetLocation(L.Domain, BlockChiseledCable.BlockCode));
        if (covered == null || held.Itemstack == null) return;

        var shape = held.Itemstack.Clone();
        shape.StackSize = 1;

        // Swap the cable for the covered cable, then load the chiseled shape into it the same way
        // the game does when a chiseled block is placed.
        world.BlockAccessor.SetBlock(covered.Id, pos);
        covered.OnBlockPlaced(world, pos, shape);

        if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            held.TakeOut(1);
            held.MarkDirty();
        }
        world.PlaySoundAt(new AssetLocation("game", "sounds/player/build"), pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, byPlayer);
    }

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        capi = api as ICoreClientAPI;
        modSystem = api.ModLoader.GetModSystem<SimpleNetworkModSystem>();
    }

    public override void OnJsonTesselation(ref MeshData sourceMesh, ref int[] lightRgbsByCorner, BlockPos pos, Block[] chunkExtBlocks, int extIndex3d)
    {
        if (capi == null || !EnsureMeshes()) return;

        int connections = 0;
        for (int side = 0; side < 6; side++)
        {
            // BlockFacing.ALLFACES uses the same north, east, south, west, up, down order.
            var face = BlockFacing.ALLFACES[side];
            if (ConnectsOnFace(face) && ConnectsTo(chunkExtBlocks[extIndex3d + NeighbourIndexOffsets[side]], face))
            {
                connections |= 1 << side;
            }
        }

        if (connections == 0) return; // nothing attached: the default mesh is already right

        // Build on top of this block's default mesh: just the core for a cable; for an
        // importer/exporter it already includes the arm and connector plate on its front.
        var baseMesh = sourceMesh;
        sourceMesh = meshByConnections.GetOrAdd(connections, c => BuildMesh(baseMesh, c));
    }

    /// <param name="towards">Direction from this cable to the neighbour.</param>
    private bool ConnectsTo(Block? neighbour, BlockFacing towards)
    {
        if (neighbour == null || neighbour.Id == 0) return false;
        // The neighbour must accept a connection on its side facing back at us.
        if (neighbour is INetworkBlock networkBlock) return networkBlock.ConnectsOnFace(towards.Opposite);
        return modSystem?.StorageFilter.IsStorage(neighbour) == true;
    }

    private MeshData BuildMesh(MeshData baseMesh, int connections)
    {
        var mesh = baseMesh.Clone();
        for (int side = 0; side < 6; side++)
        {
            if ((connections & (1 << side)) != 0 && armMeshes![side] != null)
            {
                mesh.AddMeshData(armMeshes[side]);
            }
        }
        return mesh;
    }

    /// <summary>Tesselates each arm once (with this block's own textures), the first time it's drawn.</summary>
    private bool EnsureMeshes()
    {
        if (armMeshes != null) return true;

        lock (meshLock)
        {
            if (armMeshes != null) return true;

            // Fully qualified: inside a Block, "Shape" means the block's own shape field.
            var shape = Vintagestory.API.Common.Shape.TryGet(capi!, new AssetLocation("simplenetwork", "shapes/block/cable.json"));
            if (shape == null) return false;

            var arms = new MeshData?[6];
            for (int side = 0; side < 6; side++)
            {
                capi!.Tesselator.TesselateShape(this, shape, out var arm, null, null, new[] { ArmElementNames[side] });
                arms[side] = arm;
            }

            armMeshes = arms; // assigned last: other threads treat a non-null armMeshes as "ready"
            return true;
        }
    }
}
