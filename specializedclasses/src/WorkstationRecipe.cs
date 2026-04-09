using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace SpecializedClasses.Workstations
{
    public class WorkstationRecipe : RecipeBase, IByteSerializable
    {
        public CraftingRecipeIngredient[]? Ingredients { get; set; }

        public CraftingRecipeIngredient? Ingredient { get; set; }

        public JsonItemStack? Output { get; set; }

        [JsonProperty("ingredients")]
        public CraftingRecipeIngredient[]? JsonIngredients
        {
            get => Ingredients;
            set => Ingredients = value;
        }

        [JsonProperty("ingredient")]
        public CraftingRecipeIngredient? JsonIngredient
        {
            get => Ingredient;
            set => Ingredient = value;
        }

        public string? Workstation { get; set; }

        public string MenuMode { get; set; } = "browser";

        public string? RecipeName { get; set; }

        public string? RecipeDesc { get; set; }

        public string? RecipeCode { get; set; }

        public string? RecipeGroup { get; set; }

        public string? RequiredTrait { get; set; }

        public string[]? RequiredTraits { get; set; }

        public int RequiredWorkstationTier { get; set; }

        public string? VariantCompatibility { get; set; }

        public string[] WorkableIngredientIds { get; set; } = Array.Empty<string>();

        public new Dictionary<string, string[]> AllowedVariants { get; set; } = new(StringComparer.Ordinal);

        public new Dictionary<string, string[]> SkipVariants { get; set; } = new(StringComparer.Ordinal);

        public override IEnumerable<IRecipeIngredient> RecipeIngredients => GetNormalizedIngredients();

        public override IRecipeOutput RecipeOutput => Output!;

        public override bool Resolve(IWorldAccessor world, string sourceForErrorLogging)
        {
            if (string.IsNullOrWhiteSpace(Workstation))
            {
                world.Logger.Error($"Workstation recipe '{Name}' has no workstation specified.");
                return false;
            }

            CraftingRecipeIngredient[] ingredients = GetNormalizedIngredients();

            if (ingredients.Length == 0)
            {
                world.Logger.Error($"Workstation recipe '{Name}' has no ingredients.");
                return false;
            }

            if (Output == null)
            {
                world.Logger.Error($"Workstation recipe '{Name}' has no output specified.");
                return false;
            }

            bool resolved = true;
            foreach (CraftingRecipeIngredient ingredient in ingredients)
            {
                resolved &= ingredient.Resolve(world, "Workstation recipe");
            }

            resolved &= Output.Resolve(world, "Workstation recipe");
            return resolved;
        }

        public override WorkstationRecipe Clone()
        {
            return new WorkstationRecipe
            {
                RecipeId = RecipeId,
                Name = Name == null ? null : new AssetLocation(Name.ToShortString()),
                Enabled = Enabled,
                Ingredients = CloneIngredients(GetNormalizedIngredients()),
                Output = Output?.Clone(),
                Workstation = Workstation,
                MenuMode = MenuMode,
                RecipeName = RecipeName,
                RecipeDesc = RecipeDesc,
                RecipeCode = RecipeCode,
                RecipeGroup = RecipeGroup,
                RequiredTrait = RequiredTrait,
                RequiredTraits = RequiredTraits == null ? null : (string[])RequiredTraits.Clone(),
                RequiredWorkstationTier = RequiredWorkstationTier,
                VariantCompatibility = VariantCompatibility,
                WorkableIngredientIds = (string[])WorkableIngredientIds.Clone(),
                AllowedVariants = CloneVariantMap(AllowedVariants),
                SkipVariants = CloneVariantMap(SkipVariants),
                Attributes = Attributes
            };
        }

        public IEnumerable<WorkstationRecipe> ExpandRecipesForAllIngredientCombinations(IWorldAccessor world)
        {
            Dictionary<string, HashSet<string>> mappings = GetNameToCodeMapping(world);
            if (mappings.Count == 0)
            {
                yield return Clone();
                yield break;
            }

            List<WorkstationRecipe> expanded = new() { Clone() };

            foreach ((string key, HashSet<string> values) in mappings)
            {
                if (values == null || values.Count == 0)
                {
                    continue;
                }

                List<WorkstationRecipe> next = new();
                foreach (WorkstationRecipe recipe in expanded)
                {
                    foreach (string value in values)
                    {
                        WorkstationRecipe cloned = recipe.Clone();
                        foreach (CraftingRecipeIngredient ingredient in cloned.EnumerateIngredients())
                        {
                            ingredient.FillPlaceHolder(key, value);
                        }

                        cloned.Output?.FillPlaceHolder(key, value);
                        ReplacePlaceholdersInOutputAttributes(cloned.Output, key, value);
                        next.Add(cloned);
                    }
                }

                expanded = next;
            }

            foreach (WorkstationRecipe recipe in expanded)
            {
                yield return recipe;
            }
        }

        public WorkstationMenuMode GetMenuMode()
        {
            return string.Equals(MenuMode, "helditem", StringComparison.OrdinalIgnoreCase)
                ? WorkstationMenuMode.HeldItem
                : WorkstationMenuMode.Browser;
        }

        protected override Dictionary<string, HashSet<string>> GetNameToCodeMapping(IWorldAccessor world)
        {
            Dictionary<string, HashSet<string>> collectedMappings = new(StringComparer.Ordinal);
            CraftingRecipeIngredient[] ingredients = GetNormalizedIngredients();

            foreach (CraftingRecipeIngredient ingredient in ingredients)
            {
                if (ingredient?.Code == null)
                {
                    continue;
                }

                foreach ((string mappingKey, string mappingValue) in EnumerateMappingsForIngredient(world, ingredient))
                {
                    if (!collectedMappings.TryGetValue(mappingKey, out HashSet<string>? values))
                    {
                        values = new HashSet<string>(StringComparer.Ordinal);
                        collectedMappings[mappingKey] = values;
                    }

                    values.Add(mappingValue);
                }
            }

            return collectedMappings;
        }

        public override void ToBytes(BinaryWriter writer)
        {
            writer.Write(RecipeId);
            writer.Write(Name?.ToShortString() ?? string.Empty);
            writer.Write(Enabled);

            CraftingRecipeIngredient[] ingredients = GetNormalizedIngredients();
            writer.Write(ingredients.Length);
            foreach (CraftingRecipeIngredient ingredient in ingredients)
            {
                ingredient.ToBytes(writer);
            }

            writer.Write(Output != null);
            Output?.ToBytes(writer);

            writer.Write(Workstation ?? string.Empty);
            writer.Write(MenuMode ?? string.Empty);
            writer.Write(RecipeName ?? string.Empty);
            writer.Write(RecipeDesc ?? string.Empty);
            writer.Write(RecipeCode ?? string.Empty);
            writer.Write(RecipeGroup ?? string.Empty);
            writer.Write(RequiredTrait ?? string.Empty);
            writer.Write(RequiredTraits?.Length ?? 0);
            if (RequiredTraits != null)
            {
                foreach (string trait in RequiredTraits)
                {
                    writer.Write(trait ?? string.Empty);
                }
            }
            writer.Write(RequiredWorkstationTier);
            writer.Write(VariantCompatibility ?? string.Empty);
            writer.Write(WorkableIngredientIds.Length);
            foreach (string id in WorkableIngredientIds)
            {
                writer.Write(id);
            }
            WriteVariantMap(writer, AllowedVariants);
            WriteVariantMap(writer, SkipVariants);
            writer.Write(Attributes?.Token?.ToString() ?? string.Empty);
        }

        public override void FromBytes(BinaryReader reader, IWorldAccessor resolver)
        {
            RecipeId = reader.ReadInt32();

            string name = reader.ReadString();
            Name = string.IsNullOrWhiteSpace(name) ? null : new AssetLocation(name);
            Enabled = reader.ReadBoolean();

            int ingredientCount = reader.ReadInt32();
            Ingredients = new CraftingRecipeIngredient[ingredientCount];
            for (int i = 0; i < ingredientCount; i++)
            {
                CraftingRecipeIngredient ingredient = new();
                ingredient.FromBytes(reader, resolver);
                Ingredients[i] = ingredient;
            }

            if (reader.ReadBoolean())
            {
                Output = new JsonItemStack();
                Output.FromBytes(reader, resolver.ClassRegistry);
            }

            Workstation = EmptyToNull(reader.ReadString());
            MenuMode = reader.ReadString();
            RecipeName = EmptyToNull(reader.ReadString());
            RecipeDesc = EmptyToNull(reader.ReadString());
            RecipeCode = EmptyToNull(reader.ReadString());
            RecipeGroup = EmptyToNull(reader.ReadString());
            RequiredTrait = EmptyToNull(reader.ReadString());
            int requiredTraitsCount = reader.ReadInt32();
            if (requiredTraitsCount > 0)
            {
                RequiredTraits = new string[requiredTraitsCount];
                for (int i = 0; i < requiredTraitsCount; i++)
                {
                    RequiredTraits[i] = reader.ReadString();
                }
            }
            RequiredWorkstationTier = reader.ReadInt32();
            VariantCompatibility = EmptyToNull(reader.ReadString());

            int workableCount = reader.ReadInt32();
            WorkableIngredientIds = new string[workableCount];
            for (int i = 0; i < workableCount; i++)
            {
                WorkableIngredientIds[i] = reader.ReadString();
            }

            AllowedVariants = ReadVariantMap(reader);
            SkipVariants = ReadVariantMap(reader);
            string attributesJson = reader.ReadString();
            if (!string.IsNullOrEmpty(attributesJson))
                Attributes = new JsonObject(Newtonsoft.Json.Linq.JToken.Parse(attributesJson));
        }

        private static CraftingRecipeIngredient[]? CloneIngredients(CraftingRecipeIngredient[]? ingredients)
        {
            if (ingredients == null)
            {
                return null;
            }

            CraftingRecipeIngredient[] cloned = new CraftingRecipeIngredient[ingredients.Length];
            for (int i = 0; i < ingredients.Length; i++)
            {
                cloned[i] = ingredients[i].Clone();
            }

            return cloned;
        }

        private IEnumerable<CraftingRecipeIngredient> EnumerateIngredients()
        {
            foreach (CraftingRecipeIngredient ingredient in GetNormalizedIngredients())
            {
                yield return ingredient;
            }
        }

        private CraftingRecipeIngredient[] GetNormalizedIngredients()
        {
            if (Ingredients != null && Ingredients.Length > 0)
            {
                return Ingredients.Where(ingredient => ingredient != null).ToArray()!;
            }

            if (Ingredient != null)
            {
                return new[] { Ingredient };
            }

            return Array.Empty<CraftingRecipeIngredient>();
        }

        private static string? EmptyToNull(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static void ReplacePlaceholdersInOutputAttributes(JsonItemStack? output, string key, string value)
        {
            if (output?.Attributes?.Token == null)
            {
                return;
            }

            ReplacePlaceholdersInToken(output.Attributes.Token, key, value);
        }

        private static void ReplacePlaceholdersInToken(JToken token, string key, string value)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    foreach (JProperty property in token.Children<JProperty>())
                    {
                        ReplacePlaceholdersInToken(property.Value, key, value);
                    }
                    break;

                case JTokenType.Array:
                    foreach (JToken child in token.Children())
                    {
                        ReplacePlaceholdersInToken(child, key, value);
                    }
                    break;

                case JTokenType.String:
                    string? raw = token.Value<string>();
                    if (raw != null)
                    {
                        ((JValue)token).Value = raw.Replace($"{{{key}}}", value, StringComparison.Ordinal);
                    }
                    break;
            }
        }

        private IEnumerable<KeyValuePair<string, string>> EnumerateMappingsForIngredient(IWorldAccessor world, CraftingRecipeIngredient ingredient)
        {
            if (ingredient.Code == null)
            {
                yield break;
            }

            string codeText = ingredient.Code.ToShortString();
            if (codeText.Contains('{') && codeText.Contains('}'))
            {
                foreach ((string key, string value) in EnumerateAdvancedWildcardMappings(world, ingredient))
                {
                    yield return new KeyValuePair<string, string>(key, value);
                }

                yield break;
            }

            if (string.IsNullOrWhiteSpace(ingredient.Name))
            {
                yield break;
            }

            List<CollectibleObject> matches = new();
            matches.AddRange(world.SearchBlocks(ingredient.Code));
            matches.AddRange(world.SearchItems(ingredient.Code));

            foreach (CollectibleObject match in matches)
            {
                string? wildcardValue = Vintagestory.API.Util.WildcardUtil.GetWildcardValue(ingredient.Code, match.Code);
                if (string.IsNullOrWhiteSpace(wildcardValue))
                {
                    continue;
                }

                if (!PassesVariantFilters(ingredient.Name, wildcardValue))
                {
                    continue;
                }

                yield return new KeyValuePair<string, string>(ingredient.Name, wildcardValue);
            }
        }

        private IEnumerable<KeyValuePair<string, string>> EnumerateAdvancedWildcardMappings(IWorldAccessor world, CraftingRecipeIngredient ingredient)
        {
            if (ingredient.Code == null)
            {
                yield break;
            }

            string pattern = ingredient.Code.Path;
            string regexPattern = BuildAdvancedWildcardRegex(pattern, out List<string> placeholders);
            Regex regex = new(regexPattern, RegexOptions.IgnoreCase);

            IEnumerable<CollectibleObject> collectibles = ingredient.Type == EnumItemClass.Block
                ? world.Blocks.Where(block => block != null).Cast<CollectibleObject>()
                : world.Items.Where(item => item != null).Cast<CollectibleObject>();

            foreach (CollectibleObject collectible in collectibles)
            {
                AssetLocation? code = collectible.Code;
                if (code == null || !string.Equals(code.Domain, ingredient.Code.Domain, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match match = regex.Match(code.Path);
                if (!match.Success)
                {
                    continue;
                }

                for (int i = 0; i < placeholders.Count; i++)
                {
                    string key = placeholders[i];
                    string value = match.Groups[i + 1].Value;
                    if (string.IsNullOrWhiteSpace(value) || !PassesVariantFilters(key, value))
                    {
                        continue;
                    }

                    yield return new KeyValuePair<string, string>(key, value);
                }
            }
        }

        private static string BuildAdvancedWildcardRegex(string value, out List<string> placeholders)
        {
            placeholders = new List<string>();
            StringBuilder regex = new("^");

            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];

                if (current == '{')
                {
                    int end = value.IndexOf('}', i + 1);
                    if (end > i + 1)
                    {
                        string placeholder = value.Substring(i + 1, end - i - 1);
                        placeholders.Add(placeholder);
                        regex.Append("([\\w-]+)");
                        i = end;
                        continue;
                    }
                }

                if (current == '*')
                {
                    regex.Append(".*");
                    continue;
                }

                regex.Append(Regex.Escape(current.ToString()));
            }

            regex.Append('$');
            return regex.ToString();
        }

        private bool PassesVariantFilters(string key, string value)
        {
            if (AllowedVariants.TryGetValue(key, out string[]? allowed) && allowed.Length > 0 && !allowed.Contains(value, StringComparer.Ordinal))
            {
                return false;
            }

            if (SkipVariants.TryGetValue(key, out string[]? skipped) && skipped.Contains(value, StringComparer.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static Dictionary<string, string[]> CloneVariantMap(Dictionary<string, string[]> source)
        {
            Dictionary<string, string[]> clone = new(StringComparer.Ordinal);
            foreach ((string key, string[] values) in source)
            {
                clone[key] = (string[])values.Clone();
            }

            return clone;
        }

        private static void WriteVariantMap(BinaryWriter writer, Dictionary<string, string[]> map)
        {
            writer.Write(map.Count);
            foreach ((string key, string[] values) in map)
            {
                writer.Write(key);
                writer.Write(values.Length);
                foreach (string value in values)
                {
                    writer.Write(value);
                }
            }
        }

        private static Dictionary<string, string[]> ReadVariantMap(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            Dictionary<string, string[]> map = new(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string key = reader.ReadString();
                int valueCount = reader.ReadInt32();
                string[] values = new string[valueCount];
                for (int j = 0; j < valueCount; j++)
                {
                    values[j] = reader.ReadString();
                }

                map[key] = values;
            }

            return map;
        }
    }
}
