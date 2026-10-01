using DG.Tweening;
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
        public static void BeginStoryEndDefer()
        {
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
            ModRuntime.LegacyInfo("[DreamSync] ForceLocalDreamCleanup: " + reason);
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
                UnfreezeWorld(restoreTime: false);
                FinalDreamsceneManager.OnDreamEnded();
                ClearRemoteDreamRoster();
                ClearPreDreamState();
                WorldQueryHelper.InvalidateCommonSceneScanCaches();
            }
            _localDreamActive = false;
            _localDreamPreset = null;
            _hostOrderedDreamEnd = false;
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
            DreamSession.IsActive || _localDreamActive || _remoteDreamActive.Values.Any(v => v)
            || _earlyEntryTransitionPlayed;

        public static bool IsLocalDreamActive => _localDreamActive;

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

        public static void OnLocalDreamStarted(string presetName, Vector3 locationPosition)
        {
            if (_localDreamActive) return;
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
            var net = ModRuntime.Network as LanNetworkManager;
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
                    foreach (int id in net.GetHandshakedPeerIds())
                    {
                        if (id > 0 && id != net.LocalPlayerId)
                            NoteRemoteInDream(id);
                    }
                    foreach (var dreamProxy in net.GetAllProxies())
                    {
                        if (dreamProxy != null)
                            NoteRemoteInDream(dreamProxy.PlayerId);
                    }
                    var started = DreamStartedMessage.Build(
                        presetName, locationPosition.x, locationPosition.y, locationPosition.z);
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

        public static void OnLocalDreamEnded()
        {
            if (!_localDreamActive && !_hostOrderedDreamEnd) return;

            string endedPreset = ResolveActivePresetName();
            MarkDreamCompleted(0, endedPreset);
            UnfreezeWorld();

            FinalDreamsceneManager.OnDreamEnded();
            ClearRemoteDreamRoster();
            (ModRuntime.Network as LanNetworkManager)?.GameEventHandlers?.ClearPendingDreamGameEvents();

            _localDreamActive = false;
            bool hostOrdered = _hostOrderedDreamEnd;
            _hostOrderedDreamEnd = false;

            string outcomeName = (Dreams.Instance != null) ? (Dreams.Instance.outcome ?? "") : "";

            var net = ModRuntime.Network as LanNetworkManager;
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

            ModRuntime.LegacyInfo($"[DreamSync] Local dream ended: {endedPreset}, outcome={outcomeName}");

            _localDreamPreset = null;
        }

    }
}
