using System.Collections.Generic;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace SimpleNetwork.Network;

/// <summary>Priority and filter of one storage container.</summary>
public class ContainerSettings
{
    public const int MinPriority = -10;
    public const int MaxPriority = 10;
    public const int MaxFilters = 9;

    /// <summary>Higher = filled first and emptied last.</summary>
    public int Priority;

    /// <summary>If not empty, the container only accepts these item types from the network.</summary>
    public List<ItemStack> Filters = new();

    public bool IsDefault => Priority == 0 && Filters.Count == 0;

    public bool Accepts(ItemStack stack) =>
        Filters.Count == 0 || Filters.Any(f => SameType(f, stack));

    public bool HasFilterFor(ItemStack stack) => Filters.Any(f => SameType(f, stack));

    private static bool SameType(ItemStack a, ItemStack b) =>
        a.Collectible?.Code != null && a.Collectible.Code.Equals(b.Collectible?.Code);
}

/// <summary>
/// Server-side store of per-container settings, keyed by block position and saved in the world save.
/// Vanilla chests can't hold extra data of ours, so the settings live here instead.
/// </summary>
public class StorageSettingsRegistry
{
    private const string SaveKey = "simplenetwork-storagesettings";

    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SaveData
    {
        public Dictionary<string, SavedEntry>? Entries;
    }

    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class SavedEntry
    {
        public int Priority;
        public List<byte[]>? Filters;
    }

    private readonly ICoreServerAPI sapi;
    private readonly Dictionary<string, ContainerSettings> settings = new();

    public StorageSettingsRegistry(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
        sapi.Event.SaveGameLoaded += Load;
        sapi.Event.GameWorldSave += Save;
        // Breaking a container forgets its settings, so a new block there starts fresh.
        sapi.Event.DidBreakBlock += (byPlayer, oldBlockId, blockSel) => settings.Remove(Key(blockSel.Position));
    }

    private static string Key(BlockPos pos) => $"{pos.X},{pos.Y},{pos.Z},{pos.dimension}";

    /// <summary>The settings for a container, or null if it has none (priority 0, no filter).</summary>
    public ContainerSettings? Get(BlockPos pos) => settings.TryGetValue(Key(pos), out var s) ? s : null;

    public void Set(BlockPos pos, ContainerSettings value)
    {
        if (value.IsDefault) settings.Remove(Key(pos));
        else settings[Key(pos)] = value;
    }

    private void Load()
    {
        settings.Clear();
        var data = sapi.WorldManager.SaveGame.GetData(SaveKey);
        if (data == null) return;

        var saved = SerializerUtil.Deserialize<SaveData>(data);
        foreach (var (key, entry) in saved.Entries ?? new Dictionary<string, SavedEntry>())
        {
            var filters = new List<ItemStack>();
            foreach (var bytes in entry.Filters ?? new List<byte[]>())
            {
                var stack = new ItemStack(bytes);
                if (stack.ResolveBlockOrItem(sapi.World)) filters.Add(stack); // skip items from removed mods
            }
            settings[key] = new ContainerSettings { Priority = entry.Priority, Filters = filters };
        }
    }

    private void Save()
    {
        var data = new SaveData
        {
            Entries = settings.ToDictionary(kv => kv.Key, kv => new SavedEntry
            {
                Priority = kv.Value.Priority,
                Filters = kv.Value.Filters.Select(f => f.ToBytes()).ToList()
            })
        };
        sapi.WorldManager.SaveGame.StoreData(SaveKey, SerializerUtil.Serialize(data));
    }
}
