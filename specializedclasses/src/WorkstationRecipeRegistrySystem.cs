using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SpecializedClasses.Workstations
{
    public static class WorkstationRecipeApi
    {
        public static List<WorkstationRecipe> GetWorkstationRecipes(this ICoreAPI api)
        {
            if (api.World?.GetRecipeRegistry(WorkstationRecipeRegistrySystem.RegistryCode) is RecipeRegistryGeneric<WorkstationRecipe> registry)
            {
                return registry.Recipes;
            }

            return api.ModLoader.GetModSystem<WorkstationRecipeRegistrySystem>().Recipes;
        }

        public static IEnumerable<WorkstationRecipe> GetWorkstationRecipes(this ICoreAPI api, string workstation)
        {
            return api.GetWorkstationRecipes().FindAll(recipe =>
                string.Equals(recipe.Workstation, workstation, StringComparison.OrdinalIgnoreCase));
        }

        public static IEnumerable<WorkstationRecipe> GetWorkstationRecipes(this ICoreAPI api, string workstation, WorkstationMenuMode menuMode)
        {
            return api.GetWorkstationRecipes().FindAll(recipe =>
                string.Equals(recipe.Workstation, workstation, StringComparison.OrdinalIgnoreCase)
                && recipe.GetMenuMode() == menuMode);
        }

        public static void RegisterWorkstationRecipe(this ICoreServerAPI api, WorkstationRecipe recipe)
        {
            api.ModLoader.GetModSystem<WorkstationRecipeRegistrySystem>().RegisterWorkstationRecipe(recipe);
        }
    }

    public class WorkstationRecipeRegistrySystem : ModSystem
    {
        public const string RegistryCode = "workstationrecipes";
        private static readonly bool DebugLogging = false;

        public List<WorkstationRecipe> Recipes = new();
        private Dictionary<string, string> workstationMenuModes = new(StringComparer.OrdinalIgnoreCase);

        public override double ExecuteOrder()
        {
            return 0.65;
        }

        public override void Start(ICoreAPI api)
        {
            Recipes = api.RegisterRecipeRegistry<RecipeRegistryGeneric<WorkstationRecipe>>(RegistryCode).Recipes;
            workstationMenuModes = LoadWorkstationMenuModes(api);
            DebugLog(api, $"registry start side={api.Side} recipeCount={Recipes.Count}");
        }

        public override void AssetsLoaded(ICoreAPI api)
        {
            Dictionary<AssetLocation, JToken> files = api.Assets.GetMany<JToken>(api.Logger, "recipes/workstation");
            int recipeQuantity = 0;
            int recipesLoaded = 0;
            int failedResolveCount = 0;

            DebugLog(api, $"assets loaded on side={api.Side}; found {files.Count} workstation recipe file(s)");

            foreach ((AssetLocation location, JToken content) in files)
            {
                foreach (JObject recipeObject in EnumerateRecipeObjects(content))
                {
                    recipeQuantity++;
                    LoadRecipeObject(api, location, recipeObject, ref recipesLoaded, ref failedResolveCount);
                }
            }

            if (api.Side == EnumAppSide.Server)
            {
                if (failedResolveCount > 0)
                {
                    api.World.Logger.Event($"{recipeQuantity} workstation recipes loaded from {files.Count} files, failed to resolve {failedResolveCount} recipes");
                }
                else
                {
                    api.World.Logger.Event($"{recipeQuantity} workstation recipes loaded from {files.Count} files");
                }
            }

            DebugLog(api, $"assets loaded summary rawObjects={recipeQuantity} registered={recipesLoaded} failed={failedResolveCount} finalRegistryCount={Recipes.Count}");
        }

        public void RegisterWorkstationRecipe(WorkstationRecipe recipe)
        {
            recipe.RecipeId = Recipes.Count + 1;
            Recipes.Add(recipe);
            DebugLog(null, $"registered recipe id={recipe.RecipeId} workstation={recipe.Workstation} code={GetRecipeIdentifier(recipe)}");
        }

        private void LoadRecipeObject(
            ICoreAPI api,
            AssetLocation location,
            JObject recipeObject,
            ref int recipesLoaded,
            ref int failedResolveCount)
        {
            WorkstationRecipe? parsedRecipe = recipeObject.ToObject<WorkstationRecipe>(location.Domain);
            if (parsedRecipe == null || !parsedRecipe.Enabled)
            {
                DebugLog(api, $"skipping recipe object at {location}: parsed={(parsedRecipe != null)} enabled={parsedRecipe?.Enabled.ToString() ?? "null"}");
                return;
            }

            EnsureIngredientsParsed(api, parsedRecipe, recipeObject, location);
            ApplyWorkstationDefaults(parsedRecipe, recipeObject);

            if (parsedRecipe.Name == null)
            {
                parsedRecipe.Name = location;
            }

            WorkstationRecipe[] expandedRecipes = parsedRecipe.ExpandRecipesForAllIngredientCombinations(api.World).ToArray();
            DebugLog(api, $"loading {location} code={GetRecipeIdentifier(parsedRecipe)} expandedCount={expandedRecipes.Length}");

            int registeredThisEntry = 0;
            int skippedThisEntry = 0;
            foreach (WorkstationRecipe workstationRecipe in expandedRecipes)
            {
                if (!HasResolvableExactCollectibles(api.World, workstationRecipe))
                {
                    skippedThisEntry++;
                    DebugLog(api, $"skipped unresolved exact collectible workstation={workstationRecipe.Workstation} code={GetRecipeIdentifier(workstationRecipe)}");
                    continue;
                }

                if (workstationRecipe.Resolve(api.World, "WorkstationRecipeRegistrySystem"))
                {
                    RegisterWorkstationRecipe(workstationRecipe);
                    recipesLoaded++;
                    registeredThisEntry++;
                }
                else
                {
                    skippedThisEntry++;
                    DebugLog(api, $"failed resolve workstation={workstationRecipe.Workstation} code={GetRecipeIdentifier(workstationRecipe)}");
                }
            }

            // only count the original JSON entry as a failure if none of its expansions registered
            if (registeredThisEntry == 0 && expandedRecipes.Length > 0)
            {
                failedResolveCount++;
            }

            DebugLog(api, $"finished loading object {location} code={GetRecipeIdentifier(parsedRecipe)} registered={registeredThisEntry} skipped={skippedThisEntry} registeredSoFar={recipesLoaded} failedSoFar={failedResolveCount}");
        }

        private static bool HasResolvableExactCollectibles(IWorldAccessor world, WorkstationRecipe recipe)
        {
            if (!HasResolvableExactCollectible(world, recipe.Output))
            {
                return false;
            }

            if (recipe.Ingredients != null)
            {
                foreach (CraftingRecipeIngredient ingredient in recipe.Ingredients)
                {
                    if (!HasResolvableExactCollectible(world, ingredient))
                    {
                        return false;
                    }
                }
            }

            if (recipe.Ingredient != null && !HasResolvableExactCollectible(world, recipe.Ingredient))
            {
                return false;
            }

            return true;
        }

        private static bool HasResolvableExactCollectible(IWorldAccessor world, JsonItemStack? stack)
        {
            if (stack?.Code == null)
            {
                return false;
            }

            string codeText = stack.Code.ToString();
            if (ContainsPattern(codeText))
            {
                return true;
            }

            return string.Equals(stack.Type.ToString(), "block", StringComparison.OrdinalIgnoreCase)
                ? world.GetBlock(stack.Code) != null
                : world.GetItem(stack.Code) != null;
        }

        private static bool HasResolvableExactCollectible(IWorldAccessor world, CraftingRecipeIngredient? ingredient)
        {
            if (ingredient?.Code == null)
            {
                return false;
            }

            string codeText = ingredient.Code.ToString();
            if (ContainsPattern(codeText))
            {
                return true;
            }

            return ingredient.Type == EnumItemClass.Block
                ? world.GetBlock(ingredient.Code) != null
                : world.GetItem(ingredient.Code) != null;
        }

        private static readonly char[] PatternChars = ['*', '{', '}'];

        private static bool ContainsPattern(string codeText)
        {
            return codeText.IndexOfAny(PatternChars) >= 0;
        }

        private static void EnsureIngredientsParsed(ICoreAPI api, WorkstationRecipe recipe, JObject recipeObject, AssetLocation location)
        {
            if ((recipe.Ingredients != null && recipe.Ingredients.Length > 0) || recipe.Ingredient != null)
            {
                DebugLog(api, $"ingredients already present after JObject parse code={GetRecipeIdentifier(recipe)} ingredientCount={(recipe.Ingredients?.Length ?? (recipe.Ingredient != null ? 1 : 0))}");
                return;
            }

            if (recipeObject["ingredients"] is JArray ingredientsToken)
            {
                CraftingRecipeIngredient[]? parsedIngredients = ingredientsToken.ToObject<CraftingRecipeIngredient[]>(location.Domain);
                recipe.Ingredients = parsedIngredients;
                DebugLog(api, $"fallback parsed ingredients array code={GetRecipeIdentifier(recipe)} tokenCount={ingredientsToken.Count} parsedCount={parsedIngredients?.Length ?? -1}");
                return;
            }

            if (recipeObject["ingredient"] is JObject ingredientToken)
            {
                recipe.Ingredient = ingredientToken.ToObject<CraftingRecipeIngredient>(location.Domain);
                DebugLog(api, $"fallback parsed single ingredient code={GetRecipeIdentifier(recipe)} parsed={(recipe.Ingredient != null)}");
                return;
            }

            DebugLog(api, $"no ingredient tokens found in raw json code={GetRecipeIdentifier(recipe)}");
        }

        private void ApplyWorkstationDefaults(WorkstationRecipe recipe, JObject recipeObject)
        {
            if (recipeObject["menuMode"] == null
                && !string.IsNullOrWhiteSpace(recipe.Workstation)
                && workstationMenuModes.TryGetValue(recipe.Workstation, out string? menuMode)
                && !string.IsNullOrWhiteSpace(menuMode))
            {
                recipe.MenuMode = menuMode;
            }
        }

        private static Dictionary<string, string> LoadWorkstationMenuModes(ICoreAPI api)
        {
            Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

            try
            {
                Dictionary<AssetLocation, JToken> files = api.Assets.GetMany<JToken>(api.Logger, "recipes/workstation");
                foreach ((AssetLocation _, JToken token) in files.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
                {
                    if (token is not JObject obj || obj["recipes"] == null)
                    {
                        continue;
                    }

                    string? workstation = obj["workstation"]?.Value<string>()?.Trim();
                    string? menuMode = obj["menuMode"]?.Value<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(workstation) || string.IsNullOrWhiteSpace(menuMode))
                    {
                        continue;
                    }

                    result.TryAdd(workstation, menuMode);
                }
            }
            catch
            {
                // Base profile loading already has stronger diagnostics elsewhere.
            }

            return result;
        }

        private static IEnumerable<JObject> EnumerateRecipeObjects(JToken content)
        {
            if (content is JObject recipeObject)
            {
                if (recipeObject["recipes"] is JArray recipeArray)
                {
                    foreach (JToken token in recipeArray)
                    {
                        if (token is not JObject childRecipeObject)
                        {
                            continue;
                        }

                        JObject mergedRecipeObject = (JObject)childRecipeObject.DeepClone();
                        ApplyFileLevelDefaults(mergedRecipeObject, recipeObject);
                        foreach (JObject expandedRecipeObject in ExpandIngredientCodeAlternatives(mergedRecipeObject))
                        {
                            yield return expandedRecipeObject;
                        }
                    }

                    yield break;
                }

                foreach (JObject expandedRecipeObject in ExpandIngredientCodeAlternatives(recipeObject))
                {
                    yield return expandedRecipeObject;
                }
                yield break;
            }

            if (content is not JArray standaloneRecipeArray)
            {
                yield break;
            }

            foreach (JToken token in standaloneRecipeArray)
            {
                if (token is JObject childRecipeObject)
                {
                    foreach (JObject expandedRecipeObject in ExpandIngredientCodeAlternatives(childRecipeObject))
                    {
                        yield return expandedRecipeObject;
                    }
                }
            }
        }

        private static IEnumerable<JObject> ExpandIngredientCodeAlternatives(JObject recipeObject)
        {
            if (recipeObject["ingredients"] is JArray ingredientsArray)
            {
                List<JObject> expanded = new() { (JObject)recipeObject.DeepClone() };

                for (int ingredientIndex = 0; ingredientIndex < ingredientsArray.Count; ingredientIndex++)
                {
                    if (ingredientsArray[ingredientIndex] is not JObject ingredientObject)
                    {
                        continue;
                    }

                    if (ingredientObject["codes"] is not JArray codesArray || codesArray.Count == 0)
                    {
                        continue;
                    }

                    List<string> codes = codesArray
                        .Values<string>()
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    if (codes.Count == 0)
                    {
                        continue;
                    }

                    List<JObject> next = new();
                    foreach (JObject currentRecipe in expanded)
                    {
                        if (currentRecipe["ingredients"] is not JArray currentIngredients
                            || currentIngredients[ingredientIndex] is not JObject currentIngredient)
                        {
                            next.Add(currentRecipe);
                            continue;
                        }

                        foreach (string code in codes)
                        {
                            JObject clonedRecipe = (JObject)currentRecipe.DeepClone();
                            JObject clonedIngredient = (JObject)clonedRecipe["ingredients"]![ingredientIndex]!;
                            clonedIngredient["code"] = code;
                            clonedIngredient.Remove("codes");
                            next.Add(clonedRecipe);
                        }
                    }

                    expanded = next;
                }

                foreach (JObject expandedRecipe in expanded)
                {
                    yield return expandedRecipe;
                }

                yield break;
            }

            if (recipeObject["ingredient"] is JObject singleIngredient
                && singleIngredient["codes"] is JArray singleCodesArray
                && singleCodesArray.Count > 0)
            {
                List<string> codes = singleCodesArray
                    .Values<string>()
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                foreach (string code in codes)
                {
                    JObject clonedRecipe = (JObject)recipeObject.DeepClone();
                    JObject clonedIngredient = (JObject)clonedRecipe["ingredient"]!;
                    clonedIngredient["code"] = code;
                    clonedIngredient.Remove("codes");
                    yield return clonedRecipe;
                }

                yield break;
            }

            yield return (JObject)recipeObject.DeepClone();
        }

        private static void ApplyFileLevelDefaults(JObject recipeObject, JObject fileObject)
        {
            foreach (JProperty property in fileObject.Properties())
            {
                if (string.Equals(property.Name, "recipes", StringComparison.OrdinalIgnoreCase)
                    || recipeObject[property.Name] != null)
                {
                    continue;
                }

                recipeObject[property.Name] = property.Value.DeepClone();
            }
        }

        private static string GetRecipeIdentifier(WorkstationRecipe recipe)
        {
            if (!string.IsNullOrWhiteSpace(recipe.RecipeCode))
            {
                return recipe.RecipeCode;
            }

            string? outputCode = recipe.Output?.Code?.ToString();
            if (!string.IsNullOrWhiteSpace(recipe.Workstation) && !string.IsNullOrWhiteSpace(outputCode))
            {
                return $"{recipe.Workstation}:{outputCode}";
            }

            if (!string.IsNullOrWhiteSpace(outputCode))
            {
                return outputCode;
            }

            return recipe.Name?.ToShortString() ?? "<unnamed>";
        }

        private static void DebugLog(ICoreAPI? api, string message)
        {
            if (!DebugLogging)
            {
                return;
            }

            message = EscapeFormatBraces(message);

            if (api?.Logger != null)
            {
                api.Logger.Notification($"Workstations: [registry] {message}");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"Workstations: [registry] {message}");
        }

        private static string EscapeFormatBraces(string message)
        {
            return message
                .Replace("{", "{{", StringComparison.Ordinal)
                .Replace("}", "}}", StringComparison.Ordinal);
        }
    }
}
