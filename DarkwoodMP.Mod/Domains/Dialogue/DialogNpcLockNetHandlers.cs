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

            if (_net.Role == NetworkRole.Host)
            {
                // A client speaks only for itself; the world bit is the requester's view.
                int sender = _net.CurrentReceivePlayerId;
                int owner = sender > 0 ? sender : msg.OwnerPlayerId;
                if (msg.Release)
                {
                    // Client ended a real talk. Vanilla close → onCloseDialogue runs only on the
                    // speaker; client one-shot GameEvents are blocked, so the bunker door's
                    // onLeaveDoorDialogue never fired on anyone. Replay the trigger on host.
                    int prevOwner = NpcDialogueLock.GetOwner(msg.NpcName, msg.Dream);
                    NpcDialogueLock.HostRelease(_net, msg.NpcName, owner, msg.Dream);

                    // Abort lookKeyhole world-only drain. Waiting for portrait boards caused a
                    // multi-second pause before leave-door GE (door finally opens late).
                    _net.DialogOutcomeHandlers.AbortWorldOnlyDrainForRelease(msg.NpcName, owner);

                    if (prevOwner < 0 || prevOwner == owner)
                        _net.DialogOutcomeHandlers.HostFireNpcCloseDialogue(msg.NpcName);
                    return;
                }
                if (msg.IsRequest || !msg.Granted)
                {
                    // The holder (either world: a dream may have started or ended mid-talk) is
                    // still talking: extend and refresh mirrors only. A fresh grant would replay
                    // onEnterDialogue every 30s.
                    if (msg.IsRequest && owner > 0 && NpcDialogueLock.HostRenewHeld(_net, msg.NpcName, owner))
                        return;
                    // A renewal that lost its hold re-takes the slot, never replaying onEnterDialogue.
                    NpcDialogueLock.HostTryGrant(_net, msg.NpcName, owner, msg.Dream,
                        fireEnterDialogue: !msg.Renewal);
                }
                return;
            }

            // Client: mirror host lock state (grant/deny/release) under the host's world bit.
            if (msg.Release)
            {
                NpcDialogueLock.Release(msg.NpcName, msg.OwnerPlayerId, msg.Dream);
                return;
            }

            if (msg.Granted)
            {
                // A peer or the local player holds the lock; track it to block dual talk.
                NpcDialogueLock.TryAcquire(msg.NpcName, msg.OwnerPlayerId, msg.Dream);
                return;
            }

            // Denied for the requestor only; other clients ignore it.
            if (msg.OwnerPlayerId != _net.LocalPlayerId)
                return;

            NpcDialogueLock.Release(msg.NpcName, msg.OwnerPlayerId, msg.Dream);
            try
            {
                var dw = Singleton<UI>.Instance?.dialogueWindow;
                if (dw != null && dw.npc != null && dw.npc.name == msg.NpcName && dw.opened)
                    dw.close();
                if (Player.Instance != null)
                {
                    DWMPHorde.Patches.PersonalFlavorHud.BeginBypass();
                    try { Player.Instance.displayMessage("Someone is already talking to them…"); }
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
