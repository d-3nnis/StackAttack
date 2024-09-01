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

        private void RegisterHotKeys(ICoreClientAPI api)
        {
            api.Input.RegisterHotKey("quickstack", "Quick Stack", GlKeys.V, HotkeyType.InventoryHotkeys);
            api.Input.SetHotKeyHandler("quickstack", QuickStackHotkey);
            api.Input.RegisterHotKey("depositall", "Deposit All", GlKeys.B, HotkeyType.InventoryHotkeys);
            api.Input.SetHotKeyHandler("depositall", DepositAllHotkey);
            api.Input.RegisterHotKey("withdrawall", "Withdraw All", GlKeys.B, HotkeyType.InventoryHotkeys, false, false, true);
            api.Input.SetHotKeyHandler("withdrawall", WithdrawAllHotkey);
        }

        IClientNetworkChannel clientChannel;
        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            base.StartClientSide(api);
            RegisterHotKeys(capi);
            clientChannel = api.Network.GetChannel(CHANNEL_NAME);
        }

        IServerNetworkChannel serverChannel;
        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            base.StartServerSide(api);
            serverChannel = api.Network.GetChannel(CHANNEL_NAME).SetMessageHandler<QuickStackPacket>(new NetworkClientMessageHandler<QuickStackPacket>(this.OnStackAttackPacketRecieved));
        }

        private void OnStackAttackPacketRecieved(IServerPlayer fromPlayer, QuickStackPacket packet)
        {
            IInventory playerInv = fromPlayer.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            foreach (var chestPos in packet.ChestPositions)
            {
                var chestBlock = sapi.World.BlockAccessor.GetBlockEntity(chestPos) as BlockEntityGenericTypedContainer;
                if (chestBlock == null)
                {
                    sapi.Logger.Debug("Block at {0} is not a container, was it removed?", chestPos);
                    continue;
                }
                InventoryBase chestInv = chestBlock.Inventory;
                if (chestInv == null) continue;
                switch(packet.MessageType)
                {
                    case StackAttackMessageType.QuickStack:
                        PerformQuickStack(playerInv, chestInv);
                        break;
                    case StackAttackMessageType.DepositAll:
                        PerformDepositAll(playerInv, chestInv);
                        break;
                    case StackAttackMessageType.WithdrawAll:
                        PerformWithdrawAll(playerInv, chestInv);
                        break;
                    default:
                        sapi.Logger.Error("Unknown message type: {0}", packet.MessageType);
                        break;
                }
            }
        }
        private void TransferItems(ItemSlot from, ItemSlot to, bool allowEmpty = false)
        {
            if (from == null || to == null) return;
            var maxStackSize = to.Itemstack?.Item?.MaxStackSize;
            if (maxStackSize == null)
            {
                maxStackSize = to.Itemstack?.Block?.MaxStackSize;
            }
            // maxStackSize will be null if the slot is allowed to be empty.
            if (maxStackSize == null && !allowEmpty) return;

            if(allowEmpty)
            {
                to.Itemstack = from.Itemstack.Clone();
                from.Itemstack = null;

            } else
            {
                int transferableAmount = GameMath.Min(from.StackSize, maxStackSize.Value - to.StackSize);
                to.Itemstack.StackSize += transferableAmount;
                from.Itemstack.StackSize -= transferableAmount;
                if (from.Itemstack.StackSize == 0) from.Itemstack = null;
            }
            to.MarkDirty();
            from.MarkDirty();
        }

        private void PerformDepositAll(IInventory playerInventory, InventoryBase chestInventory)
        {
            foreach (var playerSlot in playerInventory)
            {
                if (playerSlot.Empty) continue;
                if (playerSlot is ItemSlotBackpack) continue;
                foreach (var chestSlot in chestInventory)
                {
                    if (chestSlot.Empty || playerSlot.Itemstack.Collectible.Equals(chestSlot.Itemstack.Collectible))
                    {
                        TransferItems(playerSlot, chestSlot, true);
                        break;
                    }
                }
            }
        }

        private void PerformWithdrawAll(IInventory playerInventory, InventoryBase chestInventory)
        {
            foreach(var chestSlot in chestInventory)
            {
                if (chestSlot.Empty) continue;
                foreach (var playerSlot in playerInventory)
                {
                if (playerSlot is ItemSlotBackpack) continue;
                    if (playerSlot.Empty)
                    {
                        TransferItems(chestSlot, playerSlot, true);
                        break;
                    }
                }
            }
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
                if (playerSlot is ItemSlotBackpack) continue;

                foreach (var chestSlot in chestInventory)
                {
                    bool hasBeenWorked = playerSlot.Itemstack.Attributes.HasAttribute("voxels");
                    bool spoils = playerSlot.Itemstack.Attributes.HasAttribute("transitionstate");
                    if (!chestSlot.Empty
                        && playerSlot.Itemstack.Collectible.Equals(chestSlot.Itemstack.Collectible) 
                        && !hasBeenWorked
                        && !spoils)
                    {
                        TransferItems(playerSlot, chestSlot);

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
                                TransferItems(playerSlot, chestSlot, true);

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

        private void SendPacket(List<BlockPos> chestBlocks, StackAttackMessageType type)
        {
            clientChannel.SendPacket<QuickStackPacket>(new QuickStackPacket(chestBlocks, type));
        }

        private bool QuickStackHotkey(KeyCombination keyComb)
        {
            ClientStackManipOperation(StackAttackMessageType.QuickStack);
            return true;
        }

        private bool DepositAllHotkey(KeyCombination keyComb)
        {
            ClientStackManipOperation(StackAttackMessageType.DepositAll);
            return true;
        }

        private bool WithdrawAllHotkey(KeyCombination keyComb)
        {
            ClientStackManipOperation(StackAttackMessageType.WithdrawAll);
            return true;
        }

        private void ClientStackManipOperation(StackAttackMessageType messageType)
        {
            var openInvsPos = GetOpenInventoriesPos();
            SendPacket(openInvsPos, messageType);
        }
    }
}
