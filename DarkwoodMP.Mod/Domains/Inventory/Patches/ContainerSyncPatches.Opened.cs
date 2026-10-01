using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{

    /// <summary>
    /// Shared before/after for workbench pile drains that bypass grab/transfer/
    /// place patches (<c>removeItemAmountFromPlayer(..., includeAdditionalInventory:
    /// true)</c> and durability drains on pile slots). Reuses
    /// <see cref="ContainerSnapshotHelper.SendFullDiff"/> — no second diff path.
    /// </summary>
    internal static class WorkbenchSharedPileSync
    {
        /// <summary>
        /// Outermost wrap only. Craft/repair/upgrade/construct/HammerWork already
        /// snapshot around the whole action (including product stack into the pile);
        /// the 0.8.114 choke on <c>removeItemAmountFromPlayer</c> nests inside those
        /// and must not SendFullDiff again (duplicate RemoveItem → host deny/refund).
        /// </summary>
        private static int _snapshotDepth; // process-scoped: call-scoped, unwound by its Finalizer/finally

        internal static void PrefixSnapshot(ref ContainerSnapshotState state)
        {
            state = default;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;

            Player player = Player.Instance;
            if (player?.openedItemInventory == null || !player.openedItemInventory.isWorkbench)
                return;
            Inventory pile = player.openedItemInventory2;
            if (!ContainerSyncHelpers.IsContainer(pile)) return;

            // Nested under an outer wrap — outer Postfix owns the single diff.
            if (_snapshotDepth > 0)
                return;

            state.Active = true;
            state.Snapshot = ContainerSnapshotHelper.TakeSnapshot(pile);
            _snapshotDepth++;
        }

        /// <summary>
        /// Closes the snapshot taken by <see cref="PrefixSnapshot"/>. Runs from every patch's
        /// Finalizer, not the Postfix: Harmony skips Postfix when the original throws, and a depth
        /// left up makes every later pile drain look nested, so pile sync would stay off until
        /// restart. Postfix only sends the diff; the Finalizer owns the depth.
        /// </summary>
        internal static void FinalizeSnapshot(ContainerSnapshotState state)
        {
            if (state.Active && _snapshotDepth > 0)
                _snapshotDepth--;
        }

        internal static void PostfixSendFullDiff(ContainerSnapshotState state)
        {
            if (!state.Active) return;
            Inventory pile = Player.Instance?.openedItemInventory2;
            if (pile == null) return;
            // SendFullDiff is a true slot diff: identical before/after (bag-only
            // remove with workbench still open) sends nothing.
            ContainerSnapshotHelper.SendFullDiff(pile, state.Snapshot);
        }
    }

    /// <summary>
    /// Workbench shared pile craft: vanilla <c>doCraft</c> →
    /// <c>removeItemAmountFromPlayer(..., includeAdditionalInventory: true)</c>
    /// mutates <c>openedItemInventory2</c> via <c>removeItemAmount</c> /
    /// <c>removeAmount</c>, which never hit grab/transfer/place container
    /// patches. Snapshot the pile and fan ContainerItem Remove/Place so the
    /// host applies the consume (and any product stacked into the pile).
    /// Personal bag product/ingredients stay local.
    /// </summary>
    [HarmonyPatch(typeof(CraftingRecipes), "doCraft")]
    public static class CraftSharedPileSyncPatch
    {
        private static void Prefix(ref ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PrefixSnapshot(ref __state);

        private static void Postfix(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PostfixSendFullDiff(__state);

        [HarmonyFinalizer]
        private static void Finalizer(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.FinalizeSnapshot(__state);
    }

    /// <summary>
    /// Workbench repair: <c>InvItemClass.repair</c> →
    /// <c>RepairRequirements.removeIngredients</c> drains the shared pile the
    /// same way craft does. Repaired item stays on the crafter's slot.
    /// </summary>
    [HarmonyPatch(typeof(InvItemClass), "repair")]
    public static class RepairSharedPileSyncPatch
    {
        private static void Prefix(ref ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PrefixSnapshot(ref __state);

        private static void Postfix(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PostfixSendFullDiff(__state);

        [HarmonyFinalizer]
        private static void Finalizer(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.FinalizeSnapshot(__state);
    }

    /// <summary>
    /// Workbench item upgrade: <c>ItemUpgrade.removeIngredients</c> (from
    /// <c>Player.progressBarCompleted</c>) drains pile materials via
    /// <c>CraftingRequirement.removeIngredients</c>. Upgrade stays on the
    /// personal <c>InvItemClass</c>.
    /// </summary>
    [HarmonyPatch(typeof(ItemUpgrade), "removeIngredients")]
    public static class UpgradeSharedPileSyncPatch
    {
        private static void Prefix(ref ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PrefixSnapshot(ref __state);

        private static void Postfix(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PostfixSendFullDiff(__state);

        [HarmonyFinalizer]
        private static void Finalizer(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.FinalizeSnapshot(__state);
    }

    /// <summary>
    /// Construction menu place: <c>Constructible.construct(manual: true)</c>
    /// loops <c>ConstructionRequirement.removeIngredients</c>, which drains the
    /// open workbench pile the same way craft does. World prop spawn is already
    /// fanned by <c>ConstructibleConstructPatch</c> (<c>ConstructibleConstruction</c>);
    /// this only syncs the ingredient consume. Remote apply uses
    /// <c>manual: false</c> (no drain). Snapshot once around construct — do not
    /// patch each requirement (would multi-send).
    /// </summary>
    [HarmonyPatch(typeof(Constructible), "construct", new[] { typeof(bool), typeof(int) })]
    public static class ConstructSharedPileSyncPatch
    {
        private static void Prefix(ref ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PrefixSnapshot(ref __state);

        private static void Postfix(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PostfixSendFullDiff(__state);

        [HarmonyFinalizer]
        private static void Finalizer(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.FinalizeSnapshot(__state);
    }

    /// <summary>
    /// Barricade finish: vanilla <c>Player.checkFrameTrigger("HammerWork")</c>
    /// when <c>doneBuilding</c> loops <c>currentConstruction.requirements</c>
    /// through <c>removeItemAmountFromPlayer(..., includeAdditionalInventory:
    /// true)</c> — same shared-pile hole as construct. World plank state already
    /// fans via <c>BarricadeEvent</c> (0.8.109 alert stays there; this patch
    /// does not alert). Gate on finish only so mid-swing hammers send nothing.
    /// </summary>
    [HarmonyPatch(typeof(Player), "checkFrameTrigger")]
    public static class HammerWorkSharedPileSyncPatch
    {
        private static void Prefix(Player __instance, string eventInfo, ref ContainerSnapshotState __state)
        {
            __state = default;
            if (eventInfo != "HammerWork" || __instance == null || !__instance.doneBuilding)
                return;
            WorkbenchSharedPileSync.PrefixSnapshot(ref __state);
        }

        private static void Postfix(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PostfixSendFullDiff(__state);

        [HarmonyFinalizer]
        private static void Finalizer(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.FinalizeSnapshot(__state);
    }

    /// <summary>
    /// Choke-point for every vanilla
    /// <c>removeItemAmountFromPlayer(..., includeAdditionalInventory: true)</c>
    /// drain — including GameEvent <c>addOrRemoveInvItem</c>. Entry patches
    /// (craft/repair/upgrade/construct/HammerWork) still snapshot the outer
    /// action; nested choke Prefix is skipped via snapshot depth (0.8.116).
    /// Covers GE actor apply (0.8.99) where the remove runs after
    /// <c>WaitForSeconds</c> (NetworkApplyGuard already gone) while the
    /// workbench pile can still be open (e.g. container story triggers).
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "removeItemAmountFromPlayer")]
    public static class RemoveAmountSharedPileSyncPatch
    {
        private static void Prefix(bool includeAdditionalInventory, ref ContainerSnapshotState __state)
        {
            __state = default;
            if (!includeAdditionalInventory) return;
            WorkbenchSharedPileSync.PrefixSnapshot(ref __state);
        }

        private static void Postfix(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PostfixSendFullDiff(__state);

        [HarmonyFinalizer]
        private static void Finalizer(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.FinalizeSnapshot(__state);
    }

    /// <summary>
    /// Same choke for durability drains
    /// (<c>removeItemDurabilityFromPlayer(..., includeAdditionalInventory: true)</c>).
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "removeItemDurabilityFromPlayer")]
    public static class RemoveDurabilitySharedPileSyncPatch
    {
        private static void Prefix(bool includeAdditionalInventory, ref ContainerSnapshotState __state)
        {
            __state = default;
            if (!includeAdditionalInventory) return;
            WorkbenchSharedPileSync.PrefixSnapshot(ref __state);
        }

        private static void Postfix(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.PostfixSendFullDiff(__state);

        [HarmonyFinalizer]
        private static void Finalizer(ContainerSnapshotState __state) =>
            WorkbenchSharedPileSync.FinalizeSnapshot(__state);
    }

    /// <summary>Syncs transferring 1 item from player inventory to the opened container.</summary>
    [HarmonyPatch(typeof(InvSlot), "transferItemToOpenedInv")]
    public static class ContainerTransferToOpenedInvPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSnapshotState __state)
        {
            __state = default;
            Inventory destInv = Player.Instance?.openedItemInventory2 ?? Player.Instance?.openedItemInventory;
            if (!ContainerSyncHelpers.IsContainer(destInv)) return;
            __state.Active = true;
            __state.Snapshot = ContainerSnapshotHelper.TakeSnapshot(destInv);
        }

        private static void Postfix(InvSlot __instance, ContainerSnapshotState __state)
        {
            if (!__state.Active) return;
            Inventory destInv = Player.Instance?.openedItemInventory2 ?? Player.Instance?.openedItemInventory;
            if (destInv == null) return;
            ContainerSnapshotHelper.SendDiff(destInv, __state.Snapshot);
        }
    }

    /// <summary>Syncs transferring all items from player inventory to the opened container.</summary>
    [HarmonyPatch(typeof(InvSlot), "transferItemAllToOpenedInv")]
    public static class ContainerTransferAllToOpenedInvPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSnapshotState __state)
        {
            __state = default;
            Inventory destInv = Player.Instance?.openedItemInventory2 ?? Player.Instance?.openedItemInventory;
            if (!ContainerSyncHelpers.IsContainer(destInv)) return;
            __state.Active = true;
            __state.Snapshot = ContainerSnapshotHelper.TakeSnapshot(destInv);
        }

        private static void Postfix(InvSlot __instance, ContainerSnapshotState __state)
        {
            if (!__state.Active) return;
            Inventory destInv = Player.Instance?.openedItemInventory2 ?? Player.Instance?.openedItemInventory;
            if (destInv == null) return;
            ContainerSnapshotHelper.SendDiff(destInv, __state.Snapshot);
        }
    }

    /// <summary>Syncs placing an item into a container slot via controller input.</summary>
    [HarmonyPatch(typeof(InvSlot), "controllerPlaceItem")]
    public static class ContainerControllerPlaceItemPatch
    {
        private static void Prefix(InvSlot __instance, bool force, ref ContainerSlotActionState __state)
        {
            __state = default;
            if (!ContainerSyncHelpers.IsContainer(__instance)) return;
            // Vanilla controllerPlaceItem is a no-op for a death-drop bag unless forced.
            if (!force && __instance.inventory.invType == Inventory.InvType.deathDrop) return;

            var pickedUp = Singleton<Controller>.Instance?.pickedUpItem;
            if (pickedUp == null || InvItemClass.isNull(pickedUp)) return;

            __state.Active = true;
            __state.IsRecipe = pickedUp.isRecipe;
            __state.Type = __state.IsRecipe ? pickedUp.recipeFor : pickedUp.type;
            __state.Amount = pickedUp.amount;
            __state.Dur = pickedUp.durability;
            __state.Ammo = pickedUp.ammo;
            __state.Upgrades = Sync.InvItemUpgradeWire.CollectNames(pickedUp);
            __state.ShouldBeActive = pickedUp.shouldBeActive;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
        }

        private static void Postfix(InvSlot __instance, bool __runOriginal, ContainerSlotActionState __state)
        {
            if (!__state.Active || !__runOriginal) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo, isPlayerPlaced: true, isRecipe: __state.IsRecipe, upgrades: __state.Upgrades, shouldBeActive: __state.ShouldBeActive);
        }
    }

    /// <summary>
    /// Syncs container item removal when a player picks up a journal/quest/key
    /// item from a container slot via defaultActivateItem().
    /// </summary>
    [HarmonyPatch(typeof(InvSlot), "defaultActivateItem")]
    public static class ContainerDefaultActivateItemPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSlotActionState __state)
        {
            __state = default;
            if (!ContainerSyncHelpers.IsContainer(__instance)) return;
            if (InvItemClass.isNull(__instance.invItem)) return;

            var baseClass = __instance.invItem.baseClass;
            bool hadJournalComponent = baseClass != null && (
                baseClass.GetComponent<JournalNoteReference>() != null ||
                baseClass.GetComponent<KeyReference>() != null ||
                baseClass.GetComponent<QuestItemReference>() != null);

            if (!hadJournalComponent) return;

            __state.Active = true;
            __state.IsRecipe = __instance.invItem.isRecipe;
            __state.Type = __state.IsRecipe ? __instance.invItem.recipeFor : __instance.invItem.type;
            __state.Amount = __instance.invItem.amount;
            __state.Dur = __instance.invItem.durability;
            __state.Ammo = __instance.invItem.ammo;
            __state.Upgrades = Sync.InvItemUpgradeWire.CollectNames(__instance.invItem);
            __state.ShouldBeActive = __instance.invItem.shouldBeActive;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
        }

        private static void Postfix(InvSlot __instance, bool __result, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            // Journal items cause the slot to be emptied (removeAmount was called)
            // and the method returns true. Send a RemoveItem to clear the
            // corresponding slot on the remote peer.
            if (__result && InvItemClass.isNull(__instance.invItem))
            {
                ContainerSyncHelpers.SendContainerAction(ContainerAction.RemoveItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo, isRecipe: __state.IsRecipe, upgrades: __state.Upgrades, shouldBeActive: __state.ShouldBeActive);
            }
        }
    }

    /// <summary>
    /// Syncs the 'searched' flag on containers so the remote client sees
    /// "(Searched)" when hovering over a container that the host has already
    /// opened. Also syncs Character.searched on dead bodies.
    /// On the Client side, requests the full container state from the Host
    /// so that items the Host already looted are correctly removed.
    /// </summary>
    [HarmonyPatch(typeof(Item), "openInventory")]
    public static class ContainerSearchedPatch
    {
        private static void Postfix(Item __instance)
        {
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            // Access the private 'inventory' field via Harmony Traverse
            Inventory inv = HarmonyLib.Traverse.Create(__instance).Field("inventory").GetValue<Inventory>();
            if (inv == null) return;
            var net = ModRuntime.Network;

            Vector3 pos = inv.transform.position;

            if (net.Role == NetworkRole.Host)
            {
                // Host: notify the client that this container was opened
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Container] SearchedPatch(HOST): sending Searched for {inv.name} at {pos}");

                var msg = new ContainerItemMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    Action = ContainerAction.Searched,
                    SlotIndex = 0,
                    ItemType = "",
                    Amount = 0,
                    Durability = 0,
                    Ammo = 0,
                    IsPlayerPlaced = false
                };
                var netInst = LanNetworkManager.Instance;
                if (netInst != null)
                    netInst.Broadcast(NetMessageType.ContainerItem, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            else
            {
                // Client: request the full container state from the host.
                // Include the entity's stable hash for exact lookup.
                int entityHash = 0;
                Character c = __instance.GetComponent<Character>();
                if (c != null)
                    entityHash = Sync.CharacterTracker.GetStableId(c);

                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Container] SearchedPatch(CLIENT): requesting state for {inv.name} at {pos} hash={entityHash}");

                var req = new ContainerStateRequestMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    TargetEntityHash = entityHash
                };
                var netInst = LanNetworkManager.Instance;
                if (netInst != null)
                    netInst.Send(NetMessageType.ContainerStateRequest, w => req.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
        }
    }
}
