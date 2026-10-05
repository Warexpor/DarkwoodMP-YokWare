using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Client: a dream this player earned (a level-up, a dialogue) that the host could not start
    /// right now: the host was dead, or another dream was starting or running. Vanilla keeps such a
    /// dream wanted (<c>Dreams.wantToDream</c>, even across a save) until it happens; the mod used
    /// to drop it with the reject, and a level-up dream was gone for good. It waits here and the
    /// entry transition plays again once the host can take it.
    /// </summary>
    internal static class DreamRetry
    {
        internal const string HostDeadReason = "host_dead";

        private const float RetryDelaySec = 3f;

        private static string _requested;   // reset-in: Reset (NetworkResetRegistry)
        private static string _waiting;     // reset-in: Reset (NetworkResetRegistry)
        private static float _notBefore;    // reset-in: Reset (NetworkResetRegistry)
        /// <summary>Level-dream flags this request was for (this peer's level-up, not yet the party's).</summary>
        private static byte _levelBits;     // reset-in: Reset (NetworkResetRegistry)
        /// <summary>A dream is running on the host (this peer sits it out, or its pad failed to load).</summary>
        internal static bool HostDreamRunning; // reset-in: Reset (NetworkResetRegistry)

        internal static void Reset()
        {
            _requested = null;
            _waiting = null;
            _notBefore = 0f;
            _levelBits = 0;
            HostDreamRunning = false;
        }

        /// <summary>The entry transition sent a start request for <paramref name="dreamName"/> ("" = random roll).</summary>
        internal static void NoteRequest(string dreamName)
        {
            _requested = dreamName ?? "";
            _waiting = null;
            _levelBits = (byte)(DreamSession.ReadLocalLvlFlags() & ~DreamSession.HostLvlFlags);
        }

        internal static void Clear()
        {
            _requested = null;
            _waiting = null;
        }

        internal static void OnRejected(string outcome)
        {
            if (_requested == null)
                return; // a story-end reject, not our start request
            string reason = outcome != null && outcome.StartsWith("rejected:", System.StringComparison.Ordinal)
                ? outcome.Substring("rejected:".Length)
                : "";
            bool retry = reason == HostDeadReason
                || reason == "session_active"
                || reason == "already_prepared"
                || reason == "try_begin_failed";
            if (retry)
            {
                _waiting = _requested;
                _notBefore = Time.unscaledTime + RetryDelaySec;
                ModRuntime.LegacyInfo($"[DreamRetry] '{_waiting}' waits ({reason})");
            }
            _requested = null;
        }

        internal static void Tick(LanNetworkManager net)
        {
            if (_waiting == null || net == null || net.Role != NetworkRole.Client)
                return;
            if (Time.unscaledTime < _notBefore)
                return;
            _notBefore = Time.unscaledTime + 1f;
            Dreams dreams = Dreams.Instance;
            Player p = Player.Instance;
            if (dreams == null || p == null || dreams.startTransition == null)
                return;
            if (Core.loadingGame || Core.mainMenu || !Core.worldGenFinished())
                return;
            if (DreamSession.IsActive || HostDreamRunning || dreams.dreaming || dreams.dreamPrepared || dreams.startTransition.isPlaying)
                return;
            // Another player's dream for the same level came first: the party has had it.
            if (_levelBits != 0 && (_levelBits & DreamSession.HostLvlFlags) == _levelBits)
            {
                ModRuntime.LegacyInfo("[DreamRetry] the party already had this level's dream — dropped");
                _waiting = null;
                return;
            }
            if (DreamSyncManager.IsLocalDeadOutsideDream() || DreamSyncManager.IsLocalDreamActive
                || DreamSyncManager.HasPendingEntryTransition)
                return;
            if (net.HostPlayerId > 0 && DreamSyncManager.IsPeerDeadOutsideDream(net, net.HostPlayerId))
                return;
            // Not mid-action: talking, a menu, a scripted input lock.
            if (Core.forbidInputs || (Singleton<UI>.Instance != null && Singleton<UI>.Instance.dialogueWindow != null
                    && Singleton<UI>.Instance.dialogueWindow.opened))
                return;
            // A named dream the party finished meanwhile is not owed any more.
            if (_waiting.Length > 0 && DreamSession.IsPresetCompleted(_waiting))
            {
                _waiting = null;
                return;
            }

            string name = _waiting;
            _waiting = null;
            ModRuntime.LegacyInfo($"[DreamRetry] replay entry for '{name}'");
            // As SkillsMenu does on a level-up dream.
            dreams.wantToDream = true;
            dreams.startTransition.dreamToTransitionTo = name;
            Core.forbidInputs = true;
            dreams.startTransition.transition();
        }
    }
}
