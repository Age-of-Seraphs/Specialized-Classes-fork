using System.Collections.Generic;
using HarmonyLib;
using SpecializedClasses.Workstations;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "addIngredientForInfo")]
    public static class WorkstationHandbookIngredientForPatch
    {
        [HarmonyPostfix]
        public static void Postfix(
            ICoreClientAPI capi,
            ActionConsumable<string> openDetailPageFor,
            ItemStack stack,
            List<RichTextComponentBase> components,
            ref bool __result)
        {
            __result = WorkstationHandbookHelper.AppendIngredientFor(capi, openDetailPageFor, stack, components, __result);
        }
    }

    [HarmonyPatch(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "addCreatedByInfo")]
    public static class WorkstationHandbookCreatedByPatch
    {
        [HarmonyPrefix]
        public static void Prefix(List<RichTextComponentBase> components, out int __state)
        {
            __state = components.Count;
        }

        [HarmonyPostfix]
        public static void Postfix(
            ICoreClientAPI capi,
            ActionConsumable<string> openDetailPageFor,
            ItemStack stack,
            List<RichTextComponentBase> components,
            int __state,
            ref bool __result)
        {
            bool vanillaCreatedByRendered = components.Count > __state;
            __result = WorkstationHandbookHelper.AppendCreatedBy(capi, openDetailPageFor, stack, components, __result, vanillaCreatedByRendered);
            __result = WorkstationHandbookHelper.AppendWorkstationUsedFor(capi, openDetailPageFor, stack, components, __result);
        }
    }
}
