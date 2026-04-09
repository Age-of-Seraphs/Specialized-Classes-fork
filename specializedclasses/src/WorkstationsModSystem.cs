using Vintagestory.API.Common;

namespace SpecializedClasses.Workstations
{
    public class WorkstationsModSystem : ModSystem
    {
        private static readonly bool DebugLogging = false;

        public override void Start(ICoreAPI api)
        {
            api.RegisterBlockClass("BlockWorkstation", typeof(BlockWorkstation));
            api.RegisterBlockEntityClass("Workstation", typeof(BlockEntityWorkstation));
            DebugLog(api, $"start side={api.Side}");
            WorkstationRecipeRegistrySystem? registrySystem = api.ModLoader.GetModSystem<WorkstationRecipeRegistrySystem>(true);
            DebugLog(api, $"registry system present={(registrySystem != null)} recipeCount={(registrySystem?.Recipes.Count ?? -1)}");
        }

        public override void AssetsFinalize(ICoreAPI api)
        {
            DebugLog(api, $"assets finalize side={api.Side}");
            WorkstationProfiles.Initialize(api);
            BlockEntityWorkstation.ClearBrowserOptionCache();

            if (api is Vintagestory.API.Client.ICoreClientAPI capi)
            {
                WorkstationHandbookHelper.ClearCache(capi);
                WorkstationHandbookHelper.WarmCache(capi);
            }
        }

        private static void DebugLog(ICoreAPI? api, string message)
        {
            if (!DebugLogging || api?.Logger == null)
            {
                return;
            }

            api.Logger.Notification($"Workstations: [modsystem] {message}");
        }
    }
}
