using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace SpecializedClasses
{
    public class DropModificationBlockBehavior : BlockBehavior
    {
        private const string MODE_ADD = "add";
        private const string MODE_SELF = "self";

        // cache compiled regex patterns to avoid recompiling on every check
        private static readonly ConcurrentDictionary<string, Regex> PatternCache = new();

        private CachedDropRule[] cachedInterruptRules = Array.Empty<CachedDropRule>();
        private CachedDropRule[] cachedAddRules = Array.Empty<CachedDropRule>();

        public DropModificationBlockBehavior(Block block) : base(block)
        {
        }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);
            BuildRuleCache();
        }

        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer,
            ref float dropChanceMultiplier, ref EnumHandling handling)
        {
            if (world == null || (cachedInterruptRules.Length == 0 && cachedAddRules.Length == 0))
            {
                return base.GetDrops(world, pos, byPlayer, ref dropChanceMultiplier, ref handling);
            }

            string blockCode = world.BlockAccessor.GetBlock(pos)?.Code?.Path ?? string.Empty;
            string? activeToolCode = byPlayer?.InventoryManager.ActiveTool?.ToString()?.ToLowerInvariant();

            ItemStack[]? interruptResult = TryInterruptingModification(cachedInterruptRules, world, blockCode, byPlayer, activeToolCode);
            if (interruptResult != null)
            {
                handling = EnumHandling.PreventDefault;
                return interruptResult;
            }

            if (cachedAddRules.Length == 0)
            {
                return base.GetDrops(world, pos, byPlayer, ref dropChanceMultiplier, ref handling);
            }

            ItemStack[] baseDrops = base.GetDrops(world, pos, byPlayer, ref dropChanceMultiplier, ref handling);
            List<ItemStack> drops = new List<ItemStack>(baseDrops ?? Array.Empty<ItemStack>());

            foreach (CachedDropRule rule in cachedAddRules)
            {
                ApplyAdditiveMod(drops, rule, world, blockCode, byPlayer, activeToolCode);
            }

            return drops.ToArray();
        }

        private ItemStack[]? TryInterruptingModification(CachedDropRule[] rules, IWorldAccessor world, string blockCode, IPlayer? byPlayer, string? activeToolCode)
        {
            foreach (CachedDropRule rule in rules)
            {
                if (!PassesConditions(rule, blockCode, byPlayer, activeToolCode))
                {
                    continue;
                }

                if (rule.Mode == MODE_SELF)
                {
                    return new ItemStack[] { new ItemStack(block, 1) };
                }

                ItemStack[]? newDrops = BuildDropsFromRule(rule.Drops, world);
                if (newDrops != null && newDrops.Length > 0)
                {
                    return newDrops;
                }
            }

            return null;
        }

        private void ApplyAdditiveMod(List<ItemStack> drops, CachedDropRule rule, IWorldAccessor world, string blockCode, IPlayer? byPlayer, string? activeToolCode)
        {
            if (!PassesConditions(rule, blockCode, byPlayer, activeToolCode))
            {
                return;
            }

            ItemStack[]? newDrops = BuildDropsFromRule(rule.Drops, world);
            if (newDrops != null && newDrops.Length > 0)
            {
                drops.AddRange(newDrops);
            }
        }

        private bool PassesConditions(CachedDropRule rule, string blockCode, IPlayer? byPlayer, string? activeToolCode)
        {
            bool blockMatch = MatchesBlockPatterns(rule, blockCode);
            bool hasTools = HasRequiredTools(rule, byPlayer, activeToolCode);
            bool rollPass = RollSucceeds(rule, byPlayer);

            return blockMatch && hasTools && rollPass;
        }

        private bool MatchesBlockPatterns(CachedDropRule rule, string blockCode)
        {
            if (rule.RequiredBlocks.Length > 0)
            {
                bool matches = false;
                foreach (string? pattern in rule.RequiredBlocks)
                {
                    if (MatchesPattern(blockCode, pattern))
                    {
                        matches = true;
                        break;
                    }
                }
                if (!matches)
                {
                    return false;
                }
            }

            if (rule.ExcludedBlocks.Length > 0)
            {
                foreach (string? pattern in rule.ExcludedBlocks)
                {
                    if (MatchesPattern(blockCode, pattern))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool HasRequiredTools(CachedDropRule rule, IPlayer? byPlayer, string? activeToolCode)
        {
            if (byPlayer == null)
            {
                return true;
            }

            if (rule.RequiredTools.Length == 0)
            {
                return true;
            }

            if (string.IsNullOrEmpty(activeToolCode))
            {
                return false;
            }

            foreach (string? pattern in rule.RequiredTools)
            {
                if (!string.IsNullOrEmpty(pattern) && MatchesPattern(activeToolCode, pattern))
                {
                    return true;
                }
            }

            return false;
        }

        private bool RollSucceeds(CachedDropRule rule, IPlayer? byPlayer)
        {
            float chance = CalculateChance(rule.StatCode, byPlayer);
            return chance > 0f && byPlayer?.Entity?.World?.Rand?.NextDouble() < chance;
        }

        private float CalculateChance(string? statCode, IPlayer? byPlayer)
        {
            if (string.IsNullOrEmpty(statCode) || byPlayer == null)
            {
                return 0f;
            }

            float blended = byPlayer.Entity?.Stats?.GetBlended(statCode) ?? 1f;
            float bonus = blended - 1f;
            return Math.Clamp(bonus, 0f, 1f);
        }

        private static bool MatchesPattern(string input, string? pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return true;
            }

            if (pattern == "*")
            {
                return true;
            }

            if (!pattern.Contains("*"))
            {
                return input.Equals(pattern, StringComparison.OrdinalIgnoreCase);
            }

            Regex regex = PatternCache.GetOrAdd(pattern, key =>
            {
                string regexPattern = "^" + Regex.Escape(key).Replace("\\*", ".*") + "$";
                return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            });

            return regex.IsMatch(input ?? string.Empty);
        }

        private static ItemStack[]? BuildDropsFromRule(CachedDropEntry[] dropRules, IWorldAccessor world)
        {
            if (dropRules.Length == 0)
            {
                return null;
            }

            List<ItemStack> result = new List<ItemStack>(dropRules.Length);
            foreach (CachedDropEntry dropRule in dropRules)
            {
                try
                {
                    CollectibleObject? collectible = dropRule.IsBlock
                        ? world.GetBlock(dropRule.Code)
                        : world.GetItem(dropRule.Code);

                    if (collectible != null)
                    {
                        result.Add(new ItemStack(collectible, dropRule.Quantity));
                    }
                }
                catch (Exception)
                {
                }
            }

            return result.Count > 0 ? result.ToArray() : null;
        }

        private void BuildRuleCache()
        {
            JsonObject[]? dropMods = block?.Attributes?["dropModifications"]?.AsArray();
            if (dropMods == null || dropMods.Length == 0)
            {
                cachedInterruptRules = Array.Empty<CachedDropRule>();
                cachedAddRules = Array.Empty<CachedDropRule>();
                return;
            }

            List<CachedDropRule> interruptRules = new List<CachedDropRule>(dropMods.Length);
            List<CachedDropRule> addRules = new List<CachedDropRule>(dropMods.Length);

            foreach (JsonObject mod in dropMods)
            {
                CachedDropRule rule = ParseRule(mod);

                if (rule.Mode == MODE_ADD)
                {
                    addRules.Add(rule);
                }
                else
                {
                    // preserve existing behavior: any non-add mode is interrupting,
                    // and only MODE_SELF gets the self-drop shortcut.
                    interruptRules.Add(rule);
                }
            }

            cachedInterruptRules = interruptRules.ToArray();
            cachedAddRules = addRules.ToArray();
        }

        private static CachedDropRule ParseRule(JsonObject mod)
        {
            string mode = mod["mode"]?.AsString(MODE_ADD) ?? MODE_ADD;
            string? stat = mod["stat"]?.AsString();

            string?[] requiredBlocks = ParseStringArray(mod["requiredBlocks"]?.AsArray());
            string?[] excludedBlocks = ParseStringArray(mod["excludeBlocks"]?.AsArray());
            string?[] requiredTools = ParseStringArray(mod["requiredTools"]?.AsArray());
            CachedDropEntry[] drops = ParseDropEntries(mod["drops"]?.AsArray());

            return new CachedDropRule(mode, stat, requiredBlocks, excludedBlocks, requiredTools, drops);
        }

        private static string?[] ParseStringArray(JsonObject[]? array)
        {
            if (array == null || array.Length == 0)
            {
                return Array.Empty<string?>();
            }

            string?[] result = new string?[array.Length];
            for (int i = 0; i < array.Length; i++)
            {
                result[i] = array[i]?.AsString();
            }

            return result;
        }

        private static CachedDropEntry[] ParseDropEntries(JsonObject[]? dropsArray)
        {
            if (dropsArray == null || dropsArray.Length == 0)
            {
                return Array.Empty<CachedDropEntry>();
            }

            List<CachedDropEntry> result = new List<CachedDropEntry>(dropsArray.Length);
            foreach (JsonObject drop in dropsArray)
            {
                try
                {
                    string type = drop["type"]?.AsString("item") ?? "item";
                    string? code = drop["code"]?.AsString();
                    int quantity = drop["quantity"]?.AsInt(1) ?? 1;

                    if (string.IsNullOrEmpty(code))
                    {
                        continue;
                    }

                    result.Add(new CachedDropEntry(type == "block", new AssetLocation(code), quantity));
                }
                catch (Exception)
                {
                }
            }

            return result.ToArray();
        }

        private sealed class CachedDropRule
        {
            public CachedDropRule(string mode, string? statCode, string?[] requiredBlocks, string?[] excludedBlocks, string?[] requiredTools, CachedDropEntry[] drops)
            {
                Mode = mode;
                StatCode = statCode;
                RequiredBlocks = requiredBlocks;
                ExcludedBlocks = excludedBlocks;
                RequiredTools = requiredTools;
                Drops = drops;
            }

            public string Mode { get; }
            public string? StatCode { get; }
            public string?[] RequiredBlocks { get; }
            public string?[] ExcludedBlocks { get; }
            public string?[] RequiredTools { get; }
            public CachedDropEntry[] Drops { get; }
        }

        private sealed class CachedDropEntry
        {
            public CachedDropEntry(bool isBlock, AssetLocation code, int quantity)
            {
                IsBlock = isBlock;
                Code = code;
                Quantity = quantity;
            }

            public bool IsBlock { get; }
            public AssetLocation Code { get; }
            public int Quantity { get; }
        }
    }
}
