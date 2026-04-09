using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace SpecializedClasses.Workstations
{
    public class BlockEntityWorkstation : BlockEntity
    {
        private static readonly bool DebugLogging = false;
        private static readonly AssetLocation DefaultCraftSound = new("game", "sounds/block/anvil");
        private const string PreviewIngredientLabelKey = "specializedclasses:ingredientLabel";
        private const string GroupSelectionTokenPrefix = "group:";
        private const string NoRecipesForItemErrorCode = "workstation-norecipesforitem";
        private const string TraitRequiredErrorCode = "workstation-requirestrait";
        private const string NoRecipesForItemErrorLangKey = "specializedclasses:workstation-error-norecipesforitem";
        private static readonly HashSet<string> LoggedRecipeSchemaWarnings = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<WorkstationChoiceEntry>> browserOptionCache = new(StringComparer.Ordinal);

        private GuiDialogWorkstationRecipeSelector? dialog;

        public bool OnInteract(IPlayer byPlayer)
        {
            if (Api == null || byPlayer == null)
            {
                return false;
            }

            LogDebug($"interact start side={Api.Side} player={byPlayer.PlayerName ?? "<unknown>"} pos={Pos} block={Block?.Code}");

            if (!TryGetProfile(out WorkstationProfileDefinition profile))
            {
                LogDebug("interact abort: no profile found");
                return false;
            }

            LogDebug($"interact resolved profile={profile.ProfileCode} mode={profile.MenuMode} outputs={profile.Outputs.Count}");

            if (Api.Side == EnumAppSide.Client)
            {
                OpenDialog((ICoreClientAPI)Api, byPlayer, profile);
            }

            return true;
        }

        public override void OnReceivedClientPacket(IPlayer player, int packetid, byte[] data)
        {
            base.OnReceivedClientPacket(player, packetid, data);

            if (packetid != (int)EnumWorkstationPacket.SelectOutput || player == null)
            {
                return;
            }

            string payload = SerializerUtil.Deserialize<string>(data);
            if (!TryParseSelectionPacket(payload, out int outputId, out string assignmentToken, out bool craftToStack))
            {
                LogDebug("workstation select reject: invalid selection payload");
                return;
            }

            ExecuteSelection(player, outputId, assignmentToken, craftToStack);
        }

        public static void ClearBrowserOptionCache()
        {
            browserOptionCache.Clear();
        }

        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();
            DisposeDialog();
        }

        public override void OnBlockUnloaded()
        {
            base.OnBlockUnloaded();
            DisposeDialog();
        }

        private void OpenDialog(ICoreClientAPI capi, IPlayer byPlayer, WorkstationProfileDefinition profile)
        {
            if (dialog != null && dialog.IsOpened())
            {
                LogDebug($"open dialog skipped because dialog already open profile={profile.ProfileCode}");
                return;
            }

            dialog?.Dispose();
            dialog = null;

            BuildDialogInputState(byPlayer, out ItemSlot? activeSlot, out ItemStack? heldStack, out float inputTemp);
            LogDebug($"open dialog state profile={profile.ProfileCode} held={heldStack?.Collectible?.Code?.ToString() ?? "<none>"} inputTemp={inputTemp} outputs={profile.Outputs.Count}");
            if (TryGetProfileTraitError(byPlayer, profile, out IReadOnlyList<string> profileMissingTraits))
            {
                LogDebug($"open dialog blocked by profile traits profile={profile.ProfileCode} missing={string.Join(",", profileMissingTraits)}");
                ShowRecipeTraitError(byPlayer, profileMissingTraits);
                return;
            }

            List<WorkstationChoiceEntry> options = BuildOptions(capi, byPlayer, profile, heldStack, inputTemp, out IReadOnlyList<string> missingTraits);
            if (options.Count == 0)
            {
                LogDebug($"open dialog produced no options profile={profile.ProfileCode} missingTraits={string.Join(",", missingTraits)}");
                if (missingTraits.Count > 0)
                {
                    ShowRecipeTraitError(byPlayer, missingTraits);
                    return;
                }

                if (profile.MenuMode == WorkstationMenuMode.HeldItem)
                {
                    WorkstationLogic.ShowIngameError(Api.World, byPlayer, NoRecipesForItemErrorCode, GetNoRecipesForItemMessage(profile));
                }

                return;
            }

            LogDebug($"open dialog success profile={profile.ProfileCode} optionCount={options.Count}");

            ItemStack[] outputStacks = options.Select(option => option.Stack).ToArray();

            dialog = new GuiDialogWorkstationRecipeSelector(
                Lang.Get("Select Recipe"),
                outputStacks,
                (selectedIndex, craftToStack) =>
                {
                    string selectionToken = CreateSelectionToken(options[selectedIndex]);
                    string payload = CreateSelectionPacket(options[selectedIndex].OutputId, selectionToken, craftToStack);
                    capi.Network.SendBlockEntityPacket(Pos, (int)EnumWorkstationPacket.SelectOutput, SerializerUtil.Serialize(payload));
                },
                () => { },
                Pos,
                capi
            );

            for (int i = 0; i < options.Count; i++)
            {
                dialog.SetCustomName(i, options[i].RecipeName);
                dialog.SetCustomDescription(i, options[i].Description);
                dialog.SetRequiredTraitText(i, options[i].RequiredTraitText);

                if (options[i].IngredientPreviewStacks.Length == 0)
                {
                    continue;
                }

                dialog.SetIngredientCounts(i, options[i].IngredientPreviewStacks);
            }

            dialog.TryOpen();
        }

        private void ExecuteSelection(IPlayer byPlayer, int outputId, string assignmentToken, bool craftToStack)
        {
            if (!TryGetProfile(out WorkstationProfileDefinition profile))
            {
                return;
            }

            if (TryParseGroupedSelectionToken(assignmentToken, out List<WorkstationSelectionCandidate> groupedCandidates))
            {
                foreach (WorkstationSelectionCandidate candidate in groupedCandidates)
                {
                    if (TryExecuteSelectionCandidate(byPlayer, profile, candidate.OutputId, candidate.AssignmentToken, craftToStack, false))
                    {
                        return;
                    }
                }

                WorkstationSelectionCandidate fallback = groupedCandidates[0];
                TryExecuteSelectionCandidate(byPlayer, profile, fallback.OutputId, fallback.AssignmentToken, craftToStack, true);
                return;
            }

            TryExecuteSelectionCandidate(byPlayer, profile, outputId, assignmentToken, craftToStack, true);
        }

        private bool TryExecuteSelectionCandidate(IPlayer byPlayer, WorkstationProfileDefinition profile, int outputId, string assignmentToken, bool craftToStack, bool showErrors)
        {
            WorkstationProfiles.TryGetOutputById(profile.ProfileCode, outputId, out WorkstationOutputDefinition? definition);
            if (definition == null)
            {
                if (showErrors)
                {
                    WorkstationLogic.ShowIngameError(Api.World, byPlayer, profile.OutputErrorCode, profile.OutputErrorMessage);
                    LogDebug($"workstation select reject: unknown output option={outputId} profile={profile.ProfileCode}");
                }

                return false;
            }

            if (!HasRecipeTraitAccess(byPlayer, definition))
            {
                if (showErrors)
                {
                    ShowRecipeTraitError(byPlayer, definition.RequiredTraits);
                    LogDebug($"workstation select reject: missing trait profile={profile.ProfileCode} traits={string.Join(",", definition.RequiredTraits)}");
                }

                return false;
            }

            if (!TryDeserializeAssignment(assignmentToken, out Dictionary<string, string> assignment))
            {
                if (showErrors)
                {
                    WorkstationLogic.ShowIngameError(Api.World, byPlayer, profile.OutputErrorCode, profile.OutputErrorMessage);
                    LogDebug($"workstation select reject: invalid assignment option={outputId} profile={profile.ProfileCode}");
                }

                return false;
            }

            if (!AssignmentSatisfiesVariantFilters(definition, assignment))
            {
                if (showErrors)
                {
                    WorkstationLogic.ShowIngameError(Api.World, byPlayer, profile.OutputErrorCode, profile.OutputErrorMessage);
                    LogDebug($"workstation select reject: recipe assignment disallowed option={outputId} profile={profile.ProfileCode} assignment={assignmentToken}");
                }

                return false;
            }

            string outputPath = WorkstationProfiles.BuildOutputPath(definition, assignment);
            if (!TryResolveCollectible(definition.OutputType, outputPath, out CollectibleObject? outputCollectible))
            {
                if (showErrors)
                {
                    WorkstationLogic.ShowIngameError(Api.World, byPlayer, profile.OutputErrorCode, profile.OutputErrorMessage);
                    LogDebug($"workstation select reject: output unavailable option={outputId} assignment={assignmentToken} profile={profile.ProfileCode}");
                }

                return false;
            }

            int targetCraftRuns = GetTargetCraftRuns(definition, outputCollectible!, craftToStack);
            if (!TryCraftRuns(byPlayer, profile, definition, assignment, targetCraftRuns, out int completedRuns, out string? inputFailureMessage))
            {
                if (showErrors)
                {
                    string message = inputFailureMessage ?? Lang.Get("specializedclasses:workstation-error-missing", "required items");
                    WorkstationLogic.ShowIngameError(Api.World, byPlayer, profile.IngotErrorCode, message);
                    LogDebug($"workstation select reject: input failure option={outputId} assignment={assignmentToken} message={message}");
                }

                return false;
            }

            ItemStack? outputStack = CreateStack(
                outputCollectible,
                definition.Quantity * completedRuns,
                WorkstationProfiles.BuildOutputAttributes(definition, assignment));
            if (outputStack == null)
            {
                if (showErrors)
                {
                    WorkstationLogic.ShowIngameError(Api.World, byPlayer, profile.OutputErrorCode, profile.OutputErrorMessage);
                    LogDebug($"workstation select reject: unsupported output type option={outputId} profile={profile.ProfileCode}");
                }

                return false;
            }

            if (!byPlayer.InventoryManager.TryGiveItemstack(outputStack, true))
            {
                Vec3d spawnPos = Pos.ToVec3d().Add(0.5, 0.75, 0.5);
                Api.World.SpawnItemEntity(outputStack, spawnPos);
            }

            Api.World.PlaySoundAt(GetCraftSound(), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5);
            return true;
        }

        private List<WorkstationChoiceEntry> BuildOptions(ICoreClientAPI capi, IPlayer byPlayer, WorkstationProfileDefinition profile, ItemStack? heldStack, float inputTemp, out IReadOnlyList<string> missingTraits)
        {
            if (profile.MenuMode == WorkstationMenuMode.Browser)
            {
                return BuildBrowserOptions(capi, byPlayer, profile, out missingTraits);
            }

            List<WorkstationChoiceEntry> options = new List<WorkstationChoiceEntry>(profile.Outputs.Count);
            Dictionary<string, WorkstationChoiceEntry> groupedOptions = new(StringComparer.Ordinal);
            HashSet<string> deniedTraits = new(StringComparer.OrdinalIgnoreCase);
            int skippedForWorkstation = 0;
            int skippedForHeldItem = 0;
            int skippedForResolvedVariants = 0;
            int skippedForTraits = 0;
            int skippedForOutputStack = 0;

            foreach (WorkstationOutputDefinition definition in profile.Outputs)
            {
                if (profile.MenuMode == WorkstationMenuMode.HeldItem && heldStack == null)
                {
                    skippedForHeldItem++;
                    continue;
                }

                List<ResolvedRecipeVariant> resolvedVariants = EnumerateResolvedRecipeVariants(capi.World, profile, definition, heldStack, null, inputTemp).ToList();
                if (resolvedVariants.Count == 0)
                {
                    skippedForResolvedVariants++;
                    LogDebug($"build options no resolved variants profile={profile.ProfileCode} recipeCode={definition.RecipeCode} outputId={definition.OutputId}");
                    continue;
                }

                if (!HasRecipeTraitAccess(byPlayer, definition))
                {
                    skippedForTraits++;
                    foreach (string trait in definition.RequiredTraits)
                    {
                        deniedTraits.Add(trait);
                    }

                    continue;
                }

                foreach (ResolvedRecipeVariant resolvedVariant in resolvedVariants)
                {
                    ItemStack? outputStack = CreateStack(
                        resolvedVariant.OutputCollectible,
                        definition.Quantity,
                        WorkstationProfiles.BuildOutputAttributes(definition, resolvedVariant.Assignment));
                    if (outputStack == null)
                    {
                        skippedForOutputStack++;
                        continue;
                    }

                    options.Add(new WorkstationChoiceEntry
                    {
                        Stack = outputStack,
                        IngredientPreviewStacks = ResolveIngredientPreviewStacks(capi, resolvedVariant),
                        RequiredTraits = definition.RequiredTraits,
                        RequiredTraitText = BuildRecipeRequiredTraitText(definition.RequiredTraits),
                        SelectionCandidates = new List<WorkstationSelectionCandidate>
                        {
                            new()
                            {
                                OutputId = definition.OutputId,
                                AssignmentToken = SerializeAssignment(resolvedVariant.Assignment)
                            }
                        },
                        Description = ResolveRecipeDescription(definition, resolvedVariant.Assignment),
                        RecipeName = ResolveRecipeName(definition, resolvedVariant.Assignment)
                    });

                    WorkstationChoiceEntry addedOption = options[^1];
                    string groupKey = GetRecipeGroupKey(definition, resolvedVariant);
                    if (!groupedOptions.TryGetValue(groupKey, out WorkstationChoiceEntry? existingOption))
                    {
                        groupedOptions[groupKey] = addedOption;
                        continue;
                    }

                    options.RemoveAt(options.Count - 1);
                    MergeGroupedChoiceEntry(existingOption, addedOption);
                }
            }

            missingTraits = deniedTraits.ToArray();
            LogDebug($"build options summary profile={profile.ProfileCode} outputs={profile.Outputs.Count} built={options.Count} skippedWorkstation={skippedForWorkstation} skippedHeld={skippedForHeldItem} skippedVariants={skippedForResolvedVariants} skippedTraits={skippedForTraits} skippedOutput={skippedForOutputStack}");
            return options;
        }

        private List<WorkstationChoiceEntry> BuildBrowserOptions(ICoreClientAPI capi, IPlayer byPlayer, WorkstationProfileDefinition profile, out IReadOnlyList<string> missingTraits)
        {
            List<WorkstationChoiceEntry> cachedOptions = GetOrBuildBrowserOptions(capi, profile);
            List<WorkstationChoiceEntry> options = new(cachedOptions.Count);
            HashSet<string> deniedTraits = new(StringComparer.OrdinalIgnoreCase);

            foreach (WorkstationChoiceEntry cachedOption in cachedOptions)
            {
                if (!HasRecipeTraitAccess(byPlayer, cachedOption.RequiredTraits))
                {
                    foreach (string trait in cachedOption.RequiredTraits)
                    {
                        deniedTraits.Add(trait);
                    }

                    continue;
                }

                options.Add(CloneChoiceEntry(cachedOption));
            }

            missingTraits = deniedTraits.ToArray();
            LogDebug($"build browser options summary profile={profile.ProfileCode} cached={cachedOptions.Count} built={options.Count} skippedTraits={deniedTraits.Count}");
            return options;
        }

        private List<WorkstationChoiceEntry> GetOrBuildBrowserOptions(ICoreClientAPI capi, WorkstationProfileDefinition profile)
        {
            string cacheKey = BuildBrowserOptionCacheKey(profile, null);
            if (browserOptionCache.TryGetValue(cacheKey, out List<WorkstationChoiceEntry>? cached))
            {
                LogDebug($"browser option cache hit profile={profile.ProfileCode} key={cacheKey} count={cached.Count}");
                return cached;
            }

            List<WorkstationChoiceEntry> built = BuildBrowserOptionsUncached(capi, profile, null);
            browserOptionCache[cacheKey] = built;
            LogDebug($"browser option cache miss profile={profile.ProfileCode} key={cacheKey} count={built.Count}");
            return built;
        }

        private List<WorkstationChoiceEntry> BuildBrowserOptionsUncached(ICoreClientAPI capi, WorkstationProfileDefinition profile, string? workstationMetal)
        {
            List<WorkstationChoiceEntry> options = new List<WorkstationChoiceEntry>(profile.Outputs.Count);
            Dictionary<string, WorkstationChoiceEntry> groupedOptions = new(StringComparer.Ordinal);
            int skippedForWorkstation = 0;
            int skippedForResolvedVariants = 0;
            int skippedForOutputStack = 0;

            foreach (WorkstationOutputDefinition definition in profile.Outputs)
            {
                List<ResolvedRecipeVariant> resolvedVariants = EnumerateResolvedRecipeVariants(capi.World, profile, definition, null, null, 0f).ToList();
                if (resolvedVariants.Count == 0)
                {
                    skippedForResolvedVariants++;
                    continue;
                }

                foreach (ResolvedRecipeVariant resolvedVariant in resolvedVariants)
                {
                    ItemStack? outputStack = CreateStack(
                        resolvedVariant.OutputCollectible,
                        definition.Quantity,
                        WorkstationProfiles.BuildOutputAttributes(definition, resolvedVariant.Assignment));
                    if (outputStack == null)
                    {
                        skippedForOutputStack++;
                        continue;
                    }

                    options.Add(new WorkstationChoiceEntry
                    {
                        Stack = outputStack,
                        IngredientPreviewStacks = ResolveIngredientPreviewStacks(capi, resolvedVariant),
                        RequiredTraits = definition.RequiredTraits,
                        RequiredTraitText = BuildRecipeRequiredTraitText(definition.RequiredTraits),
                        SelectionCandidates = new List<WorkstationSelectionCandidate>
                        {
                            new()
                            {
                                OutputId = definition.OutputId,
                                AssignmentToken = SerializeAssignment(resolvedVariant.Assignment)
                            }
                        },
                        Description = ResolveRecipeDescription(definition, resolvedVariant.Assignment),
                        RecipeName = ResolveRecipeName(definition, resolvedVariant.Assignment)
                    });

                    WorkstationChoiceEntry addedOption = options[^1];
                    string groupKey = GetRecipeGroupKey(definition, resolvedVariant);
                    if (!groupedOptions.TryGetValue(groupKey, out WorkstationChoiceEntry? existingOption))
                    {
                        groupedOptions[groupKey] = addedOption;
                        continue;
                    }

                    options.RemoveAt(options.Count - 1);
                    MergeGroupedChoiceEntry(existingOption, addedOption);
                }
            }

            LogDebug($"build browser cache summary profile={profile.ProfileCode} outputs={profile.Outputs.Count} built={options.Count} skippedWorkstation={skippedForWorkstation} skippedVariants={skippedForResolvedVariants} skippedOutput={skippedForOutputStack}");
            return options;
        }

        private static string BuildBrowserOptionCacheKey(WorkstationProfileDefinition profile, string? workstationMetal)
        {
            return $"{profile.ProfileCode}|{workstationMetal ?? string.Empty}";
        }

        private static WorkstationChoiceEntry CloneChoiceEntry(WorkstationChoiceEntry source)
        {
            ItemStack[] clonedIngredients = new ItemStack[source.IngredientPreviewStacks.Length];
            for (int i = 0; i < source.IngredientPreviewStacks.Length; i++)
            {
                clonedIngredients[i] = source.IngredientPreviewStacks[i].Clone();
            }

            return new WorkstationChoiceEntry
            {
                Stack = source.Stack.Clone(),
                IngredientPreviewStacks = clonedIngredients,
                RequiredTraits = source.RequiredTraits,
                RequiredTraitText = source.RequiredTraitText,
                SelectionCandidates = source.SelectionCandidates,
                Description = source.Description,
                RecipeName = source.RecipeName
            };
        }

        private static string GetRecipeGroupKey(WorkstationOutputDefinition definition, ResolvedRecipeVariant resolvedVariant)
        {
            string groupToken = string.IsNullOrWhiteSpace(definition.RecipeGroup)
                ? "_default"
                : definition.RecipeGroup!;
            string traitKey = definition.RequiredTraits.Count == 0
                ? "_notrait"
                : string.Join("|", definition.RequiredTraits.OrderBy(value => value, StringComparer.Ordinal));

            string outputAttributesKey = SerializeOutputAttributes(
                WorkstationProfiles.BuildOutputAttributes(definition, resolvedVariant.Assignment));

            return $"{groupToken}|{traitKey}|{definition.OutputType}|{resolvedVariant.OutputPath}|{definition.Quantity}|{outputAttributesKey}";
        }

        private void MergeGroupedChoiceEntry(WorkstationChoiceEntry existing, WorkstationChoiceEntry incoming)
        {
            foreach (WorkstationSelectionCandidate candidate in incoming.SelectionCandidates)
            {
                bool alreadyPresent = false;
                foreach (WorkstationSelectionCandidate existingCandidate in existing.SelectionCandidates)
                {
                    if (existingCandidate.OutputId == candidate.OutputId
                        && string.Equals(existingCandidate.AssignmentToken, candidate.AssignmentToken, StringComparison.Ordinal))
                    {
                        alreadyPresent = true;
                        break;
                    }
                }

                if (!alreadyPresent)
                {
                    existing.SelectionCandidates.Add(candidate);
                }
            }

            existing.RequiredTraitText = JoinRequiredTraitTexts(existing.RequiredTraitText, incoming.RequiredTraitText);

            if (TryMergeSingleIngredientPreviewLabel(existing, incoming))
            {
                return;
            }
        }

        private bool TryMergeSingleIngredientPreviewLabel(WorkstationChoiceEntry existing, WorkstationChoiceEntry incoming)
        {
            if (existing.IngredientPreviewStacks.Length != 1 || incoming.IngredientPreviewStacks.Length != 1)
            {
                return false;
            }

            ItemStack existingStack = existing.IngredientPreviewStacks[0];
            ItemStack incomingStack = incoming.IngredientPreviewStacks[0];
            if (existingStack.StackSize != incomingStack.StackSize)
            {
                return false;
            }

            string existingName = GetPreviewIngredientDisplayName(existingStack);
            string incomingName = GetPreviewIngredientDisplayName(incomingStack);
            if (string.IsNullOrWhiteSpace(existingName) || string.IsNullOrWhiteSpace(incomingName) || string.Equals(existingName, incomingName, StringComparison.Ordinal))
            {
                return false;
            }

            List<string> names = new();
            AddPreviewIngredientName(names, existingName);
            AddPreviewIngredientName(names, incomingName);

            existingStack.Attributes ??= new TreeAttribute();
            existingStack.Attributes.SetString(PreviewIngredientLabelKey, JoinIngredientAlternativeNames(names));
            return true;
        }

        private static string GetPreviewIngredientDisplayName(ItemStack stack)
        {
            if (stack.Attributes != null)
            {
                string? label = stack.Attributes.GetString(PreviewIngredientLabelKey, null);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    return label;
                }
            }

            return stack.GetName().ToLowerInvariant();
        }

        private static void AddPreviewIngredientName(List<string> names, string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return;
            }

            string[] segments = rawValue.Split(new[] { ", or ", " or ", ", " }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                string trimmed = segment.Trim();
                if (!names.Contains(trimmed, StringComparer.Ordinal))
                {
                    names.Add(trimmed);
                }
            }
        }

        private static string CreateSelectionToken(WorkstationChoiceEntry choice)
        {
            if (choice.SelectionCandidates.Count <= 1)
            {
                return choice.SelectionCandidates[0].AssignmentToken;
            }

            return GroupSelectionTokenPrefix + string.Join("~", choice.SelectionCandidates.Select(candidate =>
                $"{candidate.OutputId}@{Uri.EscapeDataString(candidate.AssignmentToken)}"));
        }

        private static bool TryParseGroupedSelectionToken(string token, out List<WorkstationSelectionCandidate> candidates)
        {
            candidates = new List<WorkstationSelectionCandidate>();
            if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(GroupSelectionTokenPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            string payload = token[GroupSelectionTokenPrefix.Length..];
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            string[] entries = payload.Split('~', StringSplitOptions.RemoveEmptyEntries);
            foreach (string entry in entries)
            {
                int separatorIndex = entry.IndexOf('@');
                if (separatorIndex <= 0)
                {
                    return false;
                }

                string outputIdToken = entry[..separatorIndex];
                if (!int.TryParse(outputIdToken, out int outputId))
                {
                    return false;
                }

                string assignmentToken = Uri.UnescapeDataString(entry[(separatorIndex + 1)..]);
                candidates.Add(new WorkstationSelectionCandidate
                {
                    OutputId = outputId,
                    AssignmentToken = assignmentToken
                });
            }

            return candidates.Count > 0;
        }

        private void BuildDialogInputState(IPlayer byPlayer, out ItemSlot? activeSlot, out ItemStack? heldStack, out float inputTemp)
        {
            activeSlot = byPlayer.InventoryManager.ActiveHotbarSlot;
            heldStack = activeSlot?.Itemstack;
            inputTemp = 0f;

            if (heldStack != null)
            {
                inputTemp = heldStack.Collectible.GetTemperature(Api.World, heldStack);
            }
        }

        private bool TryGetProfile(out WorkstationProfileDefinition profile)
        {
            string? profileCode = Block?.Attributes?["workstationProfile"].AsString(null);
            if (string.IsNullOrWhiteSpace(profileCode))
            {
                LogDebug("workstation profile missing: block attribute 'workstationProfile' is not set");
                profile = null!;
                return false;
            }

            bool found = WorkstationProfiles.TryGetProfile(profileCode!, out profile!);
            LogDebug($"try get profile code={profileCode} found={found} outputCount={(found ? profile.Outputs.Count : -1)}");
            if (!found)
            {
                LogDebug($"workstation profile missing: {profileCode}");
            }

            return found;
        }

        private static string CreateSelectionPacket(int outputId, string assignmentToken, bool craftToStack)
        {
            return $"{outputId}|{assignmentToken}|{(craftToStack ? "1" : "0")}";
        }

        private static bool TryParseSelectionPacket(string payload, out int outputId, out string variant, out bool craftToStack)
        {
            outputId = -1;
            variant = string.Empty;
            craftToStack = false;

            if (string.IsNullOrEmpty(payload))
            {
                return false;
            }

            int separatorIndex = payload.IndexOf('|');
            if (separatorIndex <= 0)
            {
                return false;
            }

            if (!int.TryParse(payload.Substring(0, separatorIndex), out outputId))
            {
                return false;
            }

            int secondSeparatorIndex = payload.IndexOf('|', separatorIndex + 1);
            if (secondSeparatorIndex < 0)
            {
                variant = separatorIndex == payload.Length - 1 ? string.Empty : payload.Substring(separatorIndex + 1);
                return true;
            }

            variant = secondSeparatorIndex == separatorIndex + 1 ? string.Empty : payload.Substring(separatorIndex + 1, secondSeparatorIndex - separatorIndex - 1);
            string repeatToken = secondSeparatorIndex == payload.Length - 1 ? string.Empty : payload[(secondSeparatorIndex + 1)..];
            craftToStack = repeatToken == "1" || repeatToken.Equals("true", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private static int GetTargetCraftRuns(WorkstationOutputDefinition definition, CollectibleObject outputCollectible, bool craftToStack)
        {
            if (!craftToStack)
            {
                return 1;
            }

            int outputPerCraft = Math.Max(1, definition.Quantity);
            int maxStack = Math.Max(outputPerCraft, outputCollectible.MaxStackSize);
            int runs = maxStack / outputPerCraft;
            return Math.Max(1, runs);
        }

        private bool TryCraftRuns(
            IPlayer byPlayer,
            WorkstationProfileDefinition profile,
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> assignment,
            int targetCraftRuns,
            out int completedRuns,
            out string? failureMessage)
        {
            completedRuns = 0;
            failureMessage = null;

            int attempts = Math.Max(1, targetCraftRuns);
            for (int i = 0; i < attempts; i++)
            {
                if (!TryConsumeRecipeInputs(byPlayer, profile, definition, assignment, out string? runFailureMessage))
                {
                    if (completedRuns == 0)
                    {
                        failureMessage = runFailureMessage;
                        return false;
                    }

                    break;
                }

                completedRuns++;
            }

            return completedRuns > 0;
        }

        private bool TryConsumeRecipeInputs(IPlayer byPlayer, WorkstationProfileDefinition profile, WorkstationOutputDefinition definition, IReadOnlyDictionary<string, string> assignment, out string? failureMessage)
        {
            failureMessage = null;

            List<ResolvedInputRequirement> requirements = ResolveInputRequirements(definition, assignment);
            if (requirements.Count == 0)
            {
                failureMessage = "Recipe inputs could not be resolved";
                return false;
            }

            List<PlannedSlotConsumption> plannedConsumptions = new List<PlannedSlotConsumption>();
            List<string> missingRequirements = new List<string>();

            foreach (ResolvedInputRequirement requirement in requirements)
            {
                int remaining = requirement.Quantity;

                foreach (ItemSlot slot in EnumeratePlayerStorageSlots(byPlayer))
                {
                    ItemStack? stack = slot.Itemstack;
                    if (stack == null || !RequirementMatches(requirement, stack))
                    {
                        continue;
                    }

                    int take = Math.Min(remaining, stack.StackSize);
                    if (take <= 0)
                    {
                        continue;
                    }

                    plannedConsumptions.Add(new PlannedSlotConsumption
                    {
                        Slot = slot,
                        Quantity = take
                    });
                    remaining -= take;
                    if (remaining <= 0)
                    {
                        break;
                    }
                }

                if (remaining > 0)
                {
                    string itemName = GetRequirementDisplayName(requirement);
                    missingRequirements.Add(requirement.Quantity > 1
                        ? $"{itemName} x{requirement.Quantity}"
                        : itemName);
                }
            }

            if (missingRequirements.Count > 0)
            {
                failureMessage = Lang.Get("specializedclasses:workstation-error-missing", string.Join(", ", missingRequirements));
                return false;
            }

            foreach (PlannedSlotConsumption plannedConsumption in plannedConsumptions)
            {
                plannedConsumption.Slot.TakeOut(plannedConsumption.Quantity);
                plannedConsumption.Slot.MarkDirty();
            }

            return true;
        }

        private List<ResolvedInputRequirement> ResolveInputRequirements(WorkstationOutputDefinition definition, IReadOnlyDictionary<string, string> assignment)
        {
            List<ResolvedInputRequirement> resolved = new List<ResolvedInputRequirement>(definition.Inputs.Count);

            foreach (WorkstationInputRequirementDefinition requirement in definition.Inputs)
            {
                List<AssetLocation> codes = new();
                List<AttributeBackedInputCandidate> attributeCandidates = new();
                HashSet<string> seenAttributeCandidates = new(StringComparer.Ordinal);

                foreach (string codeTemplate in requirement.CodeTemplates)
                {
                    string path = WorkstationProfiles.ReplacePlaceholders(codeTemplate, assignment);
                    if (path.Contains('*'))
                    {
                        foreach (AssetLocation wildcardCode in ExpandWildcardInputCodes(requirement.Type, path))
                        {
                            codes.Add(wildcardCode);
                        }

                        continue;
                    }

                    if (TryParseCode(path, out AssetLocation code))
                    {
                        codes.Add(code);
                    }

                    if (Api?.World != null
                        && WorkstationProfiles.TryResolveAttributeBackedTemplate(Api.World, requirement.Type, codeTemplate, assignment, out AssetLocation attributeCode, out IReadOnlyDictionary<string, string> attributeValues))
                    {
                        string key = $"{attributeCode}|{SerializeOutputAttributes(attributeValues)}";
                        if (seenAttributeCandidates.Add(key))
                        {
                            attributeCandidates.Add(new AttributeBackedInputCandidate
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

                resolved.Add(new ResolvedInputRequirement
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

        private IEnumerable<AssetLocation> ExpandWildcardInputCodes(string requirementType, string codePattern)
        {
            if (WorkstationProfiles.TryGetPreExpandedWildcardCodes(requirementType, codePattern, out IReadOnlyList<AssetLocation> preExpanded))
            {
                foreach (AssetLocation code in preExpanded)
                {
                    yield return code;
                }
                yield break;
            }

            // Fallback path for any pattern not covered by the pre-expanded cache
            // (e.g. code called before Initialize, or a pattern added after startup).
            if (Api?.World == null || !TryParseCode(codePattern, out AssetLocation searchCode))
            {
                yield break;
            }

            IEnumerable<CollectibleObject> matches = string.Equals(requirementType, "block", StringComparison.Ordinal)
                ? Api.World.SearchBlocks(searchCode)
                : Api.World.SearchItems(searchCode);

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

        private IEnumerable<ItemSlot> EnumeratePlayerStorageSlots(IPlayer byPlayer)
        {
            foreach (IInventory inventory in byPlayer.InventoryManager.InventoriesOrdered)
            {
                if (!string.Equals(inventory.ClassName, GlobalConstants.hotBarInvClassName, StringComparison.Ordinal)
                    && !string.Equals(inventory.ClassName, GlobalConstants.backpackInvClassName, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (ItemSlot slot in inventory)
                {
                    yield return slot;
                }
            }
        }

        private static bool RequirementMatches(ResolvedInputRequirement requirement, ItemStack stack)
        {
            CollectibleObject? collectible = stack.Collectible;
            AssetLocation? stackCode = collectible?.Code;
            if (collectible == null || stackCode == null)
            {
                return false;
            }

            if (string.Equals(requirement.Type, "block", StringComparison.Ordinal))
            {
                if (collectible is not Vintagestory.API.Common.Block)
                {
                    return false;
                }
            }
            else if (collectible is not Vintagestory.API.Common.Item)
            {
                return false;
            }

            foreach (AssetLocation code in requirement.Codes)
            {
                if (stackCode.Equals(code))
                {
                    return true;
                }
            }

            foreach (AttributeBackedInputCandidate candidate in requirement.AttributeCandidates)
            {
                if (stackCode.Equals(candidate.Code) && WorkstationProfiles.StackMatchesPlaceholderAttributes(stack, candidate.Attributes))
                {
                    return true;
                }
            }

            return false;
        }

        private string GetRequirementDisplayName(ResolvedInputRequirement requirement)
        {
            if (!string.IsNullOrWhiteSpace(requirement.Label))
            {
                return requirement.Label!;
            }

            CollectibleObject? collectible = null;
            AssetLocation? code = requirement.Codes.FirstOrDefault();
            IReadOnlyDictionary<string, string>? attributes = null;

            if (code == null && requirement.AttributeCandidates.Count > 0)
            {
                AttributeBackedInputCandidate candidate = requirement.AttributeCandidates[0];
                code = candidate.Code;
                attributes = candidate.Attributes;
            }

            if (code == null)
            {
                return "unknown";
            }

            if (string.Equals(requirement.Type, "block", StringComparison.Ordinal))
            {
                collectible = Api.World.GetBlock(code);
            }
            else
            {
                collectible = Api.World.GetItem(code);
            }

            if (collectible != null)
            {
                ItemStack? stack = CreateStack(collectible, 1, attributes);
                if (stack != null)
                {
                    return stack.GetName().ToLowerInvariant();
                }
            }

            return code.Path;
        }

        private ItemStack[] ResolveIngredientPreviewStacks(ICoreClientAPI capi, ResolvedRecipeVariant resolvedVariant)
        {
            List<ItemStack> result = new List<ItemStack>(resolvedVariant.Inputs.Count);

            foreach (ResolvedInputRequirement requirement in resolvedVariant.Inputs)
            {
                CollectibleObject? collectible = null;
                List<string>? alternativeNames = null;
                IReadOnlyDictionary<string, string>? previewAttributes = null;

                // when the recipe file already provides a label (e.g. "any sand"),
                // skip iterating every matching code for display-name collection;
                // we only need one resolvable collectible for the preview icon
                bool hasLabel = !string.IsNullOrWhiteSpace(requirement.Label);

                foreach (AssetLocation code in requirement.Codes)
                {
                    if (!TryResolveCollectible(capi.World, requirement.Type, code.ToString(), out CollectibleObject? resolvedCollectible))
                    {
                        continue;
                    }

                    collectible ??= resolvedCollectible;

                    if (hasLabel)
                    {
                        break;
                    }

                    if (resolvedCollectible != null)
                    {
                        string? resolvedName = CreateStack(resolvedCollectible, 1)?.GetName()?.ToLowerInvariant();
                        if (!string.IsNullOrWhiteSpace(resolvedName))
                        {
                            alternativeNames ??= new List<string>();
                            if (!alternativeNames.Contains(resolvedName, StringComparer.Ordinal))
                            {
                                alternativeNames.Add(resolvedName);
                            }
                        }
                    }
                }

                foreach (AttributeBackedInputCandidate candidate in requirement.AttributeCandidates)
                {
                    if (!TryResolveCollectible(capi.World, requirement.Type, candidate.Code.ToString(), out CollectibleObject? resolvedCollectible))
                    {
                        continue;
                    }

                    collectible ??= resolvedCollectible;
                    previewAttributes ??= candidate.Attributes;

                    if (hasLabel)
                    {
                        break;
                    }

                    string? resolvedName = CreateStack(resolvedCollectible, 1, candidate.Attributes)?.GetName()?.ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(resolvedName))
                    {
                        alternativeNames ??= new List<string>();
                        if (!alternativeNames.Contains(resolvedName, StringComparer.Ordinal))
                        {
                            alternativeNames.Add(resolvedName);
                        }
                    }
                }

                if (collectible == null)
                {
                    continue;
                }

                ItemStack? stack = CreateStack(collectible, requirement.Quantity, previewAttributes);
                if (stack != null)
                {
                    stack.Attributes ??= new TreeAttribute();
                    string? ingredientLabel = requirement.Label;
                    if (string.IsNullOrWhiteSpace(ingredientLabel) && alternativeNames != null && alternativeNames.Count > 1)
                    {
                        ingredientLabel = JoinIngredientAlternativeNames(alternativeNames);
                    }

                    if (!string.IsNullOrWhiteSpace(ingredientLabel))
                    {
                        stack.Attributes.SetString(PreviewIngredientLabelKey, ingredientLabel);
                    }
                    result.Add(stack);
                }
            }

            return result.ToArray();
        }

        private static string JoinIngredientAlternativeNames(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0)
            {
                return string.Empty;
            }

            if (names.Count == 1)
            {
                return names[0];
            }

            if (names.Count == 2)
            {
                return $"{names[0]} or {names[1]}";
            }

            return $"{string.Join(", ", names.Take(names.Count - 1))}, or {names[names.Count - 1]}";
        }

        private static string? BuildRecipeRequiredTraitText(IReadOnlyCollection<string> requiredTraits)
        {
            if (requiredTraits.Count == 0)
            {
                return null;
            }

            return $"{FormatRequiredTraitList(requiredTraits)} trait";
        }

        private static string? JoinRequiredTraitTexts(string? existing, string? incoming)
        {
            bool hasExisting = !string.IsNullOrWhiteSpace(existing);
            bool hasIncoming = !string.IsNullOrWhiteSpace(incoming);

            if (!hasExisting)
            {
                return hasIncoming ? incoming : null;
            }

            if (!hasIncoming || string.Equals(existing, incoming, StringComparison.Ordinal))
            {
                return existing;
            }

            return $"{existing} or {incoming}";
        }

        private bool TryResolveCollectible(string collectibleType, string codeOrPath, out CollectibleObject? collectible)
        {
            return TryResolveCollectible(Api.World, collectibleType, codeOrPath, out collectible);
        }

        private static bool TryResolveCollectible(IWorldAccessor world, string collectibleType, string codeOrPath, out CollectibleObject? collectible)
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

        private static ItemStack? CreateStack(CollectibleObject? collectible, int quantity, IReadOnlyDictionary<string, string>? attributes = null)
        {
            ItemStack? stack = null;
            if (collectible is Vintagestory.API.Common.Item item)
            {
                stack = new ItemStack(item, quantity);
            }
            else if (collectible is Vintagestory.API.Common.Block block)
            {
                stack = new ItemStack(block, quantity);
            }

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

        private IEnumerable<ResolvedRecipeVariant> EnumerateResolvedRecipeVariants(
            IWorldAccessor world,
            WorkstationProfileDefinition profile,
            WorkstationOutputDefinition definition,
            ItemStack? heldStack,
            string? workstationMetal,
            float inputTemp)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (Dictionary<string, string> seed in EnumerateSeedAssignments(profile, definition, heldStack))
            {
                foreach (Dictionary<string, string> assignment in ExpandAssignments(definition, seed, profile.MenuMode == WorkstationMenuMode.Browser, profile, profile.ProfileCode))
                {
                    if (!AssignmentSatisfiesVariantFilters(definition, assignment))
                    {
                        continue;
                    }

                    if (!TryResolveRecipeVariant(world, definition, assignment, out ResolvedRecipeVariant? resolved))
                    {
                        continue;
                    }

                    if (profile.MenuMode == WorkstationMenuMode.HeldItem && heldStack != null)
                    {
                        bool heldMatchesResolvedInput = false;
                        foreach (ResolvedInputRequirement input in resolved!.Inputs)
                        {
                            if (RequirementMatches(input, heldStack))
                            {
                                heldMatchesResolvedInput = true;
                                break;
                            }
                        }

                        if (!heldMatchesResolvedInput)
                        {
                            continue;
                        }
                    }

                    string key = SerializeAssignment(assignment);
                    if (seen.Add(key))
                    {
                        yield return resolved!;
                    }
                }
            }
        }

        private IEnumerable<Dictionary<string, string>> EnumerateSeedAssignments(WorkstationProfileDefinition profile, WorkstationOutputDefinition definition, ItemStack? heldStack)
        {
            if (profile.MenuMode != WorkstationMenuMode.HeldItem)
            {
                yield return new Dictionary<string, string>(StringComparer.Ordinal);
                yield break;
            }

            if (heldStack?.Collectible?.Code == null)
            {
                yield break;
            }

            bool matchedAny = false;

            foreach (WorkstationInputRequirementDefinition input in definition.Inputs)
            {
                if (!InputTypeMatchesStack(input.Type, heldStack))
                {
                    continue;
                }

                foreach (string codeTemplate in input.CodeTemplates)
                {
                    if (!WorkstationProfiles.TryMatchTemplateToStack(codeTemplate, heldStack, out List<Dictionary<string, string>> assignments))
                    {
                        continue;
                    }

                    foreach (Dictionary<string, string> assignment in assignments)
                    {
                        matchedAny = true;
                        yield return assignment;
                    }
                }
            }

            if (!matchedAny && !WorkstationProfiles.RequiresVariantSubstitution(definition))
            {
                // Non-variant held-item recipes can still match exact codes during resolved-input validation.
                yield return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private IEnumerable<Dictionary<string, string>> ExpandAssignments(
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> seed,
            bool browserMode,
            WorkstationProfileDefinition profile,
            string profileCode)
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

            Dictionary<string, IReadOnlyList<string>> variantValuesByKey = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (string missingKey in missingKeys)
            {
                if (definition.AllowedVariants.TryGetValue(missingKey, out IReadOnlyList<string>? values) && values.Count > 0)
                {
                    variantValuesByKey[missingKey] = values;
                    continue;
                }

                if (!browserMode || !TryInferVariantValues(definition, seed, missingKey, out IReadOnlyList<string> inferredValues))
                {
                    if (browserMode)
                    {
                        WarnRecipeSchemaOnce($"missing:{profileCode}:{definition.OutputId}:{missingKey}", $"Workstations: recipe option={definition.OutputId} profile={profileCode} uses placeholder '{{{missingKey}}}' but allowedVariants.{missingKey} is missing and no matching collectibles could infer it");
                    }

                    yield break;
                }

                variantValuesByKey[missingKey] = inferredValues;
            }

            foreach (string extraKey in definition.AllowedVariants.Keys.Where(key => !definition.PlaceholderKeys.Contains(key)))
            {
                WarnRecipeSchemaOnce($"extra:{profileCode}:{definition.OutputId}:{extraKey}", $"Workstations: recipe option={definition.OutputId} profile={profileCode} has allowedVariants.{extraKey} but no '{{{extraKey}}}' placeholder");
            }

            foreach (string extraKey in definition.SkipVariants.Keys.Where(key => !definition.PlaceholderKeys.Contains(key)))
            {
                WarnRecipeSchemaOnce($"extra-skip:{profileCode}:{definition.OutputId}:{extraKey}", $"Workstations: recipe option={definition.OutputId} profile={profileCode} has skipVariants.{extraKey} but no '{{{extraKey}}}' placeholder");
            }

            Dictionary<string, string> working = new(seed, StringComparer.Ordinal);
            foreach (Dictionary<string, string> combo in ExpandAssignmentsRecursive(missingKeys, variantValuesByKey, 0, working))
            {
                yield return combo;
            }
        }

        private IEnumerable<Dictionary<string, string>> ExpandAssignmentsRecursive(
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

        private bool TryInferVariantValues(
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> seed,
            string missingKey,
            out IReadOnlyList<string> values)
        {
            values = System.Array.Empty<string>();

            if (Api?.World == null)
            {
                return false;
            }

            HashSet<string> result = new(StringComparer.Ordinal);

            CollectInferredVariantValues(definition.OutputType, definition.CodeTemplate, definition, seed, missingKey, result);

            foreach (WorkstationInputRequirementDefinition input in definition.Inputs)
            {
                foreach (string codeTemplate in input.CodeTemplates)
                {
                    CollectInferredVariantValues(input.Type, codeTemplate, definition, seed, missingKey, result);
                }
            }

            if (result.Count == 0)
            {
                return false;
            }

            values = result
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            return true;
        }

        private void CollectInferredVariantValues(
            string collectibleType,
            string codeTemplate,
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> seed,
            string missingKey,
            ISet<string> result)
        {
            if (Api?.World == null || !WorkstationProfiles.ExtractPlaceholders(codeTemplate).Contains(missingKey))
            {
                return;
            }

            if (string.Equals(collectibleType, "block", StringComparison.Ordinal))
            {
                foreach (Block? block in Api.World.Blocks)
                {
                    if (block?.Code == null)
                    {
                        continue;
                    }

                    CollectMatchingVariantValues(codeTemplate, block.Code.ToString(), definition, seed, missingKey, result);
                }

                return;
            }

            foreach (Item? item in Api.World.Items)
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

        private bool TryResolveRecipeVariant(
            IWorldAccessor world,
            WorkstationOutputDefinition definition,
            IReadOnlyDictionary<string, string> assignment,
            out ResolvedRecipeVariant? resolvedVariant)
        {
            resolvedVariant = null;

            string outputPath = WorkstationProfiles.BuildOutputPath(definition, assignment);
            if (!TryResolveCollectible(world, definition.OutputType, outputPath, out CollectibleObject? outputCollectible))
            {
                LogDebug($"resolve variant failed output profileType={definition.OutputType} recipeCode={definition.RecipeCode} outputPath={outputPath} assignment={SerializeAssignment(assignment)}");
                return false;
            }

            List<ResolvedInputRequirement> inputs = ResolveInputRequirements(definition, assignment);
            if (inputs.Count == 0)
            {
                LogDebug($"resolve variant failed no inputs recipeCode={definition.RecipeCode} assignment={SerializeAssignment(assignment)}");
                return false;
            }

            foreach (ResolvedInputRequirement input in inputs)
            {
                bool resolvedAny = false;
                foreach (AssetLocation code in input.Codes)
                {
                    if (TryResolveCollectible(world, input.Type, code.ToString(), out _))
                    {
                        resolvedAny = true;
                        break;
                    }
                }

                if (!resolvedAny)
                {
                    foreach (AttributeBackedInputCandidate candidate in input.AttributeCandidates)
                    {
                        if (TryResolveCollectible(world, input.Type, candidate.Code.ToString(), out _))
                        {
                            resolvedAny = true;
                            break;
                        }
                    }
                }

                if (!resolvedAny)
                {
                    LogDebug($"resolve variant failed input recipeCode={definition.RecipeCode} inputType={input.Type} inputCodes={string.Join(",", input.Codes.Select(code => code.ToString()))} assignment={SerializeAssignment(assignment)}");
                    return false;
                }
            }

            resolvedVariant = new ResolvedRecipeVariant
            {
                Assignment = new Dictionary<string, string>(assignment, StringComparer.Ordinal),
                Inputs = inputs,
                OutputCollectible = outputCollectible!,
                OutputPath = outputPath
            };

            return true;
        }

        private static bool AssignmentSatisfiesVariantFilters(WorkstationOutputDefinition definition, IReadOnlyDictionary<string, string> assignment)
        {
            foreach ((string key, IReadOnlyList<string> values) in definition.AllowedVariants)
            {
                if (!assignment.TryGetValue(key, out string? value))
                {
                    continue;
                }

                if (!values.Contains(value, StringComparer.Ordinal))
                {
                    return false;
                }
            }

            foreach ((string key, IReadOnlyList<string> values) in definition.SkipVariants)
            {
                if (!assignment.TryGetValue(key, out string? value))
                {
                    continue;
                }

                if (values.Contains(value, StringComparer.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static string GetNoRecipesForItemMessage(WorkstationProfileDefinition profile)
        {
            return ResolveProfileSpecificLang(NoRecipesForItemErrorLangKey, profile.ProfileCode);
        }

        private bool TryGetProfileTraitError(IPlayer byPlayer, WorkstationProfileDefinition profile, out IReadOnlyList<string> missingTraits)
        {
            HashSet<string> deniedTraits = new(StringComparer.OrdinalIgnoreCase);
            bool foundEligibleOutput = false;

            foreach (WorkstationOutputDefinition definition in profile.Outputs)
            {
                foundEligibleOutput = true;
                if (definition.RequiredTraits.Count == 0)
                {
                    missingTraits = Array.Empty<string>();
                    return false;
                }

                if (HasRecipeTraitAccess(byPlayer, definition))
                {
                    missingTraits = Array.Empty<string>();
                    return false;
                }

                foreach (string trait in definition.RequiredTraits)
                {
                    deniedTraits.Add(trait);
                }
            }

            missingTraits = deniedTraits.ToArray();
            return foundEligibleOutput && deniedTraits.Count > 0;
        }

        private static string ResolveProfileSpecificLang(string baseKey, string profileCode)
        {
            string profileSpecificKey = $"{baseKey}-{profileCode}";
            string profileSpecificMessage = Lang.Get(profileSpecificKey);
            if (!string.Equals(profileSpecificMessage, profileSpecificKey, StringComparison.Ordinal))
            {
                return profileSpecificMessage;
            }

            return Lang.Get(baseKey);
        }

        private bool HasRecipeTraitAccess(IPlayer byPlayer, WorkstationOutputDefinition definition)
        {
            return HasRecipeTraitAccess(byPlayer, definition.RequiredTraits);
        }

        private bool HasRecipeTraitAccess(IPlayer byPlayer, IReadOnlyCollection<string> requiredTraits)
        {
            if (requiredTraits.Count == 0)
            {
                return true;
            }

            if (Api == null)
            {
                return false;
            }

            foreach (string trait in requiredTraits)
            {
                if (WorkstationLogic.PlayerHasTrait(Api, byPlayer, trait))
                {
                    return true;
                }
            }

            return false;
        }

        private void ShowRecipeTraitError(IPlayer byPlayer, IReadOnlyCollection<string> requiredTraits)
        {
            string message = Lang.Get("specializedclasses:workstation-error-requirestrait", FormatRequiredTraitList(requiredTraits));
            WorkstationLogic.ShowIngameError(Api.World, byPlayer, TraitRequiredErrorCode, message);
        }

        private static string FormatRequiredTraitList(IReadOnlyCollection<string> requiredTraits)
        {
            if (requiredTraits.Count == 0)
            {
                return string.Empty;
            }

            string[] traitNames = requiredTraits
                .Select(WorkstationLogic.GetTraitDisplayName)
                .ToArray();

            return JoinIngredientAlternativeNames(traitNames);
        }

        private static string? ResolveRecipeDescription(WorkstationOutputDefinition definition, IReadOnlyDictionary<string, string> assignment)
        {
            return ResolveLocalizedRecipeText(definition.Description, assignment);
        }

        private static string? ResolveRecipeName(WorkstationOutputDefinition definition, IReadOnlyDictionary<string, string> assignment)
        {
            return ResolveLocalizedRecipeText(definition.RecipeName, assignment);
        }

        private static string? ResolveLocalizedRecipeText(string? rawTextOrLangCode, IReadOnlyDictionary<string, string> assignment)
        {
            if (string.IsNullOrWhiteSpace(rawTextOrLangCode))
            {
                return null;
            }

            string text = TryResolveLangValue(rawTextOrLangCode);
            return WorkstationProfiles.ReplacePlaceholders(text, assignment);
        }

        private static string TryResolveLangValue(string rawTextOrLangCode)
        {
            string direct = Lang.Get(rawTextOrLangCode);
            if (!string.Equals(direct, rawTextOrLangCode, StringComparison.Ordinal))
            {
                return direct;
            }

            // support shorthand custom keys like "description-leatherworkingstation-backpack"
            // by probing the mod-prefixed form as well.
            if (!rawTextOrLangCode.Contains(' ') && !rawTextOrLangCode.Contains(':'))
            {
                string namespaced = $"specializedclasses:{rawTextOrLangCode}";
                string namespacedResolved = Lang.Get(namespaced);
                if (!string.Equals(namespacedResolved, namespaced, StringComparison.Ordinal))
                {
                    return namespacedResolved;
                }
            }

            return rawTextOrLangCode;
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

        private AssetLocation GetCraftSound()
        {
            string soundCode = Block?.Attributes?["workstationCraftSound"].AsString("game:sounds/block/anvil") ?? "game:sounds/block/anvil";
            try
            {
                return new AssetLocation(soundCode);
            }
            catch
            {
                return DefaultCraftSound;
            }
        }

        private void DisposeDialog()
        {
            dialog?.TryClose();
            dialog?.Dispose();
            dialog = null;
        }

        private void LogDebug(string message)
        {
            if (!DebugLogging)
            {
                return;
            }

            Api?.World?.Logger?.Notification($"Workstations: {message}");
        }

        private void WarnRecipeSchemaOnce(string key, string message)
        {
            if (!LoggedRecipeSchemaWarnings.Add(key))
            {
                return;
            }

            Api?.World?.Logger?.Warning(EscapeLoggerBraces(message));
        }

        private static string EscapeLoggerBraces(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message ?? string.Empty;
            }

            return message
                .Replace("{", "{{")
                .Replace("}", "}}");
        }

        private static bool InputTypeMatchesStack(string requirementType, ItemStack stack)
        {
            if (stack.Collectible == null)
            {
                return false;
            }

            if (string.Equals(requirementType, "block", StringComparison.Ordinal))
            {
                return stack.Collectible is Vintagestory.API.Common.Block;
            }

            return stack.Collectible is Vintagestory.API.Common.Item;
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

        private static string SerializeOutputAttributes(IReadOnlyDictionary<string, string> attributes)
        {
            return SerializeAssignment(attributes);
        }

        private static bool TryDeserializeAssignment(string token, out Dictionary<string, string> assignment)
        {
            assignment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            string[] pairs = token.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (string pair in pairs)
            {
                int separator = pair.IndexOf('=');
                if (separator <= 0 || separator >= pair.Length - 1)
                {
                    return false;
                }

                string key = pair[..separator].Trim();
                string value = pair[(separator + 1)..].Trim();
                if (key.Length == 0 || value.Length == 0)
                {
                    return false;
                }

                assignment[key] = value;
            }

            return true;
        }

        private sealed class WorkstationChoiceEntry
        {
            public int OutputId => SelectionCandidates.Count == 0 ? -1 : SelectionCandidates[0].OutputId;
            public required ItemStack Stack { get; init; }
            public required ItemStack[] IngredientPreviewStacks { get; init; }
            public required IReadOnlyList<string> RequiredTraits { get; init; }
            public string? RequiredTraitText { get; set; }
            public required List<WorkstationSelectionCandidate> SelectionCandidates { get; init; }
            public string? Description { get; init; }
            public string? RecipeName { get; init; }
        }

        private sealed class WorkstationSelectionCandidate
        {
            public required int OutputId { get; init; }
            public required string AssignmentToken { get; init; }
        }

        private sealed class ResolvedRecipeVariant
        {
            public required Dictionary<string, string> Assignment { get; init; }
            public required List<ResolvedInputRequirement> Inputs { get; init; }
            public required CollectibleObject OutputCollectible { get; init; }
            public required string OutputPath { get; init; }
        }

        private sealed class ResolvedInputRequirement
        {
            public required string Type { get; init; }
            public required IReadOnlyList<AssetLocation> Codes { get; init; }
            public required IReadOnlyList<AttributeBackedInputCandidate> AttributeCandidates { get; init; }
            public required int Quantity { get; init; }
            public string? Label { get; init; }
        }

        private sealed class AttributeBackedInputCandidate
        {
            public required AssetLocation Code { get; init; }
            public required IReadOnlyDictionary<string, string> Attributes { get; init; }
        }

        private sealed class PlannedSlotConsumption
        {
            public required ItemSlot Slot { get; init; }
            public required int Quantity { get; init; }
        }
    }

    public enum EnumWorkstationPacket
    {
        SelectOutput = 1001
    }
}



