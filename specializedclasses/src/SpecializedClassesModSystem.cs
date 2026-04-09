using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SpecializedClasses
{
    public class SpecializedClassesModSystem : ModSystem
    {
        private const bool FORCE_TRAIT_REFRESH_EACH_LOGIN = false;
        private const string TRAIT_REFRESH_VERSION_KEY = "specializedclasses:traitRefreshVersion";
        private static readonly MethodInfo? ApplyTraitAttributesMethod =
            AccessTools.Method(typeof(CharacterSystem), "applyTraitAttributes", new[] { typeof(EntityPlayer) });
        private static int activeModSystemCount;
        private static int harmonyPatched;

        private Harmony? harmony;
        private ICoreServerAPI? serverApi;
        private string modVersion = "0.0.0";

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            modVersion = Mod?.Info?.Version ?? "0.0.0";
            Interlocked.Increment(ref activeModSystemCount);

            // In singleplayer, client and server mod systems live in the same process.
            // Guard PatchAll so we don't register every postfix twice.
            harmony = new Harmony("com.SpecializedClasses.patches");
            if (Interlocked.CompareExchange(ref harmonyPatched, 1, 0) == 0)
            {
                harmony.PatchAll();
                api.Logger.Notification("SpecializedClasses: patches applied successfully");
            }
            else
            {
                api.Logger.Notification("SpecializedClasses: patches already applied in this process");
            }

            // register custom item classes and block behaviors
            api.RegisterItemClass("ItemRestrictedBagHandbook", typeof(ItemRestrictedBagHandbook));
            api.RegisterBlockBehaviorClass("DropModificationBlockBehavior", typeof(DropModificationBlockBehavior));
            api.RegisterBlockBehaviorClass("ClutterPickupBlockBehavior", typeof(ClutterPickupBlockBehavior));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            serverApi = api;
            api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            EntityPlayer? entityPlayer = player.Entity as EntityPlayer;
            if (entityPlayer == null || serverApi == null)
            {
                return;
            }

            string playerName = player.PlayerName ?? "unknown";

            string lastAppliedVersion = entityPlayer.WatchedAttributes.GetString(TRAIT_REFRESH_VERSION_KEY, string.Empty);
            if (!FORCE_TRAIT_REFRESH_EACH_LOGIN && string.Equals(lastAppliedVersion, modVersion, StringComparison.Ordinal))
            {
                return;
            }

            CharacterSystem? characterSystem = serverApi.ModLoader.GetModSystem<CharacterSystem>(true);
            if (characterSystem == null || ApplyTraitAttributesMethod == null)
            {
                return;
            }

            try
            {
                ApplyTraitAttributesMethod.Invoke(characterSystem, new object[] { entityPlayer });

                entityPlayer.WatchedAttributes.SetString(TRAIT_REFRESH_VERSION_KEY, modVersion);
                // SetString already calls MarkPathDirty internally (SyncedTreeAttribute.SetString in vsapi)

                string previousVersion = string.IsNullOrEmpty(lastAppliedVersion) ? "none" : lastAppliedVersion;
                serverApi.Logger.Notification(
                    $"SpecializedClasses: trait refresh applied for player={playerName} oldVersion={previousVersion} newVersion={modVersion} forced={FORCE_TRAIT_REFRESH_EACH_LOGIN}"
                );
            }
            catch (Exception ex)
            {
                serverApi.Logger.Warning($"SpecializedClasses: trait refresh failed for player={playerName} error={ex}");
            }
        }

        public override void Dispose()
        {
            if (serverApi != null)
            {
                serverApi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
            }

            if (Interlocked.Decrement(ref activeModSystemCount) == 0 && Interlocked.Exchange(ref harmonyPatched, 0) == 1)
            {
                harmony?.UnpatchAll("com.SpecializedClasses.patches");
            }

            base.Dispose();
        }
    }
}
