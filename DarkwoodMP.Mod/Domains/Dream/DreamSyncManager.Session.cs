using DG.Tweening;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Spectator;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Video;

namespace DWMPHorde.Sync
{
    internal static partial class DreamSyncManager
    {
        public static void BeginStoryEndDefer(string preset, string outcome)
        {
            _storyEndDeferPreset = preset;
            _storyEndDeferOutcome = outcome;
            _storyEndDeferPending = true;
            _storyEndDeferDeadline = Time.realtimeSinceStartup + StoryEndDeferTimeoutSec;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null)
            {
                if (_storyEndWatchdog != null)
                    ctrl.StopCoroutine(_storyEndWatchdog);
                _storyEndWatchdog = ctrl.StartCoroutine(StoryEndDeferWatchdog());
            }
        }

        public static void ClearStoryEndDefer()
        {
            _storyEndDeferPending = false;
            _storyEndDeferDeadline = 0f;
            _storyEndDeferPreset = null;
            _storyEndDeferOutcome = null;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null && _storyEndWatchdog != null)
            {
                ctrl.StopCoroutine(_storyEndWatchdog);
                _storyEndWatchdog = null;
            }
        }

        private static IEnumerator StoryEndDeferWatchdog()
        {
            while (_storyEndDeferPending && Time.realtimeSinceStartup < _storyEndDeferDeadline)
                yield return null;
            if (!_storyEndDeferPending)
                yield break;
            ModRuntime.Log?.LogWarning(
                "[DreamSync] Story-end defer timed out; forcing local dream cleanup");
            _storyEndDeferPending = false;
            _storyEndWatchdog = null;
            ForceLocalDreamCleanup("storyEndTimeout");
        }

        /// <summary>Forced cleanup after rejection or story-end timeout.</summary>
        public static void ForceLocalDreamCleanup(string reason)
        {
            ClearStoryEndDefer();
            CancelPendingEntries();
            // A rejected / failed start leaves the host-resolved pick the client stored when it sent
            // DreamStartRequest; getPreset("") would keep returning it for the next random dream.
            if (DreamSession.IsFailureCleanup(reason))
                DreamSession.ClearPendingHostPreset();
            ModRuntime.LegacyInfo($"[DreamSync] ForceLocalDreamCleanup: {reason}");
            ClearRemoteDreamRoster();
            FadeOutDreamTransition();
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
            _remoteEntryTransitionPlaying = false;
            _remoteEntryAudioId = null;
            Core.EnteringDream = false;
            try
            {
                var ui = Singleton<UI>.Instance;
                if (ui != null)
                {
                    ui.tweenBlackScreen(new Color(0f, 0f, 0f, 0f), 0.5f);
                    try { ui.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 0.5f); }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }

            if (DreamSession.IsActive)
            {
                // Rejection / timeout / disconnect is not a clear. MarkCompleted
                // would party-lock the preset while the host may still be inside.
                if (DreamSession.IsFailureCleanup(reason))
                    DreamSession.AbortStarting(reason);
                else
                    DreamSession.End(reason);
            }
            if (Dreams.Instance != null && Dreams.Instance.dreaming)
            {
                ApplyRemoteDreamCleanup(DreamSession.IsFailureCleanup(reason) ? "" : reason);
            }
            else
            {
                try
                {
                    if (Dreams.Instance != null)
                    {
                        Dreams.Instance.wantToDream = false;
                        Dreams.Instance.dreamPrepared = false;
                    }
                }
                catch { /* ignore */ }
                DropUnstartedLocalPad();
                UnfreezeWorld(restoreTime: false);
                // No dream came of this entry: the movie's audio fade and paused world sounds stay
                // unless undone here.
                try
                {
                    Singleton<Controller>.Instance?.fadeAudio(fadeOut: false, 1f, musicToo: true);
                    Singleton<RandomWorldSounds>.Instance?.resumeGlobalSounds();
                }
                catch { /* ignore */ }
                FinalDreamsceneManager.OnDreamEnded();
                ClearRemoteDreamRoster();
                ClearPreDreamState();
                WorldQueryHelper.InvalidateCommonSceneScanCaches();
            }
            FinalDreamsceneManager.OnLocalWokeUp();
            ReleaseDreamInputLocks();
            _entryTransitionSeen = false;
            _localDreamActive = false;
            _localDreamPreset = null;
            _hostOrderedDreamEnd = false;
        }

        /// <summary>
        /// A dream this client prepared itself (a story step's startDream: vanilla loads the pad and
        /// only then asks the host) that never started: the pad goes, and the ending's mode, which
        /// vanilla turns on as the epilogue pad spawns, comes off. The host defers an ending asked for
        /// while it is dead to its morning; the client used to play on in ending mode meanwhile (no
        /// UI, no items) with the pad left loaded.
        /// </summary>
        private static void DropUnstartedLocalPad()
        {
            try
            {
                Dreams d = Dreams.Instance;
                if (d == null || d.dreaming)
                    return;
                var outs = Singleton<OutsideLocations>.Instance;
                if (d.preset != null && outs != null && outs.spawnedLocations != null
                    && outs.spawnedLocations.TryGetValue(d.preset.name, out Location pad)
                    && pad != null && pad == d.dreamLocation)
                {
                    d.destroyDream();
                    ReturnFromUnstartedPad(d, outs);
                    ModRuntime.LegacyInfo("[DreamSync] dropped the unstarted pad " + d.preset.name);
                }
                Player p = Player.Instance;
                if (p != null && p.inEpilogue)
                {
                    p.inEpilogue = false;
                    var cam = Singleton<CamMain>.Instance;
                    if (cam != null && cam.FireMaskCam != null)
                        cam.FireMaskCam.gameObject.SetActive(false);
                    Singleton<UI>.Instance?.showVisibleUI();
                    ModRuntime.LegacyInfo("[DreamSync] ending mode off — the ending did not start");
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] unstarted pad cleanup: " + ex.Message);
            }
        }

        /// <summary>
        /// Vanilla's pad load (transportToLocation, dream branch) already put the player's grid,
        /// location and camera look on the pad and left the place it stood in; startDreaming never
        /// came to move the body. Back to where it started dreaming, as endDreaming returns it, so
        /// culling, the location claim (a LocationExit follows on the next tick) and the light are
        /// the overworld's again.
        /// </summary>
        private static void ReturnFromUnstartedPad(Dreams d, OutsideLocations outs)
        {
            Player p = Player.Instance;
            Location from = d.placeStartedDreaming;
            string fromName = from != null ? Core.getTrueLocationName(from.name) : null;
            if (from != null && from.isOutsideLocation && outs.spawnedLocations.TryGetValue(fromName, out Location home) && home != null)
            {
                home.enter();
                Singleton<WorldGrid>.Instance.setGrid(fromName);
                outs.currentLocationName = fromName;
                outs.playerInOutsideLocation = true;
            }
            else
            {
                Singleton<WorldGrid>.Instance.setGrid("World");
                outs.currentLocationName = "";
                outs.playerInOutsideLocation = false;
                Singleton<Rain>.Instance?.unhide();
                Core.modifyCamEffects(active: false, null);
            }
            d.placeStartedDreaming = null;
            if (p != null)
                Singleton<WorldGrid>.Instance.refreshPosition(p._transform.position, true, true);
            outs.applyCamEffects();
            Singleton<Controller>.Instance?.updateAmbientLight();
            p?.whereAmI?.checkWhereAmI();
        }

        /// <summary>
        /// Called from DreamEntryClientPatch when client intercepts onFinishedVideo.
        /// Records the transition as already-played so ProcessRemoteDreamCoroutine
        /// can skip the wait (video already ended) and proceed to fadeout immediately.
        /// </summary>
        public static void MarkLocalEntryTransitionPlayed()
        {
            _earlyEntryTransitionPlayed = true;
            _earlyEntryTransitionDoneAt = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Multiplayer-facing dream gate: session is authoritative when networked;
        /// falls back to local/remote flags for solo or mid-transition.
        /// </summary>
        public static bool IsDreamActive =>
            DreamSession.IsActive || _localDreamActive || AnyRemoteDreamActive()
            || _earlyEntryTransitionPlayed;

        /// <summary>Hot path (per-frame resolvers): struct enumerator, no LINQ allocation.</summary>
        private static bool AnyRemoteDreamActive()
        {
            foreach (var kvp in _remoteDreamActive)
            {
                if (kvp.Value)
                    return true;
            }
            return false;
        }

        public static bool IsLocalDreamActive => _localDreamActive;

        /// <summary>
        /// This game is inside a dream (vanilla <c>Dreams.dreaming</c>). Vanilla never saves then
        /// (it saves before the dream and after waking): a save now stores the body on the pad
        /// without the dream, and the next load leaves the player stranded there.
        /// </summary>
        public static bool IsLocalMidDream => Dreams.Instance != null && Dreams.Instance.dreaming;

        /// <summary>
        /// A party dream is on and this player is part of it (in it, or on its way in or out):
        /// false for a host that left by its own exit while the dream runs on for others
        /// (<see cref="DreamExit"/>). For what describes where the local player is.
        /// </summary>
        public static bool IsLocalInPartyDream => IsDreamActive && !DreamExit.HostHostsFromOutside;

        /// <summary>A location name that is a dream's pad, not an ordinary outside location.</summary>
        public static bool IsDreamPadName(string locName)
            => !string.IsNullOrEmpty(locName)
               && (locName.StartsWith("dream_", System.StringComparison.OrdinalIgnoreCase)
                   || locName.StartsWith("epilog", System.StringComparison.OrdinalIgnoreCase)
                   || IsDreamLocationName(locName));

        /// <summary>True while this peer is already in the dream-entry video.</summary>
        public static bool HasPendingEntryTransition =>
            _earlyEntryTransitionPlayed || _remoteEntryTransitionPlaying;

        /// <summary>True when that remote peer is inside the shared dream.</summary>
        private static readonly System.Collections.Generic.HashSet<int> _dreamEntryConfirmed =
            new System.Collections.Generic.HashSet<int>();
        private static readonly System.Collections.Generic.Dictionary<int, float> _peerEntryDeadline =
            new System.Collections.Generic.Dictionary<int, float>();
        private static float _dreamEntryDeadline;

        public static bool IsRemoteInDream(int playerId)
        {
            if (playerId <= 0 || !_remoteDreamActive.TryGetValue(playerId, out bool active) || !active)
                return false;
            // Left by its own exit: the dream goes on without it.
            if (DreamExit.HasLeft(playerId))
                return false;
            if (_dreamEntryConfirmed.Contains(playerId))
                return true;
            // Per peer: a joiner noted after the original 10s window still counts
            // until they confirm or their own grace ends.
            if (_peerEntryDeadline.TryGetValue(playerId, out float peerDeadline))
                return UnityEngine.Time.unscaledTime < peerDeadline;
            return UnityEngine.Time.unscaledTime < _dreamEntryDeadline;
        }

        public static void NoteRemoteInDream(int playerId)
        {
            if (playerId <= 0)
                return;
            _remoteDreamActive[playerId] = true;
            if (!_dreamEntryConfirmed.Contains(playerId))
                _peerEntryDeadline[playerId] = UnityEngine.Time.unscaledTime + 25f;
            if (_dreamEntryDeadline <= 0f)
                _dreamEntryDeadline = UnityEngine.Time.unscaledTime + 10f;
        }

        public static void ConfirmRemoteInDream(int playerId)
        {
            if (playerId <= 0)
                return;
            _remoteDreamActive[playerId] = true;
            _dreamEntryConfirmed.Add(playerId);
        }

        public static void ClearRemoteDreamRoster()
        {
            _remoteDreamActive.Clear();
            _dreamEntryConfirmed.Clear();
            _peerEntryDeadline.Clear();
            _dreamEntryDeadline = 0f;
        }

        public static void ClearRemoteInDream(int playerId)
        {
            if (playerId <= 0)
                return;
            if (_remoteDreamActive.ContainsKey(playerId))
                _remoteDreamActive[playerId] = false;
            _dreamEntryConfirmed.Remove(playerId);
            _peerEntryDeadline.Remove(playerId);
        }

        /// <summary>Returns the dream Location's transform during an active dream, or null.</summary>
        public static Transform GetDreamLocationTransform()
        {
            if (!IsDreamActive) return null;
            if (Dreams.Instance != null && Dreams.Instance.dreamLocation != null)
                return Dreams.Instance.dreamLocation.transform;
            return null;
        }

        /// <summary>
        /// The loaded dream pad, dream or not: vanilla keeps <c>Dreams.dreamLocation</c> after the
        /// dream ends and destroys the pad 4 s later (Dreams.destroyDream), the window in which
        /// its events can still tick.
        /// </summary>
        public static Transform GetLoadedDreamPad()
        {
            Location pad = Dreams.Instance != null ? Dreams.Instance.dreamLocation : null;
            return pad != null ? pad.transform : null;
        }

        /// <summary>A scene object of the loaded dream pad (not the overworld twin of the same name).</summary>
        public static bool IsOnDreamPad(Transform t)
        {
            Transform pad = GetLoadedDreamPad();
            return pad != null && t != null && (t == pad || t.IsChildOf(pad));
        }

        /// <summary>
        /// Outside-location pad slots (vanilla OutsideLocations.locationPositions) sit 25000 apart:
        /// a position within half of that from the loaded dream pad is on that pad. Other slots
        /// hold ordinary outside locations (a cellar, a bunker) whose events are not the dream's.
        /// </summary>
        public static bool IsAtDreamPad(Vector3 pos) => IsInPadSlot(GetLoadedDreamPad(), pos);

        /// <summary>
        /// <paramref name="pos"/> is in the slot of the pad at <paramref name="pad"/>. A dream's
        /// scene does not sit around its origin: the grave meadow's chomper glade is ~5050 out.
        /// </summary>
        public static bool IsInPadSlot(Transform pad, Vector3 pos)
        {
            if (pad == null) return false;
            Vector3 d = pos - pad.position;
            return Mathf.Abs(d.x) < DreamPadHalfSlot && Mathf.Abs(d.z) < DreamPadHalfSlot;
        }

        private const float DreamPadHalfSlot = 12500f;

        /// <summary>Party-once: preset already finished by the shared session.</summary>
        public static bool IsDreamCompleted(int playerId, string presetName)
        {
            return DreamSession.IsPresetCompleted(presetName);
        }

        /// <summary>Party-once: preset already finished by the shared session.</summary>
        public static bool IsDreamCompleted(string presetName)
        {
            return DreamSession.IsPresetCompleted(presetName);
        }

        /// <summary>Host chain keeps ResolveActivePresetName on the live pocket.</summary>
        public static void NoteLocalDreamPreset(string presetName)
        {
            if (!string.IsNullOrEmpty(presetName))
                _localDreamPreset = presetName;
        }

        /// <summary>A peer that is dead in the overworld (night death or a pending day respawn).</summary>
        internal static bool IsPeerDeadOutsideDream(LanNetworkManager net, int playerId)
        {
            if (playerId <= 0)
                return false;
            if (DeathStateTracker.IsRemoteNightDead(playerId))
                return true;
            var proxy = net.GetProxy(playerId);
            CharBase cb = proxy != null ? proxy.CachedCharBase : null;
            return cb != null && !cb.alive;
        }

        /// <summary>This player is dead in the overworld and sits a dream out.</summary>
        internal static bool IsLocalDeadOutsideDream()
        {
            if (DeathStateTracker.LocalNightDeath)
                return true;
            Player p = Player.Instance;
            return p != null && !p.alive && (Dreams.Instance == null || !Dreams.Instance.dreaming);
        }

        /// <summary>
        /// Host: a dream it is already in with no party session. A save made as a dream starts
        /// (vanilla saves inside prepareDream with the dream wanted; the ending's road home always
        /// does) reloads straight into that dream before anyone has joined, so it ran as vanilla's
        /// solo dream and a joiner's session bulk said "no dream": friends stayed in the forest
        /// while the host was in the ending. The first joiner makes it the party's dream.
        /// </summary>
        internal static void AdoptSoloDreamForParty()
        {
            Dreams d = Dreams.Instance;
            if (d == null || !d.dreaming || d.preset == null || string.IsNullOrEmpty(d.preset.name))
                return;
            if (DreamSession.IsActive || _localDreamActive)
                return;
            string preset = d.preset.name;
            if (PersonalPrologue.IsPrologueDream(preset))
                return;
            if (!DreamSession.TryBegin(preset))
                return;
            DreamSession.MarkActive();
            OnLocalDreamStarted(preset, d.dreamLocation != null ? d.dreamLocation.transform.position : Vector3.zero);
            if (d.dreamLocation != null)
                RemapDreamUniqueObjects(d.dreamLocation.transform);
            DWMPHorde.Logging.ModLog.Event(DWMPHorde.Logging.LogCat.Dream, "[DreamSync] host's own dream " + preset + " is the party's now (first joiner)");
        }

        public static void OnLocalDreamStarted(string presetName, Vector3 locationPosition)
        {
            if (_localDreamActive) return;
            ResetHostEndClaim();
            // Party-once is enforced in TryBegin and startDreaming; this is the live start path.
            _localDreamActive = true;
            _localDreamPreset = presetName;

            CloseOpenUiForDreamEntry();
            WorldQueryHelper.InvalidateCommonSceneScanCaches();
            FreezeWorld();

            // Session already started by DreamStartPatch on host; ensure death tracking if needed
            if (!FinalDreamsceneManager.IsActive)
                FinalDreamsceneManager.OnDreamStarted();

            // Teleport the remote proxy (other player's character) to the dream
            // position so both players see each other immediately.
            var net = ModRuntime.Network;
            if (net != null && net.IsConnected)
            {
                Vector3 proxyPos = Player.Instance != null
                    ? Player.Instance._transform.position
                    : locationPosition;
                net.TeleportRemoteProxyTo(proxyPos, 0f);

                // Freeze all remote proxies until they confirm dream entry.
                // This prevents the proxy from drifting back to the real-world position
                // while the remote player is still loading the dream scene.
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = true;

                // Safety timeout: unfreeze after 10s even if DreamEntered never arrives
                if (Singleton<Controller>.Instance != null)
                    Singleton<Controller>.Instance.StartCoroutine(UnfreezeProxiesAfterDelay(10f));

                // Host alone initiates DreamStarted; clients enter via OnRemoteDreamStarted
                // and confirm with DreamEntered after scene load.
                if (net.Role == NetworkRole.Host)
                {
                    // A dead player (night death waiting for morning, or a day death before its
                    // respawn) sits the dream out: it cannot act on the pad, never reports a dream
                    // death (so "everyone is dead" never came), and vanilla endDreaming revives
                    // whoever was in it. The peer itself refuses the entry too (OnRemoteDreamStarted).
                    foreach (int id in net.GetHandshakedPeerIds())
                    {
                        if (id > 0 && id != net.LocalPlayerId && !IsPeerDeadOutsideDream(net, id))
                            NoteRemoteInDream(id);
                    }
                    foreach (var dreamProxy in net.GetAllProxies())
                    {
                        if (dreamProxy != null && !IsPeerDeadOutsideDream(net, dreamProxy.PlayerId))
                            NoteRemoteInDream(dreamProxy.PlayerId);
                    }
                    // The host is in it: the level slot(s) it is for are had, and played for the
                    // party (the played set below carries that to every peer).
                    DreamSession.ApplyLvlFlags(DreamSession.LevelBits);
                    DreamSession.MarkLevelSlotsPlayed(DreamSession.LevelBits);
                    var started = DreamStartedMessage.Build(
                        presetName, locationPosition.x, locationPosition.y, locationPosition.z);
                    started.EntryTransition = _entryTransitionSeen;
                    _entryTransitionSeen = false;
                    net.Broadcast(NetMessageType.DreamStarted,
                        w => started.Serialize(w),
                        LiteNetLib.DeliveryMethod.ReliableOrdered);
                    if (Singleton<Controller>.Instance != null)
                        Singleton<Controller>.Instance.StartCoroutine(HostDreamPropColliderDelayed());
                }
            }

            // Fix 1: If an early entry transition was played (peer's video overlay),
                // clean it up now that local entry is complete. This prevents permanent
            // black screen + paralysis from EnteringDream never being reset.
            if (_earlyEntryTransitionPlayed)
            {
                float remain = _earlyEntryTransitionDoneAt - Time.realtimeSinceStartup;
                Singleton<Controller>.Instance.StartCoroutine(
                    LocalEntryFadeoutCoroutine(Mathf.Max(0f, remain)));
            }

            ModRuntime.LegacyInfo($"[DreamSync] Local dream started: {presetName}, pos={locationPosition}");
        }

        private static System.Collections.IEnumerator HostDreamPropColliderDelayed()
        {
            yield return new WaitForSecondsRealtime(1.5f);
            WorldPhysicsSyncService.HostBroadcastDreamPropColliders(force: true);
            yield return new WaitForSecondsRealtime(2f);
            WorldPhysicsSyncService.HostBroadcastDreamPropColliders(force: true);
        }

        /// <summary>
        /// Strip vanilla completed-location suffix so dream LocationEnter stays on the live pad.
        /// </summary>
        public static string CanonicalDreamLocationName(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) return locationName;
            if (locationName.Length > 5
                && locationName.EndsWith("_done", StringComparison.OrdinalIgnoreCase))
                return locationName.Substring(0, locationName.Length - 5);
            return locationName;
        }

        public static bool IsDreamLocationName(string locationName)
        {
            if (string.IsNullOrEmpty(locationName) || !IsDreamActive) return false;
            string canon = CanonicalDreamLocationName(locationName);
            if (!string.IsNullOrEmpty(DreamSession.PresetName)
                && string.Equals(canon, DreamSession.PresetName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(_localDreamPreset)
                && string.Equals(canon, _localDreamPreset, StringComparison.OrdinalIgnoreCase))
                return true;
            return canon.StartsWith("dream_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Best-known active dream preset (local → session → Dreams.preset).</summary>
        public static string ResolveActivePresetName()
        {
            if (!string.IsNullOrEmpty(_localDreamPreset))
                return _localDreamPreset;
            if (!string.IsNullOrEmpty(DreamSession.PresetName))
                return DreamSession.PresetName;
            if (Dreams.Instance?.preset != null && !string.IsNullOrEmpty(Dreams.Instance.preset.name))
                return Core.getTrueLocationName(Dreams.Instance.preset.name);
            return "";
        }

        /// <summary>
        /// Overworld pose snapped before remote dream pad entry (any keyed pre-dream entry).
        /// Used by ClientStateBackup when live pose is still on the pad after dream flags clear.
        /// </summary>
        public static bool TryGetPreDreamOverworldPosition(out Vector3 pos)
        {
            foreach (var kvp in _preDreamPosition)
            {
                Vector3 p = kvp.Value;
                if (p.sqrMagnitude < 0.01f) continue;
                if (Mathf.Abs(p.x) >= 40000f || Mathf.Abs(p.z) >= 40000f) continue;
                pos = p;
                return true;
            }
            pos = Vector3.zero;
            return false;
        }

        /// <summary>
        /// This player left the dream by its own exit (<see cref="DreamExit"/>). The dream is not
        /// over: the host (<paramref name="keepSession"/>) keeps the session, the roster and the
        /// death tracking of the players still in it; a client only stops being part of it.
        /// </summary>
        public static void OnLocalDreamLeft(bool keepSession)
        {
            ResetHostEndClaim();
            if (!_localDreamActive)
                return;
            string leftPreset = ResolveActivePresetName();
            // A clock that ran meanwhile (someone woke earlier) is not put back.
            UnfreezeWorld(restoreTime: false);
            var net = ModRuntime.Network;
            if (!keepSession)
            {
                FinalDreamsceneManager.OnDreamEnded();
                ClearRemoteDreamRoster();
                net?.GameEventHandlers?.ClearPendingDreamGameEvents();
            }
            _localDreamActive = false;
            _hostOrderedDreamEnd = false;
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }
            // A player's own exit wakes it in its own clothes too, as the party's end does.
            RestoreLookAfterDream(leftPreset);
            ModRuntime.LegacyInfo($"[DreamSync] Local player left the dream: {leftPreset}"
                + (keepSession ? " (it goes on for the others)" : ""));
            if (!keepSession)
                _localDreamPreset = null;
        }

        /// <summary>Host, awake: the dream it kept running for the others is over.</summary>
        internal static void OnHostedDreamOver()
        {
            ResetHostEndClaim();
            ClearRemoteDreamRoster();
            _currentDreamPreset.Clear();
            _dreamEndBroadcastSent = false;
            _hostOrderedDreamEnd = false;
            _localDreamPreset = null;
            var net = ModRuntime.Network;
            net?.GameEventHandlers?.ClearPendingDreamGameEvents();
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }
        }

        public static void OnLocalDreamEnded()
        {
            ResetHostEndClaim();
            if (!_localDreamActive && !_hostOrderedDreamEnd) return;

            string endedPreset = ResolveActivePresetName();
            MarkDreamCompleted(0, endedPreset);
            UnfreezeWorld();

            FinalDreamsceneManager.OnDreamEnded();
            ClearRemoteDreamRoster();
            ModRuntime.Network?.GameEventHandlers?.ClearPendingDreamGameEvents();

            _localDreamActive = false;
            bool hostOrdered = _hostOrderedDreamEnd;
            _hostOrderedDreamEnd = false;

            string outcomeName = (Dreams.Instance != null) ? (Dreams.Instance.outcome ?? "") : "";

            var net = ModRuntime.Network;
            if (net != null && net.IsConnected)
            {
                // Host already fan-out at initiateEndDreaming; host-ordered clients never send.
                if (!_dreamEndBroadcastSent && !hostOrdered)
                {
                    var ended = DreamEndedMessage.Build(endedPreset ?? "", outcomeName);
                    if (net.Role == NetworkRole.Host)
                    {
                        net.Broadcast(NetMessageType.DreamEnded,
                            w => ended.Serialize(w),
                            LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                    else
                    {
                        net.Send(NetMessageType.DreamEnded,
                            w => ended.Serialize(w),
                            LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                }
                _dreamEndBroadcastSent = false;

                // Unfreeze all proxies; the dream has ended regardless of confirmation state.
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }
            else if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }

            RestoreLookAfterDream(endedPreset);
            ModRuntime.LegacyInfo($"[DreamSync] Local dream ended: {endedPreset}, outcome={outcomeName}");

            _localDreamPreset = null;
        }

        /// <summary>
        /// The home dream dresses the player with a toggle (<c>special_changeClothes</c> at its
        /// start, again at its end). In a party dream the end event can reach a player twice or
        /// not at all, and a player woke up in the dream's clothes, seen so by the others too.
        /// Vanilla's net effect of a dream outside the epilogue is the player's own clothes: kept.
        /// </summary>
        private static void RestoreLookAfterDream(string preset)
        {
            Player p = Player.Instance;
            if (p == null || p.inEpilogue || (!p.changedClothes && !p.naked))
                return;
            if (!string.IsNullOrEmpty(preset) && preset.StartsWith("epilog", System.StringComparison.OrdinalIgnoreCase))
                return;
            p.changedClothes = false;
            p.naked = false;
            p.switchAniLibrary("");
            ModLog.Event(LogCat.Dream, "Woke in the dream's clothes (" + preset + ") — own clothes back");
        }

    }
}
