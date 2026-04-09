using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace SpecializedClasses.Patches
{
    [HarmonyPatch(typeof(CharacterSystem), "applyTraitAttributes")]
    public static class CharacterSystem_applyTraitAttributes_Patch
    {
        private const string CHARACTER_CLASS_KEY = "characterClass";
        private const string EXTRA_TRAITS_KEY = "extraTraits";
        private const string TRAIT_KEY = "trait";

        [HarmonyPrefix]
        public static bool Prefix(CharacterSystem __instance, EntityPlayer eplr)
        {
            try
            {
                if (__instance == null || eplr == null) return true;

                string? classCode = eplr.WatchedAttributes.GetString(CHARACTER_CLASS_KEY);
                CharacterClass? charClass = __instance.characterClasses?.FirstOrDefault(c => string.Equals(c.Code, classCode, StringComparison.Ordinal));
                if (charClass == null) return true;

                string[] extras = eplr.WatchedAttributes.GetStringArray(EXTRA_TRAITS_KEY) ?? Array.Empty<string>();
                HashSet<string> traitCodes = new HashSet<string>(StringComparer.Ordinal);
                if (charClass.Traits != null)
                {
                    foreach (string trait in charClass.Traits)
                    {
                        if (!string.IsNullOrWhiteSpace(trait))
                        {
                            traitCodes.Add(trait);
                        }
                    }
                }
                foreach (string extra in extras)
                {
                    if (!string.IsNullOrWhiteSpace(extra))
                    {
                        traitCodes.Add(extra);
                    }
                }

                Dictionary<string, float> totals = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (string code in traitCodes)
                {
                    Trait? trait = __instance.TraitsByCode.GetValueOrDefault(code);
                    if (trait != null)
                    {
                        foreach (KeyValuePair<string, double> kv in trait.Attributes)
                        {
                            string key = kv.Key;
                            totals[key] = (float)(totals.GetValueOrDefault(key) + kv.Value);
                        }
                    }
                }

                foreach (KeyValuePair<string, EntityFloatStats> cat in eplr.Stats)
                {
                    cat.Value.ValuesByKey.Remove(TRAIT_KEY);
                }

                foreach (KeyValuePair<string, float> kv in totals)
                {
                    string attr = kv.Key;
                    float val = (float)kv.Value;
                    eplr.Stats.Set(attr, TRAIT_KEY, val, true);
                }

                eplr.GetBehavior<EntityBehaviorHealth>()?.MarkDirty();

                return false;
            }
            catch (Exception ex)
            {
                eplr?.Api?.Logger.Error("[SpecializedClasses] applyTraitAttributes prefix error: " + ex);
                return true;
            }
        }
    }
}
