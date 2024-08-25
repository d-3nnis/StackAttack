using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProtoBuf;
using Vintagestory;
using Vintagestory.API.MathTools;

namespace StackAttack.assets
{
    [ProtoContract]
    public class QuickStackPacket
    {
        public QuickStackPacket(List<BlockPos> chestBlocks)
        {
           ChestPositions = chestBlocks;
        }

        public QuickStackPacket() {
            ChestPositions = new List<BlockPos>();
        }

        [ProtoMember(1)]
        public List<BlockPos> ChestPositions;
        /*
        [ProtoMember(1)]
        public int PlayerSlotIndex { get; set; }
        [ProtoMember(2)]
        public int ChestSlotIndex { get; set; }
        [ProtoMember(3)]
        public int TransferAmount { get; set; }
        */
    }
}
