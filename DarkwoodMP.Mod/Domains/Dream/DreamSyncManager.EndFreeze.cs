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
        public static void OnRemoteDreamEnded(int playerId, string outcomeName = "")
        {
            if (!_remoteDreamActive.TryGetValue(playerId, out bool active) || !active)
            {
                // Host-ordered story end may arrive while we only track via DreamSession /
                // local dreaming (remote flag already true from DreamStarted).
                if (Dreams.Instance != null && Dreams.Instance.dreaming
                    && TryBeginHostOrderedStoryEnd(outcomeName))
                    return;
                return;
            }

            string presetName = _currentDreamPreset.TryGetValue(playerId, out var p) ? p : null;

            MarkDreamCompleted(playerId, presetName);

            _remoteDreamActive[playerId] = false;

            ModRuntime.LegacyInfo($"[DreamSync] Remote dream ended (p{playerId}): {presetName}, outcome={outcomeName}");

            // Story exit: play the same outcome video as host, then vanilla endDreaming.
            // Hard ApplyRemoteDreamCleanup was breaking the client (no video / stuck world).
            if (Dreams.Instance != null && Dreams.Instance.dreaming
                && TryBeginHostOrderedStoryEnd(outcomeName))
            {
                _currentDreamPreset.Remove(playerId);
                // Keep pre-dream restore data until endDreaming; proxies unfreeze after video.
                return;
            }

            if (Dreams.Instance != null && Dreams.Instance.dreaming && Dreams.Instance.preset != null)
            {
                // ApplyRemoteDreamCleanup unfreezes after restore.
                ApplyRemoteDreamCleanup(outcomeName);
            }
            else
            {
                if (presetName != null)
                {
                    CleanupDreamScene(presetName);
                    RemoveDreamCameraEffects(presetName);
                }
                RestorePreDreamState(playerId);
                UnfreezeWorld();
                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null && net.IsConnected && Player.Instance != null)
                    net.TeleportRemoteProxyTo(Player.Instance._transform.position, 0f);
            }

            FinalDreamsceneManager.OnDreamEnded();
            (ModRuntime.Network as LanNetworkManager)?.GameEventHandlers?.ClearPendingDreamGameEvents();

            var unfreezeNet = ModRuntime.Network as LanNetworkManager;
            if (unfreezeNet != null)
            {
                foreach (var proxy in unfreezeNet.GetAllProxies())
                    proxy.FreezePosition = false;
            }

            _currentDreamPreset.Remove(playerId);
            _preDreamPosition.Remove(playerId);
            _preDreamGridName.Remove(playerId);
            _remoteDreamActive.Remove(playerId);
        }

        public static void OnDisconnected()
        {
            FinalDreamsceneManager.OnDisconnected();

            // Unfreeze any frozen proxies
            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }

            // Tear down local dream / entry overlay before clearing session maps.
            // Without this, disconnect mid-dream leaves FreezeWorld + EnteringDream stuck.
            bool localDreaming = Dreams.Instance != null && Dreams.Instance.dreaming;
            if (localDreaming || _localDreamActive || _earlyEntryTransitionPlayed || Core.EnteringDream
                || DreamSession.IsActive)
            {
                ForceLocalDreamCleanup("disconnected");
            }
            else
            {
                UnfreezeWorld(restoreTime: false);
                Core.EnteringDream = false;
            }

            // Clean up any active remote dreams
            foreach (var kvp in _currentDreamPreset)
            {
                CleanupDreamScene(kvp.Value);
                RemoveDreamCameraEffects(kvp.Value);
            }
            foreach (var kvp in _preDreamPosition)
            {
                RestorePreDreamState(kvp.Key);
            }

            _localDreamActive = false;
            _localDreamPreset = null;
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
            _remoteEntryTransitionPlaying = false;
            _remoteEntryAudioId = null;
            _dreamEndBroadcastSent = false;
            _hostOrderedDreamEnd = false;
            ClearRemoteDreamRoster();
            _currentDreamPreset.Clear();
            _preDreamPosition.Clear();
            _preDreamGridName.Clear();
            FreezeTracker.Reset();
            DreamSession.Reset();
        }

        /// <summary>Pad spawn failed after FreezeWorld; clear the session and unfreeze peers.</summary>
        private static void AbortFailedRemoteDreamLoad(int playerId, string reason)
        {
            if (_remoteDreamActive.ContainsKey(playerId))
                _remoteDreamActive[playerId] = false;
            _remoteDreamActive.Remove(playerId);
            _currentDreamPreset.Remove(playerId);
            _preDreamPosition.Remove(playerId);
            _preDreamGridName.Remove(playerId);
            FadeOutDreamTransition();
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
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
            UnfreezeWorld(restoreTime: false);
            if (DreamSession.IsActive)
                DreamSession.AbortStarting(reason);
            FinalDreamsceneManager.OnDreamEnded();
            ModRuntime.Log?.LogWarning("[DreamSync] AbortFailedRemoteDreamLoad: " + reason);
        }

        /// <summary>Marks a preset completed via DreamSession (sole authority).</summary>
        public static void MarkDreamCompleted(int playerId, string presetName)
        {
            DreamSession.MarkCompleted(presetName);
            ModRuntime.LegacyInfo($"[DreamSync] Marked dream as completed (session): {presetName}");
        }

        public static bool IsHostDreamEntity(Character c)
        {
            if (!_worldFrozen) return false;
            if (c == null) return false;
            if (Player.Instance != null && c.gameObject == Player.Instance.gameObject) return false;
            return !_frozenWorldCharacters.Contains(c);
        }

        public static bool IsHostDreamEntity(Component comp)
        {
            if (!_worldFrozen) return false;
            if (comp == null) return false;
            if (Player.Instance != null && comp.gameObject == Player.Instance.gameObject) return false;
            Character c = comp.GetComponentInParent<Character>();
            return c != null && !_frozenWorldCharacters.Contains(c);
        }

        public static bool IsWorldFrozenForComponent(Component comp)
        {
            if (!_worldFrozen) return false;
            if (comp == null) return false;
            if (Player.Instance != null && comp.gameObject == Player.Instance.gameObject) return false;
            if (comp.name.Contains("RemotePlayer")) return false;
            Character c = comp.GetComponentInParent<Character>();
            return c != null && _frozenWorldCharacters.Contains(c);
        }

        public static void FreezeWorld()
        {
            if (_worldFrozen) return;
            _worldFrozen = true;

            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null)
                _savedGameTime = (int)ctrl.CurrentTime;

            // Record all currently-existing characters as "frozen" (dream characters
            // spawned later are exempt so dream AI continues to work).
            int nAll = CharacterTracker.CopyAll(out Character[] all);
            _frozenWorldCharacters.Clear();
            for (int i = 0; i < nAll; i++)
            {
                Character c = all[i];
                if (c == null) continue;
                if (Player.Instance != null && c.gameObject == Player.Instance.gameObject) continue;
                _frozenWorldCharacters.Add(c);
            }

            ModRuntime.LegacyInfo($"[DreamSync] World frozen (time={_savedGameTime}, {_frozenWorldCharacters.Count} characters frozen)");
        }

        public static void UnfreezeWorld(bool restoreTime = true)
        {
            if (!_worldFrozen) return;
            _worldFrozen = false;

            if (restoreTime)
            {
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                    ctrl.CurrentTime = _savedGameTime;
                ModRuntime.LegacyInfo($"[DreamSync] World unfrozen (time restored to {_savedGameTime})");
            }
            else
            {
                ModRuntime.LegacyInfo("[DreamSync] World unfrozen (time not restored)");
            }

            _frozenWorldCharacters.Clear();
        }

    }
}
