using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace SpecializedClasses.Workstations
{
    public class GuiDialogWorkstationRecipeSelector : GuiDialogGeneric
    {
        private const string PREVIEW_WORKABLE_MARKER_KEY = "specializedclasses:workableRequired";
        private const string PREVIEW_INGREDIENT_LABEL_KEY = "specializedclasses:ingredientLabel";

        private readonly BlockPos blockEntityPos;
        private readonly List<SkillItem> skillItems = new();
        private readonly List<SkillItem> displayedSkillItems = new();
        private readonly List<int> displayedOriginalIndices = new();
        private readonly Dictionary<int, ItemStack[]> ingredientPreviewByIndex = new();
        private readonly Dictionary<int, string?> requiredTraitTextByIndex = new();
        private readonly Dictionary<int, string?> customNameByIndex = new();
        private readonly Dictionary<int, string?> customDescriptionByIndex = new();
        private readonly Action<int, bool> onSelectedRecipe;
        private readonly Action onCancelSelect;

        private int prevSlotOver = -1;
        private bool didSelect;
        private bool pendingCraftToStack;

        public GuiDialogWorkstationRecipeSelector(
            string dialogTitle,
            ItemStack[] recipeOutputs,
            Action<int, bool> onSelectedRecipe,
            Action onCancelSelect,
            BlockPos blockEntityPos,
            ICoreClientAPI capi
        ) : base(dialogTitle, capi)
        {
            this.blockEntityPos = blockEntityPos;
            this.onSelectedRecipe = onSelectedRecipe;
            this.onCancelSelect = onCancelSelect;

            BuildSkillItems(recipeOutputs);
            RebuildDisplayedSkillItems(null);
            SetupDialog();
        }

        public void SetIngredientCounts(int num, ItemStack[] ingredStacks)
        {
            ingredientPreviewByIndex[num] = ingredStacks;
            if (num >= 0 && num < skillItems.Count)
            {
                skillItems[num].Data = ingredStacks;
            }

            if (num == GetHoveredOriginalIndex())
            {
                UpdateHoveredRecipe(prevSlotOver);
            }
        }

        public void SetRequiredTraitText(int num, string? requiredTraitText)
        {
            requiredTraitTextByIndex[num] = requiredTraitText;

            if (num == GetHoveredOriginalIndex())
            {
                UpdateHoveredRecipe(prevSlotOver);
            }
        }

        public void SetCustomName(int num, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                customNameByIndex.Remove(num);
            }
            else
            {
                customNameByIndex[num] = name;
                if (num >= 0 && num < skillItems.Count)
                {
                    skillItems[num].Name = name!;
                }
            }

            if (num == GetHoveredOriginalIndex())
            {
                UpdateHoveredRecipe(prevSlotOver);
            }
        }

        public void SetCustomDescription(int num, string? description)
        {
            customDescriptionByIndex[num] = description;
            if (num >= 0 && num < skillItems.Count)
            {
                skillItems[num].Description = description ?? string.Empty;
            }

            if (num == GetHoveredOriginalIndex())
            {
                UpdateHoveredRecipe(prevSlotOver);
            }
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();

            if (!didSelect)
            {
                onCancelSelect();
            }
        }

        public override bool OnEscapePressed()
        {
            if (DialogType == EnumDialogType.HUD)
            {
                return false;
            }

            return TryClose();
        }

        public override void OnMouseDown(MouseEvent args)
        {
            pendingCraftToStack = (args.Modifiers & (int)EnumModifierKey.SHIFT) != 0;
            base.OnMouseDown(args);
        }

        public override bool PrefersUngrabbedMouse => false;

        public override void OnRenderGUI(float deltaTime)
        {
            if (capi.Settings.Bool["immersiveMouseMode"])
            {
                Vec3d aboveHeadPos = new(blockEntityPos.X + 0.5, blockEntityPos.Y + 0.5, blockEntityPos.Z + 0.5);
                Vec3d pos = MatrixToolsd.Project(
                    aboveHeadPos,
                    capi.Render.PerspectiveProjectionMat,
                    capi.Render.PerspectiveViewMat,
                    capi.Render.FrameWidth,
                    capi.Render.FrameHeight);

                if (pos.Z < 0)
                {
                    return;
                }

                SingleComposer.Bounds.Alignment = EnumDialogArea.None;
                SingleComposer.Bounds.fixedOffsetX = 0;
                SingleComposer.Bounds.fixedOffsetY = 0;
                SingleComposer.Bounds.absFixedX = pos.X - SingleComposer.Bounds.OuterWidth / 2;
                SingleComposer.Bounds.absFixedY = capi.Render.FrameHeight - pos.Y - SingleComposer.Bounds.OuterHeight * 0.75;
                SingleComposer.Bounds.absMarginX = 0;
                SingleComposer.Bounds.absMarginY = 0;
            }

            base.OnRenderGUI(deltaTime);
        }

        private void BuildSkillItems(ItemStack[] recipeOutputs)
        {
            double size = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGrid.unscaledSlotPadding;

            for (int i = 0; i < recipeOutputs.Length; i++)
            {
                ItemStack stack = recipeOutputs[i];
                ItemSlot dummySlot = new DummySlot(stack);

                string key = GetCraftDescKey(stack);
                string desc = Lang.GetMatching(key);
                if (desc == key)
                {
                    desc = string.Empty;
                }

                skillItems.Add(new SkillItem
                {
                    Code = stack.Collectible.Code.Clone(),
                    Name = stack.GetName(),
                    Description = desc,
                    Data = null,
                    RenderHandler = (_, _, posX, posY) =>
                    {
                        double scaledSize = GuiElement.scaled(size - 5);
                        capi.Render.RenderItemstackToGui(
                            dummySlot,
                            posX + scaledSize / 2,
                            posY + scaledSize / 2,
                            100,
                            (float)GuiElement.scaled(GuiElementPassiveItemSlot.unscaledItemSize),
                            ColorUtil.WhiteArgb);
                    }
                });
            }
        }

        private string GetCraftDescKey(ItemStack stack)
        {
            string type = stack.Class.Name();
            return stack.Collectible.Code?.Domain + AssetLocation.LocationSeparator + type + "craftdesc-" + stack.Collectible.Code?.Path;
        }

        private void SetupDialog()
        {
            int cellCount = Math.Max(1, skillItems.Count);
            int columns = Math.Min(cellCount, 7);
            int rows = (int)Math.Ceiling(cellCount / (double)columns);

            double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGrid.unscaledSlotPadding;
            double innerWidth = Math.Max(300, columns * slotSize);
            ElementBounds searchBounds = ElementBounds.Fixed(0, 30, innerWidth, 30);
            ElementBounds skillGridBounds = searchBounds.BelowCopy(0, 10, 0, 0).WithFixedWidth(innerWidth).WithFixedHeight(rows * slotSize);
            ElementBounds nameBounds = skillGridBounds.BelowCopy(0, 10, 0, 0).WithFixedHeight(33);
            ElementBounds descBounds = nameBounds.BelowCopy(0, 10, 0, 0);
            ElementBounds ingredientBounds = descBounds.BelowCopy(0, 20, 0, 0);
            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            SingleComposer = capi.Gui
                .CreateCompo("workstationrecipeselector" + blockEntityPos, ElementStdBounds.AutosizedMainDialog)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar(DialogTitle, OnTitleBarClose)
                .BeginChildElements(bgBounds)
                .AddTextInput(searchBounds, OnSearchTextChanged, CairoFont.WhiteSmallishText(), "search")
                .AddSkillItemGrid(displayedSkillItems, columns, rows, OnSlotClick, skillGridBounds, "skillitemgrid")
                .AddDynamicText(string.Empty, CairoFont.WhiteSmallishText(), nameBounds, "name")
                .AddDynamicText(string.Empty, CairoFont.WhiteDetailText(), descBounds, "desc")
                .AddDynamicText(string.Empty, CairoFont.WhiteDetailText(), ingredientBounds, "ingredient")
                .EndChildElements()
                .Compose();

            SingleComposer.GetTextInput("search").SetPlaceHolderText(Lang.Get("Search..."));
            SingleComposer.GetSkillItemGrid("skillitemgrid").OnSlotOver = OnSlotOver;
            UnfocusElements();
        }

        private void OnSlotOver(int num)
        {
            if (num < 0 || num >= displayedSkillItems.Count || num == prevSlotOver)
            {
                return;
            }

            prevSlotOver = num;
            UpdateHoveredRecipe(num);
        }

        private void UpdateHoveredRecipe(int num)
        {
            if (num < 0 || num >= displayedSkillItems.Count)
            {
                return;
            }

            int originalIndex = displayedOriginalIndices[num];
            string name = customNameByIndex.TryGetValue(originalIndex, out string? customName)
                ? customName ?? string.Empty
                : displayedSkillItems[num].Name;
            string desc = customDescriptionByIndex.TryGetValue(originalIndex, out string? customDesc)
                ? customDesc ?? string.Empty
                : displayedSkillItems[num].Description;

            SingleComposer?.GetDynamicText("name")?.SetNewText(name);
            SingleComposer?.GetDynamicText("desc")?.SetNewText(desc);
            SingleComposer?.GetDynamicText("ingredient")?.SetNewText(BuildIngredientHoverText(originalIndex));
        }

        private string BuildIngredientHoverText(int num)
        {
            string? requiredTraitText = requiredTraitTextByIndex.TryGetValue(num, out string? storedRequiredTraitText)
                ? storedRequiredTraitText
                : null;

            bool hasIngredients = ingredientPreviewByIndex.TryGetValue(num, out ItemStack[]? ingredients)
                && ingredients != null
                && ingredients.Length > 0;

            if (!hasIngredients)
            {
                return string.IsNullOrWhiteSpace(requiredTraitText)
                    ? string.Empty
                    : Lang.Get("Requires: {0}", requiredTraitText);
            }

            ingredients ??= Array.Empty<ItemStack>();
            List<string> parts = new List<string>(ingredients.Length);
            foreach (ItemStack ingredient in ingredients)
            {
                string name = ingredient.Attributes?.GetString(PREVIEW_INGREDIENT_LABEL_KEY) ?? ingredient.GetName().ToLowerInvariant();
                bool workableRequired = ingredient.Attributes?.GetBool(PREVIEW_WORKABLE_MARKER_KEY, false) == true;
                string suffix = workableRequired ? " (workable)" : string.Empty;
                parts.Add($"{ingredient.StackSize}x {name}{suffix}");
            }

            if (!string.IsNullOrWhiteSpace(requiredTraitText))
            {
                parts.Insert(0, requiredTraitText);
            }

            return Lang.Get("Requires: {0}", string.Join(", ", parts));
        }

        private void OnSlotClick(int num)
        {
            if (num < 0 || num >= displayedOriginalIndices.Count)
            {
                return;
            }

            bool craftToStack = pendingCraftToStack || IsShiftKeyDown();
            pendingCraftToStack = false;
            onSelectedRecipe(displayedOriginalIndices[num], craftToStack);
            didSelect = true;
        }

        private void OnSearchTextChanged(string text)
        {
            RebuildDisplayedSkillItems(text);
            prevSlotOver = -1;
            ClearHoveredRecipeDisplay();
        }

        private void RebuildDisplayedSkillItems(string? text)
        {
            displayedSkillItems.Clear();
            displayedOriginalIndices.Clear();

            string search = text?.Trim() ?? string.Empty;
            bool hasSearch = search.Length > 0;

            for (int i = 0; i < skillItems.Count; i++)
            {
                SkillItem skillItem = skillItems[i];
                if (hasSearch && !skillItem.Name.Contains(search, StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                displayedSkillItems.Add(skillItem);
                displayedOriginalIndices.Add(i);
            }
        }

        private int GetHoveredOriginalIndex()
        {
            if (prevSlotOver < 0 || prevSlotOver >= displayedOriginalIndices.Count)
            {
                return -1;
            }

            return displayedOriginalIndices[prevSlotOver];
        }

        private void ClearHoveredRecipeDisplay()
        {
            SingleComposer?.GetDynamicText("name")?.SetNewText(string.Empty);
            SingleComposer?.GetDynamicText("desc")?.SetNewText(string.Empty);
            SingleComposer?.GetDynamicText("ingredient")?.SetNewText(string.Empty);
        }

        private void OnTitleBarClose()
        {
            TryClose();
        }

        private bool IsShiftKeyDown()
        {
            if (capi.World?.Player?.Entity?.Controls?.ShiftKey == true)
            {
                return true;
            }

            bool[] keyState = capi.Input.KeyboardKeyStateRaw;
            return IsKeyDown(keyState, (int)GlKeys.ShiftLeft) || IsKeyDown(keyState, (int)GlKeys.ShiftRight);
        }

        private static bool IsKeyDown(bool[] keyState, int keyCode)
        {
            return keyCode >= 0 && keyCode < keyState.Length && keyState[keyCode];
        }
    }
}

