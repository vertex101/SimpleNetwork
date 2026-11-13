using System.Collections.Generic;
using System.Linq;
using SimpleNetwork.Blocks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SimpleNetwork.Network;

/// <summary>
/// The layout of a network: which blocks carry it and where its storage containers are.
/// Finding this means walking every cable, so it is remembered (see NetworkCache).
/// </summary>
public class NetworkTopology
{
    public List<BlockPos> NetworkBlocks { get; } = new();
    public List<BlockPos> ContainerPositions { get; } = new();
    public bool HitSizeLimit { get; set; }
}

/// <summary>A storage container found on the network, with its position and (optional) priority/filter.</summary>
public class NetworkContainer
{
    public IBlockEntityContainer Container = null!;
    public BlockPos Pos = null!;
    public ContainerSettings? Settings;

    public int Priority => Settings?.Priority ?? 0;
}

public class NetworkScanResult
{
    /// <summary>Positions of every cable/terminal that is part of the network.</summary>
    public List<BlockPos> NetworkBlocks { get; } = new();

    /// <summary>Every storage container touching the network (each one listed once).</summary>
    public List<NetworkContainer> Entries { get; } = new();

    public int ContainerCount => Entries.Count;

    /// <summary>True if the scan stopped early because the network was too large.</summary>
    public bool HitSizeLimit { get; set; }

    /// <summary>Containers touching the network that the player isn't allowed to use (land claim or lock).</summary>
    public int DeniedContainers { get; set; }

    /// <summary>Every slot of every container in the network, in no particular order (for counting/showing).</summary>
    public IEnumerable<ItemSlot> AllSlots => Entries.SelectMany(e => e.Container.Inventory);

    /// <summary>Slots to take items from: lowest priority first, so preferred storage stays stocked.</summary>
    public IEnumerable<ItemSlot> TakeSlots =>
        Entries.OrderBy(e => e.Priority).SelectMany(e => e.Container.Inventory);

    /// <summary>
    /// Groups of slots that may receive <paramref name="stack"/>, in the order to fill them:
    /// highest priority first; within a priority, containers whose filter names this item before
    /// unfiltered ones. Containers with a filter that doesn't include the item are left out.
    /// </summary>
    public IEnumerable<List<ItemSlot>> InsertGroupsFor(ItemStack stack) =>
        Entries
            .Where(e => e.Settings == null || e.Settings.Accepts(stack))
            .GroupBy(e => (e.Priority, Matched: e.Settings?.HasFilterFor(stack) == true))
            .OrderByDescending(g => g.Key.Priority)
            .ThenByDescending(g => g.Key.Matched)
            .Select(g => g.SelectMany(e => e.Container.Inventory).ToList());
}

public static class NetworkScanner
{
    /// <summary>
    /// Flood-fills outward from <paramref name="start"/> through network blocks.
    /// Containers next to a network block are collected if <paramref name="storageFilter"/>
    /// allows them, but the scan does not continue through them (two chests touching
    /// each other don't link networks). Stops after <paramref name="maxNetworkBlocks"/>
    /// network blocks so a huge cable web can't stall the server.
    /// <paramref name="canUseContainer"/>, if given, decides per container whether it may be
    /// used (e.g. land claims and locks); refused containers are left out and counted.
    /// </summary>
    public static NetworkScanResult Scan(IBlockAccessor blockAccessor, BlockPos start, StorageFilter storageFilter, int maxNetworkBlocks,
        System.Func<BlockPos, bool>? canUseContainer = null, StorageSettingsRegistry? settings = null)
    {
        var topology = ScanTopology(blockAccessor, start, storageFilter, maxNetworkBlocks);
        return BuildResult(blockAccessor, topology, storageFilter, canUseContainer, settings)
               ?? new NetworkScanResult(); // can't happen right after a fresh walk
    }

    /// <summary>
    /// Step 2 of a scan, and the cheap part: turns a network layout into usable containers, applying
    /// permissions (<paramref name="canUseContainer"/>) and priorities/filters (<paramref name="settings"/>).
    /// Returns null if the layout is out of date (a remembered container is no longer there).
    /// </summary>
    public static NetworkScanResult? BuildResult(IBlockAccessor blockAccessor, NetworkTopology topology, StorageFilter storageFilter,
        System.Func<BlockPos, bool>? canUseContainer, StorageSettingsRegistry? settings)
    {
        var result = new NetworkScanResult { HitSizeLimit = topology.HitSizeLimit };
        result.NetworkBlocks.AddRange(topology.NetworkBlocks);

        foreach (var pos in topology.ContainerPositions)
        {
            if (!storageFilter.IsStorage(blockAccessor.GetBlock(pos))
                || blockAccessor.GetBlockEntity(pos) is not IBlockEntityContainer container
                || container.Inventory == null)
            {
                return null;
            }

            if (canUseContainer != null && !canUseContainer(pos))
            {
                result.DeniedContainers++;
                continue;
            }

            result.Entries.Add(new NetworkContainer { Container = container, Pos = pos, Settings = settings?.Get(pos) });
        }

        return result;
    }

    /// <summary>
    /// Step 1 of a scan, and the expensive part: walks the cables to find the network's layout
    /// (its network blocks and the positions of the storage containers touching it).
    /// </summary>
    public static NetworkTopology ScanTopology(IBlockAccessor blockAccessor, BlockPos start, StorageFilter storageFilter, int maxNetworkBlocks)
    {
        var result = new NetworkTopology();
        var visited = new HashSet<BlockPos> { start.Copy() };
        var queue = new Queue<BlockPos>();
        queue.Enqueue(start.Copy());

        while (queue.Count > 0)
        {
            var pos = queue.Dequeue();
            result.NetworkBlocks.Add(pos);
            var current = blockAccessor.GetBlock(pos) as INetworkBlock;

            foreach (var face in BlockFacing.ALLFACES)
            {
                // Only leave through sides this block connects on (e.g. a terminal's cable ports).
                if (current != null && !current.ConnectsOnFace(face)) continue;

                var neighbourPos = pos.AddCopy(face);
                // Not marked visited yet: a neighbour refused from this side may still be reachable from another.
                if (visited.Contains(neighbourPos)) continue;

                var block = blockAccessor.GetBlock(neighbourPos);
                if (block is INetworkBlock neighbour)
                {
                    // ...and only enter through sides the neighbour connects on.
                    if (!neighbour.ConnectsOnFace(face.Opposite)) continue;

                    if (result.NetworkBlocks.Count + queue.Count >= maxNetworkBlocks)
                    {
                        result.HitSizeLimit = true;
                        continue;
                    }
                    visited.Add(neighbourPos);
                    queue.Enqueue(neighbourPos);
                }
                else if (storageFilter.IsStorage(block)
                         && blockAccessor.GetBlockEntity(neighbourPos) is IBlockEntityContainer container
                         && container.Inventory != null)
                {
                    visited.Add(neighbourPos);
                    result.ContainerPositions.Add(neighbourPos);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Combines identical items across all containers into one stack per item type.
    /// The returned stacks are copies, so changing them does not touch real inventories.
    /// </summary>
    public static List<ItemStack> AggregateContents(IWorldAccessor world, NetworkScanResult scan)
    {
        var totals = new List<ItemStack>();
        // Group by item/block id first so we only compare stacks that can possibly match.
        var buckets = new Dictionary<(EnumItemClass, int), List<ItemStack>>();

        foreach (var slot in scan.AllSlots)
        {
            if (slot.Empty) continue;

            var stack = slot.Itemstack;
            var key = (stack.Class, stack.Id);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                buckets[key] = bucket = new List<ItemStack>();
            }

            var existing = bucket.Find(t => t.Equals(world, stack, GlobalConstants.IgnoredStackAttributes));
            if (existing != null)
            {
                existing.StackSize += stack.StackSize;
            }
            else
            {
                var copy = stack.Clone();
                bucket.Add(copy);
                totals.Add(copy);
            }
        }

        return totals;
    }

    /// <summary>
    /// Stores up to <paramref name="quantity"/> items from <paramref name="source"/> in the network,
    /// respecting container priorities and filters (see <see cref="NetworkScanResult.InsertGroupsFor"/>).
    /// Returns how many items were moved.
    /// </summary>
    public static int MoveIntoNetwork(IWorldAccessor world, ItemSlot source, NetworkScanResult scan, int quantity)
    {
        if (source.Empty) return 0;

        int moved = 0;
        foreach (var group in scan.InsertGroupsFor(source.Itemstack))
        {
            if (moved >= quantity || source.Empty) break;
            moved += MoveInto(world, source, group, quantity - moved);
        }
        return moved;
    }

    /// <summary>
    /// Moves up to <paramref name="quantity"/> items from <paramref name="source"/> into
    /// <paramref name="sinks"/>, topping up matching stacks first, then using empty slots.
    /// Uses the game's own TryPutInto, so slot rules (e.g. crates holding one item type) apply.
    /// Returns how many items were moved.
    /// </summary>
    public static int MoveInto(IWorldAccessor world, ItemSlot source, IEnumerable<ItemSlot> sinks, int quantity)
    {
        var sinkList = sinks.ToList();
        int remaining = quantity;

        foreach (bool fillPartialStacks in new[] { true, false })
        {
            foreach (var sink in sinkList)
            {
                if (remaining <= 0 || source.Empty) return quantity - remaining;
                // First pass only touches non-empty slots, second pass only empty ones.
                if (sink.Empty == fillPartialStacks) continue;

                int moved = source.TryPutInto(world, sink, remaining);
                if (moved > 0)
                {
                    sink.MarkDirty();
                    remaining -= moved;
                }
            }
        }

        return quantity - remaining;
    }
}
