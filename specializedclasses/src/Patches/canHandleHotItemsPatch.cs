using HarmonyLib;
using Vintagestory.API.Common;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(InventoryBase), "hasHeatResistantHandGear")]
    public static class InventoryBase_HeatResistantHandGear_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(IPlayer player, ref bool __result)
        {
            if (__result) return;
            if (player?.Entity?.Stats == null) return;

            float canHandleHot = player.Entity.Stats.GetBlended("canHandleHotItems");
            if (canHandleHot <= 1f) return;

            __result = true;
        }
    }
}
