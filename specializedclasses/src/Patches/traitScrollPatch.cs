using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SpecializedClasses.Patches
{
    internal sealed class ScrollableTextState
    {
        public string StateId { get; }
        public string ComposerName { get; }
        public string RichtextKey { get; }
        public string ScrollbarKey { get; }
        public ElementBounds ViewportBounds { get; }
        public double BaseTextY { get; }

        public ScrollableTextState(string stateId, string composerName, string richtextKey, string scrollbarKey, ElementBounds viewportBounds, double baseTextY = 0)
        {
            StateId = stateId;
            ComposerName = composerName;
            RichtextKey = richtextKey;
            ScrollbarKey = scrollbarKey;
            ViewportBounds = viewportBounds;
            BaseTextY = baseTextY;
        }
    }

    internal static class ScrollableTextRegistry
    {
        private static readonly ConditionalWeakTable<GuiDialog, Dictionary<string, ScrollableTextState>> StatesByDialog = new();

        public static void Register(GuiDialog dialog, ScrollableTextState state)
        {
            Dictionary<string, ScrollableTextState> states = StatesByDialog.GetOrCreateValue(dialog);
            states[state.StateId] = state;
        }

        public static bool TryGet(GuiDialog dialog, string stateId, out ScrollableTextState? state)
        {
            state = null;
            return StatesByDialog.TryGetValue(dialog, out Dictionary<string, ScrollableTextState>? states)
                && states.TryGetValue(stateId, out state);
        }
    }

    internal static class TraitScrollComposer
    {
        private const string CreateCharacterStateId = "specializedclasses:createCharacterTraits";

        private const string CreateCharacterComposerName = "createcharacter";
        private const string CreateCharacterRichtextKey = "characterDesc";
        private const string CreateCharacterScrollbarKey = "specializedclassesCharacterDescScrollbar";

        private const string TraitsTabRichtextKey = "specializedclassesTraitsRichtext";
        private const string TraitsTabScrollbarKey = "specializedclassesTraitsScrollbar";

        private const double PanelFrameMargin = 6;
        private const double PanelInsetPadding = 3;
        private const double ScrollbarWidth = 20;
        private const double ScrollbarGap = 7;
        private const double ClassHeaderWidth = 432;
        private const double ClassTextWidth = 498;
        private const double ClassTextTopGap = 15;
        private const double ClassButtonBottomPadding = 25;
        private const double ClassPreviewWidth = 193;

        private static readonly FieldInfo CharacterDialogField = AccessTools.Field(typeof(CharacterSystem), "charDlg")!;
        private static readonly AccessTools.FieldRef<GuiDialogCreateCharacter, IInventory?> CharacterInventoryField =
            AccessTools.FieldRefAccess<GuiDialogCreateCharacter, IInventory?>("characterInv");
        private static readonly AccessTools.FieldRef<GuiDialogCreateCharacter, ElementBounds> InsetSlotBoundsField =
            AccessTools.FieldRefAccess<GuiDialogCreateCharacter, ElementBounds>("insetSlotBounds");
        private static readonly AccessTools.FieldRef<GuiDialogCreateCharacter, int> CurrentTabField =
            AccessTools.FieldRefAccess<GuiDialogCreateCharacter, int>("curTab");
        private static readonly AccessTools.FieldRef<GuiDialogCreateCharacter, int> DialogHeightField =
            AccessTools.FieldRefAccess<GuiDialogCreateCharacter, int>("dlgHeight");

        private static readonly MethodInfo ChangeClassMethod = AccessTools.Method(typeof(GuiDialogCreateCharacter), "changeClass")!;
        private static readonly MethodInfo OnConfirmMethod = AccessTools.Method(typeof(GuiDialogCreateCharacter), "OnConfirm")!;
        private static readonly MethodInfo OnTitleBarCloseMethod = AccessTools.Method(typeof(GuiDialogCreateCharacter), "OnTitleBarClose")!;
        private static readonly MethodInfo OnTabClickedMethod = AccessTools.Method(typeof(GuiDialogCreateCharacter), "onTabClicked")!;
        private static readonly MethodInfo GetClassTraitTextMethod = AccessTools.Method(typeof(CharacterSystem), "getClassTraitText")!;

        public static void ReplaceTraitsTabHandler(CharacterSystem characterSystem)
        {
            if (CharacterDialogField.GetValue(characterSystem) is not GuiDialogCharacterBase charDialog)
            {
                return;
            }

            List<Action<GuiComposer>> handlers = charDialog.RenderTabHandlers;
            List<GuiTab> tabs = charDialog.Tabs;

            int traitsIndex = tabs.FindIndex(t => string.Equals(t.Name, Lang.Get("charactertab-traits"), StringComparison.Ordinal));
            if (traitsIndex < 0 || traitsIndex >= handlers.Count)
            {
                return;
            }

            handlers[traitsIndex] = composer => ComposeTraitsTab(characterSystem, charDialog, composer);
        }

        public static bool ComposeCreateCharacterClassTab(GuiDialogCreateCharacter dialog)
        {
            ICoreClientAPI capi = dialog.SingleComposer?.Api
                ?? AccessTools.Field(typeof(GuiDialog), "capi")?.GetValue(dialog) as ICoreClientAPI
                ?? throw new InvalidOperationException("Could not resolve client API for character dialog.");

            CharacterInventoryField(dialog) = capi.World.Player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName);

            double pad = GuiElementItemSlotGridBase.unscaledSlotPadding;
            double slotsize = GuiElementPassiveItemSlot.unscaledSlotSize;
            double ypos = 20 + pad - 25;
            int dlgHeight = DialogHeightField(dialog);
            int curTab = CurrentTabField(dialog);

            ElementBounds tabBounds = ElementBounds.Fixed(0, -25, 450, 25);
            ElementBounds bgBounds = ElementBounds.FixedSize(717, dlgHeight).WithFixedPadding(GuiStyle.ElementToDialogPadding);
            ElementBounds dialogBounds = ElementBounds.FixedSize(757, dlgHeight + 40).WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, 0);

            GuiTab[] tabs =
            {
                new GuiTab { Name = Lang.Get("tab-skinandvoice"), DataInt = 0 },
                new GuiTab { Name = Lang.Get("tab-charclass"), DataInt = 1 }
            };

            GuiComposer createCharacterComposer = capi.Gui
                .CreateCompo(CreateCharacterComposerName, dialogBounds)
                .AddShadedDialogBG(bgBounds, true)
                .AddDialogTitleBar(Lang.Get("Select character class"), () => OnTitleBarCloseMethod.Invoke(dialog, Array.Empty<object>()))
                .AddHorizontalTabs(
                    tabs,
                    tabBounds,
                    tabId => OnTabClickedMethod.Invoke(dialog, new object[] { tabId }),
                    CairoFont.WhiteSmallText(),
                    CairoFont.WhiteSmallText(),
                    "tabs"
                )
                .BeginChildElements(bgBounds);

            dialog.Composers[CreateCharacterComposerName] = createCharacterComposer;

            EntityBehaviorPlayerInventory? inventoryBehavior = capi.World.Player.Entity.GetBehavior<EntityBehaviorPlayerInventory>();
            if (inventoryBehavior != null)
            {
                inventoryBehavior.hideClothing = false;
            }

            EntityShapeRenderer? renderer = capi.World.Player.Entity.Properties.Client.Renderer as EntityShapeRenderer;
            renderer?.TesselateShape();

            ElementBounds leftColBounds = ElementBounds.Fixed(0, ypos, 0, dlgHeight - 23).FixedGrow(pad, pad);
            ElementBounds prevButtonBounds = ElementBounds.Fixed(0, ypos + 23, 35, slotsize - 4).WithFixedPadding(2).FixedRightOf(leftColBounds, -10);
            ElementBounds centerTextBounds = ElementBounds.Fixed(0, ypos + 25, ClassHeaderWidth, slotsize - 4 - 8).FixedRightOf(prevButtonBounds, 10);
            ElementBounds charClassInset = centerTextBounds.ForkBoundingParent(4, 4, 4, 4);
            ElementBounds nextButtonBounds = ElementBounds.Fixed(0, ypos + 23, 35, slotsize - 4).WithFixedPadding(2).FixedRightOf(charClassInset, 9);

            CairoFont font = CairoFont.WhiteMediumText();

            int visibleHeight = (int)Math.Max(120, dlgHeight - (ypos + 25) - 62);
            ElementBounds textPanelBounds = ElementBounds.Fixed(0, 0, ClassTextWidth, visibleHeight)
                .FixedUnder(prevButtonBounds, ClassTextTopGap)
                .FixedRightOf(leftColBounds, -10);
            ScrollablePanelLayout classTextLayout = CreateScrollablePanelLayout(textPanelBounds);

            InsetSlotBoundsField(dialog) = ElementBounds.Fixed(0, ypos + 25, ClassPreviewWidth, leftColBounds.fixedHeight - 2 * pad - 30).FixedRightOf(nextButtonBounds, 11);

            createCharacterComposer
                .AddInset(InsetSlotBoundsField(dialog), 2)
                .AddIconButton("left", on => ChangeClassMethod.Invoke(dialog, new object[] { -1 }), prevButtonBounds.FlatCopy())
                .AddInset(charClassInset, 2)
                .AddDynamicText("Commoner", font.Clone().WithOrientation(EnumTextOrientation.Center), centerTextBounds, "className")
                .AddIconButton("right", on => ChangeClassMethod.Invoke(dialog, new object[] { 1 }), nextButtonBounds.FlatCopy())
                ;

            ComposeScrollableRichtext(
                createCharacterComposer,
                dialog,
                CreateCharacterStateId,
                CreateCharacterScrollbarKey,
                CreateCharacterRichtextKey,
                classTextLayout,
                string.Empty,
                CairoFont.WhiteDetailText()
            );

            createCharacterComposer
                .AddSmallButton(
                    Lang.Get("Confirm Class"),
                    () => (bool)(OnConfirmMethod.Invoke(dialog, Array.Empty<object>()) ?? false),
                    ElementBounds.Fixed(0, dlgHeight - (int)ClassButtonBottomPadding).WithAlignment(EnumDialogArea.RightFixed).WithFixedPadding(12, 6),
                    EnumButtonStyle.Normal
                );

            ChangeClassMethod.Invoke(dialog, new object[] { 0 });

            GuiElementHorizontalTabs? tabElement = createCharacterComposer.GetHorizontalTabs("tabs");
            if (tabElement != null)
            {
                tabElement.unscaledTabSpacing = 20;
                tabElement.unscaledTabPadding = 10;
                tabElement.activeElement = curTab;
            }

            createCharacterComposer.Compose();
            Refresh(dialog, CreateCharacterStateId, true);

            return false;
        }

        public static void RefreshCreateCharacterScrollbar(GuiDialogCreateCharacter dialog, bool resetToTop)
        {
            Refresh(dialog, CreateCharacterStateId, resetToTop);
        }

        // Cache the traits-tab composer directly; only one character dialog is open at a time.
        private static GuiComposer? _traitsComposer;
        private static ElementBounds? _traitsBgBounds;

        public static void ComposeTraitsTab(CharacterSystem characterSystem, GuiDialogCharacterBase charDialog, GuiComposer composer)
        {
            string text = (string)(GetClassTraitTextMethod.Invoke(characterSystem, Array.Empty<object>()) ?? string.Empty);

            bool hasOverhaulLib = composer.Api.ModLoader.IsModEnabled("overhaullib");
            double textWidth = hasOverhaulLib ? 417 : 375;
            double textHeight = hasOverhaulLib ? 370 : 316;

            ElementBounds charTextBounds = ElementBounds.Fixed(-18, 14, textWidth, textHeight);
            ElementBounds bgBounds = charTextBounds.ForkBoundingParent(PanelFrameMargin, PanelFrameMargin, PanelFrameMargin, PanelFrameMargin);
            ElementBounds clipBounds = charTextBounds.FlatCopy().FixedGrow(PanelFrameMargin, 11).WithFixedOffset(0, -PanelFrameMargin);
            ElementBounds scrollbarBounds = charTextBounds.CopyOffsetedSibling(charTextBounds.fixedWidth + 7, -PanelFrameMargin, 0, 12).WithFixedWidth(ScrollbarWidth);

            composer
                .BeginChildElements(bgBounds)
                    .BeginClip(clipBounds)
                        .AddRichtext(text, CairoFont.WhiteDetailText().WithLineHeightMultiplier(1.15), charTextBounds, TraitsTabRichtextKey)
                    .EndClip()
                    .AddVerticalScrollbar(value =>
                    {
                        charTextBounds.fixedY = -value;
                        charTextBounds.CalcWorldBounds();
                    }, scrollbarBounds, TraitsTabScrollbarKey)
                .EndChildElements();

            _traitsComposer = composer;
            _traitsBgBounds = bgBounds;

            GuiElementScrollbar? sb = composer.GetScrollbar(TraitsTabScrollbarKey);
            if (sb != null)
            {
                sb.SetHeights((float)textHeight, (float)(textHeight * 2));
                sb.SetScrollbarPosition(0);
            }

            ((ICoreClientAPI)composer.Api).Event.EnqueueMainThreadTask(() =>
            {
                if (_traitsComposer == null) return;
                GuiElementRichtext? rt = _traitsComposer.GetRichtext(TraitsTabRichtextKey);
                GuiElementScrollbar? scrollbar = _traitsComposer.GetScrollbar(TraitsTabScrollbarKey);
                if (rt == null || scrollbar == null || rt.TotalHeight <= 0) return;
                float composed = (float)Math.Ceiling(rt.TotalHeight / RuntimeEnv.GUIScale);
                scrollbar.SetHeights((float)textHeight, Math.Max((float)textHeight, composed));
            }, "specializedclasses:traits-scrollbar-init");
        }

        public static void HandleMouseWheel(GuiDialog dialog, MouseWheelEventArgs args)
        {
            if (args.IsHandled)
            {
                return;
            }

            TryForwardMouseWheel(dialog, CreateCharacterStateId, args);

            if (!args.IsHandled && _traitsComposer != null && _traitsBgBounds != null)
            {
                GuiElementScrollbar? scrollbar = _traitsComposer.GetScrollbar(TraitsTabScrollbarKey);
                if (scrollbar != null && _traitsBgBounds.PointInside(_traitsComposer.Api.Input.MouseX, _traitsComposer.Api.Input.MouseY))
                {
                    scrollbar.OnMouseWheel(_traitsComposer.Api, args);
                }
            }
        }

        public static void RefreshVisibleScrollbars(GuiDialog dialog)
        {
            Refresh(dialog, CreateCharacterStateId, false);
        }

        private static void TryForwardMouseWheel(GuiDialog dialog, string stateId, MouseWheelEventArgs args)
        {
            if (!ScrollableTextRegistry.TryGet(dialog, stateId, out ScrollableTextState? state) || state == null)
            {
                return;
            }

            GuiComposer? composer = GetComposer(dialog, state);
            GuiElementScrollbar? scrollbar = composer?.GetScrollbar(state.ScrollbarKey);
            if (composer == null || scrollbar == null)
            {
                return;
            }

            if (state.ViewportBounds.PointInside(composer.Api.Input.MouseX, composer.Api.Input.MouseY))
            {
                scrollbar.OnMouseWheel(composer.Api, args);
            }
        }

        private static void Refresh(GuiDialog dialog, string stateId, bool resetToTop)
        {
            if (!ScrollableTextRegistry.TryGet(dialog, stateId, out ScrollableTextState? state) || state == null)
            {
                return;
            }

            GuiComposer? composer = GetComposer(dialog, state);
            GuiElementRichtext? richtext = composer?.GetRichtext(state.RichtextKey);
            GuiElementScrollbar? scrollbar = composer?.GetScrollbar(state.ScrollbarKey);
            if (composer == null || richtext == null || scrollbar == null)
            {
                return;
            }

            float visibleHeight = (float)state.ViewportBounds.fixedHeight;
            float composedHeight = (float)Math.Ceiling(richtext.TotalHeight / RuntimeEnv.GUIScale);

            // Skip zero-height richtext until it has rendered once, or the scrollbar range collapses.
            if (composedHeight <= 0) return;

            float totalHeight = Math.Max(visibleHeight, composedHeight);

            scrollbar.SetHeights(visibleHeight, totalHeight);

            if (resetToTop)
            {
                scrollbar.SetScrollbarPosition(0);
            }
            else
            {
                OnScrollbarChanged(dialog, stateId, scrollbar.CurrentYPosition);
            }
        }

        private static GuiComposer? GetComposer(GuiDialog dialog, ScrollableTextState state)
        {
            if (string.IsNullOrEmpty(state.ComposerName))
            {
                return dialog.SingleComposer;
            }

            return dialog.Composers.ContainsKey(state.ComposerName) ? dialog.Composers[state.ComposerName] : null;
        }

        private static void OnScrollbarChanged(GuiDialog dialog, string stateId, float value)
        {
            if (!ScrollableTextRegistry.TryGet(dialog, stateId, out ScrollableTextState? state) || state == null)
            {
                return;
            }

            GuiComposer? composer = GetComposer(dialog, state);
            GuiElementRichtext? richtext = composer?.GetRichtext(state.RichtextKey);
            if (richtext == null)
            {
                return;
            }

            richtext.Bounds.fixedY = state.BaseTextY - value;
            richtext.Bounds.CalcWorldBounds();
        }

        private static void ComposeScrollableRichtext(
            GuiComposer composer,
            GuiDialog dialog,
            string stateId,
            string scrollbarKey,
            string richtextKey,
            ScrollablePanelLayout layout,
            string text,
            CairoFont font)
        {
            composer
                .BeginChildElements(layout.FrameBounds)
                    .AddInset(ElementBounds.Fixed(0, 0, layout.FrameBounds.fixedWidth, layout.FrameBounds.fixedHeight), (int)PanelInsetPadding)
                    .AddVerticalScrollbar(value => OnScrollbarChanged(dialog, stateId, value), layout.ScrollbarBounds, scrollbarKey)
                    .BeginClip(layout.ClipBounds)
                    .AddRichtext(text, font, layout.RichtextBounds, richtextKey)
                    .EndClip()
                .EndChildElements();

            ScrollableTextRegistry.Register(dialog, new ScrollableTextState(
                stateId,
                CreateCharacterComposerName,
                richtextKey,
                scrollbarKey,
                layout.FrameBounds,
                layout.RichtextBounds.fixedY
            ));
        }

        private static ScrollablePanelLayout CreateScrollablePanelLayout(ElementBounds contentBounds)
        {
            ElementBounds frameBounds = contentBounds.ForkBoundingParent(PanelFrameMargin, PanelFrameMargin, PanelFrameMargin, PanelFrameMargin);
            ElementBounds clipBounds = ElementBounds.Fixed(
                0,
                -PanelFrameMargin,
                contentBounds.fixedWidth + PanelFrameMargin * 2,
                contentBounds.fixedHeight + 22
            );
            ElementBounds richtextBounds = ElementBounds.Fixed(PanelFrameMargin, PanelFrameMargin, contentBounds.fixedWidth, contentBounds.fixedHeight);
            ElementBounds scrollbarBounds = ElementBounds.Fixed(
                contentBounds.fixedWidth + PanelFrameMargin + ScrollbarGap,
                0,
                ScrollbarWidth,
                contentBounds.fixedHeight + PanelFrameMargin * 2
            );

            return new ScrollablePanelLayout(frameBounds, clipBounds, richtextBounds, scrollbarBounds);
        }
    }

    internal readonly record struct ScrollablePanelLayout(
        ElementBounds FrameBounds,
        ElementBounds ClipBounds,
        ElementBounds RichtextBounds,
        ElementBounds ScrollbarBounds
    );

    [HarmonyPatch(typeof(CharacterSystem), nameof(CharacterSystem.StartClientSide))]
    public static class CharacterSystem_TraitsTabScrollPatch
    {
        [HarmonyPostfix]
        public static void Postfix(CharacterSystem __instance)
        {
            TraitScrollComposer.ReplaceTraitsTabHandler(__instance);
        }
    }

    [HarmonyPatch(typeof(GuiDialogCreateCharacter), "ComposeGuis")]
    public static class GuiDialogCreateCharacter_ComposeGuis_ScrollPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(GuiDialogCreateCharacter __instance)
        {
            return CurrentTabField(__instance) != 1 || TraitScrollComposer.ComposeCreateCharacterClassTab(__instance);
        }

        private static readonly AccessTools.FieldRef<GuiDialogCreateCharacter, int> CurrentTabField =
            AccessTools.FieldRefAccess<GuiDialogCreateCharacter, int>("curTab");
    }

    [HarmonyPatch(typeof(GuiDialogCreateCharacter), "changeClass")]
    public static class GuiDialogCreateCharacter_ChangeClass_ScrollPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GuiDialogCreateCharacter __instance)
        {
            TraitScrollComposer.RefreshCreateCharacterScrollbar(__instance, true);
        }
    }

    [HarmonyPatch(typeof(GuiDialog), nameof(GuiDialog.OnMouseWheel))]
    public static class GuiDialog_OnMouseWheel_ScrollPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GuiDialog __instance, MouseWheelEventArgs args)
        {
            TraitScrollComposer.HandleMouseWheel(__instance, args);
        }
    }

    [HarmonyPatch(typeof(GuiDialog), nameof(GuiDialog.OnRenderGUI))]
    public static class GuiDialog_OnRenderGUI_ScrollPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GuiDialog __instance)
        {
            TraitScrollComposer.RefreshVisibleScrollbars(__instance);
        }
    }
}
