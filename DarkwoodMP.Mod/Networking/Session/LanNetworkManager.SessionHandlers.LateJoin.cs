using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {

        /// <summary>
        /// Host: the peer is playing on this link — a phase-3 reconnect, or it has sent an in-world
        /// PlayerState (clients send none on the title or mid-load). It ignores a world package.
        /// </summary>
        internal bool IsPeerInWorld(int playerId)
        {
            return playerId > 1
                && (_session.Link.CoopReconnect.Contains(playerId) || _session.Link.LastPlayerStateSequence.ContainsKey(playerId));
        }

        /// <summary>
        /// Host, broadcast resend / new-world share: mute only the peers that will load the package
        /// (not in-world) and queue their late-join bulk, so their first in-world PlayerState
        /// unmutes them and the bulk follows. Peers already playing keep their gameplay traffic.
        /// </summary>
        internal void MarkTitlePeersLoadingForWorldShare()
        {
            if (_role != NetworkRole.Host)
                return;
            int marked = 0;
            foreach (int id in EnumeratePeerIds())
            {
                if (id <= 1 || IsPeerInWorld(id))
                    continue;
                MarkPeerLoadingWorld(id);
                if (_session.Link.Handshaked.Contains(id))
                {
                    if (!_session.Link.AwaitingLateJoinBulk.ContainsKey(id))
                        _session.Link.AwaitingLateJoinBulk[id] = 0f;
                    ReserveIdForJoinPipeline(id);
                }
                marked++;
            }
            ModLog.Event(LogCat.Session,
                "World share: " + marked + " title peer(s) muted until in-world (late-join bulk queued)");
        }

        /// <summary>
        /// Late-join sticky world dump (host-auth only). Light packets this frame;
        /// heavy FindObjects scans are queued one phase/frame via <see cref="TickHeavyLateJoinBulk"/>.
        /// Scenario latch uses ScenarioStateBulk (138) flags only — no fire replay.
        /// Fired one-shot GameEvents go in heavy phase (FindObjects scan).
        /// </summary>
        internal void SendLateJoinGameplayBulk(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 0)
                return;

            _session.Link.AwaitingLateJoinBulk.Remove(playerId);
            _session.Link.CoopReconnect.Remove(playerId);
            ModLog.Event(LogCat.Session,
                "Sending late-join bulk → player " + playerId
                + " (light now; heavy sticky world staggered)");

            // Prologue catch-up (mid/post intro). Soft-reconnect also calls this at
            // handshake; ApplyEnd no-ops when the peer is already past intro.
            // Each step is isolated: one failing sender used to abort every step after it (and the
            // heavy phases below were never queued), leaving the joiner with half a world.
            LateJoinStep(playerId, "prologue", () => PrologueSync.SendCatchUpTo(playerId));
            LateJoinStep(playerId, "nightDeathSnapshot",
                () => DeathStateTracker.HostSendNightDeathSnapshotTo(this, playerId));

            // Light / already-capped / no full-scene bag thrash.
            LateJoinStep(playerId, "journal", () => JournalHandlers.SendJournalBulkSyncTo(playerId));
            LateJoinStep(playerId, "flags", () => FlagHandlers.SendFlagBulkSyncTo(playerId));
            LateJoinStep(playerId, "dialogTrees", () => DWMPHorde.Sync.DialogTreeSync.SendBulkTo(this, playerId));
            LateJoinStep(playerId, "reputation", () => BulkSyncHandlers.SendReputationBulkSyncTo(playerId));
            LateJoinStep(playerId, "hideoutState", () => BulkSyncHandlers.SendHideoutStateSyncTo(playerId));
            LateJoinStep(playerId, "workbenchLevel", () => BulkSyncHandlers.SendWorkbenchLevelSyncTo(playerId));
            LateJoinStep(playerId, "mapState", () => BulkSyncHandlers.SendMapStateSyncTo(playerId));
            LateJoinStep(playerId, "timeSync", () => SendTimeSyncTo(playerId));
            LateJoinStep(playerId, "sawStates", () => StationHandlers.SendSawStatesTo(playerId));
            LateJoinStep(playerId, "feederStates", () => StationHandlers.SendFeederStatesTo(playerId));
            LateJoinStep(playerId, "lureStates", () => StationHandlers.SendLureStatesTo(playerId));
            LateJoinStep(playerId, "chainStates", () => ChainHandlers.SendChainStatesTo(playerId));
            LateJoinStep(playerId, "shadowArmor", () => ShadowArmorHandlers.SendShadowArmorStatesTo(playerId));
            LateJoinStep(playerId, "worldBurn", () => WorldBurnHandlers.SendWorldBurnStatesTo(playerId));
            LateJoinStep(playerId, "dreamSession", () => DreamHandlers.SendDreamSessionBulkTo(playerId));
            LateJoinStep(playerId, "clientBackup", () => SendStoredClientBackupTo(playerId));
            LateJoinStep(playerId, "hostLight", () => SyncCurrentLightState());
            LateJoinStep(playerId, "hostAnimLibrary", () => SyncCurrentAnimLibrary());
            // Other clients' light state / anim library (the host's own went out just above).
            LateJoinStep(playerId, "clientLightAnim", () => ReplayStickyPlayerStateTo(playerId));
            LateJoinStep(playerId, "worldLights", () => WorldLateJoinHandlers.SyncExistingWorldLightsTo(playerId));
            LateJoinStep(playerId, "generators", () => WorldLateJoinHandlers.SyncExistingGeneratorsTo(playerId));
            LateJoinStep(playerId, "traps", () => SendTrapBulkTo(playerId));
            LateJoinStep(playerId, "thrownLights", () => Sync.WorldPhysicsSyncService.SendActiveThrownLightsTo(this, playerId));
            // Registry-cheap (no FindObjectsOfType scene thrash).
            LateJoinStep(playerId, "locations", () => LocationEnterExitHandlers.SyncExistingLocationsTo(playerId));
            LateJoinStep(playerId, "shadows", () => SendShadowsTo(playerId));
            LateJoinStep(playerId, "droppedItems", () => WorldObjectSendHandlers.SyncExistingDroppedItems(playerId));
            // Night scenario name + fired latch flags (no CustomEvent/RandomEvent.fire).
            LateJoinStep(playerId, "scenario", () => BulkSyncHandlers.SendScenarioBulkSyncTo(playerId));
            // Fired GameEvents: heavy phase 11 (conservative fired && !multipleFire).
            // Proxy from live PlayerState once CanSpawnRemoteProxies.

            // Heavy sticky world: weather/trade/construct/locks/barricades/gas/deathbags/GE.
            _session.Link.PendingHeavyLateJoinBulk[playerId] = 0;
            _session.Link.HeavyPhaseFailures.Remove(playerId);
        }

        /// <summary>Run one light late-join step; a throw is logged by name and does not stop the rest.</summary>
        private void LateJoinStep(int playerId, string name, Action step)
        {
            try { step(); }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Session,
                    "[BulkSync] late-join step '" + name + "' failed for p" + playerId
                    + " — joiner may be missing this state", ex);
            }
        }

        private const int HeavyPhaseMaxAttempts = 3;

        private static readonly string[] HeavyPhaseNames =
        {
            "weather", "tradeInventories", "constructedSites", "padlocks", "lockeds", "interactives",
            "barricadeDoors", "barricadeWindows", "barricadeItems", "gas+infection", "deathBags", "gameEvents"
        };

        private static string HeavyPhaseName(int phase)
            => phase >= 0 && phase < HeavyPhaseNames.Length ? HeavyPhaseNames[phase] : ("phase" + phase);

        /// <summary>
        /// Host: one heavy late-join phase per frame total (not one per peer).
        /// </summary>
        private void TickHeavyLateJoinBulk()
        {
            if (_role != NetworkRole.Host || _session.Link.PendingHeavyLateJoinBulk.Count == 0)
                return;

            // Copy keys because the dictionary changes as peers finish.
            var peers = new List<int>(_session.Link.PendingHeavyLateJoinBulk.Keys);
            for (int p = 0; p < peers.Count; p++)
            {
                int playerId = peers[p];
                if (!HasPeer(playerId))
                {
                    _session.Link.PendingHeavyLateJoinBulk.Remove(playerId);
                    _session.Link.HeavyPhaseFailures.Remove(playerId);
                    continue;
                }
                if (!_session.Link.PendingHeavyLateJoinBulk.TryGetValue(playerId, out int phase))
                    continue;

                try
                {
                    switch (phase)
                    {
                        case 0:
                            SendWeatherSyncTo(playerId);
                            break;
                        case 1:
                            TradeHandlers.SendTradeInventoriesTo(playerId);
                            break;
                        case 2:
                            LockHandlers.SendConstructedSitesTo(playerId);
                            break;
                        // Locks/interactives: one FindObjectsOfType call per frame.
                        case 3:
                            LockHandlers.SyncExistingPadlocksTo(playerId);
                            break;
                        case 4:
                            LockHandlers.SyncExistingLockedsTo(playerId);
                            break;
                        case 5:
                            LockHandlers.SyncExistingInteractivesTo(playerId);
                            break;
                        // Barricades: split Door, Window, and Item scans.
                        case 6:
                            BarricadeHandlers.SendBarricadeDoorsTo(playerId);
                            break;
                        case 7:
                            BarricadeHandlers.SendBarricadeWindowsTo(playerId);
                            break;
                        case 8:
                            BarricadeHandlers.SendBarricadeItemsTo(playerId);
                            break;
                        case 9:
                            SendGasStateTo(playerId);
                            SendInfectionStatesTo(playerId);
                            break;
                        case 10:
                            CombatDeathBagHandlers.SyncExistingDeathBags(playerId);
                            break;
                        case 11:
                            // FindObjectsOfType GameEvents — conservative fired one-shots only.
                            GameEventHandlers.SendGameEventsBulkTo(playerId);
                            break;
                        default:
                            _session.Link.PendingHeavyLateJoinBulk.Remove(playerId);
                            ModLog.Event(LogCat.Session,
                                "Late-join heavy bulk complete → player " + playerId);
                            return;
                    }
                }
                catch (System.Exception ex)
                {
                    // Retry the same phase on the next frames; only give up (loudly, naming the phase)
                    // after a bounded number of attempts. Skipping on the first throw silently left the
                    // joiner without e.g. every lock / barricade state.
                    _session.Link.HeavyPhaseFailures.TryGetValue(playerId, out int failures);
                    failures++;
                    if (failures < HeavyPhaseMaxAttempts)
                    {
                        _session.Link.HeavyPhaseFailures[playerId] = failures;
                        ModLog.Warn(LogCat.Session,
                            "[BulkSync] heavy phase " + phase + " (" + HeavyPhaseName(phase) + ") p" + playerId
                            + " failed (attempt " + failures + "/" + HeavyPhaseMaxAttempts + "), retrying: " + ex.Message);
                        return;
                    }
                    _session.Link.HeavyPhaseFailures.Remove(playerId);
                    ModLog.Error(LogCat.Session,
                        "[BulkSync] heavy phase " + phase + " (" + HeavyPhaseName(phase) + ") p" + playerId
                        + " failed " + HeavyPhaseMaxAttempts + " times — SKIPPING; the joiner is missing this state", ex);
                }

                _session.Link.HeavyPhaseFailures.Remove(playerId);
                phase++;
                if (phase >= HeavyLateJoinPhaseCount)
                {
                    _session.Link.PendingHeavyLateJoinBulk.Remove(playerId);
                    ModLog.Event(LogCat.Session,
                        "Late-join heavy bulk complete → player " + playerId);
                }
                else
                {
                    _session.Link.PendingHeavyLateJoinBulk[playerId] = phase;
                }

                // Process one peer and phase per frame to avoid stacked scene scans.
                return;
            }
        }

        /// <summary>
        /// Host: bulk only after joiner has been sending PlayerState for ClientBulkSettleSeconds.
        /// </summary>
        internal void TryFlushLateJoinBulkForPeer(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 0)
                return;
            if (!_session.Link.AwaitingLateJoinBulk.TryGetValue(playerId, out float firstSeen))
            {
                // In-world PlayerState from a muted peer with no bulk pending: it ignored a world
                // package (already playing). Nothing else would ever unmute it.
                if (_session.Link.LoadingWorld.Contains(playerId)
                    && (_worldSaveShare == null || !_worldSaveShare.IsHostShareRunning)
                    && !Patches.ChapterTransitionHelpers.IsChapterTransitionActive)
                    MarkPeerGameplayReady(playerId);
                return;
            }

            float now = Time.realtimeSinceStartup;
            float settle = _session.Link.CoopReconnect.Contains(playerId)
                ? CoopReconnectBulkSettleSeconds
                : ClientBulkSettleSeconds;

            if (firstSeen <= 0f)
            {
                _session.Link.AwaitingLateJoinBulk[playerId] = now;
                // The joiner is past LoadScene, so high-rate gameplay packets can resume.
                MarkPeerGameplayReady(playerId);
                ModLog.Event(LogCat.Session,
                    "Player " + playerId + " in-world — bulk in "
                    + settle.ToString("F1") + "s (settle"
                    + (_session.Link.CoopReconnect.Contains(playerId) ? ", phase3 reconnect" : "")
                    + ")");
                return;
            }

            if (now - firstSeen < settle)
                return;

            SendLateJoinGameplayBulk(playerId);
        }

        /// <summary>
        /// True when host is fully in-chapter and safe to start world share / download.
        /// Mid-load (<see cref="Core.loadingGame"/>), title-only, and profile-select are NOT ready.
        /// Requires a live Player past load (<see cref="Core.loadedGame"/> /
        /// <see cref="Core.coreStarted"/>). Sticky <c>Core.mainMenu</c> with a loaded
        /// player still counts (dual-box quirk that used to block share forever).
        /// Does not treat mainMenu-cleared-but-not-yet-loaded as ready (mid-transition).
        /// </summary>
        private static bool HostHasShareableWorld()
        {
            try
            {
                if (Core.loadingGame)
                    return false;

                Player p = Player.Instance;
                if (p == null || p.gameObject == null || !p.gameObject.activeInHierarchy)
                    return false;

                // Fully past load only — no !mainMenu shortcut (avoids mid-transition true).
                // Sticky mainMenu with loaded/coreStarted still wins.
                return Core.loadedGame || Core.coreStarted;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Public mirror for share service / UI (same gate as auto-share).</summary>
        internal static bool HostIsFullyInWorld() => HostHasShareableWorld();

        /// <summary>
        /// Host recovery: when host becomes fully in-world, emit HostWorldReady and push
        /// save share to title clients already waiting (Player.Start may have raced mid-load,
        /// or mainMenu was sticky). Rising-edge only.
        /// </summary>
        private void TickHostWorldShareWhenReady()
        {
            if (_role != NetworkRole.Host || !IsConnected || !_session.Link.HandshakeComplete)
            {
                _session.HostWasShareableForWaitingClients = false;
                _session.HostWorldReadyEmitted = false;
                return;
            }

            bool shareable = HostHasShareableWorld();
            if (!shareable)
            {
                _session.HostWasShareableForWaitingClients = false;
                if (_session.HostWorldReadyEmitted)
                {
                    _session.HostWorldReadyEmitted = false;
                    BroadcastHostWorldReady(ready: false);
                    ModLog.Event(LogCat.Session,
                        "Host left fully-in-world state — HostWorldReady Ready=false broadcast");
                }
                return;
            }

            // Always announce ready (even with zero waiters) so mid-session joiners
            // that handshaked during load get the signal on the next tick if missed.
            EmitHostWorldReadyIfNeeded();

            // Share only on the transition to the ready state.
            if (_session.HostWasShareableForWaitingClients)
                return;

            // A share already running would coalesce this one away: keep the edge armed and retry
            // once it is done instead of losing the waiting peers.
            if (_worldSaveShare != null && _worldSaveShare.IsBusy)
                return;
            _session.HostWasShareableForWaitingClients = true;

            int waiting = 0;
            foreach (int id in _session.Link.Handshaked)
            {
                // Peers already playing (phase-3 reconnect, or sending in-world PlayerState) have the world.
                if (id > 1 && !IsPeerInWorld(id))
                    waiting++;
            }
            if (waiting == 0)
                return;

            // The only automatic "host became ready" share: the broadcast mutes the waiting title
            // peers and queues their late-join bulk (MarkTitlePeersLoadingForWorldShare).
            ModLog.Event(LogCat.Save,
                "Host fully in-world with " + waiting
                + " peer(s) waiting — auto world share (host-ready gate)");
            _worldSaveShare?.ScheduleHostResend();
        }

        /// <summary>Host→all peers: world is fully loaded. Idempotent until host leaves world.</summary>
        private void EmitHostWorldReadyIfNeeded()
        {
            if (_role != NetworkRole.Host || !HostHasShareableWorld())
                return;
            if (_session.HostWorldReadyEmitted)
                return;
            _session.HostWorldReadyEmitted = true;
            BroadcastHostWorldReady(ready: true);
        }

        /// <summary>Host→one peer (late handshake while already in-world).</summary>
        private void SendHostWorldReadyTo(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 0 || !HostHasShareableWorld())
                return;
            var msg = BuildHostWorldReadyMessage(ready: true);
            SendToPlayer(playerId, NetMessageType.HostWorldReady,
                w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.Session,
                "HostWorldReady → p" + playerId
                + " ready=" + msg.Ready
                + " ch" + msg.ChapterId + " day" + msg.DayIndex);
        }

        private void BroadcastHostWorldReady(bool ready)
        {
            if (_role != NetworkRole.Host)
                return;
            var msg = BuildHostWorldReadyMessage(ready);
            SendToAll(NetMessageType.HostWorldReady,
                w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.Session,
                "HostWorldReady broadcast ready=" + msg.Ready
                + " ch" + msg.ChapterId
                + " day" + msg.DayIndex + " peers=" + _session.Link.Handshaked.Count);
        }

        private static HostWorldReadyMessage BuildHostWorldReadyMessage(bool ready)
        {
            int chapter = 0;
            int day = 0;
            if (ready)
            {
                try
                {
                    chapter = ClientSaveBridge.GetChapterId();
                    day = ClientSaveBridge.GetDayIndex();
                }
                catch { /* ignore */ }
            }
            return new HostWorldReadyMessage
            {
                Ready = ready,
                ChapterId = chapter,
                DayIndex = day
            };
        }

        /// <summary>
        /// Client may only apply journal/flags/world mutations once in a loaded chapter.
        /// Title menu has UI.journal stubs that NRE inside addJournalEntry / Inventory.Start.
        /// </summary>
        internal static bool ClientCanApplyWorldBulk()
        {
            try
            {
                if (Core.mainMenu)
                    return false;
                if (Core.loadingGame)
                    return false;
                if (Player.Instance == null)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
