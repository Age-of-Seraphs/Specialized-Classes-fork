using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch]
    public static class Toolsmith_TinkeredToolDamageItem_DurabilitySaveChance_Compat_Patch
    {
        private const string ToolsmithDamagePatchType = "Toolsmith.ToolTinkering.ToolTinkeringPatches";

        [HarmonyPrepare]
        public static bool Prepare()
        {
            return AccessTools.TypeByName(ToolsmithDamagePatchType) != null;
        }

        [HarmonyTargetMethod]
        public static MethodBase? TargetMethod()
        {
            Type? patchType = AccessTools.TypeByName(ToolsmithDamagePatchType);
            return patchType?.GetMethod("TinkeredToolDamageItemPrefix", BindingFlags.NonPublic | BindingFlags.Static);
        }

        [HarmonyPrefix]
        public static bool Prefix(Entity byEntity, ItemSlot itemslot, CollectibleObject __instance, ref bool __result)
        {
            if (byEntity is not EntityPlayer entityPlayer || itemslot?.Itemstack == null)
            {
                return true;
            }

            CollectibleObject? collectible = __instance ?? itemslot.Itemstack.Collectible;
            if (!DurabilitySaveChanceLogic.ShouldSaveDurability(collectible, entityPlayer))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }
}
