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
        private static void ApplyRemoteDreamCleanup(string outcomeName = "")
        {
            var dreams = Dreams.Instance;
            var player = Player.Instance;
            if (player == null || dreams == null) return;

            // Order: restore player, destroy dream, unfreeze, then apply world and journal effects.
            string pendingOutcome = outcomeName ?? "";

            bool prevApply1 = LanNetworkManager.GetExplicitApplyingRemoteState();
            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                if (dreams.preset != null)
                    Core.modifyCamEffects(active: false, dreams.preset.gameObject);

                AudioController.StopMusic(1f);
                Core.spawnCharactersAtNight = true;

                // Vanilla endDreaming: drop whatever the dream left in hand or in progress, or a
                // dream item stayed on the cursor and a half-done action ran on in the overworld.
                player.deselectObject(force: true);
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null && !InvItemClass.isNull(ctrl.pickedUpItem))
                    ctrl.pickedUpItem = null;
                player.stopWhatImDoing();

                player.Hotbar.clear();
                player.Inventory.clear();
                if (dreams.inventorySlotsCopy.Count > 0)
                {
                    Inventory.moveSlots(dreams.inventorySlotsCopy, player.Inventory.slots);
                    Inventory.moveSlots(dreams.hotbarSlotsCopy, player.Hotbar.slots);
                    player.Hotbar.hide();
                    player.Hotbar.show();
                }


                // Vanilla endDreaming parity: journal dream entries, rain, unique teleport, time.
                try { Singleton<UI>.Instance?.journal?.clearDreamEntries(); }
                catch (Exception) { /* journal may be null mid-teardown */ }

                Vector3 restorePos = dreams.positionCopy;
                if (dreams.preset != null)
                {
                    // Vanilla endDreaming clears dreaming before UniqueObjects.getObject.
                    // Leaving it true makes the dream getObject patch return the pad twin.
                    dreams.dreaming = false;
                    RestoreStashedOverworldUniqueObjects();

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

                // Personal rewards (items and journal) after the move home, as vanilla endDreaming
                // does: a reward that does not fit in the bag is dropped at the player's feet, which
                // before the move was the dream pad (destroyed seconds later). World changes and
                // fireGameEvent wait until unfreeze.
                if (!string.IsNullOrEmpty(pendingOutcome))
                    ApplyOutcomeEffects(dreams, player, pendingOutcome, worldEvents: false);

                // Prefer freeze snapshot over timeCopy when remote startDreaming overwrote it.
                // The tutorial wakes at its fixed hour (vanilla timeCopy = 5), not at the snapshot.
                bool tutorialWake = dreams.preset != null
                    && Core.getTrueLocationName(dreams.preset.name) == "dream_tutorial_01";
                int restoreTime = !tutorialWake && _worldFrozen && _savedGameTime > 0
                    ? _savedGameTime
                    : (int)dreams.timeCopy;
                // Vanilla sets the outcome's own wake time (customEndTime) after the restore; the
                // personal-effects pass above ran first and its end time was overwritten here.
                global::DreamPreset.Outcome endOc = FindOutcome(dreams, pendingOutcome);
                if (endOc != null && endOc.customEndTime && endOc.effects != null && endOc.effects.Count > 0
                    && !FinalDreamsceneManager.WasLocalDeadThisDream)
                    restoreTime = endOc.endTime;
                dreams.timeCopy = restoreTime;
                Singleton<Controller>.Instance.CurrentTime = restoreTime;
                UnfreezeWorld(restoreTime: false);

                bool endDiving = dreams.preset != null && dreams.preset.endDivingOut;
                dreams.destroyDream();
                ClearOverworldUniqueStash();
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

                // Vanilla passes the outcome's dontLieDown: a bed wake-up unless the outcome skips it.
                DreamPreset.Outcome endOutcome = ResolveOutcome(dreams, pendingOutcome);
                player.endDreaming(endOutcome == null || endOutcome.dontLieDown);

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

                var net = ModRuntime.Network;
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
                LanNetworkManager.SetExplicitApplyingRemoteState(prevApply1);
            }

            // positionCopy can be a pad coordinate on a remote-entry path; the saved overworld pose
            // is the fallback before it is dropped.
            if (_preDreamPosition.Count > 0
                && ClientStateBackup.IsDreamPadCoordinate(player._transform.position))
            {
                foreach (var kvp in _preDreamPosition)
                {
                    RestorePreDreamState(kvp.Key);
                    break;
                }
            }
            ClearPreDreamState();
            ClearRemoteDreamRoster();
            UnfreezeWorld();

            var spec = SpectatorModeController.Instance;
            if (spec != null && spec.IsSpectating)
                spec.ExitWithoutPositionRestore();
            FinalDreamsceneManager.OnLocalWokeUp();
            ReleaseDreamInputLocks();

            // World events after forest is live again.
            if (!string.IsNullOrEmpty(pendingOutcome) && dreams.preset != null)
            {
                bool prevApply2 = LanNetworkManager.GetExplicitApplyingRemoteState();
                LanNetworkManager.IsApplyingRemoteState = true;
                try { ApplyOutcomeEffects(dreams, player, pendingOutcome, worldEvents: true); }
                finally { LanNetworkManager.SetExplicitApplyingRemoteState(prevApply2); }
            }
        }

        /// <summary>Vanilla Dreams.getOutcome: the named outcome, else "default", else the first.</summary>
        private static DreamPreset.Outcome ResolveOutcome(Dreams dreams, string outcomeName)
        {
            if (dreams == null || dreams.preset == null || dreams.preset.outcomes == null)
                return null;
            var outcomes = dreams.preset.outcomes;
            for (int i = 0; i < outcomes.Count; i++)
            {
                if (outcomes[i] != null && outcomes[i].name == outcomeName)
                    return outcomes[i];
            }
            for (int i = 0; i < outcomes.Count; i++)
            {
                if (outcomes[i] != null && outcomes[i].name == "default")
                    return outcomes[i];
            }
            return outcomes.Count > 0 ? outcomes[0] : null;
        }

        /// <summary>
        /// The input locks a dream leaves that only its own exit transition clears
        /// (DreamTransition.onLoaded): a dream death forbids inputs, and doNotUnforbidInputsOnStart
        /// dreams pin them with cantChangeForbidInputs. A hard cleanup (all-dead end on a client,
        /// reject, disconnect, host lost) never plays that transition.
        /// </summary>
        internal static void ReleaseDreamInputLocks()
        {
            Core.cantChangeForbidInputs = false;
            var controller = Singleton<Controller>.Instance;
            Core.forbidInputs = controller != null && controller.playingCutscene;
            Core.EnteringDream = false;
        }

        /// <param name="worldEvents">
        /// false means personal rewards only; true means fireGameEvent or fireWorldEvent only.
        /// </param>
        private static void ApplyOutcomeEffects(Dreams dreams, Player player, string outcomeName, bool worldEvents = true)
        {
            if (dreams.preset == null || dreams.preset.outcomes == null) return;

            // Hard cleanup can still carry a story outcome name; dead peers restore inventory
            // only (createInvItem / journal grants skipped).
            if (!worldEvents && FinalDreamsceneManager.WasLocalDeadThisDream)
            {
                ModRuntime.LegacyInfo(
                    "[DreamDeath] ApplyOutcomeEffects — local dead, personal rewards skipped");
                return;
            }

            DreamPreset.Outcome outcomePreset = null;
            foreach (var oc in dreams.preset.outcomes)
            {
                if (oc != null && oc.name == outcomeName)
                {
                    outcomePreset = oc;
                    break;
                }
            }
            // allDead / reject / disconnect must not grant the preset's default reward.
            if (outcomePreset == null && DreamSession.IsNonRewardOutcome(outcomeName))
            {
                if (outcomeName == "allDead" || outcomeName == "playerDeath")
                {
                    foreach (var oc in dreams.preset.outcomes)
                    {
                        if (oc != null && oc.name == "playerDeath")
                        {
                            outcomePreset = oc;
                            break;
                        }
                    }
                }
                if (outcomePreset == null)
                    return;
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

        }

        private static global::DreamPreset.Outcome FindOutcome(Dreams dreams, string outcomeName)
        {
            if (dreams?.preset?.outcomes == null || string.IsNullOrEmpty(outcomeName))
                return null;
            foreach (var oc in dreams.preset.outcomes)
            {
                if (oc != null && oc.name == outcomeName)
                    return oc;
            }
            return null;
        }

        /// <summary>True when a physics object should sync during an active dream.</summary>
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

        /// <summary>
        /// Drop the saved overworld pose once the dream is over. It is keyed by peer but always
        /// holds the local player's pose at pad entry, and TryGetPreDreamOverworldPosition feeds
        /// ClientStateBackup; stale it points at the last dream start.
        /// </summary>
        public static void ClearPreDreamState()
        {
            _preDreamPosition.Clear();
            _preDreamGridName.Clear();
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

    }
}
