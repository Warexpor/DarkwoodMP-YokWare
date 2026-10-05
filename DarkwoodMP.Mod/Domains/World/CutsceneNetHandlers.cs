using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;

namespace DWMPHorde.Networking
{
    /// <summary>Cutscene sync handler composed for 0.8.</summary>
    internal sealed class CutsceneNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal CutsceneNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>
        /// Actions a client may originate (CutsceneSyncPatches: DreamTransition.skip and the
        /// pre-dream entry transition). Cutscene begin/end and prologue start/end are host-only.
        /// </summary>
        internal static bool IsClientRequestAction(byte action) =>
            action == CutsceneSyncMessage.ActionSkipTransition
            || action == CutsceneSyncMessage.ActionDreamEntryTransition;

        internal void HandleCutsceneSync(CutsceneSyncMessage msg)
        {
            // A client must not start or end host-owned cutscenes: drop it and stop the
            // Forwardable relay so other clients never see it.
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0
                && !IsClientRequestAction(msg.Action))
            {
                _net.SuppressRelay();
                ModLog.Warn(LogCat.Session,
                    "[Cutscene] dropped host-only action " + msg.Action + " from p" + _net.CurrentReceivePlayerId);
                return;
            }

            // Host already applied locally; only apply on non-originators.
            // When host receives client skip, rebroadcast is Forwardable — host should skip too.
            switch (msg.Action)
            {
                case CutsceneSyncMessage.ActionBegin:
                    if (_net.Role == NetworkRole.Host)
                        return; // begin is host-originated; host already running
                    DWMPHorde.Patches.CutsceneSyncHelpers.ApplyBegin(msg);
                    break;

                case CutsceneSyncMessage.ActionEnd:
                    if (_net.Role == NetworkRole.Host)
                        return;
                    DWMPHorde.Patches.CutsceneSyncHelpers.ApplyEnd();
                    break;

                case CutsceneSyncMessage.ActionSkipTransition:
                    // Apply on everyone (incl. host when a client skipped). Do NOT gate on
                    // LanNetworkManager.IsApplyingRemoteState — ProcessInboundMessage's NetworkApplyGuard keeps
                    // that flag true for the whole receive, so a check here would drop the
                    // apply. Prefixes still suppress rebroadcast.
                    DWMPHorde.Patches.CutsceneSyncHelpers.ApplySkipTransition();
                    break;

                case CutsceneSyncMessage.ActionDreamEntryTransition:
                    // Originator already plays vanilla transition; peers (incl. host if client
                    // led) start the remote video now. Same note as Skip: never early-out on
                    // LanNetworkManager.IsApplyingRemoteState inside this inbound handler.
                    DWMPHorde.Sync.DreamSyncManager.OnPeerDreamEntryTransition();
                    break;

                case CutsceneSyncMessage.ActionDreamEntryCancel:
                    if (_net.Role == NetworkRole.Host)
                        return;
                    DWMPHorde.Sync.DreamSyncManager.CancelRefusedEntry();
                    break;
            }
        }
    
    }
}
