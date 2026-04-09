using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SpecializedClasses.Workstations
{
    public static class WorkstationLogic
    {
        private const string CHARACTER_CLASS_KEY = "characterClass";
        private const string EXTRA_TRAITS_KEY = "extraTraits";

        public static void ShowIngameError(IWorldAccessor world, IPlayer byPlayer, string errorKey, string message)
        {
            if (world.Side == EnumAppSide.Server && byPlayer is IServerPlayer serverPlayer && world.Api is ICoreServerAPI sapi)
            {
                sapi.SendIngameError(serverPlayer, $"specializedclasses:{errorKey}", message);
                return;
            }

            if (world.Side != EnumAppSide.Client)
            {
                return;
            }

            if (byPlayer is not IClientPlayer || world.Api is not ICoreClientAPI capi)
            {
                return;
            }

            capi.TriggerIngameError(byPlayer, $"specializedclasses:{errorKey}", message);
        }

        public static bool PlayerHasTrait(ICoreAPI api, IPlayer player, string traitCode)
        {
            if (api == null || player?.Entity is not EntityPlayer entityPlayer || string.IsNullOrWhiteSpace(traitCode))
            {
                return false;
            }

            if (api.World?.Config?.GetBool("classExclusiveRecipes", true) == false)
            {
                return true;
            }

            string required = traitCode.Trim();
            string[] extras = entityPlayer.WatchedAttributes.GetStringArray(EXTRA_TRAITS_KEY) ?? Array.Empty<string>();
            foreach (string extra in extras)
            {
                if (string.Equals(extra, required, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            CharacterSystem? characterSystem = api.ModLoader.GetModSystem<CharacterSystem>(true);
            if (characterSystem == null)
            {
                return false;
            }

            string classCode = entityPlayer.WatchedAttributes.GetString(CHARACTER_CLASS_KEY);
            CharacterClass? charClass = characterSystem.characterClasses?.FirstOrDefault(c => string.Equals(c.Code, classCode, StringComparison.Ordinal));
            if (charClass?.Traits == null)
            {
                return false;
            }

            foreach (string trait in charClass.Traits)
            {
                if (string.Equals(trait, required, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static string GetTraitDisplayName(string traitCode)
        {
            if (string.IsNullOrWhiteSpace(traitCode))
            {
                return string.Empty;
            }

            string key = $"game:traitname-{traitCode}";
            string resolved = Lang.Get(key);
            return string.Equals(resolved, key, StringComparison.Ordinal) ? traitCode : resolved;
        }
    }
}
