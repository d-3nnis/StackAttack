using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using StackAttack.assets;
using Vintagestory.GameContent;
using StackAttack.Configuration;
using System;

namespace StackAttack
{
    public class StackAttackModSystem : ModSystem
    {
        ICoreClientAPI capi;
        ICoreServerAPI sapi;

        readonly string CHANNEL_NAME = "stackattack";

        public static Config config { get; set; }

        private void TryToLoadConfig(ICoreAPI api)
        {
            string configFileName = "StackAttackConfig.json";
            try
            {
                config = api.LoadModConfig<Config>(configFileName);
                if (config == null)
                {
                    config = new Config();
                }

                api.StoreModConfig<Config>(config, configFileName);
            }
            catch (Exception e)
            {
                Mod.Logger.Error("Could not load config! Loading default settings instead.");
                Mod.Logger.Error(e);
                config = new Config();
            }
        }

        public override void StartPre(ICoreAPI api)
        {
            base.StartPre(api);
            TryToLoadConfig(api);
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.Network.RegisterChannel(CHANNEL_NAME).RegisterMessageType<QuickStackPacket>();
            api.World.Logger.Event("Mod '{0}' started", Mod.Info.Name);
        }

        private void RegisterHotKeys(ICoreClientAPI api)
        {
            if(config.EnableQuickStackHotkey)
            {
                api.Input.RegisterHotKey("quickstack", "Quick Stack", GlKeys.V, HotkeyType.InventoryHotkeys);
                api.Input.SetHotKeyHandler("quickstack", QuickStackHotkey);
            }
            if(config.EnableDepositAllHotkey)
            {
                api.Input.RegisterHotKey("depositall", "Deposit All", GlKeys.B, HotkeyType.InventoryHotkeys);
                api.Input.SetHotKeyHandler("depositall", DepositAllHotkey);
            }
            if(config.EnableWithdrawAllHotkey)
            {
                api.Input.RegisterHotKey("withdrawall", "Withdraw All", GlKeys.B, HotkeyType.InventoryHotkeys, false, false, true);
                api.Input.SetHotKeyHandler("withdrawall", WithdrawAllHotkey);
            }
            if(config.EnableQuickStackNearbyHotkey)
            {
                api.Input.RegisterHotKey("quickstacknearby", "Quick Stack to Nearby Chests", GlKeys.N, HotkeyType.CharacterControls);
                api.Input.SetHotKeyHandler("quickstacknearby", QuickStackNearbyHotkey);
            }
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

        private static readonly HashSet<Type> SupportedContainerTypes = new HashSet<Type>
        {
            typeof(BlockEntityGenericTypedContainer),
            typeof(BlockEntityCrate)
        };

        private List<BlockPos> GetNearbyStorageContainers(IServerPlayer player, int radius)
        {
            if(sapi == null)
            {
                throw new InvalidOperationException("GetNearbyStorageContainers should be called from server side only.");
            }
            IBlockAccessor blockAccessor = sapi.World.BlockAccessor;
            List<BlockPos> containerPos = new List<BlockPos>();
            blockAccessor.SearchBlocks(player.Entity.Pos.XYZ.AsBlockPos.AddCopy(-radius, -radius, -radius), player.Entity.Pos.XYZ.AsBlockPos.AddCopy(radius, radius, radius), (block, pos) =>
            {
                var be = blockAccessor.GetBlockEntity(pos);
                if (be is BlockEntityContainer container && container.Inventory != null && SupportedContainerTypes.Contains(be.GetType()))
                {
                    containerPos.Add(pos.Copy());
                    sapi.Logger.Debug(
                        "[StackAttack] Found container at {0}: {1} (Type: {2}, Slots: {3})",
                        pos,
                        block.Code?.ToString() ?? "Unknown",
                        be.GetType().Name,
                        container.Inventory.Count
                    );
                }
                return true;
            });
            sapi.Logger.Debug(
                "[StackAttack] Player {0} found {1} nearby containers within radius {2}",
                player.PlayerName,
                containerPos.Count,
                radius
            );

            return containerPos;
        }

        private void OnStackAttackPacketRecieved(IServerPlayer fromPlayer, QuickStackPacket packet)
        {
            List<BlockPos> chestPositions;
            if(packet.MessageType == StackAttackMessageType.QuickStackNearby)
            {
                chestPositions = GetNearbyStorageContainers(fromPlayer, config.QuickStackNearbyRadius);
            } else
            {
                chestPositions = packet.ChestPositions;
            }

            InventoryBase playerInv = fromPlayer.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName) as InventoryBase;
            if (playerInv == null)
            {
                sapi.Logger.Error("Player inventory is null, HOW?");
                return;
            }
            foreach (var chestPos in chestPositions)
            {
                if(CanPlayerAccessContainer(fromPlayer, chestPos) == false)
                {
                    sapi.Logger.Debug("Player {0} cannot access container at {1}", fromPlayer.PlayerName, chestPos);
                    continue;
                }

                var containerBlock = sapi.World.BlockAccessor.GetBlockEntity(chestPos) as BlockEntityContainer;
                if (containerBlock == null)
                {
                    sapi.Logger.Debug("Block at {0} is not a container, was it removed?", chestPos);
                    continue;
                }
                InventoryBase containerInv = containerBlock.Inventory;
                if (containerInv == null) continue;
                bool isCrate = containerBlock is BlockEntityCrate;
                switch(packet.MessageType)
                {
                    case StackAttackMessageType.QuickStack:
                    case StackAttackMessageType.QuickStackNearby:
                        if (isCrate) PerformQuickStackCrate(playerInv, containerInv);
                        else PerformQuickStack(playerInv, containerInv, false);
                        break;
                    case StackAttackMessageType.DepositAll:
                        if (isCrate) PerformQuickStackCrate(playerInv, containerInv);
                        else PerformQuickStack(playerInv, containerInv, true);
                        break;
                    case StackAttackMessageType.WithdrawAll:
                        PerformQuickStack(containerInv, playerInv, true);
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

        private bool CanPlayerAccessContainer(IServerPlayer player, BlockPos containerPos)
        {
            BlockPos playerPos = player.Entity.Pos.XYZ.AsBlockPos;
            if (playerPos.DistanceTo(containerPos) > config.QuickStackNearbyRadius) return false;

            EnumWorldAccessResponse landClaim = sapi.World.Claims.TestAccess(player, containerPos, EnumBlockAccessFlags.Use);
            return landClaim == EnumWorldAccessResponse.Granted;
        }

        private bool ItemStackMatches(ItemSlot from, ItemSlot to)
        {
            if (from == null) return false;
            if (to == null) return false;

            bool match = from.Itemstack.Collectible.Equals(from.Itemstack, to.Itemstack);
            return match;
        }

        private void PerformQuickStack(InventoryBase fromInv, InventoryBase toInv, bool moveAll)
        {
            HashSet<CollectibleObject> chestCollectibles = new HashSet<CollectibleObject>();
            if (!moveAll)
            {
                chestCollectibles = toInv
                    .Where(slot => !slot.Empty)
                    .Select(slot => slot.Itemstack.Collectible)
                    .ToHashSet();
            }

            foreach (var fromSlot in fromInv)
            {
                if (fromSlot.Empty) continue;
                if (fromSlot is ItemSlotBackpack) continue;

                foreach (var toSlot in toInv)
                {
                    if (toSlot is ItemSlotBackpack) continue;
                    CheckMergeItems(fromSlot, toSlot);
                    if (fromSlot.Empty) break;
                }

                if (!fromSlot.Empty && (moveAll || chestCollectibles.Contains(fromSlot.Itemstack.Collectible)))
                {
                    foreach (var toSlot in toInv)
                    {
                        if (toSlot is ItemSlotBackpack) continue;
                        if (toSlot.Empty)
                        {
                            TransferItems(fromSlot, toSlot, true);
                            break;
                        }
                    }
                }
            }
        }

        private void PerformQuickStackCrate(InventoryBase fromInv, InventoryBase toInv)
        {
            foreach (var fromSlot in fromInv)
            {
                if (fromSlot.Empty) continue;
                if (fromSlot is ItemSlotBackpack) continue;

                foreach (var toSlot in toInv)
                {
                    if (toSlot is ItemSlotBackpack) continue;
                    if (toSlot.Empty) continue;
                    if (!fromSlot.Itemstack.Equals(sapi.World, toSlot.Itemstack, GlobalConstants.IgnoredStackAttributes)) continue;
                    CheckMergeItems(fromSlot, toSlot);
                    if (fromSlot.Empty) break;
                }

                bool crateHasMatchingStack = toInv.Any(slot => !slot.Empty
                    && fromSlot.Itemstack.Equals(sapi.World, slot.Itemstack, GlobalConstants.IgnoredStackAttributes));

                if (!fromSlot.Empty && crateHasMatchingStack)
                {
                    foreach (var toSlot in toInv)
                    {
                        if (toSlot is ItemSlotBackpack) continue;
                        if (toSlot.Empty)
                        {
                            TransferItems(fromSlot, toSlot, true);
                            break;
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

        private bool QuickStackNearbyHotkey(KeyCombination keyComb)
        {
            ClientStackManipOperation(StackAttackMessageType.QuickStackNearby);
            return true;
        }

        private void ClientStackManipOperation(StackAttackMessageType messageType)
        {
            var openInvsPos = GetOpenInventoriesPos();
            SendPacket(openInvsPos, messageType);
        }
    }
}
