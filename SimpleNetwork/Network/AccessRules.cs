using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SimpleNetwork.Network;

/// <summary>
/// Who may use which container through the network. The rule everywhere is: the network only
/// touches containers the responsible player could open by hand, so it can't be used to reach
/// into other players' land claims or padlocked storage.
/// </summary>
public class AccessRules
{
    private readonly ICoreServerAPI sapi;

    public AccessRules(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
    }

    /// <summary>For a player who is online and using a terminal right now.</summary>
    public bool CanUse(IPlayer player, BlockPos pos)
    {
        if (sapi.World.Claims.TestAccess(player, pos, EnumBlockAccessFlags.Use) != EnumWorldAccessResponse.Granted)
        {
            return false;
        }

        var reinforcement = sapi.ModLoader.GetModSystem<ModSystemBlockReinforcement>();
        return reinforcement == null || !reinforcement.IsLockedForInteract(pos, player);
    }

    /// <summary>
    /// For blocks that work on their own (importers/exporters), acting for whoever placed them.
    /// Uses the full player check when the game knows that player; otherwise only allows
    /// unclaimed, unlocked containers.
    /// </summary>
    public bool CanUse(string? ownerUid, BlockPos pos)
    {
        var owner = string.IsNullOrEmpty(ownerUid) ? null : sapi.World.PlayerByUid(ownerUid);
        if (owner != null) return CanUse(owner, pos);

        if (sapi.World.Claims.Get(pos)?.Length > 0) return false;
        var lockInfo = sapi.ModLoader.GetModSystem<ModSystemBlockReinforcement>()?.GetReinforcment(pos);
        return lockInfo == null || !lockInfo.Locked;
    }
}
