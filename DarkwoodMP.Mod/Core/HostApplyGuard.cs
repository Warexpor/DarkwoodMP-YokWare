using System;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Host is applying a remote peer's world action (dialogue outcome, door, light, client
    /// combat hit). The apply runs inside NetworkApplyGuard, which swallows GameEventsFired /
    /// flag / reputation / journal fan-out; this scope lets those fan out (stamped with the
    /// inbound peer as GameEventsFired actor) and keeps host personal rewards off the host.
    /// Dialogue-only presentation state (silent close, board gating, black-screen suppress)
    /// is <see cref="DialogHostApplyGuard"/>; a client combat hit opens only this scope.
    /// </summary>
    public static class HostApplyGuard
    {
        private static int _depth;
        private static bool _autoPushedActor;

        /// <summary>True while the host applies any remote peer's world action.</summary>
        public static bool Active => _depth > 0;

        public static void Begin()
        {
            _depth++;
            if (_depth != 1)
                return;
            _autoPushedActor = false;
            // Prefer an explicit GeFireActorContext.Push (proxy volume, cursor).
            // Otherwise stamp the inbound packet peer as the GE actor.
            if (GeFireActorContext.Depth == 0)
            {
                try
                {
                    var net = ModRuntime.Network as Networking.LanNetworkManager;
                    if (net != null && net.IsConnected && net.CurrentReceivePlayerId > 0)
                    {
                        GeFireActorContext.Push(net.CurrentReceivePlayerId);
                        _autoPushedActor = true;
                    }
                }
                catch { /* ignore */ }
            }
            try { DWMPHorde.Patches.JournalSyncHelpers.BeginWorldApplyDiff(); }
            catch { /* journal UI may be missing */ }
        }

        public static void End()
        {
            if (_depth <= 0)
                return;
            if (_depth == 1)
            {
                try { DWMPHorde.Patches.JournalSyncHelpers.EndWorldApplyDiffAndBroadcastRemoves(); }
                catch { /* ignore */ }
                if (_autoPushedActor)
                {
                    GeFireActorContext.Pop();
                    _autoPushedActor = false;
                }
            }
            _depth--;
        }

        /// <summary>
        /// Host apply of a remote peer's world action without any dialogue semantics (client
        /// combat hits). No-op wrapper off-host.
        /// </summary>
        public static void Run(Action body)
        {
            if (body == null) return;
            bool host = ModRuntime.Network is Networking.LanNetworkManager net
                && net.IsConnected
                && net.Role == Networking.NetworkRole.Host;
            if (host) Begin();
            try { body(); }
            finally { if (host) End(); }
        }

        public static void Reset()
        {
            _depth = 0;
            _autoPushedActor = false;
        }
    }
}
