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
                if (msg.Release)
                {
                    // Client ended a real talk. Vanilla close → onCloseDialogue runs only on the
                    // speaker; client one-shot GameEvents are blocked, so the bunker door's
                    // onLeaveDoorDialogue never fired on anyone. Replay the trigger on host.
                    int prevOwner = NpcDialogueLock.GetOwner(msg.NpcName);
                    NpcDialogueLock.HostRelease(_net, msg.NpcName, msg.OwnerPlayerId);

                    // Abort lookKeyhole world-only drain. Waiting for portrait boards caused a
                    // multi-second pause before leave-door GE (door finally opens late).
                    _net.DialogOutcomeHandlers.AbortWorldOnlyDrainForRelease();

                    if (prevOwner < 0 || prevOwner == msg.OwnerPlayerId)
                        _net.DialogOutcomeHandlers.HostFireNpcCloseDialogue(msg.NpcName);
                    return;
                }
                if (msg.IsRequest || !msg.Granted)
                {
                    int owner = msg.OwnerPlayerId > 0 ? msg.OwnerPlayerId : _net.CurrentReceivePlayerId;
                    NpcDialogueLock.HostTryGrant(_net, msg.NpcName, owner);
                }
                return;
            }

            // Client: mirror host lock state (grant/deny/release).
            if (msg.Release)
            {
                NpcDialogueLock.Release(msg.NpcName, msg.OwnerPlayerId);
                return;
            }

            if (msg.Granted)
            {
                // A peer or the local player holds the lock; track it to block dual talk.
                NpcDialogueLock.TryAcquire(msg.NpcName, msg.OwnerPlayerId);
                return;
            }

            // Denied for the requestor only; other clients ignore it.
            if (msg.OwnerPlayerId != _net.LocalPlayerId)
                return;

            NpcDialogueLock.Release(msg.NpcName, msg.OwnerPlayerId);
            try
            {
                var dw = Singleton<UI>.Instance?.dialogueWindow;
                if (dw != null && dw.npc != null && dw.npc.name == msg.NpcName && dw.opened)
                    dw.close();
                if (Player.Instance != null)
                    Player.Instance.displayMessage("Someone is already talking to them…");
            }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[DialogLock] deny close: " + ex.Message);
            }
        }
    }
}
