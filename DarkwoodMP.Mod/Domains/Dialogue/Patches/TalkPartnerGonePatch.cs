using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The NPC this player talks or trades with is gone while the window is open (killed, a
    /// host body such as the morning trader despawned or re-created by entity sync). Vanilla
    /// pauses the game for a talk, so its NPC never goes mid-talk: close() and closeTrade() both
    /// need it, and with it destroyed the window could not be closed and Esc in a trade threw
    /// halfway, the Sell and Buy trays and their Accept button left on screen for good. The
    /// talk ends as vanilla's close does, with nobody to close on: the goods laid out to sell go
    /// back to the player, the gone trader's goods go with it.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "Update")]
    public static class TalkPartnerGonePatch
    {
        private static NPC _seen;   // reset-in: Reset
        private static NpcRef _ref; // reset-in: Reset (the talk's NPC as the lock knows it, taken while it lived)

        /// <summary>Registered with NetworkResetRegistry.</summary>
        public static void Reset()
        {
            _seen = null;
            _ref = default;
        }

        private static void Prefix(DialogueWindow __instance)
        {
            DialogueWindow dw = __instance;
            if (dw == null || !dw.opened || dw.tweening)
                return;
            NPC npc = dw.npc;
            if ((object)npc == null)
                return;
            if (npc != null)
            {
                if (!ReferenceEquals(npc, _seen))
                {
                    _seen = npc;
                    _ref = NpcRef.Of(npc);
                }
                return;
            }
            if (!NetGuard.Connected(out LanNetworkManager net))
                return;
            bool listener = DialogMirror.SpectatorActive;
            ModRuntime.LegacyInfo($"[Dialogue] {_ref.Name} gone mid-talk: the talk ends");
            try
            {
                if (listener)
                    DialogMirror.StopView(sendLeave: true); // its close finds the window shut
                EndTrade(dw);
                EndTalk(dw);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[Dialogue] ending a talk with a gone NPC: " + ex.Message);
            }
            if (!listener && ReferenceEquals(_seen, npc))
                ReleaseLock(net, _ref);
            _seen = null;
        }

        /// <summary>Vanilla closeTrade without the trader.</summary>
        private static void EndTrade(DialogueWindow dw)
        {
            Player p = Player.Instance;
            if (p == null || !p.inShop)
                return;
            Singleton<InventoryController>.Instance?.itemPopup?.hide();
            dw.trading.gameObject.SetActive(false);
            p.inShop = false;
            p.talkedToNPC = null;
            p.Hotbar.refresh();
            p.Inventory.hide();
            dw.exchangePlayer.transferAllItemsToPlayer();
            dw.exchangePlayer.hide();
            dw.exchangeTrader.clear();
            dw.exchangeTrader.hide();
            p.Hotbar.thisUI.transform.localPosition = p.Hotbar.position;
            PositionMe pos = p.Hotbar.thisUI.GetComponent<PositionMe>();
            if (pos != null)
            {
                pos.offset = new Vector2(p.Hotbar.position.x, p.Hotbar.position.z);
                pos.init();
            }
        }

        /// <summary>Vanilla close() without the NPC (its exit dialogue and close events went with it).</summary>
        private static void EndTalk(DialogueWindow dw)
        {
            dw.tweening = true;
            dw.currentMenu = DialogueWindow.CurrentMenu.main;
            dw.displayingDialogue = false;
            dw.needsDecision = false;
            dw.currentDialogue = null;
            dw.currentBoardElements.Clear();
            dw.wantToCook = false;
            dw.npc = null;
            Player.Instance?.halt();
            Core.forbidInputsFor(1.2f);
            Singleton<Controller>.Instance.Invoke(
                () => Traverse.Create(dw).Method("onTweenClose").GetValue(), 0.5f, timeScaleDependent: false);
            Singleton<UI>.Instance.tweenBlackScreenTop(new Color(0f, 0f, 0f, 1f), 0.5f);
        }

        /// <summary>As NpcDialogueLockReleasePatch does on a real close.</summary>
        private static void ReleaseLock(LanNetworkManager net, NpcRef npcRef)
        {
            if (string.IsNullOrEmpty(npcRef.Name))
                return;
            int localId = net.LocalPlayerId;
            if (net.Role == NetworkRole.Host)
            {
                NpcDialogueLock.HostRelease(net, npcRef, localId);
                return;
            }
            NpcRef released = NpcDialogueLock.Release(npcRef, localId);
            net.Send(NetMessageType.DialogNpcLock,
                w => NpcDialogueLock.BuildMessage(released, localId, granted: true, release: true).Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }
    }
}
