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
        public static void OnRemoteDreamStarted(int playerId, string presetName, Vector3 locationPosition,
            bool entryVideo = true)
        {
            if (_remoteDreamActive.TryGetValue(playerId, out bool active) && active) return;
            if (IsLocalDeadOutsideDream())
            {
                // Dead in the overworld: stay where the death left us (the host leaves us off the
                // dream roster). Entering would revive us at the dream's end.
                ModRuntime.LegacyInfo($"[DreamSync] Sit out remote dream (p{playerId}) {presetName}: local player is dead");
                return;
            }
            _remoteEntryHasVideo = entryVideo;
            // Host already refused completed presets in TryBegin / HandleDreamStarted.
            CloseOpenUiForDreamEntry();
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
        /// Peer started Dreams.startTransition. Play the same video now, not after DreamStarted.
        /// </summary>
        public static void OnPeerDreamEntryTransition()
        {
            if (_localDreamActive) return;
            // Dead here: this player sits the dream out, so it does not play the movie either
            // (it was left frozen, black and muted with no dream to end it).
            if (IsLocalDeadOutsideDream()) return;
            if (_earlyEntryTransitionPlayed) return;
            // DreamStarted already started the video; do not stack a second Play.
            if (_remoteEntryTransitionPlaying) return;

            _earlyEntryTransitionPlayed = true;
            NoteEntryTransitionStarted();
            CloseOpenUiForDreamEntry();
            FreezeWorld();
            HostBeginDreamEntry();

            float wait = StartRemoteDreamTransition();
            _earlyEntryTransitionDoneAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, wait);
            // So DreamTransition.skip / ActionSkipTransition can cut the wait.
            if (Dreams.Instance?.startTransition != null)
                Dreams.Instance.startTransition.isPlaying = true;

            // Arm safety watchdog: if nothing resolves the transition within 20s of
            // the expected completion, force-clear the stuck overlay + EnteringDream.
            Singleton<Controller>.Instance.StartCoroutine(
                EntryTransitionWatchdog(_earlyEntryTransitionDoneAt + 20f, ++_entryWatchGen));

            ModRuntime.LegacyInfo($"[DreamSync] Early entry transition (peer), wait={wait:F1}s");
        }

        /// <summary>
        /// Pull-in closes whatever the player has open the way a vanilla dream start does
        /// (trade, container/workbench, journal, then the dialogue itself). Nothing closed them
        /// on a pulled-in peer, so the NPC dialogue lock stayed held until its lease expired
        /// and the window sat over the pad.
        /// </summary>
        internal static void CloseOpenUiForDreamEntry()
        {
            try
            {
                var player = Player.Instance;
                var ui = Singleton<UI>.Instance;
                if (player == null || ui == null) return;

                if (ui.journal != null && ui.journal.opened)
                    ui.journal.close(doUnpause: true);

                var dw = ui.dialogueWindow;
                if (dw != null && dw.npc != null && player.inShop && !dw.tweening)
                    dw.closeTrade();

                if (player.openedItemInventory != null || player.openedItemInventory2 != null)
                    player.closeInventory();

                if (dw != null && dw.npc != null && !dw.tweening)
                {
                    dw.close();
                    // An exit dialogue is shown instead of closing on the first call.
                    if (dw.npc != null && !dw.tweening)
                        dw.close();
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] CloseOpenUiForDreamEntry: " + ex.Message);
            }
        }

        /// <summary>Skip / cancel early entry wait so DreamStarted load is not blocked.</summary>
        /// <summary>The dream video on screen is a peer's entry replayed here, not a local vanilla transition.</summary>
        internal static bool IsPeerEntryTransitionPlaying => _earlyEntryTransitionPlayed || _remoteEntryTransitionPlaying;

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
            int gen = _entryGeneration;
            int sid = DreamSession.SessionId;
            string presetName = _currentDreamPreset.TryGetValue(playerId, out var p) ? p : null;

            // Keep the snapshot aligned with host prepareDream.
            if (Dreams.Instance != null && !Dreams.Instance.dreaming && !Dreams.Instance.switchingDream)
            {
                try { Dreams.Instance.saveCurrentPlayerState(); }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DreamSync] saveCurrentPlayerState: " + ex.Message);
                }
            }

            // Host prepareDream shows Saving; SaveSync is suppressed for the whole dream
            // window (avoids hitch mid-video). Peer path never ran prepareDream; mirror a
            // local Save+indicator so clients see the same cue. Fan-out stays suppressed.
            try
            {
                Singleton<SaveManager>.Instance?.Save(
                    doJson: true, doSaveProfile: true, force: true,
                    forceSaveStatic: false, showSavingIndicator: true);
                ModRuntime.LegacyInfo("[DreamSync] peer dream-entry local Save (Saving UI)");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] peer dream-entry Save: " + ex.Message);
            }

            // 1. Entry video: already started on CutsceneSync DreamEntry, or play now (late/missed).
            if (_earlyEntryTransitionPlayed || _remoteEntryTransitionPlaying)
            {
                float remain = _earlyEntryTransitionDoneAt - Time.realtimeSinceStartup;
                if (remain > 0.05f)
                {
                    ModRuntime.LegacyInfo($"[DreamSync] Waiting remaining entry transition {remain:F1}s");
                    // Realtime: EnteringDream / pause can zero timescale and stall WaitForSeconds,
                    // which prolonged the black screen and made the stinger feel doubled.
                    yield return new WaitForSecondsRealtime(remain);
                }
            }
            else
            {
                // Same entry as the host: its video, or the black fade of a dialogue / event start.
                float waitTime;
                if (_remoteEntryHasVideo)
                    waitTime = StartRemoteDreamTransition();
                else
                {
                    ShowDreamTransitionFallback();
                    waitTime = 0f;
                }
                if (waitTime > 0f)
                {
                    ModRuntime.LegacyInfo($"[DreamSync] Waiting {waitTime:F1}s for remote dream transition");
                    yield return new WaitForSecondsRealtime(waitTime);
                }
            }

            // Disconnect / reject / cleanup during the video: that teardown already unfroze and
            // cleared the overlay; loading the pad now would strand us in a sessionless dream.
            if (EntryStale(gen, sid))
            {
                ModRuntime.LegacyInfo("[DreamSync] Remote entry cancelled during transition — not loading pad");
                yield break;
            }

            // 2. Clean up the video overlay (fade out)
            FadeOutDreamTransition();
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
            _remoteEntryTransitionPlaying = false;
            _remoteEntryAudioId = null;

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
        /// <summary>Client: the pocket the host's DreamChainStart is loading (see ClientDreamSwitchPatch).</summary>
        private static string _chainPocketLoading; // reset-in: OnDisconnectedCleanup

        internal static string ChainPocketLoading => _chainPocketLoading;

        internal static void ClearChainPocketLoading() => _chainPocketLoading = null;

        public static void OnDreamChain(string nextPreset)
        {
            if (string.IsNullOrEmpty(nextPreset)) return;
            _chainPocketLoading = nextPreset;
            // Pocket 1's host-ordered exit is done; leaving the flag would let the initiateEndDreaming
            // authority patch end pocket 2 locally.
            _hostOrderedDreamEnd = false;
            _localDreamPreset = nextPreset;
            _localDreamActive = true;
            if (Player.Instance != null)
            {
                int pid = 0;
                var net = ModRuntime.Network;
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
            int gen = _entryGeneration;
            int sid = DreamSession.SessionId;
            // Keep world frozen; tear previous dream location if still present.
            if (Dreams.Instance != null && Dreams.Instance.dreaming)
            {
                bool prevApply1 = LanNetworkManager.GetExplicitApplyingRemoteState();
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
                finally { LanNetworkManager.SetExplicitApplyingRemoteState(prevApply1); }
            }

                // Preserve inventory and time copies across a dream-chain pocket.
            if (Dreams.Instance != null)
                Dreams.Instance.switchingDream = true;

            yield return LoadDreamSceneCoroutine(presetName, locationPosition, false, 0);
            if (EntryStale(gen, sid))
                yield break;
            DreamSession.MarkActive();
        }

        /// <summary>
        /// Host-only: waits out the remaining early transition time, then fades the
        /// video overlay and clears EnteringDream. Mirrors the peer-path cleanup
        /// that ProcessRemoteDreamCoroutine performs for clients.
        /// </summary>
        private static IEnumerator LocalEntryFadeoutCoroutine(float delay)
        {
            if (delay > 0.05f)
                yield return new WaitForSecondsRealtime(delay);
            FadeOutDreamTransition();
            // Peer path sets base UI.blackScreen opaque; vanilla startDreaming only clears
            // blackScreenTop; without this the host stays black forever.
            FadeInDreamBlackScreen();
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
            _remoteEntryTransitionPlaying = false;
            _remoteEntryAudioId = null;
        }

        /// <summary>
        /// Safety watchdog: if the early entry transition is still active after the
        /// timeout and neither a local nor remote dream session started, force-clear
        /// the stuck state so the player is not permanently blinded + paralysed.
        /// </summary>
        private static bool _hostEntryFreeze; // reset-in: OnDisconnectedCleanup

        /// <summary>
        /// Host: its own entry movie or prepare is under way, before the session begins. A join let
        /// in here loaded into a world about to freeze and missed the DreamStarted roster.
        /// </summary>
        internal static bool IsHostDreamEntryPending => _hostEntryFreeze;
        private static int _hostEntryWatch;   // process-scoped: generation of the host entry poll, bumped per start

        /// <summary>
        /// Host: a dream is on its way in (the entry movie started, here or on a peer, or a dialogue
        /// or event prepares one). The host's world stopped only once the pad was up, so for the
        /// whole movie, the prepare wait, the save and the pad spawn its creatures kept attacking
        /// players who sat locked in the movie; a player killed there entered the dream dead. The
        /// clock is also taken here, before the dream sets its own time.
        /// </summary>
        internal static void HostBeginDreamEntry()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            if (_localDreamActive || (Dreams.Instance != null && Dreams.Instance.dreaming))
                return;
            if (!_worldFrozen)
                FreezeWorld();
            if (_hostEntryFreeze)
                return;
            _hostEntryFreeze = true;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null)
                ctrl.StartCoroutine(HostEntryFreezeWatch(++_hostEntryWatch));
        }

        private static bool HostEntryInProgress()
        {
            Dreams d = Dreams.Instance;
            return DreamSession.IsActive || _localDreamActive
                || (d != null && (d.dreaming || d.dreamPrepared
                    || (d.startTransition != null && d.startTransition.isPlaying)));
        }

        /// <summary>
        /// The entry ends either in the dream (OnLocalDreamStarted takes the freeze over) or with
        /// nothing in progress any more (a rejected request, a failed prepare): then the world runs again.
        /// </summary>
        private static IEnumerator HostEntryFreezeWatch(int gen)
        {
            int idle = 0;
            while (_hostEntryFreeze && gen == _hostEntryWatch)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (!_hostEntryFreeze || gen != _hostEntryWatch)
                    yield break;
                if (_localDreamActive)
                {
                    _hostEntryFreeze = false;
                    yield break;
                }
                idle = HostEntryInProgress() || _earlyEntryTransitionPlayed ? 0 : idle + 1;
                if (idle >= 2)
                {
                    _hostEntryFreeze = false;
                    ModRuntime.LegacyInfo("[DreamSync] Host dream entry ended without a dream — world released");
                    AbortEntryVisuals();
                    UnfreezeWorld(restoreTime: false);
                    yield break;
                }
            }
        }

        /// <summary>
        /// The host refused a start request. Everyone who played the requester's entry movie and
        /// froze for it (each peer, and the host) lets go now; before, each sat in the black until
        /// its 20 s stuck-movie watchdog. Left alone when a dream of its own is on the way.
        /// </summary>
        internal static void CancelRefusedEntry()
        {
            if (DreamSession.IsActive || _localDreamActive)
                return;
            Dreams d = Dreams.Instance;
            if (d != null && (d.dreaming || d.dreamPrepared))
                return;
            if (_earlyEntryTransitionPlayed)
            {
                _earlyEntryTransitionPlayed = false;
                _earlyEntryTransitionDoneAt = 0f;
                AbortEntryVisuals();
            }
            _hostEntryFreeze = false;
            // The world and its clock ran on through the movie; no dream replaced the time.
            UnfreezeWorld(restoreTime: false);
        }

        /// <summary>A dream start refused at the last step (the party had finished it meanwhile).</summary>
        internal static void AbortBlockedStart()
        {
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
            _hostEntryFreeze = false;
            AbortEntryVisuals();
            UnfreezeWorld(restoreTime: false);
        }

        /// <summary>
        /// An entry movie that leads to no dream here: undo what it did. The fade-out left both
        /// black layers opaque, and the movie had paused the world sounds and faded the game audio
        /// out (vanilla DreamTransition.transition); only a dream's start or wake ever undid them,
        /// so a refused or abandoned entry left the player on a black screen with the sound gone.
        /// </summary>
        internal static void AbortEntryVisuals()
        {
            FadeOutDreamTransition();
            DoFadeInDreamBlackScreen();
            try
            {
                Singleton<Controller>.Instance?.fadeAudio(fadeOut: false, 1f, musicToo: true);
                Singleton<RandomWorldSounds>.Instance?.resumeGlobalSounds();
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] restore audio after entry: " + ex.Message);
            }
            ReleaseDreamInputLocks();
            try { Core.showGameCursor(); } catch { /* UI not ready */ }
        }

        /// <summary>Each peer entry movie arms its own watchdog; an older one must not end a newer movie.</summary>
        private static int _entryWatchGen; // process-scoped: monotonic

        private static IEnumerator EntryTransitionWatchdog(float expireAt, int gen)
        {
            float delay = expireAt - Time.realtimeSinceStartup;
            if (delay > 0f)
                yield return new WaitForSeconds(delay);
            yield return null; // one frame for any pending transitions to settle
            if (gen == _entryWatchGen && _earlyEntryTransitionPlayed && !DreamSession.IsActive && !_localDreamActive)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] Watchdog: early entry transition stuck — force-clearing");
                _earlyEntryTransitionPlayed = false;
                _earlyEntryTransitionDoneAt = 0f;
                try
                {
                    if (Dreams.Instance != null && !Dreams.Instance.dreaming)
                        Dreams.Instance.dreamPrepared = false;
                }
                catch { /* ignore */ }
                AbortEntryVisuals();
                UnfreezeWorld(restoreTime: false);
            }
        }

        /// <summary>
        /// Client sent DreamStartRequest and is holding opaque black. If host never
        /// delivers DreamStarted, clear the void so the player is not stuck blind.
        /// </summary>
        public static void ArmClientEntryWatchdog()
        {
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            ctrl.StartCoroutine(ClientEntryWatchdog());
        }

        private static IEnumerator ClientEntryWatchdog()
        {
            float deadline = Time.realtimeSinceStartup + 25f;
            while (Time.realtimeSinceStartup < deadline)
            {
                // Do not use IsDreamActive; it includes _earlyEntryTransitionPlayed, which
                // made this watchdog exit immediately after peer CutsceneSync and never clear
                // the void when DreamStarted never arrived.
                if (_localDreamActive
                    || (Dreams.Instance != null && Dreams.Instance.dreaming))
                    yield break;
                if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                    break;
                yield return null;
            }
            if (_localDreamActive
                || (Dreams.Instance != null && Dreams.Instance.dreaming))
                yield break;

            ModRuntime.Log?.LogWarning(
                "[DreamSync] Client entry watchdog — no DreamStarted, clearing black void");
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
        }

        private static void FadeOutDreamTransition()
        {
            // Keep EnteringDream until LoadDreamSceneCoroutine fades in. Clearing it here
            // left a frame of overworld between video teardown and scene load.
            _remoteEntryTransitionPlaying = false;
            // Stop the entry stinger so it cannot overlap dream-scene music after a long wait.
            if (!string.IsNullOrEmpty(_remoteEntryAudioId))
            {
                try { AudioController.Stop(_remoteEntryAudioId); } catch { /* ignore */ }
                _remoteEntryAudioId = null;
            }
            if (Dreams.Instance?.startTransition != null)
                Dreams.Instance.startTransition.isPlaying = false;
            if (Singleton<UI>.Instance == null) return;
            try
            {
                var ui = Singleton<UI>.Instance;
                // Snap black opaque first so any video fade cannot expose overworld.
                try
                {
                    if (ui.blackScreen != null)
                    {
                        var baseSprite = ui.blackScreen.GetComponent<tk2dBaseSprite>();
                        if (baseSprite != null)
                            baseSprite.color = new Color(0f, 0f, 0f, 1f);
                    }
                    if (ui.blackScreenTop != null)
                    {
                        var topSprite = ui.blackScreenTop.GetComponent<tk2dBaseSprite>();
                        if (topSprite != null)
                            topSprite.color = new Color(0f, 0f, 0f, 1f);
                    }
                }
                catch { /* ignore */ }
                ui.tweenBlackScreen(new Color(0f, 0f, 0f, 1f), 0f);

                var overlay = ui.videoOverlay;
                if (overlay != null && overlay.gameObject.activeSelf)
                {
                    var renderer = overlay.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        VideoPlayer vp = renderer.GetComponent<VideoPlayer>();
                        if (vp != null && vp.isPlaying)
                            vp.Stop();
                        // Snap off. DOFade(0.5s) exposed overworld when black was not yet solid.
                        if (renderer.material != null)
                            renderer.material.color = new Color(1f, 1f, 1f, 0f);
                        renderer.enabled = false;
                    }
                    overlay.gameObject.SetActive(false);
                    Core.showGameCursor();
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

    }
}
