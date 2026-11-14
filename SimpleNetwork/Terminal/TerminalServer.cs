using System;
using System.Collections.Generic;
using System.Linq;
using SimpleNetwork.Blocks;
using SimpleNetwork.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SimpleNetwork.Terminal;

/// <summary>
/// Server side of the terminal. Tracks which player has which terminal open,
/// sends them the network contents, carries out take/insert requests, and
/// refills the crafting grid from the network after each craft.
/// The server never trusts the client: every request is re-checked here.
/// </summary>
public class TerminalServer
{
    /// <summary>How far (in blocks) a player may be from the terminal and still use it.</summary>
    private const double MaxUseDistance = 10;

    private const int RefreshIntervalMs = 1000;

    /// <summary>
    /// Attributes ignored when deciding whether a network item can refill a crafting slot.
    /// Durability is ignored too, so a worn tool can replace a used-up one of the same kind.
    /// </summary>
    private static readonly string[] RefillIgnoredAttributes =
        GlobalConstants.IgnoredStackAttributes.Append("durability").ToArray();

    private readonly ICoreServerAPI sapi;
    private readonly SimpleNetworkModSystem modSystem;
    private readonly IServerNetworkChannel channel;
    private readonly Dictionary<string, Session> sessions = new();

    private class Session
    {
        public IServerPlayer Player = null!;
        public BlockPos Pos = null!;

        /// <summary>How far the player may be from the terminal: a few blocks normally, the wireless range otherwise.</summary>
        public double MaxDistance = MaxUseDistance;
        public bool IsWireless;

        /// <summary>Hash of the last contents sent, so unchanged contents aren't re-sent.</summary>
        public int LastHash;

        public IInventory? CraftingGrid;
        public Action<int>? CraftingSlotHandler;

        /// <summary>Crafting grid contents as of the end of the last server tick, indexed by slot.</summary>
        public ItemStack?[] GridSnapshot = Array.Empty<ItemStack?>();

        public bool SettleScheduled;
        public bool CraftHappened;

        /// <summary>
        /// True when the last craft was a shift-click (output went to the inventory, not the cursor).
        /// Then, after refilling, we craft again, and keep going until nothing more can be crafted.
        /// </summary>
        public bool CraftAllActive;

        /// <summary>Hidden inventory that receives shift-clicked items (see InventoryNetworkIntake).</summary>
        public InventoryNetworkIntake? Intake;
        public Action<int>? IntakeSlotHandler;
        public bool IntakeDrainScheduled;
    }

    public TerminalServer(ICoreServerAPI sapi, SimpleNetworkModSystem modSystem, IServerNetworkChannel channel)
    {
        this.sapi = sapi;
        this.modSystem = modSystem;
        this.channel = channel;

        channel
            .SetMessageHandler<TerminalActionPacket>(OnActionPacket)
            .SetMessageHandler<TerminalClosedPacket>((player, _) => CloseSession(player.PlayerUID, returnGridItems: true));

        sapi.Event.RegisterGameTickListener(OnRefreshTick, RefreshIntervalMs);
        sapi.Event.PlayerDisconnect += player => CloseSession(player.PlayerUID, returnGridItems: true);

        // The game pushes this event at the end of every crafting-grid craft (including shift-click "craft many").
        sapi.Event.RegisterEventBusListener(OnItemCrafted, 0.5, "onitemcrafted");
    }

    /// <summary>
    /// Opens a terminal's network for a wireless terminal user, or tells them why it can't.
    /// </summary>
    public void OpenWireless(IServerPlayer player, BlockPos terminalPos)
    {
        void Tell(string key, params object[] args) =>
            player.SendMessage(GlobalConstants.GeneralChatGroup, L.GetL(player.LanguageCode, key, args), EnumChatType.Notification);

        var entity = player.Entity;
        if (entity == null) return;

        if (!modSystem.Config.EnableWirelessTerminal)
        {
            Tell("msg-disabled-wireless");
            return;
        }

        int range = modSystem.Config.WirelessRange;
        double distance = entity.Pos.DistanceTo(terminalPos.ToVec3d().Add(0.5, 0.5, 0.5));
        bool sameDimension = entity.Pos.Dimension == terminalPos.dimension;
        if (!sameDimension || (range > 0 && distance > range))
        {
            Tell("msg-wireless-range", sameDimension ? (int)distance : "?", range);
            return;
        }

        if (sapi.World.BlockAccessor.GetChunkAtBlockPos(terminalPos) == null)
        {
            Tell("msg-wireless-unloaded");
            return;
        }

        if (sapi.World.BlockAccessor.GetBlock(terminalPos) is not BlockNetworkTerminal)
        {
            Tell("msg-wireless-missing");
            return;
        }

        Open(player, terminalPos, range > 0 ? range : double.MaxValue, wireless: true);
    }

    /// <summary>Called when a player right-clicks a terminal (or uses a linked wireless terminal).</summary>
    public void Open(IServerPlayer player, BlockPos terminalPos, double maxDistance = MaxUseDistance, bool wireless = false)
    {
        // Re-opening (e.g. clicking another terminal) replaces the old session; the grid stays as it is.
        CloseSession(player.PlayerUID, returnGridItems: false);

        var session = new Session { Player = player, Pos = terminalPos.Copy(), MaxDistance = maxDistance, IsWireless = wireless };
        session.CraftingGrid = CraftingGridSlots.Get(player);
        if (session.CraftingGrid != null)
        {
            TakeGridSnapshot(session);
            session.CraftingSlotHandler = _ => ScheduleSettle(session);
            session.CraftingGrid.SlotModified += session.CraftingSlotHandler;
        }

        session.Intake = new InventoryNetworkIntake(player.PlayerUID, sapi);
        session.IntakeSlotHandler = _ => ScheduleIntakeDrain(session);
        session.Intake.SlotModified += session.IntakeSlotHandler;
        player.InventoryManager.OpenInventory(session.Intake);

        sessions[player.PlayerUID] = session;
        SendContents(session, open: true, force: true);
    }

    private void CloseSession(string playerUid, bool returnGridItems)
    {
        if (!sessions.Remove(playerUid, out var session)) return;

        if (session.CraftingGrid != null && session.CraftingSlotHandler != null)
        {
            session.CraftingGrid.SlotModified -= session.CraftingSlotHandler;
        }

        if (returnGridItems) ReturnCraftingGridItems(session);

        if (session.Intake != null)
        {
            if (session.IntakeSlotHandler != null) session.Intake.SlotModified -= session.IntakeSlotHandler;
            DrainIntake(session); // anything still in transit goes to the network (or back to the player)
            session.Player.InventoryManager.CloseInventory(session.Intake);
        }
    }

    /// <summary>Server-initiated close: the player walked away or the terminal is gone.</summary>
    private void EndSession(Session session)
    {
        CloseSession(session.Player.PlayerUID, returnGridItems: true);
        channel.SendPacket(new TerminalContentsPacket { Close = true }, session.Player);

        if (session.IsWireless)
        {
            session.Player.SendMessage(GlobalConstants.GeneralChatGroup,
                L.GetL(session.Player.LanguageCode, "msg-wireless-lost"), EnumChatType.Notification);
        }
    }

    /// <summary>Scans the session's network, leaving out containers the player isn't allowed to use (see AccessRules).</summary>
    private NetworkScanResult Scan(Session session) =>
        modSystem.NetworkCache!.Scan(session.Pos, pos => modSystem.Access!.CanUse(session.Player, pos));

    private bool IsStillValid(Session session)
    {
        var entity = session.Player.Entity;
        if (entity == null) return false;
        if (sapi.World.BlockAccessor.GetBlock(session.Pos) is not BlockNetworkTerminal) return false;

        if (entity.Pos.Dimension != session.Pos.dimension) return false;
        if (session.MaxDistance == double.MaxValue) return true; // unlimited wireless range

        var terminalCenter = session.Pos.ToVec3d().Add(0.5, 0.5, 0.5);
        return entity.Pos.SquareDistanceTo(terminalCenter) <= session.MaxDistance * session.MaxDistance;
    }

    private void SendContents(Session session, bool open, bool force, NetworkScanResult? scan = null)
    {
        scan ??= Scan(session);
        var contents = NetworkScanner.AggregateContents(sapi.World, scan);

        var packet = new TerminalContentsPacket
        {
            Open = open,
            Stacks = contents.Select(s => s.ToBytes()).ToList(),
            ContainerCount = scan.ContainerCount,
            HitSizeLimit = scan.HitSizeLimit,
            MaxNetworkBlocks = modSystem.Config.MaxNetworkBlocks,
            DeniedContainers = scan.DeniedContainers
        };
        foreach (var slot in scan.AllSlots)
        {
            packet.TotalSlots++;
            if (!slot.Empty) packet.UsedSlots++;
        }

        var hash = new HashCode();
        hash.Add(packet.ContainerCount);
        hash.Add(packet.HitSizeLimit);
        hash.Add(packet.UsedSlots);
        hash.Add(packet.TotalSlots);
        hash.Add(packet.DeniedContainers);
        foreach (var bytes in packet.Stacks) hash.AddBytes(bytes);
        int newHash = hash.ToHashCode();

        if (!force && newHash == session.LastHash) return;
        session.LastHash = newHash;
        channel.SendPacket(packet, session.Player);
    }

    private void OnRefreshTick(float dt)
    {
        foreach (var (uid, session) in sessions.ToList())
        {
            if (sapi.World.PlayerByUid(uid) is not IServerPlayer)
            {
                CloseSession(uid, returnGridItems: false);
                continue;
            }

            if (!IsStillValid(session))
            {
                EndSession(session);
                continue;
            }

            SendContents(session, open: false, force: false);
        }
    }

    private void OnActionPacket(IServerPlayer player, TerminalActionPacket packet)
    {
        if (!sessions.TryGetValue(player.PlayerUID, out var session)) return;
        if (!IsStillValid(session))
        {
            EndSession(session);
            return;
        }

        var scan = Scan(session);
        switch (packet.Action)
        {
            case TerminalAction.Take:
                Take(player, scan, packet);
                break;
            case TerminalAction.Insert:
                Insert(player, scan, packet.Quantity);
                break;
            case TerminalAction.FillRecipe:
                FillRecipe(session, packet.RecipeIndex, packet.RecipeOutputCode);
                break;
        }

        // Send the updated contents right away so the grid feels responsive.
        SendContents(session, open: false, force: true);
    }

    // ---------------------------------------------------------------- take / insert

    private void Take(IServerPlayer player, NetworkScanResult scan, TerminalActionPacket packet)
    {
        if (packet.Stack == null) return;

        ItemStack wanted;
        try
        {
            wanted = new ItemStack(packet.Stack);
        }
        catch (Exception)
        {
            return; // malformed packet
        }
        if (!wanted.ResolveBlockOrItem(sapi.World)) return;

        var world = sapi.World;
        int quantity = GameMath.Clamp(packet.Quantity, 1, Math.Max(1, wanted.Collectible.MaxStackSize));

        // Pull matching items out of the containers into a temporary slot.
        var taken = new DummySlot();
        foreach (var slot in scan.TakeSlots)
        {
            if (slot.Empty || !slot.Itemstack.Equals(world, wanted, GlobalConstants.IgnoredStackAttributes)) continue;

            if (slot.TryPutInto(world, taken, quantity - taken.StackSize) > 0)
            {
                slot.MarkDirty();
            }
            if (taken.StackSize >= quantity) break;
        }
        if (taken.Empty) return;
        sapi.World.PlaySoundFor(HeldSounds.InvPickUpDefault, player);

        // Hand the items to the player: mouse cursor first (unless shift-clicked), then inventory.
        if (!packet.ToInventory)
        {
            var mouseSlot = player.InventoryManager.MouseItemSlot;
            if (taken.TryPutInto(world, mouseSlot, taken.StackSize) > 0)
            {
                mouseSlot.MarkDirty();
            }
        }

        GiveOrReturn(player, scan, taken);
    }

    private void Insert(IServerPlayer player, NetworkScanResult scan, int requestedQuantity)
    {
        var mouseSlot = player.InventoryManager.MouseItemSlot;
        if (mouseSlot.Empty) return;

        int quantity = GameMath.Clamp(requestedQuantity, 1, mouseSlot.StackSize);
        if (NetworkScanner.MoveIntoNetwork(sapi.World, mouseSlot, scan, quantity) > 0)
        {
            mouseSlot.MarkDirty();
            sapi.World.PlaySoundFor(HeldSounds.InvPlaceDefault, player);
        }
    }

    /// <summary>
    /// Puts whatever is in <paramref name="source"/> into the player's inventory; what doesn't
    /// fit goes into the network, and as a last resort is dropped at the player's feet.
    /// </summary>
    private void GiveOrReturn(IServerPlayer player, NetworkScanResult scan, ItemSlot source)
    {
        var world = sapi.World;
        if (!source.Empty) NetworkScanner.MoveInto(world, source, PlayerInventorySlots(player), source.StackSize);
        if (!source.Empty) NetworkScanner.MoveIntoNetwork(world, source, scan, source.StackSize);
        if (!source.Empty && player.Entity != null)
        {
            world.SpawnItemEntity(source.Itemstack, player.Entity.Pos.XYZ);
            source.Itemstack = null;
        }
    }

    /// <summary>The player's hotbar followed by their backpack slots.</summary>
    private static IEnumerable<ItemSlot> PlayerInventorySlots(IServerPlayer player)
    {
        foreach (var invName in new[] { GlobalConstants.hotBarInvClassName, GlobalConstants.backpackInvClassName })
        {
            var inv = player.InventoryManager.GetOwnInventory(invName);
            if (inv == null) continue;
            foreach (var slot in inv) yield return slot;
        }
    }

    // ---------------------------------------------------------------- shift-click intake

    private void ScheduleIntakeDrain(Session session)
    {
        if (session.IntakeDrainScheduled) return;
        session.IntakeDrainScheduled = true;

        sapi.Event.RegisterCallback(_ =>
        {
            session.IntakeDrainScheduled = false;
            if (!sessions.TryGetValue(session.Player.PlayerUID, out var current) || current != session) return;
            DrainIntake(session);
        }, 0);
    }

    /// <summary>Moves everything in the intake into the network; what doesn't fit goes back to the player.</summary>
    private void DrainIntake(Session session)
    {
        var intake = session.Intake;
        if (intake == null || intake.Empty) return;

        var scan = Scan(session);
        foreach (var slot in intake)
        {
            if (slot.Empty) continue;

            NetworkScanner.MoveIntoNetwork(sapi.World, slot, scan, slot.StackSize);
            GiveOrReturn(session.Player, scan, slot);
            slot.MarkDirty();
        }

        if (sessions.ContainsKey(session.Player.PlayerUID))
        {
            SendContents(session, open: false, force: true, scan);
        }
    }

    // ---------------------------------------------------------------- crafting grid

    /// <summary>
    /// Runs once at the end of the tick in which the grid changed. Crafts happen entirely
    /// within one tick, so if a craft happened the snapshot still holds the pre-craft grid
    /// and we can top every slot back up to it. Then we take a fresh snapshot.
    /// </summary>
    private void ScheduleSettle(Session session)
    {
        if (session.SettleScheduled) return;
        session.SettleScheduled = true;

        sapi.Event.RegisterCallback(_ =>
        {
            session.SettleScheduled = false;
            if (!sessions.TryGetValue(session.Player.PlayerUID, out var current) || current != session) return;

            bool continueCraftAll = false;
            if (session.CraftHappened)
            {
                session.CraftHappened = false;
                RefillCraftingGrid(session);
                continueCraftAll = session.CraftAllActive;
                session.CraftAllActive = false;
            }

            // Snapshot before crafting again, so the next refill tops up to the full amounts.
            TakeGridSnapshot(session);

            // Each craft fires onitemcrafted again, which brings us back here next tick.
            // The loop ends by itself once nothing can be crafted (inventory full or ingredients gone).
            if (continueCraftAll) CraftManyIntoInventory(session);
        }, 0);
    }

    /// <summary>Does what shift-clicking the crafting output does: craft as many as possible into the inventory.</summary>
    private void CraftManyIntoInventory(Session session)
    {
        var grid = session.CraftingGrid;
        if (grid == null || !IsStillValid(session)) return;

        var outputSlot = grid[CraftingGridSlots.OutputIndex(grid)];
        if (outputSlot == null || outputSlot.Empty) return;

        var op = new ItemStackMoveOperation(sapi.World, EnumMouseButton.Left, EnumModifierKey.SHIFT, EnumMergePriority.AutoMerge, outputSlot.StackSize)
        {
            ActingPlayer = session.Player
        };
        session.Player.InventoryManager.TryTransferAway(outputSlot, ref op, onlyPlayerInventory: true);
    }

    private void OnItemCrafted(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (data is not ITreeAttribute tree) return;
        long crafterEntityId = tree.GetLong("byentityid");

        var session = sessions.Values.FirstOrDefault(s => s.Player.Entity?.EntityId == crafterEntityId);
        if (session == null) return;

        // A normal click puts the result on the cursor; a shift-click sends it to the inventory.
        // So if the cursor isn't holding the crafted item, this was a shift-click: keep crafting.
        var crafted = tree.GetItemstack("itemstack");
        var mouseStack = session.Player.InventoryManager.MouseItemSlot.Itemstack;
        bool wentToCursor = mouseStack != null && crafted != null
                            && mouseStack.Collectible?.Code != null
                            && mouseStack.Collectible.Code.Equals(crafted.Collectible?.Code);

        session.CraftHappened = true;
        session.CraftAllActive = !wentToCursor;
        ScheduleSettle(session);
    }

    private void TakeGridSnapshot(Session session)
    {
        var grid = session.CraftingGrid;
        if (grid == null) return;

        session.GridSnapshot = new ItemStack?[grid.Count];
        foreach (int i in CraftingGridSlots.InputIndices(grid))
        {
            session.GridSnapshot[i] = grid[i]?.Itemstack?.Clone();
        }
    }

    private void RefillCraftingGrid(Session session)
    {
        var grid = session.CraftingGrid;
        if (grid == null || !IsStillValid(session)) return;

        var world = sapi.World;
        var scan = Scan(session);
        bool anyRefilled = false;

        foreach (int i in CraftingGridSlots.InputIndices(grid))
        {
            var target = i < session.GridSnapshot.Length ? session.GridSnapshot[i] : null;
            if (target == null) continue;

            var gridSlot = grid[i];
            if (gridSlot == null) continue;
            if (!gridSlot.Empty && !gridSlot.Itemstack.Equals(world, target, RefillIgnoredAttributes)) continue;

            int need = Math.Min(target.StackSize, target.Collectible.MaxStackSize) - gridSlot.StackSize;
            if (need <= 0) continue;

            foreach (var networkSlot in scan.TakeSlots)
            {
                if (networkSlot.Empty || !networkSlot.Itemstack.Equals(world, target, RefillIgnoredAttributes)) continue;

                int moved = networkSlot.TryPutInto(world, gridSlot, need);
                if (moved > 0)
                {
                    networkSlot.MarkDirty();
                    need -= moved;
                    anyRefilled = true;
                }
                if (need <= 0) break;
            }

            // Tells the grid to re-check the recipe and syncs the slot to the client.
            gridSlot.MarkDirty();
        }

        if (anyRefilled) SendContents(session, open: false, force: true, scan);
    }

    /// <summary>
    /// Recipe lookup: empties the crafting grid into the network, then lays out one craft's worth
    /// of the recipe's ingredients from the network. For wildcard ingredients (e.g. "any plank")
    /// it uses whichever matching item the network has the most of.
    /// </summary>
    private void FillRecipe(Session session, int recipeIndex, string? expectedOutputCode)
    {
        var grid = session.CraftingGrid;
        var recipes = sapi.World.GridRecipes;
        if (grid == null || recipeIndex < 0 || recipeIndex >= recipes.Count) return;

        var recipe = recipes[recipeIndex];
        var output = recipe.Output?.ResolvedItemStack;
        if (output == null || output.Collectible.Code.ToString() != expectedOutputCode) return; // lists out of sync
        if (recipe.Width <= 0 || recipe.Width > 3 || recipe.Height > 3) return;

        var world = sapi.World;
        ReturnCraftingGridItems(session);

        var scan = Scan(session);
        var networkTotals = NetworkScanner.AggregateContents(world, scan);
        var inputIndices = CraftingGridSlots.InputIndices(grid);
        var chosenByIngredient = new Dictionary<CraftingRecipeIngredient, ItemStack?>();
        var missing = new List<string>();

        var ingredients = recipe.ResolvedIngredients ?? Array.Empty<CraftingRecipeIngredient>();
        for (int i = 0; i < ingredients.Length; i++)
        {
            var ingredient = ingredients[i];
            if (ingredient == null) continue;

            // Ingredients are laid out row by row in a Width x Height area; place it at the same spot in the 3x3 grid.
            int gridIndex = (i / recipe.Width) * 3 + (i % recipe.Width);
            if (gridIndex >= inputIndices.Length) continue;
            var gridSlot = grid[inputIndices[gridIndex]];
            if (gridSlot == null) continue;

            // Pick one concrete item for this ingredient (same choice for every slot using it).
            if (!chosenByIngredient.TryGetValue(ingredient, out var chosen))
            {
                chosen = networkTotals
                    .Where(s => ingredient.SatisfiesAsIngredient(s, checkStackSize: false))
                    .OrderByDescending(s => s.StackSize)
                    .FirstOrDefault();
                chosenByIngredient[ingredient] = chosen;
            }

            int need = Math.Max(1, ingredient.Quantity);
            if (chosen != null)
            {
                foreach (var networkSlot in scan.TakeSlots)
                {
                    if (need <= 0) break;
                    if (networkSlot.Empty || !networkSlot.Itemstack.Equals(world, chosen, GlobalConstants.IgnoredStackAttributes)) continue;

                    int moved = networkSlot.TryPutInto(world, gridSlot, need);
                    if (moved > 0)
                    {
                        networkSlot.MarkDirty();
                        need -= moved;
                    }
                }
                gridSlot.MarkDirty();
            }

            if (need > 0)
            {
                string name = ingredient.ResolvedItemStack?.GetName() ?? ingredient.Code?.ToShortString() ?? "?";
                missing.Add(L.GetL(session.Player.LanguageCode, "msg-missing-entry", need, name));
            }
        }

        sapi.World.PlaySoundFor(HeldSounds.InvPlaceDefault, session.Player);

        if (missing.Count > 0)
        {
            session.Player.SendMessage(GlobalConstants.GeneralChatGroup,
                L.GetL(session.Player.LanguageCode, "msg-missing-ingredients", output.GetName(), string.Join(", ", missing)),
                EnumChatType.Notification);
        }
    }

    /// <summary>When the terminal closes, ingredients left in the grid go back into the network.</summary>
    private void ReturnCraftingGridItems(Session session)
    {
        var grid = session.CraftingGrid;
        if (grid == null) return;

        var scan = Scan(session);
        foreach (int i in CraftingGridSlots.InputIndices(grid))
        {
            var slot = grid[i];
            if (slot == null || slot.Empty) continue;

            NetworkScanner.MoveIntoNetwork(sapi.World, slot, scan, slot.StackSize);
            GiveOrReturn(session.Player, scan, slot);
            slot.MarkDirty();
        }
    }
}
