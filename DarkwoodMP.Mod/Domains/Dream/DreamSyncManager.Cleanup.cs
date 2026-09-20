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

                // Personal rewards first (items and journal); defer fireGameEvent and world
                // changes until unfreeze.
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

                // Prefer freeze snapshot over timeCopy when remote startDreaming overwrote it.
                int restoreTime = _worldFrozen && _savedGameTime > 0
                    ? _savedGameTime
                    : (int)dreams.timeCopy;
                dreams.timeCopy = restoreTime;
                Singleton<Controller>.Instance.CurrentTime = restoreTime;
                UnfreezeWorld(restoreTime: false);

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

            var spec = SpectatorModeController.Instance;
            if (spec != null && spec.IsSpectating)
                spec.ExitWithoutPositionRestore();

            // World events after forest is live again.
            if (!string.IsNullOrEmpty(pendingOutcome) && dreams.preset != null)
            {
                LanNetworkManager.IsApplyingRemoteState = true;
                try { ApplyOutcomeEffects(dreams, player, pendingOutcome, worldEvents: true); }
                finally { LanNetworkManager.IsApplyingRemoteState = false; }
            }
        }

        /// <param name="worldEvents">
        /// false means personal rewards only; true means fireGameEvent or fireWorldEvent only.
        /// </param>
        private static void ApplyOutcomeEffects(Dreams dreams, Player player, string outcomeName, bool worldEvents = true)
        {
            if (dreams.preset == null || dreams.preset.outcomes == null) return;

            // Hard cleanup can still carry a story outcome name; dead peers restore inventory
            // only (createInvItem / journal grants skipped).
            if (!worldEvents && FinalDreamsceneManager.IsLocalDead)
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
