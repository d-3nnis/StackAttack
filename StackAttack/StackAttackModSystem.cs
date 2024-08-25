using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using StackAttack.assets;
using Vintagestory.GameContent;

namespace StackAttack
{
    public class StackAttackModSystem : ModSystem
    {
        ICoreClientAPI capi;
        ICoreServerAPI sapi;

        readonly string CHANNEL_NAME = "stackattack";

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.Network.RegisterChannel(CHANNEL_NAME).RegisterMessageType<QuickStackPacket>();
        }

        IClientNetworkChannel clientChannel;
        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            base.StartClientSide(api);
            api.Input.RegisterHotKey("quickstack", "Quick Stack", GlKeys.F, HotkeyType.InventoryHotkeys);
            api.Input.SetHotKeyHandler("quickstack", QuickStackHotkey);
            clientChannel = api.Network.GetChannel(CHANNEL_NAME);
        }

        IServerNetworkChannel serverChannel;
        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            base.StartServerSide(api);
            serverChannel = api.Network.GetChannel(CHANNEL_NAME).SetMessageHandler<QuickStackPacket>(new NetworkClientMessageHandler<QuickStackPacket>(this.OnQuickStackPacketRecieved));
        }

        private void OnQuickStackPacketRecieved(IServerPlayer fromPlayer, QuickStackPacket packet)
        {
            IInventory playerInv = fromPlayer.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            foreach (var chestPos in packet.ChestPositions)
            {
                var chestBlock = sapi.World.BlockAccessor.GetBlockEntity(chestPos) as BlockEntityGenericTypedContainer;
                if (chestBlock == null) continue;
                InventoryBase chestInv = chestBlock.Inventory;
                if (chestInv == null) continue;
                PerformQuickStack(playerInv, chestInv);
            }
        }
        private void TransferItems(ItemSlot from, ItemSlot to)
        {
            if (from == null || to == null) return;
            var maxStackSize = to.Itemstack?.Item?.MaxStackSize;
            if (maxStackSize == null)
            {
                maxStackSize = to.Itemstack?.Block?.MaxStackSize;
            }
            if (maxStackSize == null) return;
            int transferableAmount = GameMath.Min(from.StackSize, maxStackSize.Value - to.StackSize);
            to.Itemstack.StackSize += transferableAmount;
            from.Itemstack.StackSize -= transferableAmount;
            if (from.Itemstack.StackSize == 0) from.Itemstack = null;
        }

        private void PerformQuickStack(IInventory playerInventory, InventoryBase chestInventory)
        {
            HashSet<CollectibleObject> chestCollectibles = chestInventory
                .Where(slot => !slot.Empty)  // Filter out empty slots
                .Select(slot => slot.Itemstack.Collectible)  // Select the collectible types
                .ToHashSet();

            foreach (var playerSlot in playerInventory)
            {
                if (playerSlot.Empty) continue;

                foreach (var chestSlot in chestInventory)
                {
                    if (!chestSlot.Empty && playerSlot.Itemstack.Collectible.Equals(chestSlot.Itemstack.Collectible))
                    {
                        TransferItems(playerSlot, chestSlot);
                        chestSlot.MarkDirty();
                        playerSlot.MarkDirty();

                        if (playerSlot.Empty) break;
                    }
                }

                // second pass for empty slots
                if (!playerSlot.Empty)
                {
                    if (chestCollectibles.Contains(playerSlot.Itemstack.Collectible))
                    {
                        // Find the first empty slot and place the items there
                        foreach (var chestSlot in chestInventory)
                        {
                            if (chestSlot.Empty)
                            {
                                // Move the player's stack to the empty slot
                                chestSlot.Itemstack = playerSlot.Itemstack.Clone();
                                playerSlot.Itemstack = null;

                                // Mark the slots as dirty
                                chestSlot.MarkDirty();
                                playerSlot.MarkDirty();

                                // Break after transferring the stack to an empty slot
                                break;
                            }
                        }
                    }
                }

            }
        }

        private List<InventoryGeneric> GetOpenInventories()
        {
            var ret = capi.Gui.OpenedGuis.OfType<GuiDialogBlockEntity>().Select(gui => gui.Inventory).OfType<InventoryGeneric>().Where(inv => inv is InventoryGeneric).ToList();
            return ret;
        }

        private List<BlockPos> GetOpenInventoriesPos()
        {
           var ret = capi.Gui.OpenedGuis.OfType<GuiDialogBlockEntity>().Select(gui => gui.BlockEntityPosition).ToList();
            return ret;
        }

        private IInventory GetPlayerInventory()
        {
            var player = capi.World.Player;
            var playerInv = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            return playerInv;
        }

        private void SendQuickStackPacket(List<BlockPos> chestBlocks)
        {
            clientChannel.SendPacket<QuickStackPacket>(new QuickStackPacket(chestBlocks));
        }

        private bool QuickStackHotkey(KeyCombination keyComb)
        {
            ClientQuickStack();
            return true;
        }
        private void ClientQuickStack()
        {
            var openInvsPos = GetOpenInventoriesPos();
            SendQuickStackPacket(openInvsPos);
        }
    }
}
