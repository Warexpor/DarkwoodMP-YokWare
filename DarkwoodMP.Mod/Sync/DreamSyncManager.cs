using DG.Tweening;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Video;

namespace DWMPHorde.Sync
{
    internal static class DreamSyncManager
    {
        private static bool _localDreamActive;
        private static readonly Dictionary<int, bool> _remoteDreamActive = new Dictionary<int, bool>();
        private static readonly Dictionary<int, string> _currentDreamPreset = new Dictionary<int, string>();
        private static string _localDreamPreset;

        private static readonly Dictionary<int, Vector3> _preDreamPosition = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, string> _preDreamGridName = new Dictionary<int, string>();

        private static bool _worldFrozen;
        private static int _savedGameTime;
        private static readonly HashSet<Character> _frozenWorldCharacters = new HashSet<Character>();

        /// <summary>Peer already played startTransition via early CutsceneSync (before DreamStarted).</summary>
        private static bool _earlyEntryTransitionPlayed;
        private static float _earlyEntryTransitionDoneAt;

        /// <summary>
        /// Multiplayer-facing dream gate: session is authoritative when networked;
        /// falls back to local/remote flags for solo or mid-transition.
        /// </summary>
        public static bool IsDreamActive =>
            DreamSession.IsActive || _localDreamActive || _remoteDreamActive.Values.Any(v => v)
            || _earlyEntryTransitionPlayed;

        public static bool IsLocalDreamActive => _localDreamActive;

        /// <summary>Returns the dream Location's transform during an active dream, or null.</summary>
        public static Transform GetDreamLocationTransform()
        {
            if (!IsDreamActive) return null;
            if (Dreams.Instance != null && Dreams.Instance.dreamLocation != null)
                return Dreams.Instance.dreamLocation.transform;
            return null;
        }

        /// <summary>Delegates to DreamSession (sole completion authority).</summary>
        public static bool IsDreamCompleted(int playerId, string presetName)
        {
            return DreamSession.IsPresetCompleted(presetName);
        }

        /// <summary>Delegates to DreamSession (sole completion authority).</summary>
        public static bool IsDreamCompleted(string presetName)
        {
            return DreamSession.IsPresetCompleted(presetName);
        }

        public static void OnLocalDreamStarted(string presetName, Vector3 locationPosition)
        {
            if (_localDreamActive) return;
            if (DreamSession.IsPresetCompleted(presetName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Skipping completed dream: {presetName}");
                return;
            }
            _localDreamActive = true;
            _localDreamPreset = presetName;

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
                    var started = DreamStartedMessage.Build(
                        presetName, locationPosition.x, locationPosition.y, locationPosition.z);
                    net.Broadcast(NetMessageType.DreamStarted,
                        w => started.Serialize(w),
                        LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
            }

            ModRuntime.LegacyInfo($"[DreamSync] Local dream started: {presetName}, pos={locationPosition}");
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

        public static void OnLocalDreamEnded()
        {
            if (!_localDreamActive) return;

            MarkDreamCompleted(0, _localDreamPreset);
            UnfreezeWorld();

            FinalDreamsceneManager.OnDreamEnded();

            _localDreamActive = false;

            string outcomeName = (Dreams.Instance != null) ? (Dreams.Instance.outcome ?? "") : "";

            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.IsConnected)
            {
                var ended = DreamEndedMessage.Build(_localDreamPreset ?? "", outcomeName);
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

                // Unfreeze all proxies — dream has ended regardless of confirmation state
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }
            else if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }

            ModRuntime.LegacyInfo($"[DreamSync] Local dream ended: {_localDreamPreset}, outcome={outcomeName}");

            _localDreamPreset = null;
        }

        public static void OnRemoteDreamStarted(int playerId, string presetName, Vector3 locationPosition)
        {
            if (_remoteDreamActive.TryGetValue(playerId, out bool active) && active) return;
            if (DreamSession.IsPresetCompleted(presetName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Skipping completed dream on remote (p{playerId}): {presetName}");
                return;
            }
            _remoteDreamActive[playerId] = true;
            _currentDreamPreset[playerId] = presetName;

            FreezeWorld();

            if (!FinalDreamsceneManager.IsActive)
                FinalDreamsceneManager.OnDreamStarted();

            ModRuntime.LegacyInfo($"[DreamSync] Remote dream started (p{playerId}): {presetName}, pos={locationPosition}");

            SavePreDreamState(playerId);
            ProcessRemoteDream(playerId, locationPosition);
        }

        private static void ProcessRemoteDream(int playerId, Vector3 locationPosition)
        {
            string presetName = _currentDreamPreset.TryGetValue(playerId, out var p) ? p : null;
            if (presetName == null) return;
            ApplyDreamCameraEffects(presetName);
            Singleton<Controller>.Instance.StartCoroutine(ProcessRemoteDreamCoroutine(playerId, locationPosition));
        }

        /// <summary>
        /// Peer started Dreams.startTransition — play the same video now (not after DreamStarted).
        /// </summary>
        public static void OnPeerDreamEntryTransition()
        {
            if (_localDreamActive) return;
            if (_earlyEntryTransitionPlayed) return;

            _earlyEntryTransitionPlayed = true;
            FreezeWorld();

            float wait = StartRemoteDreamTransition();
            _earlyEntryTransitionDoneAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, wait);
            // So DreamTransition.skip / ActionSkipTransition can cut the wait.
            if (Dreams.Instance?.startTransition != null)
                Dreams.Instance.startTransition.isPlaying = true;
            ModRuntime.LegacyInfo($"[DreamSync] Early entry transition (peer), wait={wait:F1}s");
        }

        /// <summary>Skip / cancel early entry wait so DreamStarted load is not blocked.</summary>
        public static void OnEntryTransitionSkipped()
        {
            if (!_earlyEntryTransitionPlayed) return;
            _earlyEntryTransitionDoneAt = Time.realtimeSinceStartup;
            if (Dreams.Instance?.startTransition != null)
                Dreams.Instance.startTransition.isPlaying = false;
            FadeOutDreamTransition();
        }

        private static IEnumerator ProcessRemoteDreamCoroutine(int playerId, Vector3 locationPosition)
        {
            string presetName = _currentDreamPreset.TryGetValue(playerId, out var p) ? p : null;

            // Snapshot parity with host prepareDream (D4).
            if (Dreams.Instance != null && !Dreams.Instance.dreaming && !Dreams.Instance.switchingDream)
            {
                try { Dreams.Instance.saveCurrentPlayerState(); }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DreamSync] saveCurrentPlayerState: " + ex.Message);
                }
            }

            // 1. Entry video: already started on CutsceneSync DreamEntry, or play now (late/missed).
            if (_earlyEntryTransitionPlayed)
            {
                float remain = _earlyEntryTransitionDoneAt - Time.realtimeSinceStartup;
                if (remain > 0.05f)
                {
                    ModRuntime.LegacyInfo($"[DreamSync] Waiting remaining entry transition {remain:F1}s");
                    yield return new WaitForSeconds(remain);
                }
            }
            else
            {
                float waitTime = StartRemoteDreamTransition();
                if (waitTime > 0f)
                {
                    ModRuntime.LegacyInfo($"[DreamSync] Waiting {waitTime:F1}s for remote dream transition");
                    yield return new WaitForSeconds(waitTime);
                }
            }

            // 2. Clean up the video overlay (fade out)
            FadeOutDreamTransition();
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;

            // 3. NOW load the dream scene (after transition is complete)
            if (presetName != null)
                yield return LoadDreamSceneCoroutine(presetName, locationPosition, false, playerId);
            else
            {
                ModRuntime.Log?.LogError("[DreamSync] Remote dream entry missing preset — unfreezing");
                UnfreezeWorld();
                if (_remoteDreamActive.ContainsKey(playerId))
                    _remoteDreamActive[playerId] = false;
            }
        }

        /// <summary>Host broadcast chain: load next pocket without full session Idle.</summary>
        public static void OnDreamChain(string nextPreset)
        {
            if (string.IsNullOrEmpty(nextPreset)) return;
            _localDreamPreset = nextPreset;
            _localDreamActive = true;
            if (Player.Instance != null)
            {
                int pid = 0;
                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null)
                    pid = net.LocalPlayerId;
                _currentDreamPreset[pid] = nextPreset;
                _remoteDreamActive[pid] = true;
            }
            Vector3 pos = Dreams.Instance?.dreamLocation != null
                ? Dreams.Instance.dreamLocation.transform.position
                : (Player.Instance != null ? Player.Instance._transform.position : Vector3.zero);
            if (Singleton<Controller>.Instance != null)
                Singleton<Controller>.Instance.StartCoroutine(ProcessChainCoroutine(nextPreset, pos));
        }

        private static IEnumerator ProcessChainCoroutine(string presetName, Vector3 locationPosition)
        {
            // Keep world frozen; tear previous dream location if still present.
            if (Dreams.Instance != null && Dreams.Instance.dreaming)
            {
                LanNetworkManager.IsApplyingRemoteState = true;
                try
                {
                    Dreams.Instance.dreaming = false;
                    Dreams.Instance.destroyDream();
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DreamSync] chain destroy: " + ex.Message);
                }
                finally { LanNetworkManager.IsApplyingRemoteState = false; }
            }

            yield return LoadDreamSceneCoroutine(presetName, locationPosition, false, 0);
            DreamSession.MarkActive();
        }

        private static void FadeOutDreamTransition()
        {
            Core.EnteringDream = false;
            if (Singleton<UI>.Instance == null) return;
            try
            {
                var overlay = Singleton<UI>.Instance.videoOverlay;
                if (overlay != null && overlay.gameObject.activeSelf)
                {
                    var renderer = overlay.GetComponent<Renderer>();
                    if (renderer != null && renderer.material != null)
                    {
                        renderer.material.DOFade(0f, 0.5f).OnComplete(() =>
                        {
                            overlay.gameObject.SetActive(false);
                            renderer.enabled = false;
                            Core.showGameCursor();
                        }).SetUpdate(true);
                    }
                    else
                    {
                        overlay.gameObject.SetActive(false);
                        Core.showGameCursor();
                    }
                }
                else
                {
                    Core.showGameCursor();
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Error fading out transition: {ex}");
            }
        }

        public static void OnRemoteDreamEnded(int playerId, string outcomeName = "")
        {
            if (!_remoteDreamActive.TryGetValue(playerId, out bool active) || !active) return;

            string presetName = _currentDreamPreset.TryGetValue(playerId, out var p) ? p : null;

            MarkDreamCompleted(playerId, presetName);
            FinalDreamsceneManager.OnDreamEnded();

            _remoteDreamActive[playerId] = false;

            ModRuntime.LegacyInfo($"[DreamSync] Remote dream ended (p{playerId}): {presetName}, outcome={outcomeName}");

            if (Dreams.Instance != null && Dreams.Instance.dreaming && Dreams.Instance.preset != null)
            {
                // ApplyRemoteDreamCleanup unfreezes after restore (D8).
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
            _remoteDreamActive.Clear();
            _currentDreamPreset.Clear();
            _preDreamPosition.Clear();
            _preDreamGridName.Clear();
            FreezeTracker.Reset();
            DreamSession.Reset();
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
            Character[] all = CharacterTracker.GetAll();
            _frozenWorldCharacters.Clear();
            foreach (var c in all)
            {
                if (c == null) continue;
                if (Player.Instance != null && c.gameObject == Player.Instance.gameObject) continue;
                _frozenWorldCharacters.Add(c);
            }

            ModRuntime.LegacyInfo($"[DreamSync] World frozen (time={_savedGameTime}, {_frozenWorldCharacters.Count} characters frozen)");
        }

        public static void UnfreezeWorld()
        {
            if (!_worldFrozen) return;
            _worldFrozen = false;

            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null)
                ctrl.CurrentTime = _savedGameTime;

            _frozenWorldCharacters.Clear();

            ModRuntime.LegacyInfo($"[DreamSync] World unfrozen (time restored to {_savedGameTime})");
        }

        private static void ApplyRemoteDreamCleanup(string outcomeName = "")
        {
            var dreams = Dreams.Instance;
            var player = Player.Instance;
            if (player == null || dreams == null) return;

            // D8 order: restore player → destroy dream → unfreeze → world/journal effects.
            string pendingOutcome = outcomeName ?? "";

            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                if (dreams.preset != null)
                    Core.modifyCamEffects(active: false, dreams.preset.gameObject);

                AudioController.StopMusic(1f);
                Core.spawnCharactersAtNight = true;

                player.Hotbar.clear();
                player.Inventory.clear();
                if (dreams.inventorySlotsCopy.Count > 0)
                {
                    Inventory.moveSlots(dreams.inventorySlotsCopy, player.Inventory.slots);
                    Inventory.moveSlots(dreams.hotbarSlotsCopy, player.Hotbar.slots);
                    player.Hotbar.hide();
                    player.Hotbar.show();
                }

                // Personal rewards first (items/journal) — defer fireGameEvent/world until unfreeze.
                if (!string.IsNullOrEmpty(pendingOutcome))
                    ApplyOutcomeEffects(dreams, player, pendingOutcome, worldEvents: false);

                // Vanilla endDreaming parity: journal dream entries, rain, unique teleport, time.
                try { Singleton<UI>.Instance?.journal?.clearDreamEntries(); }
                catch (Exception) { /* journal may be null mid-teardown */ }

                Vector3 restorePos = dreams.positionCopy;
                if (dreams.preset != null)
                {
                    string trueName = Core.getTrueLocationName(dreams.preset.name);
                    if (trueName == "dream_tutorial_01")
                    {
                        var wg = Singleton<WorldGenerator>.Instance;
                        if (wg?.playerBase != null)
                        {
                            var loc = wg.playerBase.GetComponent<Location>();
                            if (loc?.playerSpawn != null)
                                restorePos = loc.playerSpawn.transform.position;
                        }
                        player.firstPlay = false;
                        dreams.timeCopy = 5f;
                    }
                    if (!string.IsNullOrEmpty(dreams.preset.uniqueObjectToTransportToAfterDreamEnd)
                        && Singleton<UniqueObjects>.Instance != null)
                    {
                        GameObject uo = Singleton<UniqueObjects>.Instance.getObject(
                            dreams.preset.uniqueObjectToTransportToAfterDreamEnd);
                        if (uo != null)
                            restorePos = uo.transform.position;
                    }
                }

                if (dreams.placeStartedDreaming != null)
                {
                    if (!dreams.placeStartedDreaming.isOutsideLocation)
                    {
                        Singleton<OutsideLocations>.Instance.playerInOutsideLocation = false;
                        Singleton<OutsideLocations>.Instance.currentLocationName = "";
                        Singleton<Rain>.Instance?.unhide();
                    }
                    else
                    {
                        Singleton<OutsideLocations>.Instance.currentLocationName =
                            Core.getTrueLocationName(dreams.placeStartedDreaming.name);
                        if (!dreams.placeStartedDreaming.isUnderground)
                            Singleton<Rain>.Instance?.unhide();
                    }
                }
                else
                {
                    Singleton<OutsideLocations>.Instance.playerInOutsideLocation = false;
                    Singleton<OutsideLocations>.Instance.currentLocationName = "";
                    Singleton<Rain>.Instance?.unhide();
                }

                player.teleportTo(restorePos, Quaternion.Euler(90f, 0f, 0f));
                player.Hotbar.selectSlot(0, noiseless: true, force: true);

                Singleton<Controller>.Instance.CurrentTime = (int)dreams.timeCopy;

                bool endDiving = dreams.preset != null && dreams.preset.endDivingOut;
                dreams.destroyDream();
                dreams.dreaming = false;
                dreams.dreamPrepared = false;
                dreams.wantToDream = false;
                _localDreamActive = false;

                if (dreams.placeStartedDreaming != null)
                {
                    string locName = Core.getTrueLocationName(dreams.placeStartedDreaming.name);
                    if (dreams.placeStartedDreaming.isOutsideLocation &&
                        Singleton<OutsideLocations>.Instance.spawnedLocations.ContainsKey(locName))
                    {
                        Singleton<OutsideLocations>.Instance.spawnedLocations[locName].enter();
                    }
                    Singleton<WorldGrid>.Instance.setGrid(
                        dreams.placeStartedDreaming.isOutsideLocation ? locName : "World");
                    Singleton<WorldGrid>.Instance.refreshPosition(player._transform.position, true, true);
                }
                else
                {
                    Singleton<WorldGrid>.Instance.setGrid("World");
                }

                player.endDreaming(true);

                if (endDiving && Singleton<Controller>.Instance != null)
                {
                    Singleton<Controller>.Instance.Invoke(delegate
                    {
                        if (Player.Instance != null)
                            Player.Instance.diveOut();
                    }, 1f, timeScaleDependent: true);
                }

                // Restore pre-dream effects (vanilla endDreaming).
                try
                {
                    if (dreams.effectsCopy != null)
                    {
                        dreams.effectsCopy.loadValues(player.effects);
                        dreams.effectsCopy.effects.Clear();
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DreamSync] effectsCopy restore: " + ex.Message);
                }

                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null && net.IsConnected)
                    net.TeleportRemoteProxyTo(player._transform.position, 0f);

                dreams.placeStartedDreaming = null;
                DreamSession.ClearPendingHostPreset();

                try
                {
                    Singleton<RandomWorldSounds>.Instance?.resumeGlobalSounds();
                    if (Singleton<Controller>.Instance != null && Singleton<Controller>.Instance.isAfterNight)
                        Singleton<Controller>.Instance.addAfterNightEffect();
                    Singleton<Controller>.Instance?.refreshTimeNoLogic();
                    Singleton<Controller>.Instance?.updateAmbientLight();
                    player.whereAmI?.checkWhereAmI();
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DreamSync] post-cleanup world hooks: " + ex.Message);
                }

                ModRuntime.LegacyInfo($"[DreamSync] Remote dream cleanup applied");
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = false;
            }

            UnfreezeWorld();

            // World events after forest is live again.
            if (!string.IsNullOrEmpty(pendingOutcome) && dreams.preset != null)
            {
                LanNetworkManager.IsApplyingRemoteState = true;
                try { ApplyOutcomeEffects(dreams, player, pendingOutcome, worldEvents: true); }
                finally { LanNetworkManager.IsApplyingRemoteState = false; }
            }
        }

        /// <param name="worldEvents">
        /// false = personal rewards only; true = fireGameEvent / fireWorldEvent only (D8).
        /// </param>
        private static void ApplyOutcomeEffects(Dreams dreams, Player player, string outcomeName, bool worldEvents = true)
        {
            if (dreams.preset == null || dreams.preset.outcomes == null) return;

            DreamPreset.Outcome outcomePreset = null;
            foreach (var oc in dreams.preset.outcomes)
            {
                if (oc != null && oc.name == outcomeName)
                {
                    outcomePreset = oc;
                    break;
                }
            }
            if (outcomePreset == null)
            {
                foreach (var oc in dreams.preset.outcomes)
                {
                    if (oc != null && oc.name == "default")
                    {
                        outcomePreset = oc;
                        break;
                    }
                }
            }
            if (outcomePreset == null && dreams.preset.outcomes.Count > 0)
                outcomePreset = dreams.preset.outcomes[0];

            if (outcomePreset == null) return;

            foreach (var effect in outcomePreset.effects)
            {
                switch (effect.type)
                {
                    case global::DreamPreset.Outcome.Effect.Type.createInvItem:
                        if (worldEvents) break;
                        if (effect.invItem != null)
                        {
                            var go = effect.invItem as UnityEngine.GameObject;
                            if (go != null)
                            {
                                var invItem = go.GetComponent<InvItem>();
                                if (invItem != null)
                                    player.Inventory.addItemTypeToPlayer(invItem.type, effect.amount, dropIfNoRoom: true);
                            }
                        }
                        break;

                    case global::DreamPreset.Outcome.Effect.Type.addJournalItem:
                        if (worldEvents) break;
                        if (effect.invItem != null)
                        {
                            var go = effect.invItem as UnityEngine.GameObject;
                            if (go != null)
                            {
                                var invItem = go.GetComponent<InvItem>();
                                if (invItem != null)
                                    player.Inventory.addJournalItem(invItem.type, showImmediately: false, noPopup: true);
                                var journalEntry = go.GetComponent<JournalEntry>();
                                if (journalEntry != null)
                                    Singleton<UI>.Instance.journal.addJournalEntry(journalEntry.name, noPopup: true);
                            }
                        }
                        break;

                    case global::DreamPreset.Outcome.Effect.Type.fireGameEvent:
                        if (!worldEvents) break;
                        if (effect.destPrefab != null)
                        {
                            var go = effect.destPrefab as UnityEngine.GameObject;
                            if (go != null)
                            {
                                var gameEvents = go.GetComponent<GameEvents>();
                                if (gameEvents != null)
                                {
                                    gameEvents.fired = false;
                                    gameEvents.fire();
                                }
                            }
                        }
                        break;

                    case global::DreamPreset.Outcome.Effect.Type.fireWorldEvent:
                        if (!worldEvents) break;
                        if (!string.IsNullOrEmpty(effect.worldEventType))
                            Singleton<Events>.Instance.fireWorldEvent(effect.worldEventType);
                        break;

                    case global::DreamPreset.Outcome.Effect.Type.transferToDream:
                        // Host owns chain via DreamChainStart / prepareDream(switchingDream).
                        break;

                    case global::DreamPreset.Outcome.Effect.Type.addCharacterEffect:
                        // Vanilla endDreaming loop does not apply this type either; effectsCopy restores pre-dream.
                        break;
                    default:
                        if (!worldEvents)
                            ModRuntime.Log?.LogWarning($"[DreamSync] Unhandled outcome effect type: {effect.type}");
                        break;
                }
            }

            if (!worldEvents && outcomePreset.customEndTime)
                Singleton<Controller>.Instance.CurrentTime = outcomePreset.endTime;
        }

        /// <summary>True when a physics object should sync during an active dream (D12).</summary>
        public static bool ShouldSyncPhysicsObject(Transform t)
        {
            if (t == null) return true;
            if (!IsLocalDreamActive && !DreamSession.IsActive)
                return true; // overworld: all free bodies
            Transform dreamLoc = GetDreamLocationTransform();
            if (dreamLoc == null)
                return true;
            // Only free bodies under the dream pocket (or near player in dream grid).
            if (t.IsChildOf(dreamLoc) || t == dreamLoc)
                return true;
            if (Player.Instance != null
                && Dreams.Instance?.preset != null
                && Singleton<WorldGrid>.Instance?.currentGrid != null
                && string.Equals(Singleton<WorldGrid>.Instance.currentGrid.name, Dreams.Instance.preset.name,
                    StringComparison.OrdinalIgnoreCase)
                && Vector3.Distance(t.position, Player.Instance._transform.position) < 80f)
                return true;
            return false;
        }

        private static void SavePreDreamState(int playerId)
        {
            var player = Player.Instance;
            _preDreamPosition[playerId] = player != null ? player._transform.position : Vector3.zero;
            _preDreamGridName[playerId] = Singleton<WorldGrid>.Instance != null && Singleton<WorldGrid>.Instance.currentGrid != null
                ? Singleton<WorldGrid>.Instance.currentGrid.name
                : "World";
        }

        private static void RestorePreDreamState(int playerId)
        {
            if (!_preDreamPosition.TryGetValue(playerId, out var position)) return;
            if (!_preDreamGridName.TryGetValue(playerId, out var gridName)) gridName = "World";

            var player = Player.Instance;
            if (player != null)
            {
                player.invulnerable = false;
                if (player.immobilised)
                    player.stopImmobilise();
                player.switchVisibilty(true);
                player.teleportTo(position, Quaternion.Euler(90f, 0f, 0f));
            }

            if (Singleton<WorldGrid>.Instance != null)
            {
                if (Singleton<WorldGrid>.Instance.currentGrid != null)
                    Singleton<WorldGrid>.Instance.currentGrid.leave();
                Singleton<WorldGrid>.Instance.setGrid(gridName ?? "World");
                Vector3 restorePos = player != null ? player._transform.position : position;
                Singleton<WorldGrid>.Instance.refreshPosition(restorePos, instant: true, force: true);
            }

            if (Singleton<UI>.Instance != null)
                Singleton<UI>.Instance.showVisibleUI();
        }

        private static IEnumerator LoadDreamSceneCoroutine(string locationName, Vector3 position, bool _, int playerId = 0)
        {
            yield return null;

            if (IsDreamCompleted(playerId, locationName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Aborting remote dream load — already completed: {locationName}");
                if (_remoteDreamActive.ContainsKey(playerId))
                    _remoteDreamActive[playerId] = false;
                UnfreezeWorld();
                FinalDreamsceneManager.OnDreamEnded();
                _currentDreamPreset.Remove(playerId);
                yield break;
            }

            Location component = null;
            yield return StartLoadDreamScene(locationName, position, result => component = result);

            if (component == null)
            {
                yield break;
            }

            if (IsDreamCompleted(playerId, locationName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Aborting remote dream entry — completed during load: {locationName}");
                if (_remoteDreamActive.ContainsKey(playerId))
                    _remoteDreamActive[playerId] = false;
                UnfreezeWorld();
                FinalDreamsceneManager.OnDreamEnded();
                _currentDreamPreset.Remove(playerId);
                yield break;
            }

            var player = Player.Instance;
            if (player == null) yield break;

            Vector3 spawnPos = component.playerSpawn != null
                ? component.playerSpawn.transform.position
                : position;

            player.teleportTo(spawnPos, Quaternion.Euler(90f, 0f, 0f));
            ApplyDreamCameraEffects(locationName);

            if (Singleton<WorldGrid>.Instance != null)
            {
                if (Singleton<WorldGrid>.Instance.currentGrid != null)
                    Singleton<WorldGrid>.Instance.currentGrid.leave();
                Singleton<WorldGrid>.Instance.setGrid(locationName);
                Singleton<WorldGrid>.Instance.refreshPosition(player._transform.position, instant: true, force: true);
            }

            // Call startDreaming() on the remote player so they receive dream items,
            // proper animation library, dream health, etc. (same as vanilla initiator).
            if (Dreams.Instance != null && !Dreams.Instance.dreaming && !IsDreamCompleted(playerId, locationName))
            {
                LanNetworkManager.IsApplyingRemoteState = true;
                try
                {
                    if (Dreams.Instance.preset == null)
                    {
                        GameObject presetGO = Resources.Load("DreamPresets/" + locationName) as GameObject;
                        if (presetGO != null)
                            Dreams.Instance.preset = presetGO.GetComponent<DreamPreset>();
                    }
                    // Resources path skips getPreset random remove — keep one-shot pool aligned.
                    DreamSession.MirrorPoolRemove(locationName);
                    Dreams.Instance.dreamLocation = component;
                    ApplyEpilogueModeIfNeeded(component, locationName);
                    Dreams.Instance.startDreaming();
                    _localDreamActive = true;
                }
                finally
                {
                    LanNetworkManager.IsApplyingRemoteState = false;
                }
            }
            else if (IsDreamCompleted(playerId, locationName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Blocked remote startDreaming — already completed: {locationName}");
            }

            // Teleport the remote proxy (host's character) to the dream position so
            // both players see each other immediately.
            var network = ModRuntime.Network as LanNetworkManager;
            if (network != null && network.IsConnected)
            {
                Vector3 dreamPos = player != null ? player._transform.position : spawnPos;
                network.TeleportRemoteProxyTo(dreamPos, 0f);

                // Send confirmation back to the dream initiator so they unfreeze our proxy.
                // From this point forward, position updates will come from the dream scene.
                network.Send(NetMessageType.DreamEntered,
                    w => new DreamEnteredMessage().Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            ModRuntime.LegacyInfo($"[DreamSync] Player positioned at dream location: {locationName}");
        }

        private static IEnumerator StartLoadDreamScene(string locationName, Vector3 position, Action<Location> onComplete)
        {
            yield return null;

            // Must unload textures before spawning new location — vanilla
            // OutsideLocations.spawnLocation does this first thing.
            if (Singleton<Controller>.Instance != null)
                Singleton<Controller>.Instance.unloadTextures();

            GameObject markerObj = Core.AddPrefab("LocationMarker",
                position,
                Quaternion.Euler(90f, 0f, 0f),
                null);

            if (markerObj == null)
            {
                ModRuntime.Log?.LogError("[DreamSync] Failed to create LocationMarker prefab");
                onComplete?.Invoke(null);
                yield break;
            }

            LocationMarker marker = markerObj.GetComponent<LocationMarker>();
            marker.locationName = locationName;

            if (Singleton<WorldGenerator>.Instance != null)
                OutsideLocations.createGrid(locationName, marker.transform.position);

            GameObject holder = markerObj;
            Transform parentTransform = null;
            if (Singleton<WorldGenerator>.Instance != null && Singleton<WorldGenerator>.Instance.OutsideLocationsGO != null)
            {
                holder = Singleton<WorldGenerator>.Instance.OutsideLocationsGO;
                parentTransform = holder.transform;
            }

            yield return marker.StartCoroutine(marker.spawnLocation(holder));

            if (marker.thisLocation == null)
            {
                ModRuntime.Log?.LogError("[DreamSync] marker.thisLocation is null after spawnLocation");
                onComplete?.Invoke(null);
                yield break;
            }

            Location component = marker.thisLocation.GetComponent<Location>();

            if (parentTransform != null)
                marker.thisLocation.transform.parent = parentTransform;

            Singleton<OutsideLocations>.Instance.spawnedLocations[locationName] = component;
            Dreams.Instance.dreamLocation = component;

            // Activate all child objects — vanilla transportToLocation calls
            // spawnedLocations[locationName].enter() which does activateChildren(true).
            // Without this, terrain renderers stay inactive → all-black scene.
            component.enter();

            // Vanilla Dreams.onLocationSpawned sets inEpilogue for epilogue locations.
            // Remote load path never hits that — clients would miss crawl/death/UI mode.
            ApplyEpilogueModeIfNeeded(component, locationName);

            if (holder != markerObj)
                UnityEngine.Object.Destroy(markerObj);

            ModRuntime.LegacyInfo($"[DreamSync] Dream scene loaded: {locationName} at {position}");
            onComplete?.Invoke(component);
        }

        /// <summary>
        /// Mirror vanilla Dreams.onLocationSpawned epilogue branch for remote/host co-op entry.
        /// </summary>
        internal static void ApplyEpilogueModeIfNeeded(Location location, string locationName = null)
        {
            try
            {
                bool isEpilogue = (location != null && location.isEpilogueLocation)
                    || (!string.IsNullOrEmpty(locationName)
                        && locationName.IndexOf("epilog", System.StringComparison.OrdinalIgnoreCase) >= 0);

                if (!isEpilogue) return;
                if (Player.Instance == null || Player.Instance.inEpilogue) return;

                Player.Instance.inEpilogue = true;
                if (Singleton<UI>.Instance != null)
                    Singleton<UI>.Instance.hideVisibleUI();

                var cam = Singleton<CamMain>.Instance;
                if (cam != null && cam.FireMaskCam != null)
                    cam.FireMaskCam.gameObject.SetActive(true);

                // First-entry epilogue title (same delay spirit as OutsideLocations.onSpawnedLocation).
                if (Singleton<Controller>.Instance != null && Singleton<UI>.Instance != null)
                {
                    Singleton<Controller>.Instance.Invoke(
                        Singleton<UI>.Instance.showEpilogueText, 1f, timeScaleDependent: true);
                }

                ModRuntime.LegacyInfo($"[DreamSync] Epilogue mode applied (loc={locationName ?? location?.name})");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] ApplyEpilogueMode failed: {ex.Message}");
            }
        }

        private static void CleanupDreamScene(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) return;

            try
            {
                if (Singleton<WorldGrid>.Instance != null)
                {
                    var grid = Singleton<WorldGrid>.Instance.getGrid(locationName);
                    if (grid != null)
                        Singleton<WorldGrid>.Instance.grids.Remove(grid);
                }

                if (Singleton<OutsideLocations>.Instance != null &&
                    Singleton<OutsideLocations>.Instance.spawnedLocations.ContainsKey(locationName))
                {
                    Singleton<OutsideLocations>.Instance.spawnedLocations.Remove(locationName);
                }

                GameObject targetObj = null;
                if (Dreams.Instance != null && Dreams.Instance.dreamLocation != null && Dreams.Instance.dreamLocation.gameObject != null)
                {
                    string objName = Dreams.Instance.dreamLocation.gameObject.name.Replace("_done", "");
                    if (string.Equals(objName, locationName, StringComparison.OrdinalIgnoreCase))
                        targetObj = Dreams.Instance.dreamLocation.gameObject;
                }

                if (targetObj == null)
                    targetObj = GameObject.Find(locationName + "_done");

                if (targetObj != null)
                {
                    UnityEngine.Object.Destroy(targetObj, 2f);
                    if (Dreams.Instance != null)
                        Dreams.Instance.dreamLocation = null;
                }

                ModRuntime.LegacyInfo($"[DreamSync] Dream scene cleaned up: {locationName}");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Error during dream scene cleanup: {ex}");
            }
        }

        private static void ApplyDreamCameraEffects(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            try
            {
                GameObject presetGO = Resources.Load("DreamPresets/" + presetName) as GameObject;
                if (presetGO != null)
                {
                    Core.modifyCamEffects(active: true, presetGO);
                    ModRuntime.LegacyInfo($"[DreamSync] Applied camera effects for dream: {presetName}");
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to apply camera effects: {ex}");
            }
        }

        private static void RemoveDreamCameraEffects(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            try
            {
                GameObject presetGO = Resources.Load("DreamPresets/" + presetName) as GameObject;
                if (presetGO != null)
                {
                    Core.modifyCamEffects(active: false, presetGO);
                    ModRuntime.LegacyInfo($"[DreamSync] Removed camera effects for dream: {presetName}");
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to remove camera effects: {ex}");
            }
        }

        /// <summary>
        /// Starts the dream transition (video/audio overlay) on the remote client.
        /// Returns the number of seconds to wait for the transition to finish,
        /// or 0 if the transition should be skipped (fallback to black screen).
        /// </summary>
        private static float StartRemoteDreamTransition()
        {
            var transition = Dreams.Instance?.startTransition;
            if (transition == null || transition.transitionObjects == null)
            {
                ShowDreamTransitionFallback();
                return 0f;
            }

            try
            {
                Core.EnteringDream = true;

                // Stop all audio (same as DreamTransition.transition)
                AudioController.StopAll(transition.fadeAllAudioTime);
                Singleton<Controller>.Instance?.fadeAudio(fadeOut: true, 2f, musicToo: false);

                float videoLength = 0f;
                bool hasVideo = false;

                for (int i = 0; i < transition.transitionObjects.Count; i++)
                {
                    var obj = transition.transitionObjects[i];
                    if (obj == null) continue;

                    if (obj.type == DreamTransition.TransitionObject.Type.Audio)
                    {
                        AudioController.Play(obj.audioItemName);
                    }
                    else if (obj.type == DreamTransition.TransitionObject.Type.Video)
                    {
                        hasVideo = true;
                        Renderer renderer = Singleton<UI>.Instance.videoOverlay.GetComponent<Renderer>();
                        if (renderer == null) continue;

                        string path = "Video/" + obj.videoName;
                        if (obj.localizedVideo)
                            path = path + "_" + GameSettings.GetString("LanguageCode");

                        VideoClip clip = Resources.Load(path, typeof(VideoClip)) as VideoClip;
                        if (clip == null) continue;

                        videoLength = (float)clip.length;
                        renderer.enabled = false;
                        VideoPlayer vp = renderer.GetComponent<VideoPlayer>();
                        vp.clip = clip;

                        // Fade in
                        if (obj.fadeIn > 0f)
                        {
                            renderer.material.color = new Color(1f, 1f, 1f, 0f);
                            renderer.material.DOFade(1f, obj.fadeIn).SetUpdate(true);
                        }
                        else
                        {
                            renderer.material.color = new Color(1f, 1f, 1f, 1f);
                        }

                        vp.prepareCompleted += OnRemoteTransitionVideoPrepared;
                        vp.Prepare();
                        Singleton<UI>.Instance.videoOverlay.gameObject.SetActive(true);
                    }
                }

                // Use durationOverride if set (same logic as DreamTransition.transition)
                if (transition.durationOverride > 0f)
                    videoLength = transition.durationOverride;

                if (!hasVideo)
                {
                    ShowDreamTransitionFallback();
                    return 0f;
                }

                // Set black screen behind the video overlay so there's no
                // visual gap when the video ends and the dream loads.
                if (Singleton<UI>.Instance != null)
                    Singleton<UI>.Instance.tweenBlackScreen(new Color(0f, 0f, 0f, 1f), 0.1f);

                ModRuntime.LegacyInfo($"[DreamSync] Remote dream transition started, wait={videoLength:F1}s");
                return videoLength;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to play remote dream transition: {ex}");
                ShowDreamTransitionFallback();
                return 0f;
            }
        }

        private static void OnRemoteTransitionVideoPrepared(VideoPlayer player)
        {
            player.prepareCompleted -= OnRemoteTransitionVideoPrepared;
            Singleton<UI>.Instance.videoOverlay.GetComponent<Renderer>().enabled = true;
            player.Play();
        }

        private static void ShowDreamTransitionFallback()
        {
            if (Singleton<UI>.Instance == null) return;

            try
            {
                var blackTop = Singleton<UI>.Instance.blackScreenTop;
                if (blackTop != null)
                {
                    var sprite = blackTop.GetComponent<tk2dBaseSprite>();
                    if (sprite != null)
                    {
                        Singleton<UI>.Instance.tweenBlackScreenTop(new Color(0f, 0f, 0f, 1f), 0.3f);
                        Singleton<Controller>.Instance?.waitFramesAndRun(delegate
                        {
                            if (sprite != null && sprite.color.a != 0f)
                            {
                                Singleton<UI>.Instance.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 0.5f);
                            }
                        }, 1);
                    }
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to show dream transition fallback: {ex}");
            }
        }

        /// <summary>
        /// Safety timeout: unfreezes all remote proxies after <paramref name="delay"/> seconds
        /// of real time. Prevents permanent proxy freeze if DreamEntered never arrives.
        /// </summary>
        private static System.Collections.IEnumerator UnfreezeProxiesAfterDelay(float delay)
        {
            yield return new UnityEngine.WaitForSecondsRealtime(delay);
            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }
        }
    }
}
