using System.Collections.Generic;
using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Spectator;
using HarmonyLib;
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

        public static bool IsActive => _isActive;
        public static bool IsLocalDead => _localDeadInDream;

        /// <summary>True when ALL players (local + all remotes) are dead in the dream.</summary>
        public static bool AllDead => _isActive && _localDeadInDream
            && _deadPlayerIds.Count > 0
            && _deadPlayerIds.SetEquals(_connectedPlayerIds);

        /// <summary>True when any remote player is dead in the shared dream (N-player set).</summary>
        public static bool IsRemoteDead => _deadPlayerIds.Count > 0;

        public static void OnDreamStarted()
        {
            _isActive = true;
            _localDeadInDream = false;
            _ending = false;
            _deadPlayerIds.Clear();
            RefreshConnectedPlayers();
            ModRuntime.LegacyInfo(
                $"[FinalDreamscene] Dream started — death tracking active ({_connectedPlayerIds.Count} remotes connected)");
        }

        public static void OnDreamEnded()
        {
            if (!_isActive) return;
            _isActive = false;
            _localDeadInDream = false;
            _ending = false;
            _deadPlayerIds.Clear();
            _connectedPlayerIds.Clear();
            ModRuntime.LegacyInfo("[FinalDreamscene] Dream ended — state reset");
        }

        /// <summary>
        /// Rebuild remote participant set from live proxies + handshaked peers (D7).
        /// Proxies may spawn after session start; peers table is more complete.
        /// </summary>
        public static void RefreshConnectedPlayers()
        {
            _connectedPlayerIds.Clear();
            var net = ModRuntime.Network as LanNetworkManager;
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

            // Epilogue uses crawl / camera pan — never convert to dream spectate.
            if (Player.Instance != null && Player.Instance.inEpilogue)
            {
                ModRuntime.LegacyInfo("[FinalDreamscene] Local death in epilogue — leaving vanilla crawl/cam path");
                return;
            }

            // Proxies may not have been ready at OnDreamStarted — refresh once.
            if (_connectedPlayerIds.Count == 0)
                RefreshConnectedPlayers();

            // Solo death in dream (no remote participants) → fall through to vanilla endDreaming.
            if (_connectedPlayerIds.Count == 0)
            {
                ModRuntime.LegacyInfo(
                    "[FinalDreamscene] Solo dream death — skipping redirect, letting vanilla endDreaming handle");
                return;
            }

            _localDeadInDream = true;

            ModRuntime.LegacyInfo("[FinalDreamscene] Local player died in dream");

            var net = ModRuntime.Network as LanNetworkManager;
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
        /// until DreamEnded — avoids double endDreaming races when both peers call EndDreamForBoth.
        /// </summary>
        private static void TryHostEndAllDead(string reason)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.IsConnected && net.Role != NetworkRole.Host)
            {
                ModRuntime.LegacyInfo(
                    $"[FinalDreamscene] All dead ({reason}) — client waits for host DreamEnded");
                return;
            }
            ModRuntime.LegacyInfo($"[FinalDreamscene] All dead ({reason}) — host ending dream");
            EndDreamForBoth();
        }

        public static void OnDisconnected()
        {
            _isActive = false;
            _localDeadInDream = false;
            _ending = false;
            _deadPlayerIds.Clear();
            _connectedPlayerIds.Clear();
        }

        /// <summary>Called when a remote player disconnects mid-dream — removes from tracking sets.</summary>
        public static void OnRemoteDisconnected(int playerId)
        {
            _connectedPlayerIds.Remove(playerId);
            _deadPlayerIds.Remove(playerId);
            ModRuntime.LegacyInfo(
                $"[FinalDreamscene] Remote player {playerId} disconnected — removed from death tracking ({_deadPlayerIds.Count}/{_connectedPlayerIds.Count})");

            if (!_isActive || _ending) return;

            // Last peer gone while local already dead: do not zombie-spectate — end dream.
            if (_connectedPlayerIds.Count == 0 && _localDeadInDream)
            {
                ModRuntime.LegacyInfo(
                    "[FinalDreamscene] No remotes left and local dead — ending dream");
                EndDreamForBoth();
                return;
            }

            if (AllDead)
                TryHostEndAllDead("after disconnect");
        }

        private static void EnterDreamSpectator()
        {
            var net = ModRuntime.Network as LanNetworkManager;
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

            var net = ModRuntime.Network as LanNetworkManager;

            var player = Player.Instance;
            if (player != null)
            {
                player.invulnerable = false;
                if (player.immobilised)
                    player.stopImmobilise();
            }

            if (Singleton<Dreams>.Instance != null && Singleton<Dreams>.Instance.dreaming)
            {
                var traverse = Traverse.Create(Singleton<Dreams>.Instance);
                if (traverse.Field("outcomePreset").GetValue() == null)
                    traverse.Field("outcomePreset").SetValue(new DreamPreset.Outcome { dontLieDown = true });

                ModRuntime.LegacyInfo("[FinalDreamscene] Calling Dreams.endDreaming()");
                Singleton<Dreams>.Instance.endDreaming();
            }
            else
            {
                ModRuntime.Log?.LogWarning("[FinalDreamscene] Dreams.Instance not dreaming — cleaning up directly");
                var cam = Singleton<CamMain>.Instance;
                if (cam != null) cam.followTarget = player != null ? player.transform : null;
            }

            var spec = SpectatorModeController.Instance;
            if (spec != null && spec.IsSpectating)
                spec.ExitWithoutPositionRestore();

            if (player != null)
                player.switchVisibilty(true);

            // endDreaming → DreamEndPatch already Broadcasts DreamEnded when local dream active.
            // Extra empty DreamEnded only if patch did not run (not dreaming / not local active).
            if (net != null && net.IsConnected && DreamSyncManager.IsLocalDreamActive)
            {
                net.Broadcast(NetMessageType.DreamEnded,
                    w => DreamEndedMessage.Build(DreamSession.PresetName ?? "", "allDead").Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }

            _isActive = false;
            _localDeadInDream = false;
            _deadPlayerIds.Clear();
            _connectedPlayerIds.Clear();
            _ending = false;
        }
    }
}
