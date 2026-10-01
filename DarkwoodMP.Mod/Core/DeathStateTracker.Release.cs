using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Spectator;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>Morning release of night-dead peers (host edge, client apply, local transport home).</summary>
    public static partial class DeathStateTracker
    {
        /// <summary>
        /// This peer itself is in (or entering/leaving) a dream. Deliberately local-only:
        /// another peer dreaming says nothing about whether this one may be sent home, and
        /// dream-death spectating is resolved by the dream end, not by the morning release.
        /// </summary>
        private static bool LocalDreamRunning()
        {
            Dreams dreams = Singleton<Dreams>.Instance;
            return DreamSyncManager.IsLocalDreamActive
                || (dreams != null && (dreams.dreaming || dreams.switchingDream));
        }

        /// <summary>
        /// Host, from <c>Controller.startDay</c>: somebody survived to dawn, so every
        /// night-dead peer (host included) is released now, the way vanilla's death-time
        /// skipDay puts a lone player back at home. Idempotent; when the whole party is
        /// dead <see cref="TryResolveNightMorning"/> owns the release instead.
        /// </summary>
        public static void HostReleaseNightDeadAtMorning(string reason)
        {
            if (_resolvingMorning || PartyWipeDeclared) return;
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;

            // Night-dead remotes are released regardless of who is dreaming: each peer
            // decides for itself (ClientReleaseNightDeadAtMorning). Only the host's own
            // local release is skipped while the host itself is in a dream.
            bool localInDream = LocalDreamRunning();
            bool localDead = LocalNightDeath && !localInDream;
            var spec = SpectatorModeController.Instance;
            bool localSpectating = spec != null && spec.IsSpectating && !localInDream;
            // No early return when nothing looks dead here: a night-dead client that soft-reconnected
            // during the night lost its mark on the host (the blip cleared it) but is still spectating
            // locally. The client handler is idempotent, so the broadcast is always sent.

            Controller ctrl = Singleton<Controller>.Instance;
            int day = ctrl != null ? ctrl.day : 0;

            _morningDeadIds.Clear();
            foreach (int id in _remoteDeathPositions.Keys)
                _morningDeadIds.Add(id);
            _morningDeadDay = day;

            ModLog.Event(LogCat.Death,
                $"Morning release ({reason}): local={localDead} remotes={_remoteDeathPositions.Count} day={day}");

            net.Broadcast(NetMessageType.NightDeathRelease,
                w => new NightDeathReleaseMessage { Day = day }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);

            // Host proxies come back alive on the peer's next real PlayerState once
            // IsRemoteNightDead is false (PlayerStateNetHandlers).
            ClearRemoteNightState();
            if (!localInDream)
                ReleaseLocalNightDeath(worldAuthority: true);
        }

        /// <summary>Client: host says it is morning. Release locally and drop peers' night-death marks.</summary>
        public static void ClientReleaseNightDeadAtMorning()
        {
            ClearRemoteNightState();
            if (!LocalDreamRunning())
                ReleaseLocalNightDeath(worldAuthority: false);
        }

        private static void ClearRemoteNightState()
        {
            _remoteDeathPositions.Clear();
            _remotePermadeathEligible.Clear();
            RemoteNightDeathCount = 0;
            _nightParticipantIds.Clear();
            _nightParticipantsCaptured = false;
        }

        /// <summary>
        /// Leave night-death spectate and stand the local player at home. The host runs
        /// vanilla <c>transportToHome</c> (it owns the world); a client only moves itself.
        /// No-op when the local peer is not night-dead.
        /// </summary>
        private static void ReleaseLocalNightDeath(bool worldAuthority)
        {
            var spec = SpectatorModeController.Instance;
            bool spectating = spec != null && spec.IsSpectating;
            if (!LocalNightDeath && !spectating)
                return;

            if (spectating)
                spec.ExitAndRespawn(restorePosition: false);
            // Clear before transporting: the transportToHome suppress patch keys off LocalNightDeath.
            // Remote bookkeeping was already dropped by ClearRemoteNightState.
            ResetLocal();

            Player player = Player.Instance;
            if (player == null)
                return;

            player.invulnerable = false;
            player.noClipMode = false;
            try
            {
                if (worldAuthority)
                {
                    var method = AccessTools.Method(typeof(Player), "transportToHome");
                    if (method != null)
                        method.Invoke(player, null);
                }
                else
                {
                    TeleportClientHome(player);
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[Death] morning release transport home failed: " + ex.Message);
            }

            ModLog.Event(LogCat.Death, "Night-dead peer released at morning — sent home");
        }

        /// <summary>
        /// Vanilla transportToHome without its world mutations (return characters to
        /// spawn, clear traps): those belong to the host on a connected client.
        /// </summary>
        private static void TeleportClientHome(Player player)
        {
            Location home = null;
            if (player.experienceMachine != null)
                home = player.experienceMachine.transform.GetLocation();
            if (home == null || home.playerSpawn == null)
            {
                var wg = Singleton<WorldGenerator>.Instance;
                if (wg != null && wg.locations != null)
                {
                    for (int i = 0; i < wg.locations.Count; i++)
                    {
                        Location loc = wg.locations[i];
                        if (loc != null && loc.playerBase && loc.playerSpawn != null)
                        {
                            home = loc;
                            break;
                        }
                    }
                }
            }
            if (home == null || home.playerSpawn == null)
            {
                ModRuntime.Log?.LogWarning("[Death] morning release: no home location — left in place");
                return;
            }
            player.teleportTo(home.playerSpawn.transform.position, Quaternion.Euler(90f, 0f, 0f));
        }
    }
}
