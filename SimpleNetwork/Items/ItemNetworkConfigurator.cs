using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SimpleNetwork.Items;

/// <summary>
/// A tool for setting a storage container's network priority and filter:
/// sneak + right-click the container to open its settings window.
/// </summary>
public class ItemNetworkConfigurator : Item
{
    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (!firstEvent || blockSel == null || !byEntity.Controls.ShiftKey)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
            return;
        }

        handling = EnumHandHandling.PreventDefault;
        if (byEntity is EntityPlayer { Player: IServerPlayer player })
        {
            api.ModLoader.GetModSystem<SimpleNetworkModSystem>().StorageSettingsServer?.Open(player, blockSel.Position);
        }
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        dsc.AppendLine(L.Get("configurator-howto"));
    }

    public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot) => new[]
    {
        new WorldInteraction { ActionLangCode = $"{L.Domain}:configurator-help", MouseButton = EnumMouseButton.Right, HotKeyCode = "shift" }
    };
}
