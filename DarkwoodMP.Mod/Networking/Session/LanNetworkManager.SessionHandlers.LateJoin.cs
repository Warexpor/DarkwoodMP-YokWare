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
        /// After a broadcast world resend (host entered chapter), mark peers for bulk.
        /// </summary>
        public void ScheduleLateJoinBulkAfterWorldShare()
        {
            if (_role != NetworkRole.Host)
                return;
            foreach (int id in _handshakedPeers)
            {
                if (id > 1)
                    _awaitingLateJoinBulk[id] = 0f;
            }
            ModLog.Event(LogCat.Session,
                "Marked " + _awaitingLateJoinBulk.Count + " peer(s) for late-join bulk after settle");
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

            _awaitingLateJoinBulk.Remove(playerId);
            _peersCoopReconnect.Remove(playerId);
            ModLog.Event(LogCat.Session,
                "Sending late-join bulk → player " + playerId
                + " (light now; heavy sticky world staggered)");

            // Light / already-capped / no full-scene bag thrash.
            JournalHandlers.SendJournalBulkSyncTo(playerId);
            FlagHandlers.SendFlagBulkSyncTo(playerId);
            DWMPHorde.Sync.DialogTreeSync.SendBulkTo(this, playerId);
            BulkSyncHandlers.SendReputationBulkSyncTo(playerId);
            BulkSyncHandlers.SendHideoutStateSyncTo(playerId);
            BulkSyncHandlers.SendWorkbenchLevelSyncTo(playerId);
            BulkSyncHandlers.SendMapStateSyncTo(playerId);
            SendTimeSyncTo(playerId);
            StationHandlers.SendSawStatesTo(playerId);
            StationHandlers.SendFeederStatesTo(playerId);
            StationHandlers.SendLureStatesTo(playerId);
            ChainHandlers.SendChainStatesTo(playerId);
            ShadowArmorHandlers.SendShadowArmorStatesTo(playerId);
            WorldBurnHandlers.SendWorldBurnStatesTo(playerId);
            DreamHandlers.SendDreamSessionBulkTo(playerId);
            SendStoredClientBackupTo(playerId);
            SyncCurrentLightState();
            WorldLateJoinHandlers.SyncExistingWorldLightsTo(playerId);
            WorldLateJoinHandlers.SyncExistingGeneratorsTo(playerId);
            SendTrapBulkTo(playerId);
            Sync.WorldPhysicsSyncService.SendActiveThrownLightsTo(this, playerId);
            // Registry-cheap (no FindObjectsOfType scene thrash).
            LocationHandlers.SyncExistingLocationsTo(playerId);
            SendShadowsTo(playerId);
            WorldObjectSendHandlers.SyncExistingDroppedItems(playerId);
            // Night scenario name + fired latch flags (no CustomEvent/RandomEvent.fire).
            BulkSyncHandlers.SendScenarioBulkSyncTo(playerId);
            // Fired GameEvents: heavy phase 11 (conservative fired && !multipleFire).
            // Proxy from live PlayerState once CanSpawnRemoteProxies.

            // Heavy sticky world: weather/trade/construct/locks/barricades/gas/deathbags/GE.
            _pendingHeavyLateJoinBulk[playerId] = 0;
        }

        /// <summary>
        /// Host: one heavy late-join phase per frame total (not one per peer).
        /// </summary>
        private void TickHeavyLateJoinBulk()
        {
            if (_role != NetworkRole.Host || _pendingHeavyLateJoinBulk.Count == 0)
                return;

            // Copy keys because the dictionary changes as peers finish.
            var peers = new List<int>(_pendingHeavyLateJoinBulk.Keys);
            for (int p = 0; p < peers.Count; p++)
            {
                int playerId = peers[p];
                if (!HasPeer(playerId))
                {
                    _pendingHeavyLateJoinBulk.Remove(playerId);
                    continue;
                }
                if (!_pendingHeavyLateJoinBulk.TryGetValue(playerId, out int phase))
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
                            CombatHandlers.SyncExistingDeathBags(playerId);
                            break;
                        case 11:
                            // FindObjectsOfType GameEvents — conservative fired one-shots only.
                            GameEventHandlers.SendGameEventsBulkTo(playerId);
                            break;
                        default:
                            _pendingHeavyLateJoinBulk.Remove(playerId);
                            ModLog.Event(LogCat.Session,
                                "Late-join heavy bulk complete → player " + playerId);
                            return;
                    }
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.LogWarning(
                        "[BulkSync] heavy phase " + phase + " p" + playerId + ": " + ex.Message);
                }

                phase++;
                if (phase >= HeavyLateJoinPhaseCount)
                {
                    _pendingHeavyLateJoinBulk.Remove(playerId);
                    ModLog.Event(LogCat.Session,
                        "Late-join heavy bulk complete → player " + playerId);
                }
                else
                {
                    _pendingHeavyLateJoinBulk[playerId] = phase;
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
            if (!_awaitingLateJoinBulk.TryGetValue(playerId, out float firstSeen))
                return;

            float now = Time.realtimeSinceStartup;
            float settle = _peersCoopReconnect.Contains(playerId)
                ? CoopReconnectBulkSettleSeconds
                : ClientBulkSettleSeconds;

            if (firstSeen <= 0f)
            {
                _awaitingLateJoinBulk[playerId] = now;
                // The joiner is past LoadScene, so high-rate gameplay packets can resume.
                MarkPeerGameplayReady(playerId);
                ModLog.Event(LogCat.Session,
                    "Player " + playerId + " in-world — bulk in "
                    + settle.ToString("F1") + "s (settle"
                    + (_peersCoopReconnect.Contains(playerId) ? ", phase3 reconnect" : "")
                    + ")");
                return;
            }

            if (now - firstSeen < settle)
                return;

            SendLateJoinGameplayBulk(playerId);
        }

        /// <summary>True when host has a chapter world worth sending to title clients.</summary>
        private static bool HostHasShareableWorld()
        {
            try
            {
                // Live loaded player wins over a sticky Core.mainMenu flag.
                // Dual-box saw: mainMenu=true + player=true + loaded=true while host still
                // had full world bulk (lights/generators); the old gate blocked all world share,
                // so clients never left CONNECTED and never saw ENTER WORLD.
                if (Player.Instance != null && (Core.loadedGame || Core.coreStarted || Core.loadingGame))
                    return true;
                if (Core.mainMenu)
                    return false;
                if (Player.Instance != null)
                    return true;
                if (Singleton<WorldGenerator>.Instance != null)
                    return true;
                if (Core.loadedGame || Core.loadingGame)
                    return true;
                if (Core.currentProfile != null)
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Host recovery: if we become shareable while title clients are already connected
        /// (Player.Start may already have run, or mainMenu flag was sticky), push the world once.
        /// </summary>
        private void TickHostWorldShareWhenReady()
        {
            if (_role != NetworkRole.Host || !IsConnected || !_handshakeComplete)
            {
                _hostWasShareableForWaitingClients = false;
                return;
            }

            bool shareable = HostHasShareableWorld();
            if (!shareable)
            {
                _hostWasShareableForWaitingClients = false;
                return;
            }

                // Share only on the transition to the ready state.
            if (_hostWasShareableForWaitingClients)
                return;
            _hostWasShareableForWaitingClients = true;

            int waiting = 0;
            foreach (int id in _handshakedPeers)
            {
                if (id > 1)
                    waiting++;
            }
            if (waiting == 0)
                return;
            if (_worldSaveShare != null && _worldSaveShare.IsBusy)
                return;

            ModLog.Event(LogCat.Save,
                "Host shareable with " + waiting
                + " peer(s) waiting — auto world share (sticky-mainMenu / late enter recovery)");
            _worldSaveShare?.ScheduleHostResend();
            ScheduleLateJoinBulkAfterWorldShare();
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
