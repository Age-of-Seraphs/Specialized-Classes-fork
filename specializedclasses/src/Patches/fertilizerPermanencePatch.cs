using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(BlockEntitySoilNutrition), nameof(BlockEntitySoilNutrition.OnBlockInteract))]
    public static class BlockEntityFarmland_OnBlockInteract_FertilizerPermanence_Patch
    {
        private const string FERTILIZER_PERMANENCE_STAT = "fertilizerPermanencePercentage";
        private const string FERTILIZER_PROPS_KEY = "fertilizerProps";
        private const long DUPLICATE_GUARD_MS = 50;
        private const long DUPLICATE_GUARD_CLEANUP_MS = 2000;
        private const int DUPLICATE_GUARD_CLEANUP_INTERVAL = 64;
        private static readonly Dictionary<string, long> LastApplyMsByKey = new();
        private static readonly object LastApplyLock = new object();
        private static int _duplicateCacheTicks;
        private static readonly AccessTools.FieldRef<BlockEntitySoilNutrition, int[]> OriginalFertilityRef =
            AccessTools.FieldRefAccess<BlockEntitySoilNutrition, int[]>("originalFertility");

        private sealed class PermanenceState
        {
            public int DeltaN;
            public int DeltaP;
            public int DeltaK;

            public bool HasAnyDelta => DeltaN != 0 || DeltaP != 0 || DeltaK != 0;
        }

        [HarmonyPrefix]
        private static void Prefix(BlockEntitySoilNutrition __instance, IPlayer byPlayer, ref PermanenceState? __state)
        {
            __state = null;

            if (byPlayer?.Entity?.Stats == null)
            {
                return;
            }

            float blended = byPlayer.Entity.Stats.GetBlended(FERTILIZER_PERMANENCE_STAT);
            if (blended <= 1f)
            {
                return;
            }

            float permanencePercent = blended - 1f;
            if (permanencePercent <= 0f)
            {
                return;
            }

            ItemSlot? activeSlot = byPlayer.InventoryManager?.ActiveHotbarSlot;
            ItemStack? heldStack = activeSlot?.Itemstack;
            if (heldStack?.Collectible?.Attributes == null)
            {
                return;
            }

            FertilizerProps? fertilizerProps = heldStack
                .Collectible
                .Attributes[FERTILIZER_PROPS_KEY]
                .AsObject<FertilizerProps>(null!);
            if (fertilizerProps == null)
            {
                return;
            }

            int deltaN = (int)Math.Round(fertilizerProps.N * permanencePercent, MidpointRounding.AwayFromZero);
            int deltaP = (int)Math.Round(fertilizerProps.P * permanencePercent, MidpointRounding.AwayFromZero);
            int deltaK = (int)Math.Round(fertilizerProps.K * permanencePercent, MidpointRounding.AwayFromZero);

            if (deltaN == 0 && deltaP == 0 && deltaK == 0)
            {
                return;
            }

            __state = new PermanenceState
            {
                DeltaN = deltaN,
                DeltaP = deltaP,
                DeltaK = deltaK
            };
        }

        [HarmonyPostfix]
        private static void Postfix(BlockEntitySoilNutrition __instance, IPlayer byPlayer, bool __result, PermanenceState? __state)
        {
            if (!__result || __state == null || !__state.HasAnyDelta)
            {
                return;
            }

            if (__instance.Api?.Side != EnumAppSide.Server)
            {
                return;
            }

            int[]? originalFertility = OriginalFertilityRef(__instance);
            if (originalFertility == null || originalFertility.Length < 3)
            {
                return;
            }

            string duplicateKey = $"{byPlayer.PlayerUID}:{__instance.Pos.X}:{__instance.Pos.Y}:{__instance.Pos.Z}";
            if (IsDuplicateApplication(duplicateKey))
            {
                return;
            }

            originalFertility[0] = Math.Clamp(originalFertility[0] + __state.DeltaN, 0, 100);
            originalFertility[1] = Math.Clamp(originalFertility[1] + __state.DeltaP, 0, 100);
            originalFertility[2] = Math.Clamp(originalFertility[2] + __state.DeltaK, 0, 100);

            __instance.MarkDirty(false, byPlayer);
        }

        private static bool IsDuplicateApplication(string key)
        {
            long now = Environment.TickCount64;
            lock (LastApplyLock)
            {
                if (LastApplyMsByKey.TryGetValue(key, out long lastMs))
                {
                    long elapsedMs = now - lastMs;
                    if (elapsedMs >= 0 && elapsedMs < DUPLICATE_GUARD_MS)
                    {
                        return true;
                    }
                }

                LastApplyMsByKey[key] = now;

                if ((++_duplicateCacheTicks & (DUPLICATE_GUARD_CLEANUP_INTERVAL - 1)) != 0)
                {
                    return false;
                }

                long staleBeforeMs = now - DUPLICATE_GUARD_CLEANUP_MS;
                if (LastApplyMsByKey.Count <= 8)
                {
                    return false;
                }

                List<string> staleKeys = new();
                foreach (KeyValuePair<string, long> entry in LastApplyMsByKey)
                {
                    if (entry.Value < staleBeforeMs)
                    {
                        staleKeys.Add(entry.Key);
                    }
                }

                foreach (string staleKey in staleKeys)
                {
                    LastApplyMsByKey.Remove(staleKey);
                }

                return false;
            }
        }
    }
}
