using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace SpecializedClasses
{
    public class ClassHandbookModSystem : ModSystem
    {
        // All 15 SC class codes in the order they appear in characterclasses.json.
        private static readonly string[] ScClassCodes =
        [
            "archivist", "blackguard", "brickmaker", "butcher", "clockmaker",
            "farmhand", "florist", "forester", "hunter", "malefactor",
            "messenger", "quarrier", "spelunker", "tailor", "vintner"
        ];

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            ModSystemSurvivalHandbook? handbook = api.ModLoader.GetModSystem<ModSystemSurvivalHandbook>(true);
            if (handbook == null)
            {
                api.Logger.Warning("ClassHandbookModSystem: ModSystemSurvivalHandbook not found – class handbook pages will not be created.");
                return;
            }

            handbook.OnInitCustomPages += pages => OnInitCustomPages(api, pages);
        }

        private void OnInitCustomPages(ICoreClientAPI capi, List<GuiHandbookPage> pages)
        {
            CharacterSystem? charSystem = capi.ModLoader.GetModSystem<CharacterSystem>(true);
            if (charSystem == null)
            {
                capi.Logger.Warning("ClassHandbookModSystem: CharacterSystem not found – class handbook pages will not be created.");
                return;
            }

            foreach (string classCode in ScClassCodes)
            {
                CharacterClass? charClass = charSystem.characterClasses
                    ?.FirstOrDefault(c => string.Equals(c.Code, classCode, StringComparison.Ordinal));
                if (charClass == null)
                    continue;

                string displayName = Lang.Get($"game:characterclass-{classCode}");
                GuiHandbookClassPage page = new GuiHandbookClassPage(capi, classCode, displayName, charClass, charSystem)
                {
                    Visible = true
                };
                pages.Add(page);
            }
        }
    }
}
