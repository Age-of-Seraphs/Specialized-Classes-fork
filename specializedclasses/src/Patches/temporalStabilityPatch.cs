using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(EntityBehaviorTemporalStabilityAffected), nameof(EntityBehaviorTemporalStabilityAffected.OnGameTick))]
    public static class EntityBehaviorTemporalStability_OnGameTick_Patch
    {
        private const int INDOOR_LIGHT_THRESHOLD = 8;
        private const int OUTDOOR_LIGHT_THRESHOLD = 16;

        [HarmonyPrefix]
        public static void Prefix(EntityBehaviorTemporalStabilityAffected __instance, float deltaTime)
        {
            ApplyStabilityStats(__instance, deltaTime);
        }

        private static void ApplyStabilityStats(EntityBehaviorTemporalStabilityAffected __instance, float deltaTime)
        {
            EntityStats stats = __instance.entity.Stats;
            double stabilityChange = __instance.TempStabChangeVelocity;

            if (stabilityChange < 0)
            {
                stabilityChange *= Math.Max(stats.GetBlended("stabilityLossMul"), 0);
            }
            else
            {
                stabilityChange *= Math.Max(stats.GetBlended("stabilityGainMul"), 0);
            }

            float baseOffsetModifier = stats.GetBlended("stabilityOffset") - 1f;
            if (baseOffsetModifier != 0)
            {
                stabilityChange += baseOffsetModifier * deltaTime;
            }

            float outdoorMod = stats.GetBlended("stabilityOutdoorOffset") - 1f;
            float indoorMod = stats.GetBlended("stabilityIndoorOffset") - 1f;

            if (outdoorMod != 0 || indoorMod != 0)
            {
                int sunlightLevel = GetSunlightLevel(__instance.entity);
                float outdoorBlend = GetOutdoorBlend(sunlightLevel);

                float appliedMod = indoorMod + (outdoorMod - indoorMod) * outdoorBlend;

                if (appliedMod != 0)
                {
                    stabilityChange += appliedMod * deltaTime;
                }
            }

            __instance.TempStabChangeVelocity = stabilityChange;
        }

        private static int GetSunlightLevel(Entity entity)
        {
            return entity.World.BlockAccessor.GetLightLevel(entity.Pos.AsBlockPos, EnumLightLevelType.OnlySunLight);
        }

        private static float GetOutdoorBlend(int sunlightLevel)
        {
            if (sunlightLevel <= INDOOR_LIGHT_THRESHOLD) return 0f;
            if (sunlightLevel >= OUTDOOR_LIGHT_THRESHOLD) return 1f;

            return (sunlightLevel - INDOOR_LIGHT_THRESHOLD) / (float)(OUTDOOR_LIGHT_THRESHOLD - INDOOR_LIGHT_THRESHOLD);
        }
    }
}
