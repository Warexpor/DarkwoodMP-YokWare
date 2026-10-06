using DWMPHorde.Networking;
using DWMPHorde.Players;
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
        internal const string HostPrologueReason = "host_prologue";

        private const float RetryDelaySec = 3f;

        private static string _requested;   // reset-in: Reset (NetworkResetRegistry)
        private static string _waiting;     // reset-in: Reset (NetworkResetRegistry)
        private static float _notBefore;    // reset-in: Reset (NetworkResetRegistry)
        /// <summary>Level slot(s) this request was for (this peer's own level-up).</summary>
        private static byte _levelBits;     // reset-in: Reset (NetworkResetRegistry)
        /// <summary>A dream is running on the host (this peer sits it out, or its pad failed to load).</summary>
        internal static bool HostDreamRunning; // reset-in: Reset (NetworkResetRegistry)
        /// <summary>
        /// Host: a dream waiting for the host to be able to start it (a peer's dialogue dream, or
        /// one this peer was owed when it became the host; "" = a random level dream).
        /// </summary>
        private static string _hostWaiting; // reset-in: Reset (NetworkResetRegistry)
        private static float _hostNotBefore; // reset-in: Reset (NetworkResetRegistry)

        internal static void Reset()
        {
            _requested = null;
            _waiting = null;
            _notBefore = 0f;
            _levelBits = 0;
            HostDreamRunning = false;
            _hostWaiting = null;
            _hostNotBefore = 0f;
        }

        /// <summary>
        /// Host: a peer's dialogue chose a dream (the host applies the outcome). Vanilla starts it
        /// right after the dialogue closes. When the host cannot take it then (dead, another dream
        /// starting or running) the start used to be refused and the dream was gone, the dialogue
        /// already spent. It waits instead, as vanilla's wantToDream does, and starts once the host can.
        /// </summary>
        internal static void HostDialogueDream(string preset)
        {
            if (string.IsNullOrEmpty(preset))
                return;
            if (HostCanStart())
            {
                Singleton<Controller>.Instance?.Invoke(delegate
                {
                    if (HostCanStart())
                        HostStart(preset);
                    else
                        HostPark(preset);
                }, 0.1f, timeScaleDependent: false);
                return;
            }
            HostPark(preset);
        }

        private static void HostPark(string preset)
        {
            _hostWaiting = preset;
            _hostNotBefore = Time.unscaledTime + RetryDelaySec;
            // Kept here, not in wantToDream: with no dream of its own coming, that flag would
            // hold the host's location activation and spawning as if one were (WhereAmI, Location).
            Dreams dreams = Dreams.Instance;
            if (dreams != null && !dreams.dreaming && !dreams.dreamPrepared
                && (dreams.startTransition == null || !dreams.startTransition.isPlaying)
                && !DreamSyncManager.IsHostDreamEntryPending)
                dreams.wantToDream = false;
            ModRuntime.LegacyInfo($"[DreamRetry] host: dialogue dream '{preset}' waits");
        }

        private static bool HostCanStart()
        {
            Dreams dreams = Dreams.Instance;
            if (dreams == null || dreams.dreaming || dreams.dreamPrepared)
                return false;
            if (dreams.startTransition != null && dreams.startTransition.isPlaying)
                return false;
            if (Core.loadingGame || Core.mainMenu || !Core.worldGenFinished())
                return false;
            return !DreamSession.IsActive && !DreamSyncManager.IsHostDreamEntryPending
                && !DreamSyncManager.IsLocalDeadOutsideDream() && !PersonalPrologue.LocalInPrologue;
        }

        private static void HostStart(string preset)
        {
            Dreams dreams = Dreams.Instance;
            dreams.wantToDream = true;
            dreams.StartCoroutine(dreams.prepareDream(preset));
        }

        private static void HostTick()
        {
            if (_hostWaiting == null || Time.unscaledTime < _hostNotBefore)
                return;
            _hostNotBefore = Time.unscaledTime + 1f;
            if (_hostWaiting.Length > 0 && DreamSession.IsPresetCompleted(_hostWaiting))
            {
                ModRuntime.LegacyInfo($"[DreamRetry] host: '{_hostWaiting}' finished meanwhile — dropped");
                _hostWaiting = null;
                return;
            }
            if (!HostCanStart())
                return;
            string name = _hostWaiting;
            _hostWaiting = null;
            ModRuntime.LegacyInfo($"[DreamRetry] host: start waiting dialogue dream '{name}'");
            HostStart(name);
        }

        /// <summary>The entry transition sent a start request for <paramref name="dreamName"/> ("" = random roll).</summary>
        internal static void NoteRequest(string dreamName, byte levelBits)
        {
            _requested = dreamName ?? "";
            _waiting = null;
            _levelBits = levelBits;
        }

        /// <summary>
        /// This peer was in a dream for these level slots: a waiting level-up dream for the
        /// same slot is had (it was in that level's dream), so it is not owed any more.
        /// </summary>
        internal static void OnJoinedLevelDream(byte bits)
        {
            if (_levelBits == 0 || (bits & _levelBits) == 0)
                return;
            _levelBits = (byte)(_levelBits & ~bits);
            if (_levelBits != 0)
                return;
            if (_waiting != null || _requested != null)
                ModRuntime.LegacyInfo("[DreamRetry] was in a dream for this level — owed dream dropped");
            _waiting = null;
            _requested = null;
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
                || reason == HostPrologueReason
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
            if (net != null && net.Role == NetworkRole.Host)
            {
                // This peer became the host (migration) while owed a dream: the host starts it.
                if (_waiting != null && _hostWaiting == null)
                {
                    _hostWaiting = _waiting;
                    _hostNotBefore = Time.unscaledTime + RetryDelaySec;
                }
                _waiting = null;
                _requested = null;
                HostTick();
                return;
            }
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
            if (DreamSyncManager.IsLocalDeadOutsideDream() || DreamSyncManager.IsLocalDreamActive
                || DreamSyncManager.HasPendingEntryTransition)
                return;
            if (net.HostPlayerId > 0 && DreamSyncManager.IsPeerDeadOutsideDream(net, net.HostPlayerId))
                return;
            // The host is in its own prologue: party dreams wait for it.
            RemotePlayerProxy host = net.HostPlayerId > 0 ? net.GetProxy(net.HostPlayerId) : null;
            if (host != null && host.RemoteInPrologue)
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
            DreamSession.PendingRequestBits = _levelBits;
            Core.forbidInputs = true;
            dreams.startTransition.transition();
        }
    }
}
