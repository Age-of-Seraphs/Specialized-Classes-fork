using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace SpecializedClasses
{
    public class ClutterPickupBlockBehavior : BlockBehavior
    {
        private const string CLUTTER_PICKUP_STAT = "canPickupClutter";
        private const int CHISEL_DURABILITY_COST = 10;

        public ClutterPickupBlockBehavior(Block block) : base(block)
        {
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
        {
            if (world == null || byPlayer == null || blockSel == null)
            {
                return false;
            }

            CollectibleObject? heldItem = byPlayer.InventoryManager.ActiveHotbarSlot.Itemstack?.Collectible;
            if (heldItem == null)
            {
                return false;
            }

            bool isChisel = heldItem.Code?.Path?.Contains("chisel") == true;
            if (!isChisel)
            {
                return false;
            }

            if (!CheckPlayerStat(byPlayer))
            {
                return false;
            }

            if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
            {
                return false;
            }

            handling = EnumHandling.PreventDefault;

            if (world.Side == EnumAppSide.Client)
            {
                PlayPickupEffects(world, blockSel);
                return true;
            }

            BlockEntity? be = world.BlockAccessor.GetBlockEntity(blockSel.Position);
            if (be == null)
            {
                return false;
            }

            string? clutterType = null;
            string? variant = null;
            string blockCodeToUse = block.Code.ToString();

            BEBehaviorClutterBookshelfWithLore? loreBehavior = be.GetBehavior<BEBehaviorClutterBookshelfWithLore>();
            if (loreBehavior != null)
            {
                clutterType = loreBehavior.Type;
                variant = loreBehavior.Variant;

                // convert lore bookshelf to non-lore agedacacia variant
                blockCodeToUse = "game:clutteredbookshelf-agedacacia";
                // strip -lore from type string (e.g., bookshelf-ruined-full-lore3 -> bookshelf-ruined-full3)
                clutterType = clutterType?.Replace("-lore", "");
            }

            if (clutterType == null)
            {
                BEBehaviorClutterBookshelf? bookshelfBehavior = be.GetBehavior<BEBehaviorClutterBookshelf>();
                if (bookshelfBehavior != null)
                {
                    clutterType = bookshelfBehavior.Type;
                    variant = bookshelfBehavior.Variant;
                }
            }

            if (clutterType == null)
            {
                BEBehaviorShapeFromAttributes? shapeFromAtts = be.GetBehavior<BEBehaviorShapeFromAttributes>();
                if (shapeFromAtts != null)
                {
                    clutterType = shapeFromAtts.Type;
                }
            }

            if (clutterType == null)
            {
                return false;
            }
            HandleClutterPickup(world, blockSel, byPlayer, clutterType, variant ?? "", blockCodeToUse);
            return true;
        }

        private void PlayPickupEffects(IWorldAccessor world, BlockSelection blockSel)
        {
            world.PlaySoundAt(new AssetLocation("sounds/player/buildhigh"), blockSel.Position.X + 0.5, blockSel.Position.Y + 0.5, blockSel.Position.Z + 0.5);
        }

        private bool CheckPlayerStat(IPlayer byPlayer)
        {
            if (byPlayer == null)
            {
                return false;
            }

            float blended = byPlayer.Entity?.Stats?.GetBlended(CLUTTER_PICKUP_STAT) ?? 1f;

            return blended > 1f;
        }

        private void HandleClutterPickup(IWorldAccessor world, BlockSelection blockSel, IPlayer byPlayer, string clutterType, string variant, string blockCodeToUse)
        {
            Block? baseClutterBlock = world.GetBlock(new AssetLocation(blockCodeToUse));

            if (baseClutterBlock == null)
            {
                return;
            }

            ItemStack stack = new ItemStack(baseClutterBlock, 1);
            stack.Attributes.SetString("type", clutterType);
            stack.Attributes.SetBool("collected", true);

            if (!string.IsNullOrEmpty(variant))
            {
                stack.Attributes.SetString("variant", variant);
            }

            if (!byPlayer.InventoryManager.TryGiveItemstack(stack, true))
            {
                world.SpawnItemEntity(stack, blockSel.Position.ToVec3d().AddCopy(0.5, 0.1, 0.5));
            }

            ItemSlot chiselSlot = byPlayer.InventoryManager.ActiveHotbarSlot;
            if (chiselSlot?.Itemstack != null)
            {
                chiselSlot.Itemstack.Collectible.DamageItem(world, byPlayer.Entity, chiselSlot, CHISEL_DURABILITY_COST, destroyOnZeroDurability: true);
            }

            world.BlockAccessor.SetBlock(0, blockSel.Position);
            world.BlockAccessor.TriggerNeighbourBlockUpdate(blockSel.Position);
        }
    }
}
