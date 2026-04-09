using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(BlockPan), nameof(BlockPan.OnHeldInteractStop))]
    public static class BlockPan_OnHeldInteractStop_Patch
    {
        private static readonly bool DebugLogging = false;
        private static MethodInfo? createDropMethod;
        private static int debugSequence;

        [HarmonyPrefix]
        public static void Prefix(float secondsUsed, ItemSlot slot, EntityAgent byEntity, out string? __state)
        {
            __state = null;

            if (byEntity?.Api == null)
            {
                return;
            }

            if (secondsUsed >= 3.4f && byEntity.Api.Side == EnumAppSide.Server)
            {
                __state = slot.Itemstack?.Attributes?.GetString("materialBlockCode");
            }
        }

        [HarmonyPostfix]
        public static void Postfix(BlockPan __instance, float secondsUsed, EntityAgent byEntity, string? __state)
        {
            int debugCallId = DebugLogging ? System.Threading.Interlocked.Increment(ref debugSequence) : 0;

            if (PanningDropRateCompatHelper.IsKnapsterEasyPanningLoaded())
            {
                return;
            }

            // only proceed if panning was successful
            if (secondsUsed < 3.4f) return;
            if (byEntity?.Api == null) return;
            if (byEntity.Api.Side != EnumAppSide.Server) return;

            EntityStats? stats = (byEntity as EntityPlayer)?.Player?.Entity?.Stats;
            if (stats == null) return;

            float multiplier = stats.GetBlended("panningDropRate");
            if (multiplier <= 1f) return;

            string? code = __state;
            if (code == null) return;

            int additionalDrops = (int)(multiplier - 1f);
            float fractional = (multiplier - 1f) - additionalDrops;

            if (fractional > 0 && byEntity.World.Rand.NextDouble() < fractional)
            {
                additionalDrops++;
            }

            if (additionalDrops <= 0) return;

            DebugLog(
                byEntity,
                $"call={debugCallId} vanilla path material={code} secondsUsed={secondsUsed:0.###} baseRolls=1 multiplier={multiplier:0.###} extraRolls={additionalDrops} totalRolls={1 + additionalDrops}");

            if (createDropMethod == null)
            {
                createDropMethod = typeof(BlockPan).GetMethod(
                    "CreateDrop",
                    BindingFlags.NonPublic | BindingFlags.Instance
                );
            }

            object[] args = new object[] { byEntity, code };
            for (int i = 0; i < additionalDrops; i++)
            {
                createDropMethod?.Invoke(__instance, args);
            }
        }

        private static void DebugLog(EntityAgent? byEntity, string message)
        {
            if (!DebugLogging || byEntity?.Api == null || byEntity.Api.Side != EnumAppSide.Server) return;

            string playerName = (byEntity as EntityPlayer)?.Player?.PlayerName ?? byEntity.GetType().Name;
            byEntity.Api.Logger.Notification($"SpecializedClasses panning debug: player={playerName} {message}");
        }
    }

    [HarmonyPatch]
    public static class Knapster_EasyPanning_OnHeldInteractStop_Patch
    {
        private static readonly bool DebugLogging = false;
        private const string KnapsterPanningPatchType = "Knapster.Features.EasyPanning.Patches.EasyPanningUniversalPatches";
        private static int debugSequence;

        [HarmonyPrepare]
        public static bool Prepare()
        {
            return AccessTools.TypeByName(KnapsterPanningPatchType) != null;
        }

        [HarmonyTargetMethod]
        public static MethodBase? TargetMethod()
        {
            Type? patchType = AccessTools.TypeByName(KnapsterPanningPatchType);
            return patchType?.GetMethod("Harmony_BlockPan_OnHeldInteractStop_Prefix", BindingFlags.Public | BindingFlags.Static);
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            Type? patchType = AccessTools.TypeByName(KnapsterPanningPatchType);
            if (patchType == null)
            {
                return instructions;
            }

            MethodInfo? originalDropsPerLayer = patchType.GetMethod("DropsPerLayer", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo? replacement = AccessTools.Method(typeof(Knapster_EasyPanning_OnHeldInteractStop_Patch), nameof(GetScaledDropsPerLayer));
            if (originalDropsPerLayer == null || replacement == null)
            {
                return instructions;
            }

            List<CodeInstruction> codes = instructions.ToList();

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].Calls(originalDropsPerLayer))
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, replacement);
                }
            }

            return codes;
        }

        public static int GetScaledDropsPerLayer(EntityAgent byEntity)
        {
            Type? patchType = AccessTools.TypeByName(KnapsterPanningPatchType);
            MethodInfo? originalDropsPerLayer = patchType?.GetMethod("DropsPerLayer", BindingFlags.NonPublic | BindingFlags.Static);
            if (originalDropsPerLayer == null)
            {
                return 1;
            }

            object? baseResult = originalDropsPerLayer.Invoke(null, new object[] { byEntity });
            int baseRolls = baseResult is int value ? value : 1;
            int finalRolls = baseRolls;
            float multiplier = 1f;

            if (byEntity?.Api != null && byEntity.Api.Side == EnumAppSide.Server)
            {
                EntityStats? stats = (byEntity as EntityPlayer)?.Player?.Entity?.Stats;
                if (stats != null)
                {
                    multiplier = stats.GetBlended("panningDropRate");
                    if (multiplier > 1f)
                    {
                        float scaledRolls = baseRolls * multiplier;
                        finalRolls = (int)Math.Floor(scaledRolls);

                        float fractionalRoll = scaledRolls - finalRolls;
                        if (fractionalRoll > 0f && byEntity.World.Rand.NextDouble() < fractionalRoll)
                        {
                            finalRolls++;
                        }
                    }
                }
            }

            int debugCallId = DebugLogging ? System.Threading.Interlocked.Increment(ref debugSequence) : 0;
            int extraRolls = finalRolls - baseRolls;

            DebugLog(
                byEntity,
                $"call={debugCallId} knapster path baseRolls={baseRolls} multiplier={multiplier:0.###} extraRolls={extraRolls} finalRolls={finalRolls}");

            return finalRolls;
        }

        private static void DebugLog(EntityAgent? byEntity, string message)
        {
            if (!DebugLogging || byEntity?.Api == null || byEntity.Api.Side != EnumAppSide.Server) return;

            string playerName = (byEntity as EntityPlayer)?.Player?.PlayerName ?? byEntity.GetType().Name;
            byEntity.Api.Logger.Notification($"SpecializedClasses panning debug: player={playerName} {message}");
        }
    }

    internal static class PanningDropRateCompatHelper
    {
        private const string KnapsterPanningPatchType = "Knapster.Features.EasyPanning.Patches.EasyPanningUniversalPatches";

        public static bool IsKnapsterEasyPanningLoaded()
        {
            return AccessTools.TypeByName(KnapsterPanningPatchType) != null;
        }
    }
}
