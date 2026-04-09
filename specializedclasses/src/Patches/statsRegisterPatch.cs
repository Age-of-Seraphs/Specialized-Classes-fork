using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(EntityPlayer))]
    public static class EntityPlayer_StatsRegister_Patch
    {
        private static readonly string[] REGISTERED_STATS =
        {
            "plantMiningSpeedMul",
            "leavesMiningSpeedMul",
            "woodMiningSpeedMul",
            "soilMiningSpeedMul",
            "sandMiningSpeedMul",
            "gravelMiningSpeedMul",
            "stoneMiningSpeedMul",
            "oreMiningSpeedMul",
            "ceramicMiningSpeedMul",
            "glassMiningSpeedMul",
            "snowMiningSpeedMul",
            "iceMiningSpeedMul",
            "clothMiningSpeedMul",
            "otherMiningSpeedMul",
            "durabilitySaveChanceAllTools",
            "durabilitySaveChanceAllWeapons",
            "durabilitySaveChanceAxe",
            "durabilitySaveChanceBow",
            "durabilitySaveChanceChisel",
            "durabilitySaveChanceClub",
            "durabilitySaveChanceHammer",
            "durabilitySaveChanceHoe",
            "durabilitySaveChanceKnife",
            "durabilitySaveChancePickaxe",
            "durabilitySaveChanceSaw",
            "durabilitySaveChanceScythe",
            "durabilitySaveChanceShears",
            "durabilitySaveChanceShovel",
            "durabilitySaveChanceSling",
            "durabilitySaveChanceSpear",
            "durabilitySaveChanceSword",
            "durabilitySaveChanceWrench",
            "flaxFiberRareDropChance",
            "cattailRareDropChance",
            "stabilityGainMul",
            "stabilityLossMul",
            "stabilityOffset",
            "stabilityOutdoorOffset",
            "stabilityIndoorOffset",
            "canHandleHotItems",
            "canOpenExtraTradeWindow",
            "fertilizerPermanencePercentage",
            "pathWalkSpeedMul",
            "sneakSpeedPenaltyReduction",
            "swimSpeedMul",
            "canHammerAllPlugsAtOnce"
        };

        [HarmonyPatch(MethodType.Constructor)]
        [HarmonyPostfix]
        public static void Postfix(EntityPlayer __instance)
        {
            EntityStats? stats = __instance?.Stats;
            if (stats == null) return;

            foreach (string stat in REGISTERED_STATS)
            {
                stats.Register(stat);
            }
        }
    }
}
