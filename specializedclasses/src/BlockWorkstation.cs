using Vintagestory.API.Common;

namespace SpecializedClasses.Workstations
{
    public class BlockWorkstation : Block
    {
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world == null || byPlayer == null || blockSel == null)
            {
                return false;
            }

            BlockEntityWorkstation? be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BlockEntityWorkstation;
            if (be == null)
            {
                return false;
            }

            return be.OnInteract(byPlayer);
        }
    }
}


