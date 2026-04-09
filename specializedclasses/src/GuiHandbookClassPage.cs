using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using SpecializedClasses.Workstations;

namespace SpecializedClasses
{
    public class GuiHandbookClassPage : GuiHandbookPage
    {
        private readonly ICoreClientAPI capi;
        private readonly string classCode;
        private readonly string classDisplayName;
        private readonly CharacterClass charClass;
        private readonly CharacterSystem charSystem;

        private LoadedTexture? titleTexture;

        public override string PageCode => $"specializedclasses:class-{classCode}";
        public override string CategoryCode => "guide";
        public override bool IsDuplicate => false;
        public override float SearchWeightOffset => 2f;

        public GuiHandbookClassPage(
            ICoreClientAPI capi,
            string classCode,
            string classDisplayName,
            CharacterClass charClass,
            CharacterSystem charSystem)
        {
            this.capi = capi;
            this.classCode = classCode;
            this.classDisplayName = classDisplayName;
            this.charClass = charClass;
            this.charSystem = charSystem;
        }

        public override PageText GetPageText() => new PageText
        {
            Title = $"Class: {classDisplayName}",
            Text  = $"Class: {classDisplayName}"
        };

        public override void Dispose()
        {
            titleTexture?.Dispose();
            titleTexture = null;
        }

        public override void RenderListEntryTo(
            ICoreClientAPI capi,
            float dt,
            double x,
            double y,
            double cellWidth,
            double cellHeight)
        {
            float size = (float)GuiElement.scaled(25);
            float pad  = (float)GuiElement.scaled(10);

            if (titleTexture == null)
                titleTexture = new TextTextureUtil(capi).GenTextTexture(
                    $"Class: {classDisplayName}", CairoFont.WhiteSmallText());

            capi.Render.Render2DTexturePremultipliedAlpha(
                titleTexture.TextureId,
                x + pad,
                y + size / 4 - GuiElement.scaled(3),
                titleTexture.Width,
                titleTexture.Height,
                50);
        }

        public override void ComposePage(
            GuiComposer detailViewGui,
            ElementBounds textBounds,
            ItemStack[] allStacks,
            ActionConsumable<string> openDetailPageFor)
        {
            CairoFont font = CairoFont.WhiteDetailText().WithLineHeightMultiplier(1.15);

            List<RichTextComponentBase> components = new List<RichTextComponentBase>();

            components.AddRange(VtmlUtil.Richtextify(capi, $"<strong>Class: {classDisplayName}</strong>\n\n", CairoFont.WhiteSmallishText()));

            components.AddRange(VtmlUtil.Richtextify(capi, BuildClassText(), font));

            AppendStartingGear(components, font, openDetailPageFor);

            // Vanilla nulls RequiresTrait when class-exclusive recipes are disabled,
            // so checking the config here is equivalent and cheaper than scanning recipes.
            bool classExclusiveEnabled = capi.World.Config.GetBool("classExclusiveRecipes", true);
            if (!classExclusiveEnabled)
            {
                components.AddRange(VtmlUtil.Richtextify(capi,
                    "\n<i>Class exclusive recipes are disabled.</i>", font));
            }
            else
            {
                List<(GridRecipe[] recipes, ItemStack[] outputStacks)> gridGroups = GetGridRecipeGroups();

                if (gridGroups.Count > 0)
                {
                    components.AddRange(VtmlUtil.Richtextify(capi, "\n<strong>Exclusive grid recipes</strong>\n", font));

                    int j = 0;
                    foreach ((GridRecipe[] recipes, ItemStack[] outputStacks) in gridGroups)
                    {
                        SlideshowGridRecipeTextComponent comp;
                        try
                        {
                            comp = new SlideshowGridRecipeTextComponent(
                                capi, recipes, 40, EnumFloat.Inline,
                                cs => openDetailPageFor(GuiHandbookItemStackPage.PageCodeForStack(cs)),
                                allStacks);
                        }
                        catch (ArgumentException ex)
                        {
                            capi.Logger.Warning($"[SpecializedClasses] Skipping grid recipe group for class '{classCode}' in handbook: {ex.Message}");
                            continue;
                        }

                        if (j++ % 2 == 0)
                            components.Add(new ClearFloatTextComponent(capi, 7));

                        comp.VerticalAlign = EnumVerticalAlign.Top;
                        comp.PaddingRight = 8;
                        comp.PaddingLeft = 4 + (1 - j % 2) * 20;
                        components.Add(comp);

                        RichTextComponent ecomp = new RichTextComponent(capi, "=", CairoFont.WhiteMediumText());
                        ecomp.VerticalAlign = EnumVerticalAlign.Middle;
                        ecomp.PaddingRight = 5;
                        components.Add(ecomp);

                        SlideshowItemstackTextComponent ocomp = new SlideshowItemstackTextComponent(
                            capi, outputStacks, 40, EnumFloat.Inline,
                            cs => openDetailPageFor(GuiHandbookItemStackPage.PageCodeForStack(cs)));
                        ocomp.overrideCurrentItemStack = comp.GenerateCurrentVisibleOutputStack;
                        ocomp.VerticalAlign = EnumVerticalAlign.Middle;
                        ocomp.ShowStackSize = true;
                        components.Add(ocomp);
                    }

                    components.Add(new ClearFloatTextComponent(capi, 14));
                }

                List<RichTextComponentBase> wsComponents = new List<RichTextComponentBase>();
                if (WorkstationHandbookHelper.AppendClassExclusiveRecipeRows(capi, openDetailPageFor, classCode, wsComponents))
                {
                    components.AddRange(VtmlUtil.Richtextify(capi, "\n<strong>Exclusive workstation recipes</strong>\n", font));
                    components.AddRange(wsComponents);
                }
            }

            detailViewGui.AddRichtext(components.ToArray(), textBounds, "richtext");
        }

        private void AppendStartingGear(
            List<RichTextComponentBase> components,
            CairoFont font,
            ActionConsumable<string> openDetailPageFor)
        {
            if (charClass.Gear == null || charClass.Gear.Length == 0) return;

            List<ItemStack> gearStacks = new List<ItemStack>();
            foreach (JsonItemStack jsonStack in charClass.Gear)
            {
                // Gear was already resolved during character-class load, so clone it directly.
                ItemStack? resolved = jsonStack.ResolvedItemStack?.Clone();
                if (resolved != null)
                    gearStacks.Add(resolved);
            }

            if (gearStacks.Count == 0) return;

            components.AddRange(VtmlUtil.Richtextify(capi, "\n<strong>Starting gear</strong>\n", font));
            components.Add(new ClearFloatTextComponent(capi, 4));

            foreach (ItemStack stack in gearStacks)
            {
                components.Add(new ItemstackTextComponent(
                    capi,
                    stack,
                    40,
                    0,
                    EnumFloat.Inline,
                    cs => openDetailPageFor(GuiHandbookItemStackPage.PageCodeForStack(cs))));
            }

            components.Add(new ClearFloatTextComponent(capi, 7));
        }

        private string BuildClassText()
        {
            StringBuilder sb = new StringBuilder();
            StringBuilder attributes = new StringBuilder();

            sb.AppendLine(Lang.Get($"characterdesc-{classCode}"));
            sb.AppendLine();
            sb.AppendLine(Lang.Get("traits-title"));

            IOrderedEnumerable<Trait> charTraits = charClass.Traits
                .Where(code => charSystem.TraitsByCode.ContainsKey(code))
                .Select(code => charSystem.TraitsByCode[code])
                .OrderBy(trait => (int)trait.Type);

            foreach (Trait trait in charTraits)
            {
                attributes.Clear();
                foreach (KeyValuePair<string, double> val in trait.Attributes)
                {
                    if (attributes.Length > 0) attributes.Append(", ");
                    attributes.Append(Lang.Get(string.Format(
                        GlobalConstants.DefaultCultureInfo,
                        "charattribute-{0}-{1}", val.Key, val.Value)));
                }

                if (attributes.Length > 0)
                {
                    sb.AppendLine(Lang.Get("traitwithattributes",
                        Lang.Get("trait-" + trait.Code), attributes));
                }
                else
                {
                    string? desc = Lang.GetIfExists("traitdesc-" + trait.Code);
                    if (desc != null)
                        sb.AppendLine(Lang.Get("traitwithattributes",
                            Lang.Get("trait-" + trait.Code), desc));
                    else
                        sb.AppendLine(Lang.Get("trait-" + trait.Code));
                }
            }

            if (charClass.Traits.Length == 0)
                sb.AppendLine(Lang.Get("No positive or negative traits"));

            return sb.ToString();
        }

        private List<(GridRecipe[] recipes, ItemStack[] outputStacks)> GetGridRecipeGroups()
        {
            Dictionary<string, List<GridRecipe>> groups = new Dictionary<string, List<GridRecipe>>(StringComparer.Ordinal);

            foreach (GridRecipe recipe in capi.World.GridRecipes)
            {
                if (!string.Equals(recipe.RequiresTrait, classCode, StringComparison.OrdinalIgnoreCase)) continue;
                ItemStack? output = recipe.Output?.ResolvedItemStack;
                if (output?.Collectible?.Code == null) continue;
                string pc = GuiHandbookItemStackPage.PageCodeForStack(output);
                string groupKey = recipe.Attributes?["handbookOverviewGroup"].AsString(null) ?? pc;
                if (!groups.TryGetValue(groupKey, out List<GridRecipe>? list))
                    groups[groupKey] = list = new List<GridRecipe>();
                list.Add(recipe);
            }

            return groups.Values
                .OrderBy(list => list[0].Output?.ResolvedItemStack?.GetName() ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(list =>
                {
                    ItemStack[] outputStacks = list
                        .Select(r => r.Output?.ResolvedItemStack)
                        .Where(s => s?.Collectible?.Code != null)
                        .Cast<ItemStack>()
                        .ToArray();
                    return (recipes: list.ToArray(), outputStacks);
                })
                .ToList();
        }

    }
}
