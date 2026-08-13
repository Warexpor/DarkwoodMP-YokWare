namespace DWMPHorde.Sync
{
    /// <summary>
    /// While host applies a remote peer's dialog outcome, suppress personal
    /// bag mutations on host Player.Instance (audit C2). Journal is shared world
    /// identity — apply and fan out, do not snapshot-restore.
    /// </summary>
    public static class DialogHostApplyGuard
    {
        private static int _depth;

        /// <summary>Allow exactly one displayNextBoard, then block chained/delayed calls.</summary>
        public static bool OneShotBoardActive { get; set; }

        /// <summary>Dest displayDialogue + drain may chain portrait boards.</summary>
        public static bool DestDrainActive { get; set; }

        private static bool _oneShotConsumed;

        public static bool SuppressPersonalRewards => _depth > 0;

        /// <summary>True while host is applying a client's dialog outcome (no host UI session).</summary>
        public static bool Active => _depth > 0;

        public static void BeginWorldOnly()
        {
            _depth++;
            if (_depth == 1)
            {
                try { DWMPHorde.Patches.JournalSyncHelpers.BeginWorldApplyDiff(); }
                catch { /* journal UI may be missing */ }
            }
        }

        public static void EndWorldOnly()
        {
            if (_depth == 1)
            {
                try { DWMPHorde.Patches.JournalSyncHelpers.EndWorldApplyDiffAndBroadcastRemoves(); }
                catch { /* ignore */ }
            }
            if (_depth > 0)
                _depth--;
            if (_depth == 0)
            {
                OneShotBoardActive = false;
                DestDrainActive = false;
                _oneShotConsumed = false;
            }
        }

        public static void Reset()
        {
            _depth = 0;
            OneShotBoardActive = false;
            DestDrainActive = false;
            _oneShotConsumed = false;
            _blockChainedDisplayUntilMs = 0;
        }

        /// <summary>
        /// One-shot board apply: first displayNextBoard runs; nested/delayed
        /// portrait auto-advance is skipped until dest drain.
        /// </summary>
        private static int _blockChainedDisplayUntilMs;

        public static bool ShouldRunDisplayNextBoard()
        {
            if (DestDrainActive)
                return true;
            if (OneShotBoardActive)
            {
                if (_oneShotConsumed)
                    return false;
                _oneShotConsumed = true;
                return true;
            }
            int now = System.Environment.TickCount;
            if (_blockChainedDisplayUntilMs != 0
                && now - _blockChainedDisplayUntilMs < 0)
                return false;
            return true;
        }

        public static void BeginOneShotBoard()
        {
            OneShotBoardActive = true;
            _oneShotConsumed = false;
        }

        public static void EndOneShotBoard()
        {
            OneShotBoardActive = false;
            _oneShotConsumed = false;
            _blockChainedDisplayUntilMs = System.Environment.TickCount + 2500;
        }

        public static void ClearChainedDisplayBlock()
        {
            _blockChainedDisplayUntilMs = 0;
        }
    }
}
