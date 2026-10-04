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

        /// <summary>Controller.day at the host's last morning edge (startDay); -1 when none.</summary>
        private static int _hostMorningEdgeDay = -1; // reset-in: ResetSession

        /// <summary>
        /// Host: this day's morning edge already ran and the host's own clock is outside the
        /// night-death window. A client "night" death now comes from a lagging client clock
        /// and must not be recorded as a night death after the release.
        /// </summary>
        public static bool HostMorningAlreadyReleased()
        {
            if (_hostMorningEdgeDay < 0) return false;
            Controller ctrl = Singleton<Controller>.Instance;
            return ctrl != null && ctrl.day == _hostMorningEdgeDay && !IsNightDeathWindow();
        }

        /// <summary>
        /// Host, from <c>Controller.startDay</c>: somebody survived to dawn, so every
        /// night-dead peer (host included) is released now, the way vanilla's death-time
        /// skipDay puts a lone player back at home. Idempotent; when the whole party is
        /// dead <see cref="TryResolveNightMorning"/> owns the release instead.
        /// </summary>
        public static void HostReleaseNightDeadAtMorning(string reason)
        {
            // Recorded before the guards: an all-dead resolve (skipDay inside
            // TryResolveNightMorning) is a morning edge too.
            Controller edgeCtrl = Singleton<Controller>.Instance;
            if (edgeCtrl != null)
                _hostMorningEdgeDay = edgeCtrl.day;

            if (_resolvingMorning || PartyWipeDeclared) return;
            var net = ModRuntime.Network;
            // Role alone: the host's own release must still run after its last peer left.
            if (net == null || net.Role != NetworkRole.Host)
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
        internal static void TeleportClientHome(Player player)
        {
            Location home = HomeLocation(player);
            if (home == null)
            {
                ModRuntime.Log?.LogWarning("[Death] transport home: no home location — left in place");
                return;
            }
            player.teleportTo(home.playerSpawn.transform.position, Quaternion.Euler(90f, 0f, 0f));
        }

        /// <summary>The hideout a player respawns in (vanilla: its oven's location), else any hideout.</summary>
        internal static Location HomeLocation(Player player)
        {
            Location home = null;
            if (player != null && player.experienceMachine != null)
                home = player.experienceMachine.transform.GetLocation();
            if (home == null || home.playerSpawn == null)
            {
                home = null;
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
            return home;
        }

        /// <summary>
        /// Host, for a peer that respawns at home after a day death: vanilla transportToHome's
        /// world half (characters around home back to their spawn points, infection and armed
        /// traps within 100 of the spawn cleared), run on the world everyone shares. Vanilla
        /// destroys infection without its own sync, so peers are told here.
        /// </summary>
        internal static void HostClearHomeForRespawn()
        {
            if (!Core.worldGenFinished() || !Core.randomGeneration)
                return;
            Player host = Player.Instance;
            Location home = HomeLocation(host);
            if (host == null || home == null)
                return;
            Vector3 spawn = home.playerSpawn.transform.position;
            home.returnCharactersAroundMeToSpawnPoint();
            var net = ModRuntime.Network;
            Collider[] buf = WorldQueryHelper.SharedOverlapBuf;
            int n = Physics.OverlapSphereNonAlloc(Core.getYPos(spawn, PosType.low1), 100f, buf, 2);
            for (int i = 0; i < n; i++)
            {
                Infection inf = buf[i] != null ? buf[i].GetComponent<Infection>() : null;
                if (inf == null || net == null)
                    continue;
                Vector3 p = inf.transform.position;
                net.SendWorldObjectRemoved(new WorldObjectRemovedMessage { PosX = p.x, PosY = p.y, PosZ = p.z, ObjectName = "infection_splat" });
            }
            host.removeDangerousStuffToPlayerAround(spawn, 100f);
            ModLog.Event(LogCat.Death, "Peer respawned home — home area cleared on the host");
        }
    }
}
