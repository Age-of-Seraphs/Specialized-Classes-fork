using HarmonyLib;
using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.DamageItem))]
    public static class CollectibleObject_DurabilitySaveChance_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(CollectibleObject __instance, Entity byEntity, ItemSlot itemSlot, ref int amount)
        {
            if (amount <= 0 || byEntity is not EntityPlayer entityPlayer || itemSlot?.Itemstack == null)
            {
                return;
            }

            if (DurabilitySaveChanceLogic.ShouldSaveDurability(__instance, entityPlayer))
            {
                amount = 0;
            }
        }
    }

    internal static class DurabilitySaveChanceLogic
    {
        public static bool ShouldSaveDurability(CollectibleObject? collectible, EntityPlayer? entityPlayer)
        {
            if (collectible == null || entityPlayer?.Stats == null || entityPlayer.World?.Rand == null)
            {
                return false;
            }

            EnumTool? tool = collectible.Tool;
            if (tool == null)
            {
                return false;
            }

            string? specificStatCode = tool.Value switch
            {
                EnumTool.Axe => "durabilitySaveChanceAxe",
                EnumTool.Bow => "durabilitySaveChanceBow",
                EnumTool.Chisel => "durabilitySaveChanceChisel",
                EnumTool.Club => "durabilitySaveChanceClub",
                EnumTool.Hammer => "durabilitySaveChanceHammer",
                EnumTool.Hoe => "durabilitySaveChanceHoe",
                EnumTool.Knife => "durabilitySaveChanceKnife",
                EnumTool.Pickaxe => "durabilitySaveChancePickaxe",
                EnumTool.Saw => "durabilitySaveChanceSaw",
                EnumTool.Scythe => "durabilitySaveChanceScythe",
                EnumTool.Shears => "durabilitySaveChanceShears",
                EnumTool.Shovel => "durabilitySaveChanceShovel",
                EnumTool.Sling => "durabilitySaveChanceSling",
                EnumTool.Spear => "durabilitySaveChanceSpear",
                EnumTool.Sword => "durabilitySaveChanceSword",
                EnumTool.Wrench => "durabilitySaveChanceWrench",
                _ => null
            };

            string? aggregateStatCode = tool.Value switch
            {
                EnumTool.Axe => "durabilitySaveChanceAllTools",
                EnumTool.Chisel => "durabilitySaveChanceAllTools",
                EnumTool.Hammer => "durabilitySaveChanceAllTools",
                EnumTool.Hoe => "durabilitySaveChanceAllTools",
                EnumTool.Pickaxe => "durabilitySaveChanceAllTools",
                EnumTool.Saw => "durabilitySaveChanceAllTools",
                EnumTool.Scythe => "durabilitySaveChanceAllTools",
                EnumTool.Shears => "durabilitySaveChanceAllTools",
                EnumTool.Shovel => "durabilitySaveChanceAllTools",
                EnumTool.Sickle => "durabilitySaveChanceAllTools",
                EnumTool.Wrench => "durabilitySaveChanceAllTools",
                EnumTool.Club => "durabilitySaveChanceAllWeapons",
                EnumTool.Knife => "durabilitySaveChanceAllWeapons",
                EnumTool.Bow => "durabilitySaveChanceAllWeapons",
                EnumTool.Sling => "durabilitySaveChanceAllWeapons",
                EnumTool.Spear => "durabilitySaveChanceAllWeapons",
                EnumTool.Sword => "durabilitySaveChanceAllWeapons",
                _ => null
            };

            if (specificStatCode == null && aggregateStatCode == null)
            {
                return false;
            }

            float saveChance = 0f;

            if (specificStatCode != null)
            {
                saveChance += entityPlayer.Stats.GetBlended(specificStatCode) - 1f;
            }

            if (aggregateStatCode != null)
            {
                saveChance += entityPlayer.Stats.GetBlended(aggregateStatCode) - 1f;
            }

            saveChance = GameMath.Clamp(saveChance, 0f, 1f);
            if (saveChance <= 0f)
            {
                return false;
            }

            return entityPlayer.World.Rand.NextDouble() < saveChance;
        }
    }
}
