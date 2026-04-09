using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(EntityPlayer), nameof(EntityPlayer.GetWalkSpeedMultiplier))]
    public static class EntityPlayer_GetWalkSpeedMultiplier_Patch
    {
        private const string PATH_WALK_SPEED_MUL = "pathWalkSpeedMul";
        private const string SNEAK_SPEED_PENALTY_REDUCTION = "sneakSpeedPenaltyReduction";

        [HarmonyPostfix]
        public static void Postfix(EntityPlayer __instance, ref double __result)
        {
            EntityStats? stats = __instance.Stats;
            if (stats == null) return;

            ApplyPathWalkSpeedBonus(__instance, stats, ref __result);
            ApplySneakSpeedPenaltyReduction(__instance, stats, ref __result);
        }

        private static void ApplyPathWalkSpeedBonus(EntityPlayer instance, EntityStats stats, ref double result)
        {
            // Creative mode ignores block walk multipliers, so there is no path bonus to amplify.
            if (instance.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative) return;

            float pathStatVal = stats.GetBlended(PATH_WALK_SPEED_MUL);
            if (pathStatVal == 1f) return;

            int y1 = (int)(instance.Pos.InternalY - 0.05f);
            Block belowBlock = instance.World.BlockAccessor.GetBlockRaw((int)instance.Pos.X, y1, (int)instance.Pos.Z);
            float belowBlockMul = belowBlock.WalkSpeedMultiplier;

            if (belowBlockMul <= 1f) return;

            // Replace only the below-block contribution so other walk modifiers stay unchanged.
            double enhancedBelowMul = 1.0 + (belowBlockMul - 1.0) * pathStatVal;
            result *= enhancedBelowMul / belowBlockMul;
        }

        private static void ApplySneakSpeedPenaltyReduction(EntityPlayer instance, EntityStats stats, ref double result)
        {
            if (!instance.ServerControls.Sneak) return;

            float reductionFactor = GameMath.Clamp(stats.GetBlended(SNEAK_SPEED_PENALTY_REDUCTION) - 1f, 0f, 1f);
            if (reductionFactor == 0f) return;

            float effectiveSneakMul = GlobalConstants.SneakSpeedMultiplier + (1f - GlobalConstants.SneakSpeedMultiplier) * reductionFactor;
            result *= effectiveSneakMul / GlobalConstants.SneakSpeedMultiplier;
        }
    }

    [HarmonyPatch(typeof(PModulePlayerInLiquid), "HandleSwimming")]
    public static class PModulePlayerInLiquid_HandleSwimming_Patch
    {
        private const string SWIM_SPEED_MUL = "swimSpeedMul";

        private static readonly Type? PmlStatsPatchesType =
            AccessTools.TypeByName("PlayerModelLib.StatsPatches");

        private static readonly bool PmlPresent = PmlStatsPatchesType != null;

        // Use thread-local state so the prefix still records motion even if another mod skips the original.
        [ThreadStatic]
        private static Stack<Vec3d?>? _preSwimMotionStack;

        [HarmonyReversePatch(HarmonyReversePatchType.Original)]
        [HarmonyPatch(typeof(PModulePlayerInLiquid), "HandleSwimming")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CallVanillaHandleSwimming(PModulePlayerInLiquid instance, float dt, Entity entity, EntityPos pos, EntityControls controls)
        {
            throw new NotImplementedException("Harmony reverse patch stub");
        }

        [HarmonyPriority(Priority.First)]
        [HarmonyBefore("PlayerModelLibStats")]
        public static void Prefix(EntityPos pos)
        {
            _preSwimMotionStack ??= new Stack<Vec3d?>();

            if (pos?.Motion == null)
            {
                _preSwimMotionStack.Push(null);
                return;
            }

            _preSwimMotionStack.Push(new Vec3d(pos.Motion.X, pos.Motion.Y, pos.Motion.Z));
        }

        public static void Postfix(PModulePlayerInLiquid __instance, float dt, Entity entity, EntityPos pos, EntityControls controls)
        {
            Vec3d? preSwimMotion = null;
            if (_preSwimMotionStack != null && _preSwimMotionStack.Count > 0)
            {
                preSwimMotion = _preSwimMotionStack.Pop();
            }

            if (entity is not EntityPlayer player) return;
            if (pos?.Motion == null) return;
            if (preSwimMotion == null) return;

            EntityStats? stats = player.Stats;
            if (stats == null) return;

            float statVal = stats.GetBlended(SWIM_SPEED_MUL);
            if (statVal == 1f) return;

            if (PmlPresent)
            {
                // PML already scales the full swim delta, so we add only our extra bonus on top of raw vanilla motion.
                EntityPos scratchPos = pos.Copy();

                scratchPos.Motion.Set(preSwimMotion);
                CallVanillaHandleSwimming(__instance, dt, entity, scratchPos, controls);

                Vec3d vanillaDelta = scratchPos.Motion - preSwimMotion;

                float bonus = statVal - 1f;
                pos.Motion.X += vanillaDelta.X * bonus;
                pos.Motion.Y += vanillaDelta.Y * bonus;
                pos.Motion.Z += vanillaDelta.Z * bonus;
            }
            else
            {
                // Without PML, scale only the swim delta contributed here and leave later flow pushes alone.
                pos.Motion.X = preSwimMotion.X + (pos.Motion.X - preSwimMotion.X) * statVal;
                pos.Motion.Y = preSwimMotion.Y + (pos.Motion.Y - preSwimMotion.Y) * statVal;
                pos.Motion.Z = preSwimMotion.Z + (pos.Motion.Z - preSwimMotion.Z) * statVal;
            }
        }
    }
}
