using SimpleNetwork.Gui;
using Vintagestory.API.Client;

namespace SimpleNetwork.Network;

/// <summary>Client side of the container settings window: opens it and keeps it in sync with the server.</summary>
public class StorageSettingsClient
{
    private readonly ICoreClientAPI capi;
    private readonly IClientNetworkChannel channel;
    private GuiDialogStorageSettings? dialog;

    public StorageSettingsClient(ICoreClientAPI capi, IClientNetworkChannel channel)
    {
        this.capi = capi;
        this.channel = channel;
        channel.SetMessageHandler<StorageSettingsPacket>(OnSettingsPacket);
    }

    private void OnSettingsPacket(StorageSettingsPacket packet)
    {
        if (packet.Open)
        {
            // A fresh window each time, so the title shows the right container name.
            dialog?.TryClose();
            dialog = new GuiDialogStorageSettings(capi, packet.ContainerName ?? "", p => channel.SendPacket(p));
            dialog.SetData(packet);
            dialog.TryOpen();
        }
        else if (dialog != null && dialog.IsOpened() && dialog.IsFor(packet))
        {
            dialog.SetData(packet);
        }
    }
}
