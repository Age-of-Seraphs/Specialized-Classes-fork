using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace SpecializedClasses
{
    public class ItemRestrictedBagHandbook : Item, ICustomHandbookPageContent
    {
        private const string AcceptedItemsTitleKey = "specializedclasses:handbooktitle-accepteditems";

        public void OnHandbookPageComposed(List<RichTextComponentBase> components, ItemSlot inSlot, ICoreClientAPI capi, ItemStack[] allStacks, ActionConsumable<string> openDetailPageFor)
        {
            int bagStorageFlags = Attributes?["backpack"]?["storageFlags"].AsInt() ?? 0;
            if (bagStorageFlags == 0 || allStacks == null || allStacks.Length == 0)
            {
                return;
            }

            List<ItemStack> acceptedStacks = BuildAcceptedStacks((EnumItemStorageFlags)bagStorageFlags, allStacks);
            if (acceptedStacks.Count == 0)
            {
                return;
            }

            bool haveText = true;
            CollectibleBehaviorHandbookTextAndExtraInfo.AddHeading(components, capi, AcceptedItemsTitleKey, ref haveText);
            components.Add(new ClearFloatTextComponent(capi, 2));

            CollectibleBehaviorHandbookTextAndExtraInfo? handbookBehavior = inSlot.Itemstack?.Collectible?.GetBehavior<CollectibleBehaviorHandbookTextAndExtraInfo>();
            handbookBehavior?.AddSlideShowComponent(components, capi, acceptedStacks, openDetailPageFor, false);
            components.Add(new RichTextComponent(capi, "\n", CairoFont.WhiteSmallText()));
        }

        private static List<ItemStack> BuildAcceptedStacks(EnumItemStorageFlags bagStorageFlags, ItemStack[] allStacks)
        {
            Dictionary<string, ItemStack> uniqueStacks = new(StringComparer.Ordinal);

            foreach (ItemStack stack in allStacks)
            {
                if (stack?.Collectible == null)
                {
                    continue;
                }

                EnumItemStorageFlags storageFlags;
                try
                {
                    storageFlags = stack.Collectible.GetStorageFlags(stack);
                }
                catch
                {
                    continue;
                }

                if ((storageFlags & bagStorageFlags) == 0)
                {
                    continue;
                }

                string uniqueKey;
                try
                {
                    uniqueKey = stack.Collectible.Code + "|" + stack.GetName();
                }
                catch
                {
                    uniqueKey = stack.Collectible.Code.ToShortString();
                }

                uniqueStacks.TryAdd(uniqueKey, stack);
            }

            return uniqueStacks.Values
                .OrderBy(stack =>
                {
                    try
                    {
                        return stack.GetName();
                    }
                    catch
                    {
                        return stack.Collectible.Code.ToShortString();
                    }
                }, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
