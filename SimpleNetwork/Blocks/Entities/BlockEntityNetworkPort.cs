using System;
using System.Linq;
using System.Text;
using SimpleNetwork.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SimpleNetwork.Blocks.Entities;

/// <summary>
/// The working part of a Network Importer / Exporter. On the server it ticks regularly and moves
/// a few items between the network and the block in front of it.
///
/// Which slot of the target is used is decided by the target itself, through the same rules the
/// game uses for hoppers and chutes (GetAutoPullFromSlot / GetAutoPushIntoSlot). So a firepit
/// takes fuel into its fuel slot and gives up its output slot, just like with a chute.
/// </summary>
public class BlockEntityNetworkPort : BlockEntity
{
    /// <summary>Who placed it: the importer/exporter only touches containers this player may use.</summary>
    public string? OwnerUid { get; private set; }
    public string? OwnerName { get; private set; }

    /// <summary>Item type to move. Importer: null = everything. Exporter: null = nothing.</summary>
    public ItemStack? Filter { get; private set; }

    private SimpleNetworkModSystem? modSystem;

    private BlockNetworkPort? PortBlock => Block as BlockNetworkPort;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        modSystem = api.ModLoader.GetModSystem<SimpleNetworkModSystem>();

        if (api.Side == EnumAppSide.Server)
        {
            int interval = Math.Max(100, modSystem.Config.PortIntervalMs);
            // Random start offset, so many ports don't all run in the same tick.
            RegisterGameTickListener(OnServerTick, interval, api.World.Rand.Next(interval));
        }
    }

    public void SetOwner(IPlayer player)
    {
        OwnerUid = player.PlayerUID;
        OwnerName = player.PlayerName;
        MarkDirty();
    }

    public void SetFilter(ItemStack? stack)
    {
        Filter = stack?.Clone();
        if (Filter != null) Filter.StackSize = 1;
        MarkDirty(redrawOnClient: true);
    }

    private bool MatchesFilter(ItemStack stack) =>
        Filter == null || stack.Collectible?.Code != null && stack.Collectible.Code.Equals(Filter.Collectible?.Code);

    private void OnServerTick(float dt)
    {
        var block = PortBlock;
        if (block == null || modSystem?.Access == null) return;
        if (block.IsExporter && Filter == null) return; // exporters only run with a filter

        var world = Api.World;
        var targetPos = Pos.AddCopy(block.Front);
        if (world.BlockAccessor.GetBlockEntity(targetPos) is not IBlockEntityContainer target
            || target.Inventory is not InventoryBase targetInventory)
        {
            return;
        }
        if (!modSystem.Access.CanUse(OwnerUid, targetPos)) return;

        // The network, minus the target itself (it may also be connected by a cable elsewhere)
        // and minus anything the owner isn't allowed to use.
        var scan = modSystem.NetworkCache!.Scan(Pos, pos => !pos.Equals(targetPos) && modSystem.Access.CanUse(OwnerUid, pos));
        if (scan.ContainerCount == 0) return;

        int amount = Math.Max(1, modSystem.Config.PortTransferAmount);
        var faceOfTarget = block.Front.Opposite; // the target's side that touches us

        if (block.IsExporter)
        {
            Export(world, scan, targetInventory, faceOfTarget, amount);
        }
        else
        {
            Import(world, scan, targetInventory, faceOfTarget, amount, modSystem.StorageFilter.IsStorage(world.BlockAccessor.GetBlock(targetPos)));
        }
    }

    private void Import(IWorldAccessor world, NetworkScanResult scan, InventoryBase target, BlockFacing face, int amount, bool targetIsPlainStorage)
    {
        // Ask the target which slot it gives up (e.g. a firepit's output slot).
        var source = target.GetAutoPullFromSlot(face);

        // With a filter, a plain chest might offer a non-matching slot first; for plain storage it's
        // safe to look through every slot. Machines are only ever pulled from their chosen slot.
        if ((source == null || source.Empty || !MatchesFilter(source.Itemstack)) && Filter != null && targetIsPlainStorage)
        {
            source = target.FirstOrDefault(slot => !slot.Empty && MatchesFilter(slot.Itemstack));
        }
        if (source == null || source.Empty || !MatchesFilter(source.Itemstack)) return;

        if (NetworkScanner.MoveIntoNetwork(world, source, scan, amount) > 0)
        {
            source.MarkDirty();
        }
    }

    private void Export(IWorldAccessor world, NetworkScanResult scan, InventoryBase target, BlockFacing face, int amount)
    {
        int remaining = amount;
        foreach (var networkSlot in scan.TakeSlots)
        {
            if (remaining <= 0) break;
            if (networkSlot.Empty || !MatchesFilter(networkSlot.Itemstack)) continue;

            // Ask the target where this item should go (e.g. firewood into a firepit's fuel slot).
            var sink = target.GetAutoPushIntoSlot(face, networkSlot);
            if (sink == null) break; // the target doesn't want this item (right now)

            int moved = networkSlot.TryPutInto(world, sink, remaining);
            if (moved <= 0) break; // the target slot is full
            networkSlot.MarkDirty();
            sink.MarkDirty();
            remaining -= moved;
        }
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (OwnerUid != null) tree.SetString("ownerUid", OwnerUid);
        if (OwnerName != null) tree.SetString("ownerName", OwnerName);
        if (Filter != null) tree.SetItemstack("filter", Filter);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        OwnerUid = tree.GetString("ownerUid");
        OwnerName = tree.GetString("ownerName");
        Filter = tree.GetItemstack("filter");
        if (Filter != null && !Filter.ResolveBlockOrItem(worldAccessForResolve)) Filter = null;
    }

    /// <summary>The text shown when looking at the block.</summary>
    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        bool exporter = PortBlock?.IsExporter == true;

        if (Filter != null) dsc.AppendLine(L.Get("port-filter", Filter.GetName()));
        else dsc.AppendLine(L.Get(exporter ? "port-filter-none-export" : "port-filter-none-import"));

        dsc.AppendLine(L.Get("port-howto"));
        if (OwnerName != null) dsc.AppendLine(L.Get("port-owner", OwnerName));
    }
}
