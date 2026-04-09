using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(DialogueController))]
    public static class DialogueController_ExtraTradeVariable_Patch
    {
        private const string EXTRA_TRADE_ACCESS_STAT = "canOpenExtraTradeWindow";
        private const string DIALOGUE_VARIABLE_KEY = "canopenextratrade";

        [HarmonyPatch(MethodType.Constructor)]
        [HarmonyPatch(new Type[] { typeof(ICoreAPI), typeof(EntityPlayer), typeof(EntityAgent), typeof(DialogueConfig) })]
        [HarmonyPostfix]
        public static void Postfix(DialogueController __instance)
        {
            EntityPlayer? player = __instance.PlayerEntity;
            if (player == null || __instance.VarSys == null)
            {
                return;
            }

            float statValue = player.Stats.GetBlended(EXTRA_TRADE_ACCESS_STAT);
            bool canOpen = statValue > 1f;
            ExtraTradeState.DebugLog(player.World, $"DialogueController: player={player.GetName()} stat={statValue:0.##} canOpen={canOpen} transpilerOk={ExtraTradeState.TranspilerInjectionSucceeded}");
            __instance.VarSys.SetVariable(
                player,
                EnumActivityVariableScope.Player,
                DIALOGUE_VARIABLE_KEY,
                canOpen ? "1" : "0"
            );
        }
    }

    [HarmonyPatch(typeof(EntityTradingHumanoid), "Dialog_DialogTriggers")]
    public static class EntityTradingHumanoid_Dialog_DialogTriggers_Patch
    {
        private const string TRIGGER_OPEN_EXTRA_TRADE = "openextratrade";
        private const string TRIGGER_OPEN_TRADE = "opentrade";
        private const string EXTRA_TRADE_ACCESS_STAT = "canOpenExtraTradeWindow";

        [HarmonyPrefix]
        public static bool Prefix(
            EntityTradingHumanoid __instance,
            EntityAgent triggeringEntity,
            ref string value,
            ref int __result)
        {
            if (string.Equals(value, TRIGGER_OPEN_TRADE, StringComparison.Ordinal)
                && __instance.World.Side == EnumAppSide.Server
                && ExtraTradeState.IsExtraTradeActive(__instance))
            {
                ExtraTradeState.DebugLog(__instance.World, "opentrade: extra trade active, force-restoring primary stock");
                ExtraTradeState.ForceRestorePrimaryStock(__instance);
            }

            if (!string.Equals(value, TRIGGER_OPEN_EXTRA_TRADE, StringComparison.Ordinal))
            {
                return true;
            }

            EntityPlayer? player = triggeringEntity as EntityPlayer;
            float accessStat = player?.Stats.GetBlended(EXTRA_TRADE_ACCESS_STAT) ?? 0f;
            ExtraTradeState.DebugLog(__instance.World, $"openextratrade: side={__instance.World.Side} player={player?.GetName() ?? "null"} accessStat={accessStat:0.##}");

            if (player == null || accessStat <= 1f)
            {
                ExtraTradeState.DebugLog(__instance.World, $"openextratrade: access denied (stat={accessStat:0.##})");
                __result = 0;
                return false;
            }

            if (__instance.World.Side == EnumAppSide.Server)
            {
                ExtraTradeState.ActivateExtraTrade(__instance);
                // push swapped inventory immediately so the client renders correct data when the window opens
                if (triggeringEntity is EntityPlayer playerEntity
                    && playerEntity.Player is IServerPlayer serverPlayer)
                {
                    ExtraTradeState.PushInventoryToPlayer(__instance, serverPlayer);
                }
            }

            value = TRIGGER_OPEN_TRADE;

            return true;
        }
    }

    [HarmonyPatch(typeof(EntityTradingHumanoid), nameof(EntityTradingHumanoid.OnGameTick))]
    public static class EntityTradingHumanoid_OnGameTick_ExtraTradeRestore_Patch
    {
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            bool foundFiveFloat = false;
            int injectIndex = -1;

            for (int i = 0; i < codes.Count - 8; i++)
            {
                if (!foundFiveFloat && codes[i].opcode == OpCodes.Ldc_R4 && codes[i].operand is float value && Math.Abs(value - 5f) < 0.0001f)
                {
                    foundFiveFloat = true;
                }

                if (foundFiveFloat && codes[i].opcode == OpCodes.Stloc_S)
                {
                    injectIndex = i + 8;
                    break;
                }
            }

            MethodInfo? restoreMethod = AccessTools.Method(
                typeof(ExtraTradeState),
                nameof(ExtraTradeState.TryRestorePrimaryStock),
                new[] { typeof(EntityTradingHumanoid) }
            );

            if (injectIndex <= -1 || restoreMethod == null)
            {
                ExtraTradeState.TranspilerInjectionSucceeded = false;
                return codes.AsEnumerable();
            }

            List<CodeInstruction> injected = new List<CodeInstruction>
            {
                CodeInstruction.LoadArgument(0),
                new CodeInstruction(OpCodes.Call, restoreMethod)
            };

            injected[0].MoveLabelsFrom(codes[injectIndex]);
            codes.InsertRange(injectIndex, injected);
            ExtraTradeState.TranspilerInjectionSucceeded = true;

            return codes.AsEnumerable();
        }
    }

    [HarmonyPatch(typeof(EntityTradingHumanoid), "RefreshBuyingSellingInventory")]
    public static class EntityTradingHumanoid_RefreshBuyingSellingInventory_ExtraTradeReset_Patch
    {
        private const float RESTOCK_REFRESH_CHANCE = 0.5f;

        [HarmonyPostfix]
        public static void Postfix(EntityTradingHumanoid __instance, float refreshChance)
        {
            if (__instance.World.Side != EnumAppSide.Server)
            {
                return;
            }

            if (ExtraTradeState.IsExtraTradeActive(__instance))
            {
                return;
            }

            if (Math.Abs(refreshChance - RESTOCK_REFRESH_CHANCE) > 0.001f)
            {
                return;
            }

            if (ExtraTradeState.ShouldSkipExtraClearForRecentRestore(__instance))
            {
                return;
            }

            ExtraTradeState.ClearExtraStock(__instance);
        }
    }

    public static class ExtraTradeState
    {
        private const string EXTRA_ACTIVE_KEY = "specializedclasses:extraTradeActive";
        private const string MAIN_TEMP_KEY = "specializedclasses:tempMainStock";
        private const string EXTRA_STOCK_KEY = "specializedclasses:extraSpecialStock";
        private const string LAST_RESTORE_DAYS_KEY = "specializedclasses:lastExtraRestoreDays";
        private const string TRADE_ITEMS_KEY = "tradeItems";
        private const string TRADER_INV_PREFIX = "traderInv-";
        private const float GENERATE_REFRESH_CHANCE = 1.1f;

        private static readonly bool DebugLogging = false;
        internal static bool TranspilerInjectionSucceeded = false;

        private static readonly Type[] LATE_INIT_SIGNATURE =
        {
            typeof(string),
            typeof(ICoreAPI),
            typeof(EntityTradingHumanoid)
        };

        private static readonly Type[] REFRESH_SIGNATURE =
        {
            typeof(float)
        };

        internal static readonly AccessTools.FieldRef<EntityTradingHumanoid, InventoryTrader> InventoryRef =
            AccessTools.FieldRefAccess<EntityTradingHumanoid, InventoryTrader>("Inventory");

        private static readonly MethodInfo? LateInitializeMethod =
            AccessTools.DeclaredMethod(typeof(InventoryTrader), "LateInitialize", LATE_INIT_SIGNATURE);

        private static readonly MethodInfo? RefreshBuyingSellingInventoryMethod =
            AccessTools.DeclaredMethod(typeof(EntityTradingHumanoid), "RefreshBuyingSellingInventory", REFRESH_SIGNATURE);

        internal static void DebugLog(IWorldAccessor? world, string msg)
        {
            if (!DebugLogging) return;
            world?.Logger.Notification($"[SC ExtraTrade] {msg}");
        }

        private static void DebugLog(ICoreAPI? api, string msg)
        {
            if (!DebugLogging) return;
            api?.Logger.Notification($"[SC ExtraTrade] {msg}");
        }

        public static bool IsExtraTradeActive(EntityTradingHumanoid trader)
        {
            return trader.WatchedAttributes.GetBool(EXTRA_ACTIVE_KEY, false);
        }

        public static void ActivateExtraTrade(EntityTradingHumanoid trader)
        {
            DebugLog(trader.Api, $"ActivateExtraTrade: traderId={trader.EntityId}");
            if (IsExtraTradeActive(trader))
            {
                // stale active states can happen when close timing is missed
                // force a restore first so this request can perform a fresh swap
                DebugLog(trader.Api, "ActivateExtraTrade: stale active state, forcing restore first");
                ForceRestorePrimaryStock(trader);

                if (IsExtraTradeActive(trader))
                {
                    DebugLog(trader.Api, "ActivateExtraTrade: still active after force-restore, aborting");
                    return;
                }
            }

            InventoryTrader inventory = EnsureInventory(trader);
            TreeAttribute watched = trader.WatchedAttributes;
            ClearTradeCarts(inventory);

            watched[MAIN_TEMP_KEY] = SnapshotInventory(inventory);

            ITreeAttribute? savedExtraStock = watched.GetTreeAttribute(EXTRA_STOCK_KEY);
            if (savedExtraStock != null)
            {
                DebugLog(trader.Api, "ActivateExtraTrade: restoring cached extra stock");
                RestoreInventoryFromSnapshot(inventory, savedExtraStock);
                MarkAllSlotsDirty(inventory);
            }
            else
            {
                DebugLog(trader.Api, "ActivateExtraTrade: no cached stock, generating new");
                InvokeRefreshBuyingSellingInventory(trader, GENERATE_REFRESH_CHANCE);

                // persist generated extra stock immediately so reopen does not reroll
                // if close or restore timing is missed for any reason
                watched[EXTRA_STOCK_KEY] = SnapshotInventory(inventory);
                MarkDirty(watched, EXTRA_STOCK_KEY);
            }

            ClearTradeCarts(inventory);
            watched.SetBool(EXTRA_ACTIVE_KEY, true);
            MarkDirty(watched, MAIN_TEMP_KEY);
            MarkDirty(watched, EXTRA_STOCK_KEY);
            MarkDirty(watched, EXTRA_ACTIVE_KEY);
            DebugLog(trader.Api, "ActivateExtraTrade: complete");
        }

        public static void TryRestorePrimaryStock(EntityTradingHumanoid trader)
        {
            RestorePrimaryStock(trader, requireNoInteraction: true);
        }

        public static void ForceRestorePrimaryStock(EntityTradingHumanoid trader)
        {
            DebugLog(trader.Api, $"ForceRestorePrimaryStock: traderId={trader.EntityId}");
            RestorePrimaryStock(trader, requireNoInteraction: false);
        }

        public static void ClearExtraStock(EntityTradingHumanoid trader)
        {
            TreeAttribute watched = trader.WatchedAttributes;
            if (!watched.HasAttribute(EXTRA_STOCK_KEY))
            {
                return;
            }

            DebugLog(trader.Api, $"ClearExtraStock: clearing cached extra stock for traderId={trader.EntityId}");
            watched.RemoveAttribute(EXTRA_STOCK_KEY);
            MarkDirty(watched, EXTRA_STOCK_KEY);
        }

        private static TreeAttribute SnapshotInventory(InventoryTrader inventory)
        {
            TreeAttribute snapshot = new TreeAttribute();
            inventory.ToTreeAttributes(snapshot);

            // inventorytrader only persists tradeitems for non-empty slots
            // include every trade slot so sold-out offers keep their stock state
            TreeAttribute tradeItemsTree = snapshot.GetTreeAttribute(TRADE_ITEMS_KEY) as TreeAttribute ?? new TreeAttribute();
            for (int i = 0; i < inventory.Count; i++)
            {
                if (inventory[i] is not ItemSlotTrade tradeSlot)
                {
                    continue;
                }

                string slotKey = i.ToString();
                if (tradeSlot.TradeItem == null)
                {
                    tradeItemsTree.RemoveAttribute(slotKey);
                    continue;
                }

                TreeAttribute tradeItemTree = new TreeAttribute();
                tradeSlot.TradeItem.ToTreeAttributes(tradeItemTree);
                tradeItemsTree[slotKey] = tradeItemTree;
            }

            snapshot[TRADE_ITEMS_KEY] = tradeItemsTree;
            return snapshot;
        }

        private static void RestoreInventoryFromSnapshot(InventoryTrader inventory, ITreeAttribute snapshot)
        {
            inventory.FromTreeAttributes(snapshot);

            ITreeAttribute? tradeItemsTree = snapshot.GetTreeAttribute(TRADE_ITEMS_KEY);
            for (int i = 0; i < inventory.Count; i++)
            {
                if (inventory[i] is not ItemSlotTrade tradeSlot)
                {
                    continue;
                }

                ITreeAttribute? tradeItemTree = tradeItemsTree?.GetTreeAttribute(i.ToString());
                tradeSlot.TradeItem = tradeItemTree != null ? new ResolvedTradeItem(tradeItemTree) : null;
            }
        }

        private static void ClearTradeCarts(InventoryTrader inventory)
        {
            for (int i = 0; i < 4; i++)
            {
                ItemSlotTrade? buyingCart = inventory.GetBuyingCartSlot(i);
                if (buyingCart != null && buyingCart.Itemstack != null)
                {
                    buyingCart.Itemstack = null;
                    buyingCart.TradeItem = null;
                    buyingCart.MarkDirty();
                }

                ItemSlot? sellingCart = inventory.GetSellingCartSlot(i);
                if (sellingCart != null && sellingCart.Itemstack != null)
                {
                    sellingCart.Itemstack = null;
                    sellingCart.MarkDirty();
                }
            }
        }

        private static InventoryTrader EnsureInventory(EntityTradingHumanoid trader)
        {
            InventoryTrader? inventory = InventoryRef(trader);
            if (inventory != null)
            {
                return inventory;
            }

            inventory = new InventoryTrader("traderInv", trader.EntityId.ToString(), trader.Api);
            InvokeLateInitialize(inventory, TRADER_INV_PREFIX + trader.EntityId, trader.Api, trader);
            InventoryRef(trader) = inventory;

            return inventory;
        }

        public static bool ShouldSkipExtraClearForRecentRestore(EntityTradingHumanoid trader)
        {
            double lastRestoreDays = trader.WatchedAttributes.GetDouble(LAST_RESTORE_DAYS_KEY, -1d);
            if (lastRestoreDays < 0)
            {
                return false;
            }

            double elapsedDays = trader.World.Calendar.TotalDays - lastRestoreDays;
            // 7.0 matches the vanilla doubleRefreshIntervalDays â€” protects through one full restock cycle
            // regardless of server day-speed, avoiding stale-clear races in fast test worlds
            return elapsedDays >= 0 && elapsedDays < 7.0d;
        }

        internal static void PushInventoryToPlayer(EntityTradingHumanoid trader, IServerPlayer player)
        {
            InventoryTrader inventory = InventoryRef(trader);
            if (inventory == null) return;
            TreeAttribute tree = new TreeAttribute();
            inventory.ToTreeAttributes(tree);
            (trader.Api as ICoreServerAPI)?.Network.SendEntityPacket(player, trader.EntityId, 1234, tree.ToBytes());
        }

        private static void InvokeLateInitialize(
            InventoryTrader inventory,
            string id,
            ICoreAPI api,
            EntityTradingHumanoid trader)
        {
            LateInitializeMethod?.Invoke(inventory, new object[] { id, api, trader });
        }

        private static void InvokeRefreshBuyingSellingInventory(EntityTradingHumanoid trader, float refreshChance)
        {
            RefreshBuyingSellingInventoryMethod?.Invoke(trader, new object[] { refreshChance });
        }

        private static void MarkDirty(TreeAttribute watched, string path)
        {
            if (watched is SyncedTreeAttribute synced)
            {
                synced.MarkPathDirty(path);
            }
        }

        private static void MarkAllSlotsDirty(InventoryTrader inventory)
        {
            for (int i = 0; i < inventory.Count; i++)
            {
                inventory.MarkSlotDirty(i);
            }
        }

        private static void RestorePrimaryStock(EntityTradingHumanoid trader, bool requireNoInteraction)
        {
            if (trader.World.Side != EnumAppSide.Server)
            {
                return;
            }

            if (requireNoInteraction && trader.interactingWithPlayer.Count > 0)
            {
                return;
            }

            if (!IsExtraTradeActive(trader))
            {
                return;
            }

            InventoryTrader inventory = EnsureInventory(trader);
            TreeAttribute watched = trader.WatchedAttributes;

            bool hadMainTemp = watched.GetTreeAttribute(MAIN_TEMP_KEY) != null;

            if (!hadMainTemp)
            {
                DebugLog(trader.Api, "RestorePrimaryStock: no main stock snapshot found, clearing flags only");
                watched.RemoveAttribute(EXTRA_ACTIVE_KEY);
                MarkDirty(watched, EXTRA_ACTIVE_KEY);

                return;
            }

            ClearTradeCarts(inventory);
            watched[EXTRA_STOCK_KEY] = SnapshotInventory(inventory);
            ITreeAttribute? mainStock = watched.GetTreeAttribute(MAIN_TEMP_KEY);
            if (mainStock != null)
            {
                RestoreInventoryFromSnapshot(inventory, mainStock);
                MarkAllSlotsDirty(inventory);
            }

            ClearTradeCarts(inventory);
            watched.RemoveAttribute(MAIN_TEMP_KEY);
            watched.RemoveAttribute(EXTRA_ACTIVE_KEY);
            watched.SetDouble(LAST_RESTORE_DAYS_KEY, trader.World.Calendar.TotalDays);

            MarkDirty(watched, EXTRA_STOCK_KEY);
            MarkDirty(watched, MAIN_TEMP_KEY);
            MarkDirty(watched, EXTRA_ACTIVE_KEY);
            MarkDirty(watched, LAST_RESTORE_DAYS_KEY);
            DebugLog(trader.Api, $"RestorePrimaryStock: complete, traderId={trader.EntityId}");
        }

    }

    [HarmonyPatch(typeof(EntityTradingHumanoid), nameof(EntityTradingHumanoid.OnReceivedServerPacket))]
    public static class EntityTradingHumanoid_OnReceivedServerPacket_ExtraTradeRefresh_Patch
    {
        private const int INVENTORY_PUSH_PACKET = 1234;

        [HarmonyPostfix]
        public static void Postfix(EntityTradingHumanoid __instance, int packetid)
        {
            if (packetid != INVENTORY_PUSH_PACKET) return;
            if (__instance.World?.Side != EnumAppSide.Client) return;
            InventoryTrader? inventory = ExtraTradeState.InventoryRef(__instance);
            if (inventory == null) return;
            for (int i = 0; i < inventory.Count; i++)
            {
                inventory.MarkSlotDirty(i);
            }
        }
    }
}
