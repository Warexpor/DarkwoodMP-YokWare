using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="LockNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal LockNetHandlers LockHandlers { get; private set; }

        private void HandleConstructible(ConstructibleMessage msg)
        {
            LockHandlers.HandleConstructible(msg);
        }

        private void HandleInteractiveItemSwitch(InteractiveItemSwitchMessage msg)
        {
            LockHandlers.HandleInteractiveItemSwitch(msg);
        }

        private void HandlePadlockUnlock(PadlockUnlockMessage msg)
        {
            LockHandlers.HandlePadlockUnlock(msg);
        }

        private void HandleLockedUnlock(LockedUnlockMessage msg)
        {
            LockHandlers.HandleLockedUnlock(msg);
        }

        internal void RegisterConstructedSite(Vector3 key, int optionIndex)
        {
            LockHandlers.RegisterConstructedSite(key, optionIndex);
        }

        internal void SendConstructedSitesTo(int targetPlayerId)
        {
            LockHandlers.SendConstructedSitesTo(targetPlayerId);
        }

        private void TryFlushPendingConstructibles()
        {
            LockHandlers.TryFlushPendingConstructibles();
        }

        internal void TryFlushPendingLocks()
        {
            LockHandlers.TryFlushPendingLocks();
        }

        internal void ClearPendingLocks()
        {
            LockHandlers.ClearPendingLocks();
        }

        private void SyncExistingPadlocksTo(int targetPlayerId)
        {
            LockHandlers.SyncExistingPadlocksTo(targetPlayerId);
        }

        private void SyncExistingLockedsTo(int targetPlayerId)
        {
            LockHandlers.SyncExistingLockedsTo(targetPlayerId);
        }

        private void SyncExistingInteractivesTo(int targetPlayerId)
        {
            LockHandlers.SyncExistingInteractivesTo(targetPlayerId);
        }

        private void SyncExistingLocksAndInteractives(int targetPlayerId)
        {
            LockHandlers.SyncExistingLocksAndInteractives(targetPlayerId);
        }

        private int PendingLockCount
        {
            get
            {
                return LockHandlers.PendingLockCount;
            }
        }

    }
}
