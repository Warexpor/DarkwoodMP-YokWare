using System.Collections.Generic;
using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Spectator;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Multiplayer dream-death session: any shared dream (not only epilogue).
    /// Death → spectate until all participants dead or story ends the dream.
    /// Epilogue crawl / camera-pan deaths are excluded (see death patches + inEpilogue).
    /// </summary>
    internal static class FinalDreamsceneManager
    {
        private static bool _isActive;
        private static bool _localDeadInDream;
        private static bool _ending;
        private static readonly HashSet<int> _deadPlayerIds = new HashSet<int>();
        private static readonly HashSet<int> _connectedPlayerIds = new HashSet<int>();

        /// <summary>
        /// The local player died in this dream. Unlike <see cref="IsLocalDead"/> it survives the
        /// DreamEnded receipt (which runs before the exit video and endDreaming), so the reward
        /// downgrade and the spectate exit at wake-up still see it.
        /// </summary>
        private static bool _localDiedThisDream;

        public static bool IsActive => _isActive;
        public static bool IsLocalDead => _localDeadInDream;
        public static bool WasLocalDeadThisDream => _localDiedThisDream || _localDeadInDream;

        /// <summary>
        /// One-shot: allow initiateEndDreaming(playerDeath) through the death Prefix
        /// when the host is ending the shared dream because all participants are dead.
        /// </summary>
        internal static bool AllowDeathEndPass;

        /// <summary>True when every peer who is actually in the dream is dead.</summary>
        public static bool AllDead
        {
            get
            {
                if (!_isActive || !_localDeadInDream)
                    return false;
                var net = ModRuntime.Network;
                if (net == null)
                    return true;
                foreach (int id in net.GetHandshakedPeerIds())
                {
                    if (id <= 0 || id == net.LocalPlayerId)
                        continue;
                    if (!DreamSyncManager.IsRemoteInDream(id))
                        continue;
                    if (!_deadPlayerIds.Contains(id))
                        return false;
                }
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy == null || proxy.PlayerId <= 0)
                        continue;
                    if (!DreamSyncManager.IsRemoteInDream(proxy.PlayerId))
                        continue;
                    if (!_deadPlayerIds.Contains(proxy.PlayerId))
                        return false;
                }
                return true;
            }
        }

        /// <summary>Refresh roster and report whether any remote participants are tracked.</summary>
        public static bool HasRemoteParticipants()
        {
            RefreshConnectedPlayers();
            return _connectedPlayerIds.Count > 0;
        }

        /// <summary>True when any remote player is dead in the shared dream (N-player set).</summary>
        public static bool IsRemoteDead => _deadPlayerIds.Count > 0;

        public static void OnDreamStarted()
        {
            _isActive = true;
            _localDeadInDream = false;
            _localDiedThisDream = false;
            _ending = false;
            _deadPlayerIds.Clear();
            ClearPeerDeadInDreamFlags();
            RefreshConnectedPlayers();
            ModRuntime.LegacyInfo(
                $"[FinalDreamscene] Dream started — death tracking active ({_connectedPlayerIds.Count} remotes connected)");
        }

        public static void OnDreamEnded()
        {
            // Before the _isActive gate: an all-dead teardown already cleared _isActive, and the
            // dialogue door / spirit state of that dream must not carry into the next one.
            ClearPeerDeadInDreamFlags();
            DWMPHorde.Patches.DialogueDoorAftermath.Reset();
            DreamForestSpiritAggro.Reset();
            if (!_isActive) return;
            _isActive = false;
            _localDeadInDream = false;
            _ending = false;
            _deadPlayerIds.Clear();
            _connectedPlayerIds.Clear();
            ModRuntime.LegacyInfo("[FinalDreamscene] Dream ended — state reset");
        }

        /// <summary>
        /// Next pocket in the same session. Door/spirit presentation resets.
        /// Death and spectator state stay so a dead peer is not marked alive.
        /// </summary>
        public static void OnDreamChained()
        {
            DWMPHorde.Patches.DialogueDoorAftermath.Reset();
            DreamForestSpiritAggro.Reset();
            ModRuntime.LegacyInfo("[FinalDreamscene] Dream chained — death roster kept");
        }

        /// <summary>
        /// RemotePlayerState.IsDeadInDream is set on a peer's FinalDreamsceneDeath but only the
        /// peer's own DreamEnded cleared it, and a peer who died never sends one. Clear it for
        /// every peer at session start/end so a later dream's story end is not rejected as
        /// "dead_in_dream".
        /// </summary>
        private static void ClearPeerDeadInDreamFlags()
        {
            var net = ModRuntime.Network;
            if (net == null) return;
            var ids = new HashSet<int>(_connectedPlayerIds);
            foreach (int id in net.GetHandshakedPeerIds())
                ids.Add(id);
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy != null)
                    ids.Add(proxy.PlayerId);
            }
            foreach (int id in ids)
            {
                if (id > 0 && net.TryGetRemoteState(id, out var st) && st != null)
                    st.IsDeadInDream = false;
            }
        }

        /// <summary>
        /// Rebuild the remote participant set from live proxies and handshaked peers.
        /// Proxies may spawn after session start; peers table is more complete.
        /// </summary>
        public static void RefreshConnectedPlayers()
        {
            _connectedPlayerIds.Clear();
            var net = ModRuntime.Network;
            if (net == null) return;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy != null && proxy.PlayerId > 0)
                    _connectedPlayerIds.Add(proxy.PlayerId);
            }
            // Include handshaked peer ids even if proxy not yet spawned.
            foreach (int id in net.GetHandshakedPeerIds())
            {
                if (id > 0 && id != net.LocalPlayerId)
                    _connectedPlayerIds.Add(id);
            }
        }

        public static void OnLocalDeathInDream()
        {
            if (!_isActive || _localDeadInDream || _ending) return;

            // Epilogue uses crawl / camera pan; never convert it to dream spectate.
            if (Player.Instance != null && Player.Instance.inEpilogue)
            {
                ModRuntime.LegacyInfo("[FinalDreamscene] Local death in epilogue — leaving vanilla crawl/cam path");
                return;
            }

            // Proxies may not have been ready at OnDreamStarted; refresh once.
            if (_connectedPlayerIds.Count == 0)
                RefreshConnectedPlayers();

            // Solo or empty peer set: the prefix should have allowed vanilla. If we still
            // land here, tear down; never block and do nothing.
            if (_connectedPlayerIds.Count == 0)
            {
                ModRuntime.LegacyInfo(
                    "[FinalDreamscene] Solo/empty-peer dream death — EndDreamForBoth fallback");
                EndDreamForBoth();
                return;
            }

            if (_localDeadInDream)
            {
                ModRuntime.LegacyInfo("[FinalDreamscene] Local death already recorded — ignore duplicate");
                return;
            }

            _localDeadInDream = true;
            _localDiedThisDream = true;

            ModRuntime.LegacyInfo("[FinalDreamscene] Local player died in dream");

            var net = ModRuntime.Network;
            if (net != null && net.IsConnected)
            {
                net.Broadcast(NetMessageType.FinalDreamsceneDeath,
                    w => new FinalDreamsceneDeathMessage { IsDead = true }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }

            EnterDreamSpectator();

            if (AllDead)
                TryHostEndAllDead("after local death");
        }

        public static void OnRemoteDeathInDream(int playerId)
        {
            if (!_isActive || _ending) return;
            if (playerId <= 0) return;

            // Late proxy / missed OnDreamStarted set membership
            if (!_connectedPlayerIds.Contains(playerId))
            {
                RefreshConnectedPlayers();
                if (!_connectedPlayerIds.Contains(playerId))
                    _connectedPlayerIds.Add(playerId);
            }

            _deadPlayerIds.Add(playerId);
            ModRuntime.LegacyInfo(
                $"[FinalDreamscene] Remote player {playerId} died in dream ({_deadPlayerIds.Count}/{_connectedPlayerIds.Count})");

            if (AllDead)
                TryHostEndAllDead("all remotes dead");
        }

        /// <summary>
        /// Only the host tears down the shared dream on all-dead. Clients stay in spectate
        /// until DreamEnded. This avoids double endDreaming races when both peers call EndDreamForBoth.
        /// </summary>
        private static void TryHostEndAllDead(string reason)
        {
            var net = ModRuntime.Network;
            if (net != null && net.IsConnected && net.Role != NetworkRole.Host)
            {
                ModRuntime.LegacyInfo(
                    $"[FinalDreamscene] All dead ({reason}) — client waits for host DreamEnded");
                return;
            }
            ModRuntime.LegacyInfo($"[FinalDreamscene] All dead ({reason}) — host ending dream");
            EndDreamForBoth();
        }

        /// <summary>
        /// The local player is back in the overworld (endDreaming ran, or a hard cleanup). A player
        /// who died in the dream leaves spectate here, without the spectate position restore (that
        /// pose is on the dream pad), and gets its body back.
        /// </summary>
        public static void OnLocalWokeUp()
        {
            bool died = _localDiedThisDream || _localDeadInDream;
            _localDiedThisDream = false;
            if (!died)
                return;
            var spec = SpectatorModeController.Instance;
            if (spec != null && spec.IsSpectating)
                spec.ExitWithoutPositionRestore();
            if (Player.Instance != null)
                Player.Instance.switchVisibilty(true);
            ModRuntime.LegacyInfo("[FinalDreamscene] Woke up after dying in the dream — spectate left");
        }

        public static void OnDisconnected()
        {
            _isActive = false;
            _localDeadInDream = false;
            _localDiedThisDream = false;
            _ending = false;
            AllowDeathEndPass = false;
            _deadPlayerIds.Clear();
            _connectedPlayerIds.Clear();
        }

        /// <summary>Called when a remote player disconnects mid-dream; removes it from tracking sets.</summary>
        public static void OnRemoteDisconnected(int playerId)
        {
            _connectedPlayerIds.Remove(playerId);
            _deadPlayerIds.Remove(playerId);
            DreamSyncManager.ClearRemoteInDream(playerId);
            ModRuntime.LegacyInfo(
                $"[FinalDreamscene] Remote player {playerId} disconnected — removed from death tracking ({_deadPlayerIds.Count}/{_connectedPlayerIds.Count})");

            // Last peer gone while the local player is alive: the host plays the dream out solo.
            // Ending here forced outcome "playerDeath", which MarkCompleted-ed and burned the
            // preset for a player who never died. Solo death/story end then use the vanilla path
            // (HasRemoteParticipants() == false). A dead local player with no one left is the
            // genuine all-dead case and ends below.
            if ((!_isActive || _ending) && _connectedPlayerIds.Count == 0 && DreamSession.IsActive)
            {
                ModRuntime.LegacyInfo(
                    "[FinalDreamscene] Last peer left mid-session — dream continues solo");
                return;
            }

            if (!_isActive || _ending) return;

            if (_connectedPlayerIds.Count == 0)
            {
                if (_localDeadInDream)
                {
                    ModRuntime.LegacyInfo(
                        "[FinalDreamscene] No remotes left and local is dead — ending shared dream");
                    TryHostEndAllDead("last peer left, local dead");
                }
                else
                {
                    ModRuntime.LegacyInfo(
                        "[FinalDreamscene] No remotes left — local alive, dream continues solo");
                }
                return;
            }

            if (AllDead)
                TryHostEndAllDead("after disconnect");
        }

        private static void EnterDreamSpectator()
        {
            var net = ModRuntime.Network;
            if (net == null) return;

            // Stable order by PlayerId; prefer living proxies (3+ cycle).
            Transform followTarget = net.GetAllProxies()
                .Where(p => p != null && p.GetComponent<CharBase>()?.alive != false)
                .OrderBy(p => p.PlayerId)
                .Select(p => p.transform)
                .FirstOrDefault();

            if (followTarget == null)
            {
                ModRuntime.Log?.LogWarning("[FinalDreamscene] No remote target for spectator");
                return;
            }

            SpectatorModeController.EnsureExists();
            SpectatorModeController.Instance.ForceEnter(followTarget);
        }

        private static void EndDreamForBoth()
        {
            if (_ending) return;
            _ending = true;

            var net = ModRuntime.Network;

            var player = Player.Instance;
            if (player != null)
            {
                player.invulnerable = false;
                if (player.immobilised)
                    player.stopImmobilise();
            }

            var dreams = Singleton<Dreams>.Instance;
            if (dreams != null && dreams.dreaming)
            {
            // Use vanilla initiateEndDreaming, transition, and endDreaming rather
            // than a hard cut.
                dreams.outcome = "playerDeath";
                AllowDeathEndPass = true;
                ModRuntime.LegacyInfo(
                    "[FinalDreamscene] All-dead — initiateEndDreaming(playerDeath)");
                try
                {
                    dreams.initiateEndDreaming();
                }
                catch (System.Exception ex)
                {
                    AllowDeathEndPass = false;
                    ModRuntime.Log?.LogWarning(
                        "[FinalDreamscene] initiateEndDreaming failed, falling back to endDreaming: "
                        + ex.Message);
                    dreams.endDreaming();
                }
            }
            else
            {
                ModRuntime.Log?.LogWarning(
                    "[FinalDreamscene] Dreams.Instance not dreaming — cleaning up directly");
                var cam = Singleton<CamMain>.Instance;
                if (cam != null) cam.followTarget = player != null ? player.transform : null;
                if (net != null && net.IsConnected && DreamSession.IsActive)
                {
                    net.Broadcast(NetMessageType.DreamEnded,
                        w => DreamEndedMessage.Build(DreamSession.PresetName ?? "", "allDead")
                            .Serialize(w),
                        DeliveryMethod.ReliableOrdered);
                    DreamSession.End("allDead");
                }
            }

            var spec = SpectatorModeController.Instance;
            if (spec != null && spec.IsSpectating)
                spec.ExitWithoutPositionRestore();

            if (player != null)
                player.switchVisibilty(true);

            _isActive = false;
            _localDeadInDream = false;
            _deadPlayerIds.Clear();
            _connectedPlayerIds.Clear();
            _ending = false;
        }
    }
}
