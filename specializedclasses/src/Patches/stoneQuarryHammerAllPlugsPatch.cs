using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch]
    public static class BlockPlugAndFeather_OnBlockInteractStart_AllPlugs_Patch
    {
        private const string BLOCK_PLUG_TYPE = "StoneQuarry.BlockPlugAndFeather";
        private const string BE_PLUG_TYPE = "StoneQuarry.BEPlugAndFeather";
        private const string STAT_NAME = "canHammerAllPlugsAtOnce";

        [HarmonyPrepare]
        public static bool Prepare()
        {
            return AccessTools.TypeByName(BLOCK_PLUG_TYPE) != null;
        }

        [HarmonyTargetMethod]
        public static MethodBase? TargetMethod()
        {
            Type? blockType = AccessTools.TypeByName(BLOCK_PLUG_TYPE);
            return blockType?.GetMethod("OnBlockInteractStart", BindingFlags.Public | BindingFlags.Instance);
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world.Side != EnumAppSide.Server) return;

            float stat = byPlayer?.Entity?.Stats?.GetBlended(STAT_NAME) ?? 0f;
            if (stat <= 1f) return;

            // resolve the stage property on the hammered block
            Type? blockType = __instance.GetType();
            int? hammeredStage = blockType?.GetProperty("Stage")?.GetValue(__instance) as int?;
            if (hammeredStage == null || hammeredStage == 0) return;

            // get the block entity and its Points list
            object? be = world.BlockAccessor.GetBlockEntity(blockSel.Position);
            if (be == null) return;

            Type? beType = be.GetType();
            System.Collections.Generic.List<BlockPos>? points =
                beType?.GetProperty("Points")?.GetValue(be) as System.Collections.Generic.List<BlockPos>;
            if (points == null || points.Count == 0) return;

            // advance every other plug in the network that is behind the current stage
            MethodInfo? switchStage = blockType?.GetMethod("SwitchStage", BindingFlags.Public | BindingFlags.Instance);
            if (switchStage == null) return;

            foreach (BlockPos point in points)
            {
                if (point.Equals(blockSel.Position)) continue;

                object? otherBlock = world.BlockAccessor.GetBlock(point);
                if (otherBlock == null || otherBlock.GetType() != blockType) continue;

                int? otherStage = blockType.GetProperty("Stage")?.GetValue(otherBlock) as int?;
                if (otherStage == null || otherStage >= hammeredStage) continue;

                switchStage.Invoke(otherBlock, new object[] { (int)hammeredStage, world, point });
            }
        }
    }
}
