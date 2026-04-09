using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace SpecializedClasses.Workstations
{
    public static class WorkstationHandbookHelper
    {
        private const string CacheKey = "specializedclasses:workstationhandbookcache";
        private const string DebugCounterCacheKey = "specializedclasses:workstationhandbookdebugcalls";
        private const int TinyPadding = 2;
        private const int TinyIndent = 2;
        private const int SmallPadding = 7;
        private static readonly bool DebugLogging = false;

        public static bool AppendIngredientFor(
            ICoreClientAPI capi,
            ActionConsumable<string> openDetailPageFor,
            ItemStack stack,
            List<RichTextComponentBase> components,
            bool haveText)
        {
            WorkstationHandbookCache cache = GetOrCreateCache(capi);
            string pageCode = GetPageCodeForStack(capi, stack);
            if (!cache.IngredientForByPageCode.TryGetValue(pageCode, out List<ItemStack>? outputs) || outputs.Count == 0)
            {
                LogSectionInvocation(capi, "ingredientfor", pageCode, stack, 0, 0);
                return haveText;
            }

            LogSectionInvocation(capi, "ingredientfor", pageCode, stack, outputs.Count, outputs
                .Select(output => GetPageCodeForStack(capi, output))
                .Distinct(StringComparer.Ordinal)
                .Count());

            CollectibleBehaviorHandbookTextAndExtraInfo.AddHeading(components, capi, "Workstation component for", ref haveText);
            components.Add(new ClearFloatTextComponent(capi, TinyPadding));

            foreach (ItemStack output in outputs)
            {
                components.Add(new ItemstackTextComponent(
                    capi,
                    output.Clone(),
                    40,
                    0,
                    EnumFloat.Inline,
                    clickedStack => openDetailPageFor(GetPageCodeForStack(capi, clickedStack))));
            }

            components.Add(new ClearFloatTextComponent(capi, 3));
            return haveText;
        }

        public static void WarmCache(ICoreClientAPI capi)
        {
            _ = GetOrCreateCache(capi);
        }

        public static void ClearCache(ICoreClientAPI capi)
        {
            capi.ObjectCache.Remove(CacheKey);
            capi.ObjectCache.Remove(DebugCounterCacheKey);
        }

        public static bool AppendCreatedBy(
            ICoreClientAPI capi,
            ActionConsumable<string> openDetailPageFor,
            ItemStack stack,
            List<RichTextComponentBase> components,
            bool haveText,
            bool mergeIntoExistingHeading)
        {
            WorkstationHandbookCache cache = GetOrCreateCache(capi);
            string pageCode = GetPageCodeForStack(capi, stack);
            if (!cache.CreatedByByPageCode.TryGetValue(pageCode, out List<WorkstationCreatedByEntry>? entries) || entries.Count == 0)
            {
                LogSectionInvocation(capi, "createdby", pageCode, stack, 0, 0);
                return haveText;
            }

            LogSectionInvocation(capi, "createdby", pageCode, stack, entries.Count, entries
                .Select(BuildEntryDebugSignature)
                .Distinct(StringComparer.Ordinal)
                .Count());

            if (!mergeIntoExistingHeading)
            {
                CollectibleBehaviorHandbookTextAndExtraInfo.AddHeading(components, capi, "Created by", ref haveText);
            }

            List<IGrouping<string, WorkstationCreatedByEntry>> stationGroups = entries
                .GroupBy(GetStationGroupKey, StringComparer.Ordinal)
                .ToList();

            bool firstStation = true;
            foreach (IGrouping<string, WorkstationCreatedByEntry> stationGroup in stationGroups)
            {
                List<WorkstationCreatedByEntry> stationEntries = stationGroup.ToList();
                components.Add(new ClearFloatTextComponent(capi, firstStation
                    ? (mergeIntoExistingHeading ? SmallPadding : TinyPadding + 1)
                    : SmallPadding));
                firstStation = false;
                AddWorkstationBullet(capi, openDetailPageFor, components, stationEntries[0]);

                bool firstRow = true;
                foreach (WorkstationCreatedByEntry entry in stationEntries)
                {
                    if (!firstRow)
                        components.Add(new ClearFloatTextComponent(capi, TinyPadding));
                    firstRow = false;
                    AddRecipeRow(capi, openDetailPageFor, components, entry);

                    if (!string.IsNullOrWhiteSpace(entry.RequiredTraitText))
                    {
                        RichTextComponent traitLine = new(capi, entry.RequiredTraitText + "\n", CairoFont.WhiteDetailText());
                        traitLine.PaddingLeft = TinyIndent;
                        components.Add(traitLine);
                    }
                }
            }

            return haveText;
        }

        public static bool AppendWorkstationUsedFor(
            ICoreClientAPI capi,
            ActionConsumable<string> openDetailPageFor,
            ItemStack stack,
            List<RichTextComponentBase> components,
            bool haveText)
        {
            WorkstationHandbookCache cache = GetOrCreateCache(capi);
            string pageCode = GetPageCodeForStack(capi, stack);
            if (!cache.WorkstationUsedForByPageCode.TryGetValue(pageCode, out List<ItemStack>? outputs) || outputs.Count == 0)
            {
                LogSectionInvocation(capi, "workstationusedfor", pageCode, stack, 0, 0);
                return haveText;
            }

            LogSectionInvocation(capi, "workstationusedfor", pageCode, stack, outputs.Count, outputs
                .Select(output => GetPageCodeForStack(capi, output))
                .Distinct(StringComparer.Ordinal)
                .Count());

            CollectibleBehaviorHandbookTextAndExtraInfo.AddHeading(components, capi, "Workstation used for", ref haveText);
            components.Add(new ClearFloatTextComponent(capi, TinyPadding));

            foreach (ItemStack output in outputs)
            {
                components.Add(new ItemstackTextComponent(
                    capi,
                    output.Clone(),
                    40,
                    0,
                    EnumFloat.Inline,
                    clickedStack => openDetailPageFor(GetPageCodeForStack(capi, clickedStack))));
            }

            components.Add(new ClearFloatTextComponent(capi, 3));
            return haveText;
        }

        public static bool AppendClassExclusiveRecipeRows(
            ICoreClientAPI capi,
            ActionConsumable<string> openDetailPageFor,
            string classCode,
            List<RichTextComponentBase> components)
        {
            WorkstationHandbookCache cache = GetOrCreateCache(capi);

            List<WorkstationCreatedByEntry> entries = cache.CreatedByByPageCode.Values
                .SelectMany(list => list)
                .Where(entry => entry.RequiredTraits.Any(
                    t => string.Equals(t, classCode, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(entry => entry.OutputSlides.Count > 0 ? entry.OutputSlides[0].GetName() : string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (entries.Count == 0) return false;

            // Group entries that share a handbookOverviewGroup into a single merged row.
            // Entries without one are each their own unique group.
            List<WorkstationCreatedByEntry> grouped = new List<WorkstationCreatedByEntry>();
            Dictionary<string, WorkstationCreatedByEntry> groupMap = new Dictionary<string, WorkstationCreatedByEntry>(StringComparer.Ordinal);
            int uniqueCounter = 0;
            foreach (WorkstationCreatedByEntry entry in entries)
            {
                string key = entry.HandbookOverviewGroup ?? $"__unique_{uniqueCounter++}";
                if (entry.HandbookOverviewGroup != null && groupMap.TryGetValue(key, out WorkstationCreatedByEntry? existing))
                {
                    // Merge slides into the existing representative entry
                    for (int i = 0; i < existing.IngredientSlides.Count && i < entry.IngredientSlides.Count; i++)
                    {
                        foreach (ItemStack stack in entry.IngredientSlides[i])
                        {
                            if (!existing.IngredientSlides[i].Any(s => s.Equals(null, stack, GlobalConstants.IgnoredStackAttributes)))
                                existing.IngredientSlides[i].Add(stack.Clone());
                        }
                    }
                    foreach (ItemStack stack in entry.OutputSlides)
                    {
                        if (!existing.OutputSlides.Any(s => s.Equals(null, stack, GlobalConstants.IgnoredStackAttributes)))
                            existing.OutputSlides.Add(stack.Clone());
                    }
                }
                else
                {
                    WorkstationCreatedByEntry copy = new WorkstationCreatedByEntry
                    {
                        WorkstationDisplayName = entry.WorkstationDisplayName,
                        WorkstationPageCode    = entry.WorkstationPageCode,
                        RequiredTraitText      = entry.RequiredTraitText,
                        RequiredTraits         = entry.RequiredTraits,
                        HandbookOverviewGroup  = entry.HandbookOverviewGroup,
                        IngredientSlides       = entry.IngredientSlides.Select(s => s.Select(st => st.Clone()).ToList()).ToList(),
                        OutputSlides           = entry.OutputSlides.Select(st => st.Clone()).ToList(),
                    };
                    grouped.Add(copy);
                    if (entry.HandbookOverviewGroup != null)
                        groupMap[key] = copy;
                }
            }

            List<IGrouping<string, WorkstationCreatedByEntry>> stationGroups = grouped
                .GroupBy(GetStationGroupKey, StringComparer.Ordinal)
                .ToList();

            bool firstStation = true;
            foreach (IGrouping<string, WorkstationCreatedByEntry> stationGroup in stationGroups)
            {
                List<WorkstationCreatedByEntry> stationEntries = stationGroup.ToList();
                components.Add(new ClearFloatTextComponent(capi, firstStation ? TinyPadding + 1 : SmallPadding));
                firstStation = false;
                AddWorkstationBullet(capi, openDetailPageFor, components, stationEntries[0]);

                bool firstRow = true;
                foreach (WorkstationCreatedByEntry entry in stationEntries)
                {
                    if (!firstRow)
                        components.Add(new ClearFloatTextComponent(capi, TinyPadding));
                    firstRow = false;
                    AddRecipeRow(capi, openDetailPageFor, components, entry);
                }
            }

            return true;
        }

        private static string GetStationGroupKey(WorkstationCreatedByEntry entry) =>
            string.IsNullOrWhiteSpace(entry.WorkstationPageCode)
                ? entry.WorkstationDisplayName
                : entry.WorkstationPageCode;

        private static void AddWorkstationBullet(
            ICoreClientAPI capi,
            ActionConsumable<string> openDetailPageFor,
            List<RichTextComponentBase> components,
            WorkstationCreatedByEntry entry)
        {
            RichTextComponent bullet = new(capi, "\u2022 ", CairoFont.WhiteSmallText());
            bullet.PaddingLeft = TinyIndent;
            components.Add(bullet);

            components.Add(new RichTextComponent(capi, "Workstation (", CairoFont.WhiteSmallText()));
            if (string.IsNullOrWhiteSpace(entry.WorkstationPageCode))
            {
                components.Add(new RichTextComponent(capi, $"{entry.WorkstationDisplayName})\n", CairoFont.WhiteSmallText()));
                return;
            }

            components.Add(new LinkTextComponent(
                capi,
                entry.WorkstationDisplayName,
                CairoFont.WhiteSmallText(),
                _ => openDetailPageFor(entry.WorkstationPageCode)));
            components.Add(new RichTextComponent(capi, ")\n", CairoFont.WhiteSmallText()));
        }

        private static void AddRecipeRow(
            ICoreClientAPI capi,
            ActionConsumable<string> openDetailPageFor,
            List<RichTextComponentBase> components,
            WorkstationCreatedByEntry entry)
        {
            int firstIndent = TinyIndent;

            for (int i = 0; i < entry.IngredientSlides.Count; i++)
            {
                if (i > 0)
                {
                    RichTextComponent plus = new(capi, " + ", CairoFont.WhiteMediumText());
                    plus.VerticalAlign = EnumVerticalAlign.Middle;
                    components.Add(plus);
                }

                SlideshowItemstackTextComponent ingredientComponent = new(
                    capi,
                    entry.IngredientSlides[i].Select(stack => stack.Clone()).ToArray(),
                    40,
                    EnumFloat.Inline,
                    clickedStack => openDetailPageFor(GetPageCodeForStack(capi, clickedStack)));
                ingredientComponent.ShowStackSize = true;
                ingredientComponent.PaddingLeft = firstIndent;
                firstIndent = 0;
                components.Add(ingredientComponent);
            }

            RichTextComponent equals = new(capi, " = ", CairoFont.WhiteMediumText());
            equals.VerticalAlign = EnumVerticalAlign.Middle;
            components.Add(equals);

            SlideshowItemstackTextComponent outputComponent = new(
                capi,
                entry.OutputSlides.Select(stack => stack.Clone()).ToArray(),
                40,
                EnumFloat.Inline,
                clickedStack => openDetailPageFor(GetPageCodeForStack(capi, clickedStack)));
            outputComponent.ShowStackSize = true;
            components.Add(outputComponent);

            components.Add(new RichTextComponent(capi, "\n", CairoFont.WhiteSmallText()));
        }

        private static WorkstationHandbookCache GetOrCreateCache(ICoreClientAPI capi)
        {
            string worldKey = capi.World.SavegameIdentifier ?? "default";
            if (capi.ObjectCache.TryGetValue(CacheKey, out object? cachedObj)
                && cachedObj is WorkstationHandbookCache cache
                && string.Equals(cache.WorldKey, worldKey, StringComparison.Ordinal))
            {
                return cache;
            }

            WorkstationHandbookCache rebuilt = BuildCache(capi, worldKey);
            capi.ObjectCache[CacheKey] = rebuilt;
            return rebuilt;
        }

        private static WorkstationHandbookCache BuildCache(ICoreClientAPI capi, string worldKey)
        {
            Dictionary<string, List<WorkstationCreatedByEntry>> createdBy = new(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, ItemStack>> ingredientFor = new(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, ItemStack>> workstationUsedFor = new(StringComparer.Ordinal);
            Dictionary<string, WorkstationDisplayInfo> workstationDisplayByProfile = BuildWorkstationDisplayLookup(capi);
            bool traitGatingEnabled = capi.World.Config.GetBool("classExclusiveRecipes", true);

            foreach (WorkstationProfileDefinition profile in WorkstationProfiles.GetAllProfiles())
            {
                workstationDisplayByProfile.TryGetValue(profile.ProfileCode, out WorkstationDisplayInfo? workstationDisplay);
                Dictionary<string, WorkstationCreatedByAccumulator> groupedEntries = new(StringComparer.Ordinal);

                foreach (WorkstationOutputDefinition definition in profile.Outputs)
                {
                    foreach (ResolvedWorkstationRecipe resolved in EnumerateResolvedRecipes(capi.World, definition))
                    {
                        ItemStack? outputStack = CreateStack(
                            resolved.OutputCollectible,
                            definition.Quantity,
                            WorkstationProfiles.BuildOutputAttributes(definition, resolved.Assignment));
                        if (outputStack == null)
                        {
                            continue;
                        }

                        string outputPageCode = GetPageCodeForStack(capi, outputStack);
                        string groupKey = BuildGroupKey(definition, resolved, outputPageCode);

                        if (!groupedEntries.TryGetValue(groupKey, out WorkstationCreatedByAccumulator? grouped))
                        {
                            grouped = new WorkstationCreatedByAccumulator(
                                workstationDisplay?.DisplayName ?? profile.ProfileCode,
                                workstationDisplay?.PageCode ?? string.Empty,
                                BuildRequiredTraitText(definition.RequiredTraits, traitGatingEnabled),
                                definition.RequiredTraits,
                                definition.HandbookOverviewGroup,
                                resolved.Inputs.Count);
                            groupedEntries[groupKey] = grouped;
                        }

                        grouped.OutputSlides.Add(outputStack.Clone());

                        if (!string.IsNullOrWhiteSpace(workstationDisplay?.PageCode))
                        {
                            if (!workstationUsedFor.TryGetValue(workstationDisplay.PageCode, out Dictionary<string, ItemStack>? workstationOutputs))
                            {
                                workstationOutputs = new Dictionary<string, ItemStack>(StringComparer.Ordinal);
                                workstationUsedFor[workstationDisplay.PageCode] = workstationOutputs;
                            }

                            if (!workstationOutputs.ContainsKey(outputPageCode))
                            {
                                workstationOutputs[outputPageCode] = outputStack.Clone();
                            }
                        }

                        for (int inputIndex = 0; inputIndex < resolved.Inputs.Count; inputIndex++)
                        {
                            foreach (ItemStack ingredientStack in ResolveIngredientSlides(capi, resolved.Inputs[inputIndex]))
                            {
                                AddUniqueStack(grouped.IngredientSlides[inputIndex], ingredientStack);

                                string ingredientPageCode = GetPageCodeForStack(capi, ingredientStack);
                                if (!ingredientFor.TryGetValue(ingredientPageCode, out Dictionary<string, ItemStack>? byOutputPage))
                                {
                                    byOutputPage = new Dictionary<string, ItemStack>(StringComparer.Ordinal);
                                    ingredientFor[ingredientPageCode] = byOutputPage;
                                }

                                if (!byOutputPage.ContainsKey(outputPageCode))
                                {
                                    byOutputPage[outputPageCode] = outputStack.Clone();
                                }
                            }
                        }
                    }
                }

                foreach (WorkstationCreatedByAccumulator grouped in groupedEntries.Values)
                {
                    if (grouped.OutputSlides.Count == 0 || grouped.IngredientSlides.Any(slides => slides.Count == 0))
                    {
                        continue;
                    }

                    string outputPageCode = GetPageCodeForStack(capi, grouped.OutputSlides[0]);
                    if (!createdBy.TryGetValue(outputPageCode, out List<WorkstationCreatedByEntry>? entries))
                    {
                        entries = new List<WorkstationCreatedByEntry>();
                        createdBy[outputPageCode] = entries;
                    }

                    entries.Add(new WorkstationCreatedByEntry
                    {
                        WorkstationDisplayName = grouped.WorkstationDisplayName,
                        WorkstationPageCode = grouped.WorkstationPageCode,
                        RequiredTraitText = grouped.RequiredTraitText,
                        RequiredTraits = grouped.RequiredTraits,
                        HandbookOverviewGroup = grouped.HandbookOverviewGroup,
                        IngredientSlides = grouped.IngredientSlides.Select(slides => slides.Select(stack => stack.Clone()).ToList()).ToList(),
                        OutputSlides = grouped.OutputSlides.Select(stack => stack.Clone()).ToList()
                    });
                }
            }

            WorkstationHandbookCache cache = new()
            {
                WorldKey = worldKey,
                CreatedByByPageCode = createdBy,
                IngredientForByPageCode = ingredientFor.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Values.Select(stack => stack.Clone()).ToList(),
                    StringComparer.Ordinal),
                WorkstationUsedForByPageCode = workstationUsedFor.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Values.Select(stack => stack.Clone()).ToList(),
                    StringComparer.Ordinal)
            };

            LogCacheSummary(capi, cache);
            return cache;
        }

        private static Dictionary<string, WorkstationDisplayInfo> BuildWorkstationDisplayLookup(ICoreClientAPI capi)
        {
            Dictionary<string, List<ItemStack>> byProfile = new(StringComparer.Ordinal);

            foreach (Block block in capi.World.Blocks)
            {
                string? profileCode = block?.Attributes?["workstationProfile"].AsString(null);
                if (string.IsNullOrWhiteSpace(profileCode))
                {
                    continue;
                }

                if (!byProfile.TryGetValue(profileCode, out List<ItemStack>? stacks))
                {
                    stacks = new List<ItemStack>();
                    byProfile[profileCode] = stacks;
                }

                stacks.Add(new ItemStack(block));
            }

            Dictionary<string, WorkstationDisplayInfo> result = new(StringComparer.Ordinal);
            foreach ((string profileCode, List<ItemStack> stacks) in byProfile)
            {
                ItemStack representative = SelectRepresentativeWorkstationStack(stacks);
                result[profileCode] = new WorkstationDisplayInfo
                {
                    DisplayName = representative.GetName(),
                    PageCode = GetPageCodeForStack(capi, representative)
                };
            }

            return result;
        }

        private static ItemStack SelectRepresentativeWorkstationStack(List<ItemStack> stacks)
        {
            ItemStack? northFacing = stacks.FirstOrDefault(stack =>
                string.Equals(stack.Block?.Variant?["side"], "north", StringComparison.Ordinal));

            return (northFacing ?? stacks[0]).Clone();
        }

        private static string BuildRequiredTraitText(IReadOnlyCollection<string> traits, bool traitGatingEnabled)
        {
            if (!traitGatingEnabled || traits.Count == 0)
            {
                return string.Empty;
            }

            return Lang.Get("gridrecipe-requirestrait", FormatRequiredTraitList(traits));
        }

        private static string FormatRequiredTraitList(IReadOnlyCollection<string> requiredTraits)
        {
            string[] traitNames = requiredTraits
                .Where(trait => !string.IsNullOrWhiteSpace(trait))
                .Select(WorkstationLogic.GetTraitDisplayName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (traitNames.Length == 0)
            {
                return string.Empty;
            }

            return JoinWithOr(traitNames);
        }

        private static string BuildGroupKey(
            WorkstationOutputDefinition definition,
            ResolvedWorkstationRecipe resolved,
            string outputPageCode)
        {
            string recipeGroup = string.IsNullOrWhiteSpace(definition.RecipeGroup)
                ? "_default"
                : definition.RecipeGroup!;

            string outputAttributesKey = SerializeAttributes(WorkstationProfiles.BuildOutputAttributes(definition, resolved.Assignment));
            string traitKey = string.Join("|", definition.RequiredTraits.OrderBy(value => value, StringComparer.Ordinal));
            return $"{recipeGroup}|{outputPageCode}|{definition.Quantity}|{outputAttributesKey}|{traitKey}|{resolved.Inputs.Count}";
        }

        private static string SerializeAttributes(IReadOnlyDictionary<string, string> attributes)
        {
            if (attributes.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(";", attributes
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        }

        private static void AddUniqueStack(List<ItemStack> target, ItemStack stack)
        {
            if (!target.Any(existing => existing.Equals(null, stack, GlobalConstants.IgnoredStackAttributes)))
            {
                target.Add(stack.Clone());
            }
        }

        private static IEnumerable<ResolvedWorkstationRecipe> EnumerateResolvedRecipes(
            IWorldAccessor world,
            WorkstationOutputDefinition definition)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (Dictionary<string, string> assignment in ExpandAssignments(world, definition, new Dictionary<string, string>(StringComparer.Ordinal), true))
            {
                if (!AssignmentSatisfiesVariantFilters(definition, assignment))
                {
                    continue;
                }

                if (!TryResolveRecipeVariant(world, definition, assignment, out ResolvedWorkstationRecipe? resolved))
                {
                    continue;
                }

                string key = SerializeAssignment(assignment);
                if (seen.Add(key))
                {
                    yield return resolved!;
                }
            }
        }

        private static IEnumerable<Dictionary<string, string>> ExpandAssignments(
            IWorldAccessor world,
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> seed,
            bool browserMode)
        {
            if (definition.PlaceholderKeys.Count == 0)
            {
                yield return new Dictionary<string, string>(seed, StringComparer.Ordinal);
                yield break;
            }

            List<string> missingKeys = definition.PlaceholderKeys
                .Where(key => !seed.ContainsKey(key))
                .ToList();

            if (missingKeys.Count == 0)
            {
                yield return new Dictionary<string, string>(seed, StringComparer.Ordinal);
                yield break;
            }

            Dictionary<string, IReadOnlyList<string>> variantValuesByKey = new(StringComparer.Ordinal);
            foreach (string missingKey in missingKeys)
            {
                if (definition.AllowedVariants.TryGetValue(missingKey, out IReadOnlyList<string>? values) && values.Count > 0)
                {
                    variantValuesByKey[missingKey] = values;
                    continue;
                }

                if (!browserMode || !TryInferVariantValues(world, definition, seed, missingKey, out IReadOnlyList<string> inferredValues))
                {
                    yield break;
                }

                variantValuesByKey[missingKey] = inferredValues;
            }

            Dictionary<string, string> working = new(seed, StringComparer.Ordinal);
            foreach (Dictionary<string, string> combo in ExpandAssignmentsRecursive(missingKeys, variantValuesByKey, 0, working))
            {
                yield return combo;
            }
        }

        private static IEnumerable<Dictionary<string, string>> ExpandAssignmentsRecursive(
            List<string> missingKeys,
            IReadOnlyDictionary<string, IReadOnlyList<string>> variantValuesByKey,
            int index,
            Dictionary<string, string> working)
        {
            if (index >= missingKeys.Count)
            {
                yield return new Dictionary<string, string>(working, StringComparer.Ordinal);
                yield break;
            }

            string key = missingKeys[index];
            if (!variantValuesByKey.TryGetValue(key, out IReadOnlyList<string>? values))
            {
                yield break;
            }

            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                working[key] = value;
                foreach (Dictionary<string, string> combo in ExpandAssignmentsRecursive(missingKeys, variantValuesByKey, index + 1, working))
                {
                    yield return combo;
                }
            }

            working.Remove(key);
        }

        private static bool TryInferVariantValues(
            IWorldAccessor world,
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> seed,
            string missingKey,
            out IReadOnlyList<string> values)
        {
            HashSet<string> result = new(StringComparer.Ordinal);
            values = Array.Empty<string>();

            CollectInferredVariantValues(world, definition.OutputType, definition.CodeTemplate, definition, seed, missingKey, result);
            foreach (WorkstationInputRequirementDefinition input in definition.Inputs)
            {
                foreach (string codeTemplate in input.CodeTemplates)
                {
                    CollectInferredVariantValues(world, input.Type, codeTemplate, definition, seed, missingKey, result);
                }
            }

            if (result.Count == 0)
            {
                return false;
            }

            values = result.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return true;
        }

        private static void CollectInferredVariantValues(
            IWorldAccessor world,
            string collectibleType,
            string codeTemplate,
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> seed,
            string missingKey,
            ISet<string> result)
        {
            if (!WorkstationProfiles.ExtractPlaceholders(codeTemplate).Contains(missingKey))
            {
                return;
            }

            if (string.Equals(collectibleType, "block", StringComparison.Ordinal))
            {
                foreach (Block? block in world.Blocks)
                {
                    if (block?.Code == null)
                    {
                        continue;
                    }

                    CollectMatchingVariantValues(codeTemplate, block.Code.ToString(), definition, seed, missingKey, result);
                }

                return;
            }

            foreach (Item? item in world.Items)
            {
                if (item?.Code == null)
                {
                    continue;
                }

                CollectMatchingVariantValues(codeTemplate, item.Code.ToString(), definition, seed, missingKey, result);
            }
        }

        private static void CollectMatchingVariantValues(
            string codeTemplate,
            string actualCode,
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> seed,
            string missingKey,
            ISet<string> result)
        {
            if (!WorkstationProfiles.TryMatchTemplate(codeTemplate, actualCode, out List<Dictionary<string, string>> assignments))
            {
                return;
            }

            foreach (Dictionary<string, string> assignment in assignments)
            {
                if (!TryMergeAssignments(seed, assignment, out Dictionary<string, string> merged))
                {
                    continue;
                }

                if (!AssignmentSatisfiesVariantFilters(definition, merged))
                {
                    continue;
                }

                if (merged.TryGetValue(missingKey, out string? value) && !string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
        }

        private static bool TryMergeAssignments(
            IReadOnlyDictionary<string, string> seed,
            IReadOnlyDictionary<string, string> candidate,
            out Dictionary<string, string> merged)
        {
            merged = new Dictionary<string, string>(seed, StringComparer.Ordinal);

            foreach ((string key, string value) in candidate)
            {
                if (merged.TryGetValue(key, out string? existing) && !string.Equals(existing, value, StringComparison.Ordinal))
                {
                    return false;
                }

                merged[key] = value;
            }

            return true;
        }

        private static bool TryResolveRecipeVariant(
            IWorldAccessor world,
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> assignment,
            out ResolvedWorkstationRecipe? resolved)
        {
            resolved = null;

            string outputPath = WorkstationProfiles.BuildOutputPath(definition, assignment);
            if (!TryResolveCollectible(world, definition.OutputType, outputPath, out CollectibleObject? outputCollectible))
            {
                return false;
            }

            List<ResolvedHandbookInput> inputs = ResolveInputRequirements(world, definition, assignment);
            if (inputs.Count == 0)
            {
                return false;
            }

            foreach (ResolvedHandbookInput input in inputs)
            {
                bool resolvedAny = input.Codes.Any(code => TryResolveCollectible(world, input.Type, code.ToString(), out _));
                if (!resolvedAny)
                {
                    resolvedAny = input.AttributeCandidates.Any(candidate => TryResolveCollectible(world, input.Type, candidate.Code.ToString(), out _));
                }

                if (!resolvedAny)
                {
                    return false;
                }
            }

            resolved = new ResolvedWorkstationRecipe
            {
                Assignment = new Dictionary<string, string>(assignment, StringComparer.Ordinal),
                Inputs = inputs,
                OutputCollectible = outputCollectible!
            };
            return true;
        }

        private static List<ResolvedHandbookInput> ResolveInputRequirements(
            IWorldAccessor world,
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> assignment)
        {
            List<ResolvedHandbookInput> resolved = new(definition.Inputs.Count);

            foreach (WorkstationInputRequirementDefinition requirement in definition.Inputs)
            {
                List<AssetLocation> codes = new();
                List<AttributeBackedHandbookInputCandidate> attributeCandidates = new();
                HashSet<string> seenAttributeCandidates = new(StringComparer.Ordinal);

                foreach (string codeTemplate in requirement.CodeTemplates)
                {
                    string path = WorkstationProfiles.ReplacePlaceholders(codeTemplate, assignment);
                    if (path.Contains('*'))
                    {
                        foreach (AssetLocation wildcardCode in ExpandWildcardInputCodes(world, requirement.Type, path))
                        {
                            codes.Add(wildcardCode);
                        }

                        continue;
                    }

                    if (TryParseCode(path, out AssetLocation code))
                    {
                        codes.Add(code);
                    }

                    if (WorkstationProfiles.TryResolveAttributeBackedTemplate(world, requirement.Type, codeTemplate, assignment, out AssetLocation attributeCode, out IReadOnlyDictionary<string, string> attributeValues))
                    {
                        string key = $"{attributeCode}|{SerializeAttributes(attributeValues)}";
                        if (seenAttributeCandidates.Add(key))
                        {
                            attributeCandidates.Add(new AttributeBackedHandbookInputCandidate
                            {
                                Code = attributeCode,
                                Attributes = attributeValues
                            });
                        }
                    }
                }

                if (codes.Count == 0 && attributeCandidates.Count == 0)
                {
                    continue;
                }

                resolved.Add(new ResolvedHandbookInput
                {
                    Type = requirement.Type,
                    Codes = codes,
                    AttributeCandidates = attributeCandidates,
                    Quantity = requirement.Quantity,
                    Label = string.IsNullOrWhiteSpace(requirement.Label)
                        ? null
                        : WorkstationProfiles.ReplacePlaceholders(requirement.Label, assignment)
                });
            }

            return resolved;
        }

        private static IEnumerable<AssetLocation> ExpandWildcardInputCodes(
            IWorldAccessor world,
            string requirementType,
            string codePattern)
        {
            if (WorkstationProfiles.TryGetPreExpandedWildcardCodes(requirementType, codePattern, out IReadOnlyList<AssetLocation> preExpanded))
            {
                foreach (AssetLocation code in preExpanded)
                {
                    yield return code;
                }
                yield break;
            }

            // Fallback: Initialize runs before WarmCache so this path should not fire
            // in normal play, but is kept for safety.
            if (!TryParseCode(codePattern, out AssetLocation searchCode))
            {
                yield break;
            }

            IEnumerable<CollectibleObject> matches = string.Equals(requirementType, "block", StringComparison.Ordinal)
                ? world.SearchBlocks(searchCode)
                : world.SearchItems(searchCode);

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (CollectibleObject match in matches)
            {
                AssetLocation? matchCode = match?.Code;
                if (matchCode == null)
                {
                    continue;
                }

                string key = matchCode.ToString();
                if (!seen.Add(key))
                {
                    continue;
                }

                yield return matchCode;
            }
        }

        private static List<ItemStack> ResolveIngredientSlides(ICoreClientAPI capi, ResolvedHandbookInput requirement)
        {
            List<ItemStack> stacks = new();
            List<string> alternativeNames = new();

            foreach (AssetLocation code in requirement.Codes)
            {
                if (!TryResolveCollectible(capi.World, requirement.Type, code.ToString(), out CollectibleObject? collectible))
                {
                    continue;
                }

                if (collectible == null || !IsHandbookVisible(capi, collectible))
                {
                    continue;
                }

                ItemStack? stack = CreateStack(collectible, requirement.Quantity);
                if (stack == null)
                {
                    continue;
                }

                AddUniqueStack(stacks, stack);

                string name = stack.GetName().ToLowerInvariant();
                if (!alternativeNames.Contains(name, StringComparer.Ordinal))
                {
                    alternativeNames.Add(name);
                }
            }

            foreach (AttributeBackedHandbookInputCandidate candidate in requirement.AttributeCandidates)
            {
                if (!TryResolveCollectible(capi.World, requirement.Type, candidate.Code.ToString(), out CollectibleObject? collectible))
                {
                    continue;
                }

                if (collectible == null || !IsHandbookVisible(capi, collectible))
                {
                    continue;
                }

                ItemStack? stack = CreateStack(collectible, requirement.Quantity, candidate.Attributes);
                if (stack == null)
                {
                    continue;
                }

                AddUniqueStack(stacks, stack);

                string name = stack.GetName().ToLowerInvariant();
                if (!alternativeNames.Contains(name, StringComparer.Ordinal))
                {
                    alternativeNames.Add(name);
                }
            }

            if (!string.IsNullOrWhiteSpace(requirement.Label))
            {
                ApplyIngredientLabel(stacks, requirement.Label!);
            }
            else if (alternativeNames.Count > 1)
            {
                ApplyIngredientLabel(stacks, JoinWithOr(alternativeNames));
            }

            return stacks;
        }

        private static bool IsHandbookVisible(ICoreClientAPI capi, CollectibleObject collectible)
        {
            List<ItemStack>? handbookStacks = collectible.GetHandBookStacks(capi);
            return handbookStacks != null && handbookStacks.Count > 0;
        }

        private static void ApplyIngredientLabel(List<ItemStack> stacks, string label)
        {
            foreach (ItemStack stack in stacks)
            {
                stack.Attributes ??= new TreeAttribute();
                stack.Attributes.SetString("specializedclasses:ingredientLabel", label);
            }
        }

        private static string JoinWithOr(IReadOnlyList<string> parts)
        {
            if (parts.Count == 0) return string.Empty;
            if (parts.Count == 1) return parts[0];
            if (parts.Count == 2) return $"{parts[0]} or {parts[1]}";
            return $"{string.Join(", ", parts.Take(parts.Count - 1))}, or {parts[^1]}";
        }

        private static bool AssignmentSatisfiesVariantFilters(
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> assignment)
        {
            foreach ((string key, IReadOnlyList<string> values) in definition.AllowedVariants)
            {
                if (assignment.TryGetValue(key, out string? value) && !values.Contains(value, StringComparer.Ordinal))
                {
                    return false;
                }
            }

            foreach ((string key, IReadOnlyList<string> values) in definition.SkipVariants)
            {
                if (assignment.TryGetValue(key, out string? value) && values.Contains(value, StringComparer.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string SerializeAssignment(IReadOnlyDictionary<string, string> assignment)
        {
            if (assignment.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(";", assignment
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        }

        private static bool TryResolveCollectible(
            IWorldAccessor world,
            string collectibleType,
            string codeOrPath,
            out CollectibleObject? collectible)
        {
            collectible = null;
            if (!TryParseCode(codeOrPath, out AssetLocation code))
            {
                return false;
            }

            if (string.Equals(collectibleType, "block", StringComparison.Ordinal))
            {
                collectible = world.GetBlock(code);
                return collectible != null;
            }

            collectible = world.GetItem(code);
            return collectible != null;
        }

        private static bool TryParseCode(string codeOrPath, out AssetLocation code)
        {
            if (string.IsNullOrWhiteSpace(codeOrPath) || codeOrPath.IndexOf(':') < 0)
            {
                code = default!;
                return false;
            }

            try
            {
                code = new AssetLocation(codeOrPath);
                return true;
            }
            catch
            {
                code = default!;
                return false;
            }
        }

        private static ItemStack? CreateStack(
            CollectibleObject? collectible,
            int quantity,
            IReadOnlyDictionary<string, string>? attributes = null)
        {
            ItemStack? stack = collectible switch
            {
                Item item => new ItemStack(item, quantity),
                Block block => new ItemStack(block, quantity),
                _ => null
            };

            if (stack == null)
            {
                return null;
            }

            if (attributes == null || attributes.Count == 0)
            {
                return stack;
            }

            stack.Attributes ??= new TreeAttribute();
            foreach ((string key, string value) in attributes)
            {
                stack.Attributes.SetString(key, value);
            }

            return stack;
        }

        private static string GetPageCodeForStack(ICoreClientAPI capi, ItemStack stack)
        {
            return stack.Collectible.GetCollectibleInterface<IHandBookPageCodeProvider>()?.HandbookPageCodeForStack(capi.World, stack)
                ?? GuiHandbookItemStackPage.PageCodeForStack(stack);
        }

        private static void LogSectionInvocation(
            ICoreClientAPI capi,
            string section,
            string pageCode,
            ItemStack stack,
            int entryCount,
            int uniqueCount)
        {
            if (!DebugLogging)
            {
                return;
            }

            Dictionary<string, int> counts = GetDebugCounters(capi);
            string key = $"{section}|{pageCode}";
            counts.TryGetValue(key, out int callCount);
            callCount++;
            counts[key] = callCount;

            capi.Logger.Notification(
                $"SpecializedClasses handbook debug: section={section} call={callCount} page={pageCode} stack=\"{stack.GetName()}\" entries={entryCount} unique={uniqueCount}");
        }

        private static Dictionary<string, int> GetDebugCounters(ICoreClientAPI capi)
        {
            if (capi.ObjectCache.TryGetValue(DebugCounterCacheKey, out object? cached)
                && cached is Dictionary<string, int> counts)
            {
                return counts;
            }

            Dictionary<string, int> created = new(StringComparer.Ordinal);
            capi.ObjectCache[DebugCounterCacheKey] = created;
            return created;
        }

        private static void LogCacheSummary(ICoreClientAPI capi, WorkstationHandbookCache cache)
        {
            if (!DebugLogging)
            {
                return;
            }

            capi.Logger.Notification(
                $"SpecializedClasses handbook debug: cache built ingredientPages={cache.IngredientForByPageCode.Count} createdByPages={cache.CreatedByByPageCode.Count}");

            foreach ((string pageCode, List<WorkstationCreatedByEntry> entries) in cache.CreatedByByPageCode
                .Where(pair => pair.Value.Count > 1)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string signatures = string.Join(" || ", entries.Select(BuildEntryDebugSignature));
                capi.Logger.Notification(
                    $"SpecializedClasses handbook debug: createdby page={pageCode} entries={entries.Count} signatures={signatures}");
            }
        }

        private static string BuildEntryDebugSignature(WorkstationCreatedByEntry entry)
        {
            string ingredients = string.Join(" + ", entry.IngredientSlides.Select(slides =>
                string.Join("/", slides.Select(stack => stack.Collectible.Code.ToShortString()).Distinct(StringComparer.Ordinal))));
            string outputs = string.Join("/", entry.OutputSlides.Select(stack => stack.Collectible.Code.ToShortString()).Distinct(StringComparer.Ordinal));
            return $"{entry.WorkstationPageCode}|trait={entry.RequiredTraitText}|ingredients={ingredients}|outputs={outputs}";
        }

        private sealed class WorkstationHandbookCache
        {
            public required string WorldKey { get; init; }
            public required Dictionary<string, List<WorkstationCreatedByEntry>> CreatedByByPageCode { get; init; }
            public required Dictionary<string, List<ItemStack>> IngredientForByPageCode { get; init; }
            public required Dictionary<string, List<ItemStack>> WorkstationUsedForByPageCode { get; init; }
        }

        private sealed class WorkstationCreatedByEntry
        {
            public required string WorkstationDisplayName { get; init; }
            public required string WorkstationPageCode { get; init; }
            public required string RequiredTraitText { get; init; }
            public required IReadOnlyList<string> RequiredTraits { get; init; }
            public string? HandbookOverviewGroup { get; init; }
            public required List<List<ItemStack>> IngredientSlides { get; init; }
            public required List<ItemStack> OutputSlides { get; init; }
        }

        private sealed class WorkstationCreatedByAccumulator
        {
            public WorkstationCreatedByAccumulator(string workstationDisplayName, string workstationPageCode, string requiredTraitText, IReadOnlyList<string> requiredTraits, string? handbookOverviewGroup, int inputCount)
            {
                WorkstationDisplayName = workstationDisplayName;
                WorkstationPageCode = workstationPageCode;
                RequiredTraitText = requiredTraitText;
                RequiredTraits = requiredTraits;
                HandbookOverviewGroup = handbookOverviewGroup;
                IngredientSlides = Enumerable.Range(0, inputCount).Select(_ => new List<ItemStack>()).ToList();
            }

            public string WorkstationDisplayName { get; }
            public string WorkstationPageCode { get; }
            public string RequiredTraitText { get; }
            public IReadOnlyList<string> RequiredTraits { get; }
            public string? HandbookOverviewGroup { get; }
            public List<List<ItemStack>> IngredientSlides { get; }
            public List<ItemStack> OutputSlides { get; } = new();
        }

        private sealed class WorkstationDisplayInfo
        {
            public required string DisplayName { get; init; }
            public required string PageCode { get; init; }
        }

        private sealed class ResolvedWorkstationRecipe
        {
            public required Dictionary<string, string> Assignment { get; init; }
            public required List<ResolvedHandbookInput> Inputs { get; init; }
            public required CollectibleObject OutputCollectible { get; init; }
        }

        private sealed class ResolvedHandbookInput
        {
            public required string Type { get; init; }
            public required IReadOnlyList<AssetLocation> Codes { get; init; }
            public required IReadOnlyList<AttributeBackedHandbookInputCandidate> AttributeCandidates { get; init; }
            public required int Quantity { get; init; }
            public string? Label { get; init; }
        }

        private sealed class AttributeBackedHandbookInputCandidate
        {
            public required AssetLocation Code { get; init; }
            public required IReadOnlyDictionary<string, string> Attributes { get; init; }
        }
    }
}
