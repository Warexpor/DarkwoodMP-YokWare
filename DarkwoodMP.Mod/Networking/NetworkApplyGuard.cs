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
    /// </summary>
    internal struct NetworkApplyGuard : IDisposable
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
                _outerPrevIsApplying = LanNetworkManager.IsApplyingRemoteState;
                _outerPrevTraverseHack = TraverseHack.GetExplicitFlag();
                LanNetworkManager.IsApplyingRemoteState = true;
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
                LanNetworkManager.IsApplyingRemoteState = _outerPrevIsApplying;
                TraverseHack.SetExplicitFlag(_outerPrevTraverseHack);
            }
        }

        /// <summary>Force-clear depth + all apply flags (network stop / emergency).</summary>
        internal static void ResetDepth()
        {
            _depth = 0;
            LanNetworkManager.IsApplyingRemoteState = false;
            TraverseHack.ResetTransientFlags();
        }
    }
}
