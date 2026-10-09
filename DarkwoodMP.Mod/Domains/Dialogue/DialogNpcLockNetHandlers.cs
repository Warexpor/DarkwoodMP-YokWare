using System;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>NPC dialogue lock grant/release handlers composed for 0.8.</summary>
    internal sealed class DialogNpcLockNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal DialogNpcLockNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleDialogNpcLock(DialogNpcLockMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName)) return;
            // Name, spot and world: NPC.name alone is shared by every hideout's oven.
            NpcRef npc = NpcRef.From(msg);

            if (_net.Role == NetworkRole.Host)
            {
                // A client speaks only for itself; the spot and world are the requester's view.
                int sender = _net.CurrentReceivePlayerId;
                int owner = sender > 0 ? sender : msg.OwnerPlayerId;
                if (msg.Release)
                {
                    // Client ended a real talk. Vanilla close → onCloseDialogue runs only on the
                    // speaker; client one-shot GameEvents are blocked, so the bunker door's
                    // onLeaveDoorDialogue never fired on anyone. Replay the trigger on host.
                    int prevOwner = NpcDialogueLock.GetOwner(npc);
                    // Others listen in: the talk goes on with them (one player's Exit does not end
                    // it for the rest), so the NPC's close events wait for the last one to leave.
                    if (prevOwner == owner && DialogMirror.HostHandOverTalk(_net, owner))
                    {
                        _net.DialogOutcomeApplyHandlers.AbortWorldOnlyDrainForRelease(npc, owner);
                        return;
                    }
                    NpcRef released = NpcDialogueLock.HostRelease(_net, npc, owner);

                    // Abort lookKeyhole world-only drain. Waiting for portrait boards caused a
                    // multi-second pause before leave-door GE (door finally opens late).
                    _net.DialogOutcomeApplyHandlers.AbortWorldOnlyDrainForRelease(released, owner);

                    if (prevOwner < 0 || prevOwner == owner)
                        _net.DialogOutcomeCloseHandlers.HostFireNpcCloseDialogue(released);
                    return;
                }
                if (msg.IsRequest || !msg.Granted)
                {
                    // The holder (either world: a dream may have started or ended mid-talk) is
                    // still talking: extend and refresh mirrors only. A fresh grant would replay
                    // onEnterDialogue every 30s.
                    if (msg.IsRequest && owner > 0 && NpcDialogueLock.HostRenewHeld(_net, npc, owner))
                        return;
                    // A renewal that lost its hold re-takes the slot, never replaying onEnterDialogue.
                    NpcDialogueLock.HostTryGrant(_net, npc, owner, fireEnterDialogue: !msg.Renewal);
                }
                return;
            }

            // Client: mirror host lock state (grant/deny/release) under the host's spot and world.
            if (msg.Release)
            {
                NpcDialogueLock.Release(npc, msg.OwnerPlayerId);
                return;
            }

            if (msg.Granted)
            {
                // A peer or the local player holds the lock; track it to block dual talk.
                NpcDialogueLock.TryAcquire(npc, msg.OwnerPlayerId);
                return;
            }

            // Denied for the requestor only; other clients ignore it.
            if (msg.OwnerPlayerId != _net.LocalPlayerId)
                return;

            NpcDialogueLock.Release(npc, msg.OwnerPlayerId);
            try
            {
                var dw = Singleton<UI>.Instance?.dialogueWindow;
                NPC talked = dw != null && dw.npc != null && npc.Matches(dw.npc) ? dw.npc : null;
                // Someone else got there first: close this talk and listen in on theirs.
                if (talked != null)
                {
                    DialogMirror.OnOwnTalkDenied(talked);
                    return;
                }
                if (Player.Instance != null)
                {
                    DWMPHorde.Patches.PersonalFlavorHud.BeginBypass();
                    try { Player.Instance.displayMessage(Loc.T("Someone is already talking to them…")); }
                    finally { DWMPHorde.Patches.PersonalFlavorHud.EndBypass(); }
                }
            }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[DialogLock] deny close: " + ex.Message);
            }
        }
    }
}
