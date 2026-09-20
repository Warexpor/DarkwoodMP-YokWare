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

        internal void HandleCutsceneSync(CutsceneSyncMessage msg)
        {
            // Host already applied locally; only apply on non-originators.
            // When host receives client skip, rebroadcast is Forwardable — host should skip too.
            switch (msg.Action)
            {
                case CutsceneSyncMessage.ActionBegin:
                    if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId <= 0)
                        return; // shouldn't happen; begin is host-originated
                    if (_net.Role == NetworkRole.Host)
                        return; // host already running
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
            }
        }
    
    }
}
