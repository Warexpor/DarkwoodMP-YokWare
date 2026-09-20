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
        private static IEnumerator LoadDreamSceneCoroutine(string locationName, Vector3 position, bool _, int playerId = 0)
        {
            yield return null;

            if (IsDreamCompleted(playerId, locationName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Aborting remote dream load — already completed: {locationName}");
                AbortFailedRemoteDreamLoad(playerId, "already_completed");
                yield break;
            }

            Location component = null;
            yield return StartLoadDreamScene(locationName, position, result => component = result);

            if (component == null)
            {
                ModRuntime.Log?.LogWarning(
                    "[DreamSync] Dream pad load failed — aborting remote entry: " + locationName);
                AbortFailedRemoteDreamLoad(playerId, "load_failed");
                yield break;
            }

            if (IsDreamCompleted(playerId, locationName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Aborting remote dream entry — completed during load: {locationName}");
                AbortFailedRemoteDreamLoad(playerId, "already_completed");
                yield break;
            }

            var player = Player.Instance;
            if (player == null) yield break;

            // Preserve overworld restore pose BEFORE any pad teleport. Vanilla
            // startDreaming() calls saveCurrentPlayerState() first. If we already
            // teleported to the pad, that overwrites positionCopy with abyss coords
            // and endDreaming leaves the client stuck at −75k.
            Vector3 overworldPosCopy = Vector3.zero;
            bool haveOverworldPosCopy = false;
            if (Dreams.Instance != null)
            {
                Vector3 copy = Dreams.Instance.positionCopy;
                if (copy.sqrMagnitude > 0.01f
                    && (Mathf.Abs(copy.x) < 40000f && Mathf.Abs(copy.z) < 40000f))
                {
                    overworldPosCopy = copy;
                    haveOverworldPosCopy = true;
                }
            }
            if (!haveOverworldPosCopy
                && _preDreamPosition.TryGetValue(playerId, out var prePos)
                && prePos.sqrMagnitude > 0.01f
                && Mathf.Abs(prePos.x) < 40000f && Mathf.Abs(prePos.z) < 40000f)
            {
                overworldPosCopy = prePos;
                haveOverworldPosCopy = true;
            }

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
                // Dream pads are small pockets; enter every node so props behind the
                // dialogue door are not left Cullable-hidden if any registered late.
                try { Singleton<WorldGrid>.Instance.enterAllNodes(); }
                catch { /* ignore */ }
                Singleton<WorldGrid>.Instance.refreshPosition(player._transform.position, instant: true, force: true);
            }
            FinishDreamOutsideLoadFlags();

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
                    // Resources path skips getPreset random removal; keep the one-shot pool aligned.
                    DreamSession.MirrorPoolRemove(locationName);
                    Dreams.Instance.dreamLocation = component;
                    ApplyEpilogueModeIfNeeded(component, locationName);
                    Dreams.Instance.startDreaming();
                    // Vanilla startDreaming() re-saves timeCopy from CurrentTime. By now
                    // TimeSync/dream may have already bumped the clock to dream time. Restore
                    // the freeze-time snapshot so exit doesn't leave the client stuck at 900.
                    if (_worldFrozen && _savedGameTime > 0)
                        Dreams.Instance.timeCopy = _savedGameTime;
                    // Mirror timeCopy fix: restore overworld positionCopy after startDreaming
                    // overwrote it with pad feet (teleport-before-startDreaming remote path).
                    if (haveOverworldPosCopy)
                        Dreams.Instance.positionCopy = overworldPosCopy;
                    _localDreamActive = true;
                    _localDreamPreset = locationName;
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

            // Vanilla: onLocationSpawned → startDreaming → enter → OnActivated → checkEnterEvents.
            // We entered earlier for render; wait for activateOverTime, then apply queued
            // onEnterLocation_* so door_underground gets welcome_opening_dream.
            float waitLoad = 0f;
            while (component != null && !component.finishedLoading && waitLoad < 15f)
            {
                waitLoad += Time.unscaledDeltaTime;
                yield return null;
            }
            try
            {
                var netFlush = ModRuntime.Network as LanNetworkManager;
                netFlush?.GameEventHandlers?.TryFlushPendingGameEventsAfterDreamLoad();
            }
            catch { /* non-fatal */ }

            // Snap host/peer proxies from PlayerPositionManager (true network pos), not
            // local player feet. The old code stacked everyone on the client spawn and then
            // LocationEnter overwrote with a bad playerSpawn Y.
            var network = ModRuntime.Network as LanNetworkManager;
            if (network != null && network.IsConnected)
            {
                network.ResyncDreamProxiesAfterLocalLoad(locationName);

                // Send confirmation back to the dream initiator so they unfreeze our proxy.
                // From this point forward, position updates will come from the dream scene.
                network.Send(NetMessageType.DreamEntered,
                    w => new DreamEnteredMessage().Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            // Host startDreaming fades black via vanilla; remote path left blackScreen solid.
            FadeInDreamBlackScreen();

            // Dream ambient/time: force after startDreaming + any late TimeSync (bunker washout).
            try
            {
                if (Dreams.Instance != null && Dreams.Instance.dreaming && Dreams.Instance.preset != null
                    && Singleton<Controller>.Instance != null)
                {
                    Singleton<Controller>.Instance.CurrentTime = (int)Dreams.Instance.preset.time;
                    Singleton<Controller>.Instance.updateAmbientLight();
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] post-load ambient: " + ex.Message);
            }

            // Flush world lights that arrived while the pad was unloading.
            try { WorldPhysicsSyncService.TryFlushPendingLights(); }
            catch { /* non-fatal */ }

            ModRuntime.LegacyInfo($"[DreamSync] Player positioned at dream location: {locationName}");
        }

        /// <summary>
        /// Match vanilla <c>Dreams.startDreaming</c>: wait 1 frame, fade only
        /// <c>blackScreenTop</c> over 0.5s when still opaque. Also clear base blackScreen
        /// if remote path left it solid (host rarely uses it for dream entry).
        /// </summary>
        private static void FadeInDreamBlackScreen()
        {
            try
            {
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                {
                    ctrl.waitFramesAndRun(DoFadeInDreamBlackScreen, 1);
                    return;
                }
                DoFadeInDreamBlackScreen();
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] FadeInDreamBlackScreen: " + ex.Message);
            }
        }

        private static void DoFadeInDreamBlackScreen()
        {
            try
            {
                Core.EnteringDream = false;
                var ui = Singleton<UI>.Instance;
                if (ui == null) return;

                // Vanilla: only blackScreenTop, 0.5s, if alpha != 0.
                if (ui.blackScreenTop != null)
                {
                    var topSprite = ui.blackScreenTop.GetComponent<tk2dBaseSprite>();
                    if (topSprite == null || topSprite.color.a != 0f)
                        ui.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 0.5f);
                }

                // Remote entry may also leave base blackScreen full; clear it at the same pace.
                try
                {
                    var baseSprite = ui.blackScreen != null
                        ? ui.blackScreen.GetComponent<tk2dBaseSprite>()
                        : null;
                    if (baseSprite != null && baseSprite.color.a > 0.01f)
                        ui.tweenBlackScreen(new Color(0f, 0f, 0f, 0f), 0.5f);
                }
                catch
                {
                    ui.tweenBlackScreen(new Color(0f, 0f, 0f, 0f), 0.5f);
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] DoFadeInDreamBlackScreen: " + ex.Message);
            }
        }

        private static IEnumerator StartLoadDreamScene(string locationName, Vector3 position, Action<Location> onComplete)
        {
            yield return null;

                // In epilog_part1a_dream, destroy the road before pocket load.
            if (string.Equals(locationName, "epilog_part1a_dream", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var outside = Singleton<OutsideLocations>.Instance;
                    if (outside != null
                        && outside.spawnedLocations != null
                        && outside.spawnedLocations.ContainsKey("outside_roadToHome_01"))
                    {
                        outside.destroyLocation("outside_roadToHome_01");
                        ModRuntime.LegacyInfo(
                            "[DreamSync] epilog_part1a — destroyed outside_roadToHome_01");
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning(
                        "[DreamSync] epilog_part1a road destroy: " + ex.Message);
                }

                try
                {
                    // Mirror vanilla prepareDream forceSaveStatic for epilog 1a.
                    Singleton<SaveManager>.Instance?.Save(
                        doJson: true, doSaveProfile: true, force: true,
                        forceSaveStatic: true, showSavingIndicator: false);
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning(
                        "[DreamSync] epilog_part1a forceSaveStatic: " + ex.Message);
                }
            }

            // Mirror OutsideLocations.prepareLocation / prepareDream:
            // - loading=true → CullableObject.Awake skips registerMe (otherwise objects
            //   register onto World grid at -75k, where WorldGrid.refresh hides far nodes;
            //   client missing props "behind the door" while host looks complete).
            // - dreamPrepared=true → Location.activateOverTime uses loadFrames=1.
            var outsideLoc = Singleton<OutsideLocations>.Instance;
            bool prevDreamPrepared = false;
            if (outsideLoc != null)
            {
                outsideLoc.loading = true;
            }
            if (Dreams.Instance != null)
            {
                prevDreamPrepared = Dreams.Instance.dreamPrepared;
                Dreams.Instance.dreamPrepared = true;
            }

            Location component = null;
            try
            {
                // Must unload textures before spawning the new location; vanilla
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

                component = marker.thisLocation.GetComponent<Location>();

                if (parentTransform != null)
                    marker.thisLocation.transform.parent = parentTransform;

                Singleton<OutsideLocations>.Instance.spawnedLocations[locationName] = component;
                Dreams.Instance.dreamLocation = component;
                RemapDreamUniqueObjects(component.transform);

                // Activate all child objects; vanilla transportToLocation calls
                // spawnedLocations[locationName].enter() which does activateChildren(true).
                // Without this, terrain renderers stay inactive → all-black scene.
                // Do not flush onEnterLocation here; enter() only starts activateOverTime.
                // GE children need player teleported + startDreaming + finishedLoading first
                // (otherwise door_underground keeps welcome_opening, not welcome_opening_dream).
                component.enter();

                // Vanilla Dreams.onLocationSpawned sets inEpilogue for epilogue locations.
                // Remote load path never hits that, so clients would miss crawl, death, and UI mode.
                ApplyEpilogueModeIfNeeded(component, locationName);

                if (holder != markerObj)
                    UnityEngine.Object.Destroy(markerObj);

                ModRuntime.LegacyInfo($"[DreamSync] Dream scene loaded: {locationName} at {position}");
                onComplete?.Invoke(component);
            }
            finally
            {
                // Spawn finished (or failed). CullableObject.Awake already skipped while
                // loading was true; clear it so WorldGrid.refreshPlayerPos can run again.
                // Do this here so abort paths after a successful spawn cannot leave loading stuck.
                if (outsideLoc != null)
                    outsideLoc.loading = false;
                if (component == null && Dreams.Instance != null)
                    Dreams.Instance.dreamPrepared = prevDreamPrepared;
            }
        }
    }
}
