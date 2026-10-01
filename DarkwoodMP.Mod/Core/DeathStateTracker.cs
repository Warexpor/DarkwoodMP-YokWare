using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Spectator;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Tracks death state for night-death coordination between host and clients.
    /// Handles: remote death tracking, morning advance when all players dead,
    /// morning release of night-dead peers when someone survives to dawn, and the
    /// coordinated party-wipe outcome on permadeath difficulties.
    /// </summary>
    public static partial class DeathStateTracker
    {
        /// <summary>
        /// Remote peer ids that count toward "everyone is dead" this night. Captured at the
        /// first death and raised by mid-night joins; a leaver is removed by id, so a peer
        /// that joined after the snapshot can never be subtracted twice.
        /// </summary>
        private static readonly HashSet<int> _nightParticipantIds = new HashSet<int>();
        private static bool _nightParticipantsCaptured;

        public static int TotalRemoteCount
        {
            get
            {
                if (_nightParticipantsCaptured)
                    return _nightParticipantIds.Count;
                var net = ModRuntime.Network as LanNetworkManager;
                return net?.RemotePlayerCount ?? 0;
            }
            set { /* kept for backward compat */ }
        }

        public static bool LocalNightDeath { get; private set; }

        public static bool AllRemoteDead
        {
            get
            {
                int total = TotalRemoteCount;
                if (total <= 0)
                    return true;
                return RemoteNightDeathCount >= total;
            }
        }

        public static int RemoteNightDeathCount { get; private set; }

        private static readonly Dictionary<int, Vector3> _remoteDeathPositions = new Dictionary<int, Vector3>();

        public static bool AllDeadAtNight => LocalNightDeath && AllRemoteDead;

        public static bool RemoteNightDeath => RemoteNightDeathCount > 0;

        public static Vector3 LocalDeathPosition { get; private set; }

        public static bool LocalBagSynced { get; set; }

        public static bool PreventSpectator { get; set; }

        /// <summary>
        /// Controller.day when the local peer last died at night; -1 when none. Survives
        /// <see cref="Reset"/> (morning release resets before startAfterNight runs) and
        /// is only consumed by <see cref="ConsumeMorningRewardSkip"/>.
        /// </summary>
        private static int _localNightDeathDay = -1;

        private static bool _localPermadeathEligible;
        private static bool _localDeathEndsRunInVanilla;
        private static readonly HashSet<int> _remotePermadeathEligible = new HashSet<int>();

        /// <summary>Remote peers that were night-dead at the morning edge (host; no reward for them).</summary>
        private static readonly HashSet<int> _morningDeadIds = new HashSet<int>();
        private static int _morningDeadDay = -1;

        /// <summary>Host declared a permadeath party wipe; no morning until the outcome runs.</summary>
        public static bool PartyWipeDeclared { get; private set; }

        /// <summary>
        /// The party-wipe outcome ended (or died with its scene) without the normal reset: the
        /// declaration must not keep blocking saves, skipDay and the morning release.
        /// </summary>
        internal static void ClearPartyWipeDeclared() => PartyWipeDeclared = false;

        /// <summary>True while host is mid TryResolveNightMorning to avoid re-entry.</summary>
        private static bool _resolvingMorning;

        /// <summary>
        /// Freeze (or raise) the remote-player set used for AllRemoteDead.
        /// First death captures the remote ids; a mid-night join adds its id so
        /// AllDeadAtNight still requires the new peer to die (or disconnect).
        /// </summary>
        public static void SnapshotNightParticipants()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            int before = _nightParticipantIds.Count;
            bool first = !_nightParticipantsCaptured;
            if (net != null)
            {
                foreach (RemotePlayerProxy proxy in net.GetAllProxies())
                {
                    if (proxy != null && proxy.PlayerId > 0 && proxy.PlayerId != net.LocalPlayerId)
                        _nightParticipantIds.Add(proxy.PlayerId);
                }
                foreach (int id in net.GetHandshakedPeerIds())
                {
                    if (id > 0 && id != net.LocalPlayerId)
                        _nightParticipantIds.Add(id);
                }
            }
            _nightParticipantsCaptured = true;

            if (first)
                ModLog.Event(LogCat.Death, $"Night participants snapshot: {_nightParticipantIds.Count} remotes");
            else if (_nightParticipantIds.Count > before)
                ModLog.Event(LogCat.Death,
                    $"Night participants raised {before} → {_nightParticipantIds.Count} (mid-night join)");
        }

        /// <summary>
        /// Local flags and position only. Spectator exits use this: leaving spectate says
        /// nothing about the other peers' deaths, so their bookkeeping must stay.
        /// <see cref="PreventSpectator"/> is cleared here too; a spectator exit sets it
        /// again after calling this.
        /// </summary>
        public static void ResetLocal()
        {
            LocalNightDeath = false;
            LocalDeathPosition = Vector3.zero;
            LocalBagSynced = false;
            PreventSpectator = false;
            _localPermadeathEligible = false;
            _localDeathEndsRunInVanilla = false;
        }

        /// <summary>
        /// A night is over for everyone (all-dead morning, release, party-wipe outcome):
        /// <see cref="ResetLocal"/> plus every remote peer's death bookkeeping.
        /// </summary>
        public static void Reset()
        {
            ResetLocal();
            RemoteNightDeathCount = 0;
            _remoteDeathPositions.Clear();
            _remotePermadeathEligible.Clear();
            PartyWipeDeclared = false;
            _nightParticipantIds.Clear();
            _nightParticipantsCaptured = false;
            _resolvingMorning = false;
        }

        /// <summary>
        /// Session / world boundary (network stop, chapter or save reload, start over):
        /// <see cref="Reset"/> plus the marks that deliberately survive it within one
        /// night, so a stale death from the old world cannot skip the next world's
        /// morning reward or hide a peer from its fan-out.
        /// </summary>
        public static void ResetSession()
        {
            Reset();
            _localNightDeathDay = -1;
            _armDeathSaveSuppress = false;
            ClearMorningDeadMarks();
        }

        /// <summary>
        /// Vanilla <c>Player.onDeath</c> night gate: hard night, and either night or the
        /// 50 minutes before it. Host and client patches must agree with the body that
        /// decides whether <c>skipDay</c> runs.
        /// </summary>
        public static bool IsNightDeathWindow()
        {
            Controller ctrl = Singleton<Controller>.Instance;
            return ctrl != null && ctrl.isHardNight
                && (!Core.isDay() || (float)ctrl.CurrentTime > ctrl.nightTime - 50f);
        }

        /// <param name="vanillaEndsRun">
        /// Vanilla's own permadeath outcome runs for this death (host with no ready peer, so
        /// the shared-death rewrite did not apply). The run is already ending locally, so
        /// this death must not also resolve a morning or declare a party wipe.
        /// </param>
        public static void OnLocalNightDeath(Vector3 pos, bool permadeathEligible = false,
            bool vanillaEndsRun = false)
        {
            SnapshotNightParticipants();
            LocalNightDeath = true;
            PreventSpectator = false;
            _localPermadeathEligible = permadeathEligible;
            _localDeathEndsRunInVanilla = vanillaEndsRun;
            Controller ctrl = Singleton<Controller>.Instance;
            _localNightDeathDay = ctrl != null ? ctrl.day : -1;
            LocalDeathPosition = pos;
            LocalBagSynced = false;
            _armDeathSaveSuppress = true;
            ModLog.Event(LogCat.Death, $"Local night death at {pos}");
        }

        /// <summary>
        /// Host startAfterNight: true when this morning follows the host's own night
        /// death (vanilla passes withReward = !isAfterDeath, so no survival reward).
        /// One-shot per night; a stale mark from a missed morning is dropped.
        /// </summary>
        public static bool ConsumeMorningRewardSkip(int day)
        {
            if (_localNightDeathDay < 0) return false;
            bool skip = day == _localNightDeathDay + 1;
            if (day > _localNightDeathDay)
                _localNightDeathDay = -1;
            return skip;
        }

        /// <summary>Host: was this remote night-dead when the morning edge released everyone?</summary>
        public static bool WasNightDeadAtMorning(int playerId, int day)
            => _morningDeadDay == day && _morningDeadIds.Contains(playerId);

        public static void ClearMorningDeadMarks()
        {
            _morningDeadIds.Clear();
            _morningDeadDay = -1;
        }

        public static void OnRemoteNightDeath(int playerId, Vector3 pos, bool permadeathEligible = false)
        {
            SnapshotNightParticipants();
            // The dead peer always counts, even before its proxy / handshake is visible here.
            if (playerId > 0)
                _nightParticipantIds.Add(playerId);
            if (_remoteDeathPositions.ContainsKey(playerId))
                return;
            _remoteDeathPositions[playerId] = pos;
            if (permadeathEligible)
                _remotePermadeathEligible.Add(playerId);
            RemoteNightDeathCount = _remoteDeathPositions.Count;
            _armDeathSaveSuppress = true;
            ModLog.Event(LogCat.Death, $"Remote night death for player {playerId} (count={RemoteNightDeathCount}/{TotalRemoteCount})");
        }

        /// <summary>True while this remote is still night-dead (until morning Reset).</summary>
        public static bool IsRemoteNightDead(int playerId)
        {
            return playerId > 0 && _remoteDeathPositions.ContainsKey(playerId);
        }

        private static bool _armDeathSaveSuppress;

        public static void OnLocalDayDeath()
        {
            LocalNightDeath = false;
            PreventSpectator = false;
            _armDeathSaveSuppress = true;
            ModLog.Event(LogCat.Death, "Local day death (normal respawn)");
        }

        public static void OnRemoteDayDeath(int playerId)
        {
            _armDeathSaveSuppress = true;
            if (_remoteDeathPositions.Remove(playerId))
            {
                _remotePermadeathEligible.Remove(playerId);
                RemoteNightDeathCount = _remoteDeathPositions.Count;
                ModLog.Event(LogCat.Death, $"Remote day death for player {playerId} (count={RemoteNightDeathCount}/{TotalRemoteCount})");
            }
        }

        /// <summary>True once after a day death; SaveSyncPatch arms suppression after the first fan-out.</summary>
        public static bool ConsumeDeathSaveSuppressArm()
        {
            if (!_armDeathSaveSuppress) return false;
            _armDeathSaveSuppress = false;
            return true;
        }

        /// <summary>
        /// A remote peer left the session (host: transport drop; client: dropped from the
        /// gossiped roster). Forgets its night-death mark and its place in the night's
        /// participant set. Safe on every role; does not resolve anything.
        /// </summary>
        /// <returns>True when the leaver was night-dead.</returns>
        public static bool OnRemotePeerGone(int playerId)
        {
            if (playerId <= 0) return false;

            bool wasNightDead = _remoteDeathPositions.Remove(playerId);
            _remotePermadeathEligible.Remove(playerId);
            _morningDeadIds.Remove(playerId);
            RemoteNightDeathCount = _remoteDeathPositions.Count;
            if (_nightParticipantsCaptured)
                _nightParticipantIds.Remove(playerId);
            return wasNightDead;
        }

        /// <summary>Frame in which <see cref="OnRemoteDisconnected"/> asked for a morning resolve.</summary>
        private static int _disconnectResolveFrame = -1;

        /// <returns>True when disconnect bookkeeping satisfies morning-resolve policy.</returns>
        public static bool OnRemoteDisconnected(int playerId)
        {
            if (playerId <= 0) return false;

            bool leaverWasNightDead = OnRemotePeerGone(playerId);

            ModLog.Event(LogCat.Death,
                $"Remote player {playerId} disconnected mid-night " +
                $"(wasDead={leaverWasNightDead}, dead={RemoteNightDeathCount}/{TotalRemoteCount})");

            bool resolve = NightDeathPolicy.ShouldResolveMorningOnDisconnect(
                LocalNightDeath,
                leaverWasNightDead,
                TotalRemoteCount,
                RemoteNightDeathCount);
            if (resolve)
                _disconnectResolveFrame = Time.frameCount;
            return resolve;
        }

        /// <summary>
        /// Host-only: if everyone relevant is dead at night, advance morning once.
        /// Used by death handlers and disconnect cleanup.
        /// </summary>
        public static bool TryResolveNightMorning(string reason)
        {
            if (_resolvingMorning || PartyWipeDeclared) return false;
            var net = ModRuntime.Network as LanNetworkManager;
            // The leaver is already out of the transport when the disconnect cleanup asks, so
            // the host may no longer be "connected" when its last peer was the one that left.
            if (net == null || net.Role != NetworkRole.Host)
                return false;
            if (!net.IsConnected && _disconnectResolveFrame != Time.frameCount)
                return false;
            if (!LocalNightDeath || !AllDeadAtNight)
                return false;
            // Vanilla is already running its own permadeath outcome for the host: a second
            // one (party wipe video) or a skipDay under it would double / corrupt it.
            if (_localDeathEndsRunInVanilla)
                return false;

            if (PermadeathPolicy.PartyWipeEndsRun(
                    _localPermadeathEligible, RemoteNightDeathCount, CountRemoteEligibleDeaths()))
            {
                return DeclarePartyWipe(net, reason);
            }

            _resolvingMorning = true;
            try
            {
                ModLog.Event(LogCat.Death, $"All dead at night — resolving morning ({reason})");

                // Mark every dead peer for tomorrow's MorningReward fan-out before
                // ExitAndRespawn → Reset clears _remoteDeathPositions (vanilla: no reward after death).
                Controller wipeCtrl = Singleton<Controller>.Instance;
                _morningDeadIds.Clear();
                foreach (int id in _remoteDeathPositions.Keys)
                    _morningDeadIds.Add(id);
                _morningDeadDay = wipeCtrl != null ? wipeCtrl.day + 1 : -1;

                net.Broadcast(NetMessageType.NightDeathState,
                    w => new NightDeathStateMessage { IsDead = true, AllDeadTrigger = true }.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);

                if (SpectatorModeController.Instance != null && SpectatorModeController.Instance.IsSpectating)
                    SpectatorModeController.Instance.ExitAndRespawn();

                if (Singleton<Controller>.Instance != null)
                    Singleton<Controller>.Instance.skipDay();

                if (Singleton<SaveManager>.Instance != null)
                    Singleton<SaveManager>.Instance.Save(doJson: true);

                Reset();
                return true;
            }
            finally
            {
                _resolvingMorning = false;
            }
        }

        private static int CountRemoteEligibleDeaths()
        {
            int n = 0;
            foreach (int id in _remotePermadeathEligible)
            {
                if (_remoteDeathPositions.ContainsKey(id))
                    n++;
            }
            return n;
        }

        /// <summary>
        /// Every peer was night-dead and every death would have ended a vanilla run
        /// (nightmare, or hard with lives spent). Tell all peers and run the real
        /// permadeath outcome once, here included, instead of a shared morning.
        /// </summary>
        /// <returns>False when the outcome could not start (no player yet, or one already
        /// running); the declaration is withdrawn so morning, save and skipDay are not
        /// blocked by a wipe that never began.</returns>
        private static bool DeclarePartyWipe(LanNetworkManager net, string reason)
        {
            // Set first: the outcome's own Reset clears it, and a synchronous start must not
            // leave the flag behind. Withdrawn below if nothing started.
            PartyWipeDeclared = true;
            if (!PartyWipeOutcome.Begin("host party wipe"))
            {
                PartyWipeDeclared = false;
                ModLog.Event(LogCat.Death,
                    $"Party wipe declaration withdrawn — outcome not started ({reason})");
                return false;
            }

            ModLog.Event(LogCat.Death, $"Party wipe on permadeath difficulty — ending run for all peers ({reason})");
            net.Broadcast(NetMessageType.NightDeathState,
                w => new NightDeathStateMessage { IsDead = true, PartyWipe = true }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            return true;
        }

        /// <summary>
        /// Host, late join: tell <paramref name="targetPlayerId"/> who is night-dead right
        /// now. Each dead peer's own PlayerDied is replayed to the joiner wrapped in a
        /// RemotePlayerForward (original id = the dead peer), exactly as the host forwards
        /// it live, so the joiner's proxy pose and death bookkeeping match everyone else's.
        /// </summary>
        public static void HostSendNightDeathSnapshotTo(LanNetworkManager net, int targetPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host || targetPlayerId <= 0)
                return;
            if (!LocalNightDeath && _remoteDeathPositions.Count == 0)
                return;

            if (LocalNightDeath)
                SendDeathReplay(net, targetPlayerId, net.LocalPlayerId, LocalDeathPosition, _localPermadeathEligible);
            foreach (var kv in _remoteDeathPositions)
            {
                if (kv.Key == targetPlayerId)
                    continue;
                SendDeathReplay(net, targetPlayerId, kv.Key, kv.Value, _remotePermadeathEligible.Contains(kv.Key));
            }
        }

        private static void SendDeathReplay(LanNetworkManager net, int targetPlayerId, int deadPlayerId,
            Vector3 pos, bool permadeathEligible)
        {
            var died = new PlayerDiedMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                IsNight = true,
                HasDropBag = false,
                PermadeathEligible = permadeathEligible
            };
            var innerWriter = new NetWriter();
            died.Serialize(innerWriter);
            byte[] inner = innerWriter.CopyData();
            var fwd = new RemotePlayerForwardMessage
            {
                OriginalPlayerId = deadPlayerId,
                InnerType = (byte)NetMessageType.PlayerDied,
                InnerPayload = inner
            };
            net.SendToPlayer(targetPlayerId, NetMessageType.RemotePlayerForward,
                w => fwd.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.Death, $"Late join: replayed night death of p{deadPlayerId} to p{targetPlayerId}");
        }
    }
}
