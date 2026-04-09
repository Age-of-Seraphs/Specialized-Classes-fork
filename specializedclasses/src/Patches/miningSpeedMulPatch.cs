using HarmonyLib;
using Vintagestory.API.Common;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetMiningSpeed))]
    public static class CollectibleObject_GetMiningSpeed_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref float __result, Block block, BlockSelection blockSel, IPlayer forPlayer)
        {
            if (block == null || blockSel == null) return;

            EntityStats? stats = forPlayer?.Entity?.Stats;
            if (stats == null) return;

            // get the material from the block at the position being mined
            EnumBlockMaterial material = block.GetBlockMaterial(forPlayer!.Entity.World.BlockAccessor, blockSel.Position);

            float multiplier = 1f;

            // apply the appropriate multiplier for this material type
            switch (material)
            {
                case EnumBlockMaterial.Plant: multiplier = stats.GetBlended("plantMiningSpeedMul"); break;
                case EnumBlockMaterial.Leaves: multiplier = stats.GetBlended("leavesMiningSpeedMul"); break;
                case EnumBlockMaterial.Wood: multiplier = stats.GetBlended("woodMiningSpeedMul"); break;
                case EnumBlockMaterial.Soil: multiplier = stats.GetBlended("soilMiningSpeedMul"); break;
                case EnumBlockMaterial.Sand: multiplier = stats.GetBlended("sandMiningSpeedMul"); break;
                case EnumBlockMaterial.Gravel: multiplier = stats.GetBlended("gravelMiningSpeedMul"); break;
                case EnumBlockMaterial.Ceramic: multiplier = stats.GetBlended("ceramicMiningSpeedMul"); break;
                case EnumBlockMaterial.Glass: multiplier = stats.GetBlended("glassMiningSpeedMul"); break;
                case EnumBlockMaterial.Snow: multiplier = stats.GetBlended("snowMiningSpeedMul"); break;
                case EnumBlockMaterial.Ice: multiplier = stats.GetBlended("iceMiningSpeedMul"); break;
                case EnumBlockMaterial.Cloth: multiplier = stats.GetBlended("clothMiningSpeedMul"); break;
                case EnumBlockMaterial.Other: multiplier = stats.GetBlended("otherMiningSpeedMul"); break;
                // stone and ore: to avoid ridiculous multiplicative speeds with other mods that adds/modifies miningspeedMul even if we're not using it
                case EnumBlockMaterial.Stone:
                    {
                        float vanillaMul = stats.GetBlended("miningSpeedMul");
                        float customMul = stats.GetBlended("stoneMiningSpeedMul");
                        if (vanillaMul <= 0f) break;
                        float combinedMul = vanillaMul + customMul - 1f;
                        multiplier = combinedMul / vanillaMul;
                        break;
                    }
                case EnumBlockMaterial.Ore:
                    {
                        float vanillaMul = stats.GetBlended("miningSpeedMul");
                        float customMul = stats.GetBlended("oreMiningSpeedMul");
                        if (vanillaMul <= 0f) break;
                        float combinedMul = vanillaMul + customMul - 1f;
                        multiplier = combinedMul / vanillaMul;
                        break;
                    }
            }

            // multiply the final result by our material-specific bonus
            __result *= multiplier;
        }
    }
}
