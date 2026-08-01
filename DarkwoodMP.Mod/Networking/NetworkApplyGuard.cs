using DWMPHorde.Sync;
using System;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Nested-safe scope that sets IsApplyingRemoteState and TraverseHack.ApplyingFromNetwork.
    /// Use: <c>using (new NetworkApplyGuard()) { ... }</c>
    /// Depth-counted so nested handlers restore correctly.
    /// Inner code must not clear flags while <see cref="IsActive"/>; TraverseHack
    /// getter stays true for the whole outer receive scope.
    ///
    /// MUST be a class, not a struct: <c>using (new NetworkApplyGuard())</c> on a struct
    /// with only an optional-arg ctor compiles to <c>initobj</c> (zero-init) under our
    /// net471/C#10 toolchain — ctor never runs, guard is a no-op, client one-shot
    /// GameEvents.fire stays blocked by GameEventsFiredPatch (dream clothes/masks/START).
    /// </summary>
    internal sealed class NetworkApplyGuard : IDisposable
    {
        private static int _depth;
        private static bool _outerPrevIsApplying;
        private static bool _outerPrevTraverseHack;

        private readonly bool _entered;

        /// <summary>True while any NetworkApplyGuard is on the stack.</summary>
        internal static bool IsActive => _depth > 0;

        public NetworkApplyGuard(bool enter = true)
        {
            _entered = enter;
            if (!enter) return;

            if (_depth == 0)
            {
                _outerPrevIsApplying = LanNetworkManager.GetExplicitApplyingRemoteState();
                _outerPrevTraverseHack = TraverseHack.GetExplicitFlag();
                LanNetworkManager.SetExplicitApplyingRemoteState(true);
                TraverseHack.SetExplicitFlag(true);
            }
            _depth++;
        }

        public void Dispose()
        {
            if (!_entered) return;
            if (_depth <= 0) return;
            _depth--;
            if (_depth == 0)
            {
                LanNetworkManager.SetExplicitApplyingRemoteState(_outerPrevIsApplying);
                TraverseHack.SetExplicitFlag(_outerPrevTraverseHack);
            }
        }

        /// <summary>Force-clear depth + all apply flags (network stop / emergency).</summary>
        internal static void ResetDepth()
        {
            _depth = 0;
            LanNetworkManager.SetExplicitApplyingRemoteState(false);
            TraverseHack.ResetTransientFlags();
        }
    }
}
