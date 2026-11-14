using System.Text;
using SimpleNetwork.Blocks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SimpleNetwork.Items;

/// <summary>
/// A handheld terminal. Sneak + right-click a Network Terminal to link it; afterwards
/// right-click anywhere (within range) to open that terminal's network window.
/// The link is stored on the item stack itself.
/// </summary>
public class ItemWirelessTerminal : Item
{
    private const string AttrX = "simplenetwork-linkX";
    private const string AttrY = "simplenetwork-linkY";
    private const string AttrZ = "simplenetwork-linkZ";
    private const string AttrDim = "simplenetwork-linkDim";

    public static BlockPos? GetLink(ItemStack? stack)
    {
        var attrs = stack?.Attributes;
        if (attrs == null || !attrs.HasAttribute(AttrX)) return null;
        return new BlockPos(attrs.GetInt(AttrX), attrs.GetInt(AttrY), attrs.GetInt(AttrZ)).SetDimension(attrs.GetInt(AttrDim));
    }

    private static void SetLink(ItemStack stack, BlockPos pos)
    {
        stack.Attributes.SetInt(AttrX, pos.X);
        stack.Attributes.SetInt(AttrY, pos.Y);
        stack.Attributes.SetInt(AttrZ, pos.Z);
        stack.Attributes.SetInt(AttrDim, pos.dimension);
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (!firstEvent) return;

        // Handled on both sides, so the game doesn't also try a default action (like placing).
        handling = EnumHandHandling.PreventDefault;

        // The real work happens on the server.
        if (byEntity is not EntityPlayer { Player: IServerPlayer player } || slot.Itemstack == null) return;
        var world = byEntity.World;

        if (!api.ModLoader.GetModSystem<SimpleNetworkModSystem>().Config.EnableWirelessTerminal)
        {
            Tell(player, "msg-disabled-wireless");
            return;
        }

        // Sneak + right-click a terminal: link to it.
        if (blockSel != null && byEntity.Controls.ShiftKey && world.BlockAccessor.GetBlock(blockSel.Position) is BlockNetworkTerminal)
        {
            if (world.Claims.TestAccess(player, blockSel.Position, EnumBlockAccessFlags.Use) != EnumWorldAccessResponse.Granted)
            {
                Tell(player, "msg-wireless-noaccess");
                return;
            }

            SetLink(slot.Itemstack, blockSel.Position);
            slot.MarkDirty();
            var local = blockSel.Position.ToLocalPosition(api);
            Tell(player, "msg-wireless-linked", local.X, local.Y, local.Z);
            return;
        }

        // Otherwise: open the linked network.
        var link = GetLink(slot.Itemstack);
        if (link == null)
        {
            Tell(player, "msg-wireless-notlinked");
            return;
        }

        api.ModLoader.GetModSystem<SimpleNetworkModSystem>().TerminalServer?.OpenWireless(player, link);
    }

    private static void Tell(IServerPlayer player, string langKey, params object[] args) =>
        player.SendMessage(GlobalConstants.GeneralChatGroup, L.GetL(player.LanguageCode, langKey, args), EnumChatType.Notification);

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

        var link = GetLink(inSlot.Itemstack);
        if (link == null)
        {
            dsc.AppendLine(L.Get("wireless-unlinked"));
        }
        else
        {
            var local = link.ToLocalPosition(api);
            dsc.AppendLine(L.Get("wireless-linked", local.X, local.Y, local.Z));
        }
    }

    public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot) => new[]
    {
        new WorldInteraction { ActionLangCode = $"{L.Domain}:wireless-help-open", MouseButton = EnumMouseButton.Right },
        new WorldInteraction { ActionLangCode = $"{L.Domain}:wireless-help-link", MouseButton = EnumMouseButton.Right, HotKeyCode = "shift" }
    };
}
