using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace SpecializedClasses.Workstations
{
    public enum WorkstationMenuMode
    {
        Browser,
        HeldItem
    }

    public sealed class WorkstationInputRequirementDefinition
    {
        public WorkstationInputRequirementDefinition(string type, IReadOnlyList<string> codeTemplates, int quantity, string? label = null)
        {
            Type = type;
            CodeTemplates = codeTemplates;
            Quantity = quantity;
            Label = label;
        }

        public string Type { get; }
        public IReadOnlyList<string> CodeTemplates { get; }
        public int Quantity { get; }
        public string? Label { get; }
    }

    public sealed class WorkstationOutputDefinition
    {
        public WorkstationOutputDefinition(
            int outputId,
            string recipeCode,
            string? recipeGroup,
            string outputType,
            string codeTemplate,
            IReadOnlyDictionary<string, string> attributeTemplates,
            int quantity,
            IReadOnlyList<WorkstationInputRequirementDefinition> inputs,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? allowedVariants = null,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? skipVariants = null,
            IReadOnlyCollection<string>? placeholderKeys = null,
            IReadOnlyList<string>? requiredTraits = null,
            string? description = null,
            string? recipeName = null,
            string? handbookOverviewGroup = null)
        {
            OutputId = outputId;
            RecipeCode = recipeCode;
            RecipeGroup = string.IsNullOrWhiteSpace(recipeGroup) ? null : recipeGroup.Trim();
            OutputType = outputType;
            CodeTemplate = codeTemplate;
            AttributeTemplates = attributeTemplates;
            Quantity = quantity;
            Inputs = inputs;
            AllowedVariants = allowedVariants ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            SkipVariants = skipVariants ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            PlaceholderKeys = placeholderKeys ?? Array.Empty<string>();
            RequiredTraits = requiredTraits ?? Array.Empty<string>();
            Description = description;
            RecipeName = recipeName;
            HandbookOverviewGroup = string.IsNullOrWhiteSpace(handbookOverviewGroup) ? null : handbookOverviewGroup.Trim();
        }

        public int OutputId { get; }
        public string RecipeCode { get; }
        public string? RecipeGroup { get; }
        public string OutputType { get; }
        public string CodeTemplate { get; }
        public IReadOnlyDictionary<string, string> AttributeTemplates { get; }
        public int Quantity { get; }
        public IReadOnlyList<WorkstationInputRequirementDefinition> Inputs { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedVariants { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> SkipVariants { get; }
        public IReadOnlyCollection<string> PlaceholderKeys { get; }
        public IReadOnlyList<string> RequiredTraits { get; }
        public string? Description { get; }
        public string? RecipeName { get; }
        public string? HandbookOverviewGroup { get; }
    }

    public sealed class WorkstationProfileDefinition
    {
        public required string ProfileCode { get; init; }
        public required string OutputErrorCode { get; init; }
        public required string OutputErrorMessage { get; init; }
        public required string IngotErrorCode { get; init; }
        public required WorkstationMenuMode MenuMode { get; init; }
        public required IReadOnlyList<WorkstationOutputDefinition> Outputs { get; init; }
    }

    public static class WorkstationProfiles
    {
        private static readonly bool DebugLogging = false;
        private static readonly Dictionary<string, WorkstationProfileDefinition> ByCode = new(StringComparer.Ordinal);
        private static readonly HashSet<string> LoggedSchemaWarnings = new(StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<string, string> EmptyAttributeTemplates = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, IReadOnlyList<AssetLocation>> ExpandedWildcardInputCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<int, WorkstationOutputDefinition>> OutputById = new(StringComparer.Ordinal);

        public static void Initialize(ICoreAPI api)
        {
            DebugLog(api, $"initialize start side={api.Side}");
            Dictionary<string, WorkstationProfileDefinition> loaded = LoadProfiles(api);
            DebugLog(api, $"profiles loaded={loaded.Count}");

            ByCode.Clear();
            OutputById.Clear();
            foreach ((string code, WorkstationProfileDefinition definition) in loaded)
            {
                ByCode[code] = definition;
                Dictionary<int, WorkstationOutputDefinition> byId = new();
                foreach (WorkstationOutputDefinition output in definition.Outputs)
                {
                    byId[output.OutputId] = output;
                }
                OutputById[code] = byId;
                DebugLog(api, $"profile cached code={code} mode={definition.MenuMode} outputs={definition.Outputs.Count}");
            }

            PopulateWildcardInputCache(api);
            DebugLog(api, $"initialize complete cachedProfiles={ByCode.Count}");
        }

        public static bool TryGetProfile(string profileCode, out WorkstationProfileDefinition definition)
        {
            return ByCode.TryGetValue(profileCode, out definition!);
        }

        public static IReadOnlyCollection<WorkstationProfileDefinition> GetAllProfiles()
        {
            return ByCode.Values.ToArray();
        }

        public static bool TryGetOutputById(string profileCode, int outputId, out WorkstationOutputDefinition? definition)
        {
            definition = null;
            return OutputById.TryGetValue(profileCode, out Dictionary<int, WorkstationOutputDefinition>? byId)
                && byId.TryGetValue(outputId, out definition);
        }

        public static string BuildOutputPath(WorkstationOutputDefinition definition, IReadOnlyDictionary<string, string> assignment)
        {
            return ReplacePlaceholders(definition.CodeTemplate, assignment);
        }

        public static IReadOnlyDictionary<string, string> BuildOutputAttributes(WorkstationOutputDefinition definition, IReadOnlyDictionary<string, string> assignment)
        {
            if (definition.AttributeTemplates.Count == 0)
            {
                return EmptyAttributeTemplates;
            }

            Dictionary<string, string> resolved = new(StringComparer.Ordinal);
            foreach ((string key, string valueTemplate) in definition.AttributeTemplates)
            {
                resolved[key] = ReplacePlaceholders(valueTemplate, assignment);
            }

            return resolved;
        }

        public static string BuildInputPath(WorkstationInputRequirementDefinition definition, IReadOnlyDictionary<string, string> assignment)
        {
            return definition.CodeTemplates.Count > 0
                ? ReplacePlaceholders(definition.CodeTemplates[0], assignment)
                : string.Empty;
        }

        public static IReadOnlyList<string> BuildInputPaths(WorkstationInputRequirementDefinition definition, IReadOnlyDictionary<string, string> assignment)
        {
            return definition.CodeTemplates
                .Select(code => ReplacePlaceholders(code, assignment))
                .ToArray();
        }

        public static bool RequiresVariantSubstitution(WorkstationOutputDefinition definition)
        {
            return definition.PlaceholderKeys.Count > 0;
        }

        public static bool TryGetPreExpandedWildcardCodes(string collectibleType, string codePattern, out IReadOnlyList<AssetLocation> codes)
        {
            if (ExpandedWildcardInputCache.TryGetValue($"{collectibleType}|{codePattern}", out IReadOnlyList<AssetLocation>? cached))
            {
                codes = cached;
                return true;
            }

            codes = Array.Empty<AssetLocation>();
            return false;
        }

        private static void PopulateWildcardInputCache(ICoreAPI api)
        {
            ExpandedWildcardInputCache.Clear();

            foreach (WorkstationProfileDefinition profile in ByCode.Values)
            {
                foreach (WorkstationOutputDefinition output in profile.Outputs)
                {
                    foreach (WorkstationInputRequirementDefinition input in output.Inputs)
                    {
                        foreach (string codeTemplate in input.CodeTemplates)
                        {
                            // Only pre-expand bare * wildcards; {placeholder} patterns are
                            // expanded into concrete recipes at load time and never reach here.
                            if (!codeTemplate.Contains('*') || codeTemplate.Contains('{')
                                || !TryParseWildcardCode(codeTemplate, out AssetLocation patternCode))
                            {
                                continue;
                            }

                            string cacheKey = $"{input.Type}|{codeTemplate}";
                            if (ExpandedWildcardInputCache.ContainsKey(cacheKey))
                            {
                                continue;
                            }

                            IEnumerable<CollectibleObject> candidates = string.Equals(input.Type, "block", StringComparison.Ordinal)
                                ? (IEnumerable<CollectibleObject>)api.World.Blocks
                                : api.World.Items;

                            List<AssetLocation> matched = new();
                            foreach (CollectibleObject candidate in candidates)
                            {
                                if (candidate?.Code != null && WildcardUtil.Match(patternCode, candidate.Code))
                                {
                                    matched.Add(candidate.Code);
                                }
                            }

                            ExpandedWildcardInputCache[cacheKey] = matched;
                            DebugLog(api, $"wildcard input cache type={input.Type} pattern={codeTemplate} matched={matched.Count}");
                        }
                    }
                }
            }

            DebugLog(api, $"wildcard input cache complete entries={ExpandedWildcardInputCache.Count}");
        }

        private static bool TryParseWildcardCode(string code, out AssetLocation location)
        {
            location = default!;
            if (string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            try
            {
                location = new AssetLocation(code);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, WorkstationProfileDefinition> LoadProfiles(ICoreAPI api)
        {
            Dictionary<string, WorkstationProfileDefinition> assetProfiles = LoadBaseProfilesFromRecipeAssets(api);
            Dictionary<string, WorkstationProfileDefinition> result = LoadProfilesFromRegisteredRecipes(api, assetProfiles);

            if (result.Count == 0)
            {
                result = assetProfiles;
            }

            if (result.Count == 0)
            {
                api.Logger.Warning("Workstations: no workstation profiles could be built from registered workstation recipes");
                return result;
            }

            api.Logger.Notification($"Workstations: loaded {result.Count} workstation profile(s) from registered workstation recipes");

            return result;
        }

        private static Dictionary<string, WorkstationProfileDefinition> LoadProfilesFromRegisteredRecipes(
            ICoreAPI api,
            IReadOnlyDictionary<string, WorkstationProfileDefinition> assetProfiles)
        {
            List<WorkstationRecipe>? registeredRecipes = api.GetWorkstationRecipes();
            Dictionary<string, WorkstationProfileDefinition> profiles = new(StringComparer.Ordinal);
            DebugLog(api, $"profile build start assetProfiles={assetProfiles.Count} registryRecipes={registeredRecipes?.Count ?? -1}");
            if (registeredRecipes == null || registeredRecipes.Count == 0)
            {
                DebugLog(api, "profile build skipped because no registered recipes were available");
                return profiles;
            }

            Dictionary<string, List<WorkstationRecipe>> recipesByWorkstation = new(StringComparer.OrdinalIgnoreCase);
            foreach (WorkstationRecipe recipe in registeredRecipes)
            {
                if (string.IsNullOrWhiteSpace(recipe.Workstation))
                {
                    continue;
                }

                if (!recipesByWorkstation.TryGetValue(recipe.Workstation, out List<WorkstationRecipe>? list))
                {
                    list = new List<WorkstationRecipe>();
                    recipesByWorkstation[recipe.Workstation] = list;
                }

                list.Add(recipe);
            }

            foreach ((string workstation, List<WorkstationRecipe> recipes) in recipesByWorkstation)
            {
                DebugLog(api, $"building workstation={workstation} registeredRecipeCount={recipes.Count}");

                List<WorkstationOutputDefinition> registeredOutputs = new(recipes.Count);
                foreach ((WorkstationRecipe recipe, int outputId) in recipes.Select((recipe, index) => (recipe, index)))
                {
                    WorkstationOutputDefinition? output = TryBuildOutputDefinitionFromRegisteredRecipe(recipe, outputId);
                    if (output != null)
                    {
                        registeredOutputs.Add(output);
                        DebugLog(api, $"converted registered recipe workstation={workstation} recipeCode={recipe.RecipeCode ?? recipe.Name?.ToShortString() ?? "<unnamed>"} outputId={output.OutputId}");
                        DebugLog(api, $"converted output detail workstation={workstation} recipeCode={output.RecipeCode} outputType={output.OutputType} outputCode={output.CodeTemplate} outputAttrs={SerializeAttributeTemplates(output.AttributeTemplates)} inputs={string.Join(" | ", output.Inputs.Select(input => $"{input.Type}:{string.Join(",", input.CodeTemplates)} x{input.Quantity}"))}");
                    }
                    else
                    {
                        DebugLog(api, $"failed to convert registered recipe workstation={workstation} recipeCode={recipe.RecipeCode ?? recipe.Name?.ToShortString() ?? "<unnamed>"}");
                    }
                }

                if (registeredOutputs.Count == 0)
                {
                    DebugLog(api, $"no registered outputs survived conversion for workstation={workstation}");
                    continue;
                }

                string profileCode = workstation.Trim();
                WorkstationProfileDefinition? assetProfile = null;
                assetProfiles.TryGetValue(profileCode, out assetProfile);
                WorkstationMenuMode menuMode = assetProfile?.MenuMode ?? recipes[0].GetMenuMode();
                WorkstationProfileDefinition baseProfile = BuildProfileDefinition(
                    profileCode,
                    menuMode,
                    assetProfile?.OutputErrorCode,
                    assetProfile?.OutputErrorMessage,
                    assetProfile?.IngotErrorCode);

                profiles[workstation] = new WorkstationProfileDefinition
                {
                    ProfileCode = baseProfile.ProfileCode,
                    OutputErrorCode = baseProfile.OutputErrorCode,
                    OutputErrorMessage = baseProfile.OutputErrorMessage,
                    IngotErrorCode = baseProfile.IngotErrorCode,
                    MenuMode = baseProfile.MenuMode,
                    Outputs = registeredOutputs
                };
                DebugLog(api, $"profile built workstation={workstation} outputs={registeredOutputs.Count} assetProfilePresent={(assetProfile != null)}");
            }

            return profiles;
        }

        private static void DebugLog(ICoreAPI api, string message)
        {
            if (!DebugLogging)
            {
                return;
            }

            api.Logger.Notification($"Workstations: [profiles] {message}");
        }

        private static string SerializeAttributeTemplates(IReadOnlyDictionary<string, string> attributes)
        {
            if (attributes.Count == 0)
            {
                return "<none>";
            }

            return string.Join(";", attributes
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        }

        private static WorkstationOutputDefinition? TryBuildOutputDefinitionFromRegisteredRecipe(WorkstationRecipe recipe, int outputId)
        {
            JsonItemStack? output = recipe.Output;
            if (output?.Code == null)
            {
                return null;
            }

            List<CraftingRecipeIngredient> ingredientDefs = new();
            if (recipe.Ingredients != null && recipe.Ingredients.Length > 0)
            {
                ingredientDefs.AddRange(recipe.Ingredients.Where(ingredient => ingredient != null));
            }
            else if (recipe.Ingredient != null)
            {
                ingredientDefs.Add(recipe.Ingredient);
            }

            if (ingredientDefs.Count == 0)
            {
                return null;
            }

            WorkstationInputRequirementDefinition[] inputs = ingredientDefs
                .Select((ingredient, index) =>
                {
                    List<string> codeTemplates = new();
                    if (ingredient.Code != null)
                    {
                        codeTemplates.Add(NormalizeCodeTemplate(ingredient.Code.ToString()));
                    }

                    codeTemplates = codeTemplates
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    if (codeTemplates.Count == 0)
                    {
                        return null;
                    }

                    string? label = null;
                    if (ingredient.RecipeAttributes?["label"].Exists == true)
                    {
                        label = ingredient.RecipeAttributes["label"].AsString(null);
                    }

                    return new WorkstationInputRequirementDefinition(
                        NormalizeCollectibleType(ingredient.Type.ToString()),
                        codeTemplates,
                        ingredient.Quantity,
                        string.IsNullOrWhiteSpace(label) ? null : label);
                })
                .Where(input => input != null)
                .Select(input => input!)
                .GroupBy(BuildRegisteredInputGroupKey, StringComparer.Ordinal)
                .Select(group =>
                {
                    WorkstationInputRequirementDefinition first = group.First();
                    return new WorkstationInputRequirementDefinition(
                        first.Type,
                        first.CodeTemplates,
                        group.Sum(input => input.Quantity),
                        first.Label);
                })
                .ToArray();

            Dictionary<string, string> outputAttributes = ResolveRegisteredOutputAttributes(output);

            string outputCode = NormalizeCodeTemplate(output.Code.ToString());
            HashSet<string> placeholderKeys = new(StringComparer.Ordinal);
            foreach (string key in ExtractPlaceholders(outputCode))
            {
                placeholderKeys.Add(key);
            }

            foreach (string valueTemplate in outputAttributes.Values)
            {
                foreach (string key in ExtractPlaceholders(valueTemplate))
                {
                    placeholderKeys.Add(key);
                }
            }

            foreach (WorkstationInputRequirementDefinition input in inputs)
            {
                foreach (string template in input.CodeTemplates)
                {
                    foreach (string key in ExtractPlaceholders(template))
                    {
                        placeholderKeys.Add(key);
                    }
                }
            }

            Dictionary<string, IReadOnlyList<string>> allowedVariants = NormalizeVariantFilterMap(recipe.AllowedVariants);
            Dictionary<string, IReadOnlyList<string>> skipVariants = NormalizeVariantFilterMap(recipe.SkipVariants);
            IReadOnlyList<string> requiredTraits = ResolveRequiredTraits(recipe.RequiredTrait, recipe.RequiredTraits);

            string recipeIdentifier = recipe.RecipeCode
                ?? (!string.IsNullOrWhiteSpace(recipe.Workstation)
                    ? $"{recipe.Workstation}:{output.Code}"
                    : output.Code.ToString());

            return new WorkstationOutputDefinition(
                outputId,
                recipeIdentifier,
                recipe.RecipeGroup,
                NormalizeCollectibleType(output.Type.ToString()),
                outputCode,
                outputAttributes,
                output.Quantity <= 0 ? 1 : output.Quantity,
                inputs,
                allowedVariants,
                skipVariants,
                placeholderKeys.ToArray(),
                requiredTraits,
                recipe.RecipeDesc,
                recipe.RecipeName,
                recipe.Attributes?["handbookOverviewGroup"].AsString(null));
        }

        private static Dictionary<string, string> ResolveRegisteredOutputAttributes(JsonItemStack output)
        {
            Dictionary<string, string> attributes = new(StringComparer.Ordinal);

            if (output.Attributes?.Token is JObject outputAttrObject)
            {
                foreach (JProperty property in outputAttrObject.Properties())
                {
                    if (property.Value.Type == JTokenType.String)
                    {
                        attributes[property.Name] = property.Value.Value<string>() ?? string.Empty;
                    }
                }
            }

            // Registered workstation recipes are already resolved before we convert them into
            // profile outputs, so use the concrete stack attribute as a fallback when the raw
            // JsonItemStack token no longer carries the string template we need.
            string? resolvedType = output.ResolvedItemstack?.Attributes?.GetString("type", null);
            if (attributes.Count == 0 && !string.IsNullOrWhiteSpace(resolvedType))
            {
                attributes["type"] = resolvedType!;
            }

            return attributes;
        }

        private static WorkstationProfileDefinition BuildProfileDefinition(
            string profileCode,
            WorkstationMenuMode menuMode,
            string? outputErrorCode = null,
            string? outputErrorMessage = null,
            string? inputErrorCode = null)
        {
            return new WorkstationProfileDefinition
            {
                ProfileCode = profileCode,
                OutputErrorCode = outputErrorCode ?? $"{profileCode}-output",
                OutputErrorMessage = outputErrorMessage ?? "Selected output is not available",
                IngotErrorCode = inputErrorCode ?? $"{profileCode}-inputs",
                MenuMode = menuMode,
                Outputs = Array.Empty<WorkstationOutputDefinition>()
            };
        }

        private static void LogIgnoredDuplicateBaseProfile(ICoreAPI api, AssetLocation location, string profileCode)
        {
            api.Logger.Warning($"Workstations: duplicate workstation base profile '{profileCode}' in '{location}' was ignored; workstation recipes must now come from assets/recipes/workstation");
        }

        private static Dictionary<string, WorkstationProfileDefinition> LoadBaseProfilesFromRecipeAssets(ICoreAPI api)
        {
            Dictionary<string, WorkstationProfileDefinition> result = new(StringComparer.Ordinal);
            Dictionary<AssetLocation, JToken> files = api.Assets.GetMany<JToken>(api.Logger, "recipes/workstation");

            foreach ((AssetLocation location, JToken token) in files.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
            {
                if (token is not JObject obj || obj["recipes"] == null)
                {
                    continue;
                }

                string? profileCode = obj["workstation"]?.Value<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(profileCode))
                {
                    continue;
                }

                if (result.ContainsKey(profileCode))
                {
                    LogIgnoredDuplicateBaseProfile(api, location, profileCode);
                    continue;
                }

                WorkstationMenuMode menuMode = ParseMenuMode(
                    obj["menuMode"]?.Value<string>(),
                    profileCode);

                result[profileCode] = BuildProfileDefinition(
                    profileCode,
                    menuMode,
                    obj["outputErrorCode"]?.Value<string>(),
                    obj["outputErrorMessage"]?.Value<string>(),
                    obj["inputErrorCode"]?.Value<string>());
            }

            if (result.Count > 0)
            {
                api.Logger.Notification("Workstations: workstation profiles loaded from assets/recipes/workstation");
            }

            return result;
        }

        private static WorkstationMenuMode ParseMenuMode(string? menuMode, string profileCode)
        {
            if (string.IsNullOrWhiteSpace(menuMode))
            {
                throw new InvalidOperationException($"Workstation profile '{profileCode}' is missing required field 'menuMode'");
            }

            return menuMode.Trim().ToLowerInvariant() switch
            {
                "browser" => WorkstationMenuMode.Browser,
                "helditem" => WorkstationMenuMode.HeldItem,
                _ => throw new InvalidOperationException($"Workstation profile '{profileCode}' has unknown menuMode '{menuMode}'")
            };
        }

        private static string NormalizeCollectibleType(string? type)
        {
            return string.Equals(type, "block", StringComparison.OrdinalIgnoreCase) ? "block" : "item";
        }

        private static string NormalizeCodeTemplate(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return string.Empty;
            }

            return code.Trim();
        }

        private static string BuildRegisteredInputGroupKey(WorkstationInputRequirementDefinition input)
        {
            string codes = string.Join("|", input.CodeTemplates);
            return $"{input.Type}|{codes}|{input.Label ?? string.Empty}";
        }

        private static Dictionary<string, IReadOnlyList<string>> NormalizeVariantFilterMap(Dictionary<string, string[]>? source)
        {
            Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.Ordinal);
            if (source == null)
            {
                return result;
            }

            foreach ((string rawKey, string[]? rawValues) in source)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || rawValues == null)
                {
                    continue;
                }

                string key = rawKey.Trim();
                List<string> values = rawValues
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (values.Count > 0)
                {
                    result[key] = values;
                }
            }

            return result;
        }

        private static Dictionary<string, string> NormalizeTemplateMap(Dictionary<string, string>? source)
        {
            Dictionary<string, string> result = new(StringComparer.Ordinal);
            if (source == null)
            {
                return result;
            }

            foreach ((string rawKey, string? rawValue) in source)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(rawValue))
                {
                    continue;
                }

                string key = rawKey.Trim();
                string value = rawValue.Trim();
                if (key.Length == 0 || value.Length == 0)
                {
                    continue;
                }

                result[key] = value;
            }

            return result;
        }

        private static IReadOnlyList<string> NormalizeTraitRequirements(string? requiredTrait, string[]? requiredTraits)
        {
            List<string> result = new();

            if (!string.IsNullOrWhiteSpace(requiredTrait))
            {
                result.Add(requiredTrait.Trim());
            }

            if (requiredTraits != null)
            {
                result.AddRange(requiredTraits
                    .Where(trait => !string.IsNullOrWhiteSpace(trait))
                    .Select(trait => trait.Trim()));
            }

            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<string> ResolveRequiredTraits(
            string? requiredTrait,
            string[]? requiredTraits)
        {
            return NormalizeTraitRequirements(requiredTrait, requiredTraits);
        }

        public static IReadOnlyCollection<string> ExtractPlaceholders(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return Array.Empty<string>();
            }

            HashSet<string> placeholders = new(StringComparer.Ordinal);
            foreach ((bool isPlaceholder, string value) in ParseTemplateTokens(code))
            {
                if (isPlaceholder && !string.IsNullOrWhiteSpace(value))
                {
                    placeholders.Add(value);
                }
            }

            return placeholders.ToArray();
        }

        public static string ReplacePlaceholders(string code, IReadOnlyDictionary<string, string> assignment)
        {
            if (string.IsNullOrEmpty(code) || assignment.Count == 0)
            {
                return code ?? string.Empty;
            }

            StringBuilder sb = new(code.Length + 16);
            foreach ((bool isPlaceholder, string value) in ParseTemplateTokens(code))
            {
                if (!isPlaceholder)
                {
                    sb.Append(value);
                    continue;
                }

                if (assignment.TryGetValue(value, out string? replacement) && replacement != null)
                {
                    sb.Append(replacement);
                }
                else
                {
                    sb.Append('{').Append(value).Append('}');
                }
            }

            return sb.ToString();
        }

        public static bool TryMatchTemplate(string template, string actual, out List<Dictionary<string, string>> assignments)
        {
            assignments = [];
            List<(bool isPlaceholder, string value)> tokens = ParseTemplateTokens(template);
            MatchTemplateRecursive(tokens, 0, actual ?? string.Empty, 0, new Dictionary<string, string>(StringComparer.Ordinal), assignments);

            if (assignments.Count > 1)
            {
                List<Dictionary<string, string>> deduped = new List<Dictionary<string, string>>();
                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (Dictionary<string, string> assignment in assignments)
                {
                    string key = SerializeAssignmentKey(assignment);
                    if (seen.Add(key))
                    {
                        deduped.Add(assignment);
                    }
                }
                assignments = deduped;
            }

            return assignments.Count > 0;
        }

        public static bool TryMatchTemplateToStack(string template, ItemStack stack, out List<Dictionary<string, string>> assignments)
        {
            assignments = [];

            string? stackCode = stack.Collectible?.Code?.ToString();
            if (string.IsNullOrWhiteSpace(stackCode))
            {
                return false;
            }

            if (TryMatchTemplate(template, stackCode, out assignments))
            {
                return true;
            }

            string attributeBackedCode = BuildAttributeBackedCodeTemplate(template);
            if (!string.Equals(stackCode, attributeBackedCode, StringComparison.Ordinal))
            {
                assignments = [];
                return false;
            }

            IReadOnlyCollection<string> placeholders = ExtractPlaceholders(template);
            if (placeholders.Count == 0)
            {
                assignments = [];
                return false;
            }

            Dictionary<string, string> attributeAssignments = new(StringComparer.Ordinal);
            foreach (string placeholder in placeholders)
            {
                if (!TryGetStackAttributePlaceholderValue(stack, placeholder, out string? value))
                {
                    assignments = [];
                    return false;
                }

                attributeAssignments[placeholder] = value!;
            }

            assignments.Add(attributeAssignments);
            return true;
        }

        public static bool TryResolveAttributeBackedTemplate(
            IWorldAccessor world,
            string collectibleType,
            string template,
            IReadOnlyDictionary<string, string> assignment,
            out AssetLocation code,
            out IReadOnlyDictionary<string, string> attributes)
        {
            code = default!;
            attributes = EmptyAttributeTemplates;

            IReadOnlyCollection<string> placeholders = ExtractPlaceholders(template);
            if (placeholders.Count == 0)
            {
                return false;
            }

            Dictionary<string, string> attributeValues = new(StringComparer.Ordinal);
            foreach (string placeholder in placeholders)
            {
                if (!assignment.TryGetValue(placeholder, out string? value) || string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                attributeValues[placeholder] = value;
            }

            string attributeBackedCode = BuildAttributeBackedCodeTemplate(template);
            try
            {
                code = new AssetLocation(attributeBackedCode);
            }
            catch
            {
                return false;
            }

            bool resolved = string.Equals(collectibleType, "block", StringComparison.Ordinal)
                ? world.GetBlock(code) != null
                : world.GetItem(code) != null;

            if (!resolved)
            {
                code = default!;
                return false;
            }

            attributes = attributeValues;
            return true;
        }

        public static bool StackMatchesPlaceholderAttributes(ItemStack stack, IReadOnlyDictionary<string, string> attributes)
        {
            foreach ((string key, string expectedValue) in attributes)
            {
                if (!TryGetStackAttributePlaceholderValue(stack, key, out string? actualValue)
                    || !string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string BuildAttributeBackedCodeTemplate(string template)
        {
            List<(bool isPlaceholder, string value)> tokens = ParseTemplateTokens(template);
            if (tokens.Count == 0)
            {
                return template ?? string.Empty;
            }

            StringBuilder sb = new(template.Length);
            foreach ((bool isPlaceholder, string value) in tokens)
            {
                if (!isPlaceholder)
                {
                    sb.Append(value);
                }
            }

            return NormalizeAttributeBackedCode(sb.ToString());
        }

        private static string NormalizeAttributeBackedCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return string.Empty;
            }

            StringBuilder sb = new(code.Length);
            char? previous = null;
            foreach (char ch in code)
            {
                bool isSeparator = ch is '-' or '_' or '.';
                if (isSeparator && previous == ch)
                {
                    continue;
                }

                sb.Append(ch);
                previous = ch;
            }

            int colonIndex = sb.ToString().IndexOf(':');
            if (colonIndex < 0)
            {
                return sb.ToString().Trim('-', '_', '.');
            }

            string domain = sb.ToString(0, colonIndex + 1);
            string path = sb.ToString(colonIndex + 1, sb.Length - colonIndex - 1).Trim('-', '_', '.');
            return domain + path;
        }

        private static bool TryGetStackAttributePlaceholderValue(ItemStack stack, string placeholder, out string? value)
        {
            value = null;
            ITreeAttribute attributes = stack.Attributes;
            if (attributes is null)
            {
                return false;
            }

            foreach (string key in EnumerateCandidateAttributeKeys(placeholder))
            {
                string? candidate = attributes.GetString(key);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    value = candidate;
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> EnumerateCandidateAttributeKeys(string placeholder)
        {
            yield return placeholder;

            if (placeholder.EndsWith("type", StringComparison.Ordinal) && !string.Equals(placeholder, "type", StringComparison.Ordinal))
            {
                yield return "type";
            }

            if (placeholder.EndsWith("variant", StringComparison.Ordinal) && !string.Equals(placeholder, "variant", StringComparison.Ordinal))
            {
                yield return "variant";
            }
        }

        private static void MatchTemplateRecursive(
            List<(bool isPlaceholder, string value)> tokens,
            int tokenIndex,
            string actual,
            int actualIndex,
            Dictionary<string, string> current,
            List<Dictionary<string, string>> results)
        {
            if (tokenIndex >= tokens.Count)
            {
                if (actualIndex == actual.Length)
                {
                    results.Add(new Dictionary<string, string>(current, StringComparer.Ordinal));
                }
                return;
            }

            (bool isPlaceholder, string value) token = tokens[tokenIndex];
            if (!token.isPlaceholder)
            {
                if (!StartsWithAt(actual, actualIndex, token.value))
                {
                    return;
                }

                MatchTemplateRecursive(tokens, tokenIndex + 1, actual, actualIndex + token.value.Length, current, results);
                return;
            }

            string key = token.value;
            string nextLiteral = GetNextLiteral(tokens, tokenIndex + 1);

            if (current.TryGetValue(key, out string? existing))
            {
                if (!StartsWithAt(actual, actualIndex, existing))
                {
                    return;
                }

                MatchTemplateRecursive(tokens, tokenIndex + 1, actual, actualIndex + existing.Length, current, results);
                return;
            }

            if (nextLiteral.Length == 0)
            {
                current[key] = actual[actualIndex..];
                MatchTemplateRecursive(tokens, tokenIndex + 1, actual, actual.Length, current, results);
                current.Remove(key);
                return;
            }

            int searchIndex = actualIndex;
            while (searchIndex <= actual.Length)
            {
                int literalIndex = actual.IndexOf(nextLiteral, searchIndex, StringComparison.Ordinal);
                if (literalIndex < 0)
                {
                    break;
                }

                current[key] = actual.Substring(actualIndex, literalIndex - actualIndex);
                MatchTemplateRecursive(tokens, tokenIndex + 1, actual, literalIndex, current, results);
                current.Remove(key);
                searchIndex = literalIndex + 1;
            }
        }

        private static bool StartsWithAt(string value, int startIndex, string expected)
        {
            if (startIndex < 0 || startIndex > value.Length)
            {
                return false;
            }

            if (expected.Length == 0)
            {
                return true;
            }

            if (startIndex + expected.Length > value.Length)
            {
                return false;
            }

            return string.CompareOrdinal(value, startIndex, expected, 0, expected.Length) == 0;
        }

        private static string GetNextLiteral(List<(bool isPlaceholder, string value)> tokens, int startIndex)
        {
            for (int i = startIndex; i < tokens.Count; i++)
            {
                if (!tokens[i].isPlaceholder && tokens[i].value.Length > 0)
                {
                    return tokens[i].value;
                }
            }

            return string.Empty;
        }

        private static List<(bool isPlaceholder, string value)> ParseTemplateTokens(string? template)
        {
            List<(bool isPlaceholder, string value)> tokens = [];
            if (string.IsNullOrEmpty(template))
            {
                return tokens;
            }

            int index = 0;
            while (index < template.Length)
            {
                int open = template.IndexOf('{', index);
                if (open < 0)
                {
                    if (index < template.Length)
                    {
                        tokens.Add((false, template[index..]));
                    }
                    break;
                }

                if (open > index)
                {
                    tokens.Add((false, template[index..open]));
                }

                int close = template.IndexOf('}', open + 1);
                if (close < 0)
                {
                    tokens.Add((false, template[open..]));
                    break;
                }

                string key = template[(open + 1)..close];
                if (key.Length == 0)
                {
                    tokens.Add((false, template[open..(close + 1)]));
                }
                else
                {
                    tokens.Add((true, key));
                }

                index = close + 1;
            }

            if (tokens.Count == 0)
            {
                tokens.Add((false, template));
            }

            return tokens;
        }

        private static string SerializeAssignmentKey(IReadOnlyDictionary<string, string> assignment)
        {
            if (assignment.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(";", assignment
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        }
    }
}


