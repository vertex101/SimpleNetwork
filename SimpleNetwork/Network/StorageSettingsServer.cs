using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SimpleNetwork.Network;

/// <summary>
/// Server side of the container settings window (opened with the Network Configurator).
/// Every change is re-checked: the block must still be network storage, the player must be
/// close by and allowed to use it.
/// </summary>
public class StorageSettingsServer
{
    private const double MaxDistance = 8;

    private readonly ICoreServerAPI sapi;
    private readonly SimpleNetworkModSystem modSystem;
    private readonly IServerNetworkChannel channel;

    public StorageSettingsServer(ICoreServerAPI sapi, SimpleNetworkModSystem modSystem, IServerNetworkChannel channel)
    {
        this.sapi = sapi;
        this.modSystem = modSystem;
        this.channel = channel;
        channel.SetMessageHandler<StorageSettingsPacket>(OnChangeRequested);
    }

    /// <summary>Called when a player sneak + right-clicks a block with the configurator.</summary>
    public void Open(IServerPlayer player, BlockPos pos)
    {
        if (!CheckAllowed(player, pos, tellPlayer: true)) return;
        SendSettings(player, pos, open: true);
    }

    private bool CheckAllowed(IServerPlayer player, BlockPos pos, bool tellPlayer)
    {
        string? problem = null;
        var block = sapi.World.BlockAccessor.GetBlock(pos);

        if (!modSystem.StorageFilter.IsStorage(block) || sapi.World.BlockAccessor.GetBlockEntity(pos) is not IBlockEntityContainer)
        {
            problem = "msg-config-notstorage";
        }
        else if (!modSystem.Access!.CanUse(player, pos))
        {
            problem = "msg-config-noaccess";
        }
        else if (player.Entity == null || player.Entity.Pos.DistanceTo(pos.ToVec3d().Add(0.5, 0.5, 0.5)) > MaxDistance)
        {
            problem = "msg-config-toofar";
        }

        if (problem != null && tellPlayer)
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, L.GetL(player.LanguageCode, problem), EnumChatType.Notification);
        }
        return problem == null;
    }

    private void SendSettings(IServerPlayer player, BlockPos pos, bool open)
    {
        var settings = modSystem.StorageSettings!.Get(pos);
        channel.SendPacket(new StorageSettingsPacket
        {
            X = pos.X, Y = pos.Y, Z = pos.Z, Dimension = pos.dimension,
            Priority = settings?.Priority ?? 0,
            Filters = settings?.Filters.Select(f => f.ToBytes()).ToList() ?? new List<byte[]>(),
            Open = open,
            ContainerName = sapi.World.BlockAccessor.GetBlock(pos).GetPlacedBlockName(sapi.World, pos)
        }, player);
    }

    private void OnChangeRequested(IServerPlayer player, StorageSettingsPacket packet)
    {
        var pos = new BlockPos(packet.X, packet.Y, packet.Z).SetDimension(packet.Dimension);
        if (!CheckAllowed(player, pos, tellPlayer: true)) return;

        var filters = new List<ItemStack>();
        foreach (var bytes in (packet.Filters ?? new List<byte[]>()).Take(ContainerSettings.MaxFilters))
        {
            try
            {
                var stack = new ItemStack(bytes);
                if (!stack.ResolveBlockOrItem(sapi.World)) continue;
                stack.StackSize = 1;
                // One entry per item type.
                if (!filters.Any(f => f.Collectible.Code.Equals(stack.Collectible.Code))) filters.Add(stack);
            }
            catch (Exception)
            {
                // ignore malformed entries
            }
        }

        modSystem.StorageSettings!.Set(pos, new ContainerSettings
        {
            Priority = GameMath.Clamp(packet.Priority, ContainerSettings.MinPriority, ContainerSettings.MaxPriority),
            Filters = filters
        });

        // Send back what was actually saved, so the window always shows the truth.
        SendSettings(player, pos, open: false);
    }
}
