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
            InventoryBase playerInv = fromPlayer.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName) as InventoryBase;
            if (playerInv == null)
            {
                sapi.Logger.Error("Player inventory is null, HOW?");
            }
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
                        PerformQuickStack(playerInv, chestInv, false);
                        break;
                    case StackAttackMessageType.DepositAll:
                        PerformQuickStack(playerInv, chestInv, true);
                        break;
                    case StackAttackMessageType.WithdrawAll:
                        PerformQuickStack(chestInv, playerInv, true);
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

            if(allowEmpty && to.Itemstack == null)
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

        public static bool ItemSpoils(ItemStack itemstack)
        {
            if (itemstack == null) return false;
            return itemstack.Attributes.HasAttribute("transitionstate");
        }

        public static bool ItemHasBeenWorked(ItemStack itemstack)
        {
            if (itemstack == null) return false;
            return itemstack.Attributes.HasAttribute("voxels");
        }

        private void CheckMergeItems(ItemSlot from, ItemSlot to)
        {
            if(from == null || to == null) return;
            if(from.Empty || to.Empty) return;
            bool hasBeenWorked = ItemHasBeenWorked(from.Itemstack) || ItemHasBeenWorked(to.Itemstack);
            bool spoils = ItemSpoils(from.Itemstack) || ItemSpoils(to.Itemstack);
            bool matches = ItemStackMatches(from, to);
            if (!to.Empty
                && matches 
                && !hasBeenWorked
                && !spoils)
            {
                TransferItems(from, to, false);
            }
        }

        private bool ItemStackMatches(ItemSlot from, ItemSlot to)
        {
            if (from == null) return false;
            if (to == null) return false;

            bool match = from.Itemstack.Collectible.Equals(from.Itemstack, to.Itemstack);
            return match;
        }

        private void PerformQuickStack(InventoryBase fromInv, InventoryBase toInv, bool moveAll = false)
        {
            HashSet<CollectibleObject> chestCollectibles = new HashSet<CollectibleObject>();
            if(!moveAll)
            {
                chestCollectibles = toInv
                    .Where(slot => !slot.Empty)  // Filter out empty slots
                    .Select(slot => slot.Itemstack.Collectible)  // Select the collectible types
                    .ToHashSet();
            }

            foreach (var fromSlot in fromInv)
            {
                // Skip empty slots and backpack slots
                if (fromSlot.Empty) continue;
                if (fromSlot is ItemSlotBackpack) continue;
                
                // Try to fill partial stacks in the target inventory
                foreach (var toSlot in toInv)
                {
                    // Skip backpack slots in target
                    if (toSlot is ItemSlotBackpack) continue;
                    CheckMergeItems(fromSlot, toSlot);
                    if (fromSlot.Empty) break;
                }

                // First pass could not empty this fromSlot, try to find an empty slot
                if (!fromSlot.Empty)
                {
                    if (chestCollectibles.Contains(fromSlot.Itemstack.Collectible) || moveAll)
                    {
                        // Find the first empty slot and place the items there
                        foreach (var toSlot in toInv)
                        {
                            if (toSlot is ItemSlotBackpack) continue;
                            if (toSlot.Empty)
                            {
                                // Move the player's stack to the empty slot
                                TransferItems(fromSlot, toSlot, true);
                                // Break after transferring the stack to an empty slot
                                break;
                            }
                        }
                    }
                }

            }
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
