using DWMPHorde;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Remote player death / night-morning / final-dreamscene death message handlers.
    /// </summary>
    internal sealed class CombatDeathStateNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal CombatDeathStateNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandlePlayerDied(PlayerDiedMessage msg)
        {
            Vector3 deathPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            bool isNight = msg.IsNight;
            int playerId = _net.CurrentReceivePlayerId;
            if (playerId <= 0)
            {
                ModRuntime.Log?.LogWarning("[Death] PlayerDied with invalid playerId — ignored");
                return;
            }

            ModRuntime.LegacyInfo($"[Death] Remote player {playerId} died at {deathPos}, isNight={isNight}");

            RemotePlayerProxy diedProxy = _net.GetProxy(playerId);
            if (diedProxy != null)
            {
                CharBase cb = diedProxy.CachedCharBase;
                if (cb != null)
                {
                    cb.alive = false;
                    cb.Health = 0f;
                }
                var deathCols = diedProxy.CachedColliders;
                if (deathCols != null)
                {
                    for (int ci = 0; ci < deathCols.Length; ci++)
                    {
                        if (deathCols[ci] != null)
                            deathCols[ci].enabled = false;
                    }
                }

                // Apply the death pose immediately; do not wait for the next PlayerState tick.
                var anim = diedProxy.GetComponent<Players.SecondPlayerAnimController>();
                if (anim != null)
                    anim.PlayDeathClip("Death1");
            }

            if (isNight)
            {
                DeathStateTracker.OnRemoteNightDeath(playerId, deathPos);

                if (_net.Role == NetworkRole.Host)
                {
                    if (!DeathStateTracker.TryResolveNightMorning("remote PlayerDied"))
                    {
                        ModRuntime.LegacyInfo(
                            $"[Death] Remote night death — waiting " +
                            $"(localDead={DeathStateTracker.LocalNightDeath} " +
                            $"remotes={DeathStateTracker.RemoteNightDeathCount}/{DeathStateTracker.TotalRemoteCount})");
                    }
                }
                else
                {
                    // If we are spectating the player who just died, retarget.
                    var spec = Spectator.SpectatorModeController.Instance;
                    if (spec != null && spec.IsSpectating)
                        ModRuntime.LegacyInfo($"[Death] Client saw remote night death of P{playerId}");
                }
                return;
            }

            DeathStateTracker.OnRemoteDayDeath(playerId);

            if (_net.Role == NetworkRole.Host && Singleton<SaveManager>.Instance != null)
                Singleton<SaveManager>.Instance.Save(doJson: true);
        }

        internal void HandleNightDeathState(NightDeathStateMessage msg)
        {
            if (!msg.AllDeadTrigger)
                return;

            int hostId = _net.HostPlayerId > 0 ? _net.HostPlayerId : 1;

            // Host is sole AllDeadTrigger emitter (DeathStateTracker.TryResolveNightMorning).
            if (_net.Role == NetworkRole.Host)
            {
                if (_net.CurrentReceivePlayerId > 0)
                    ModRuntime.LegacyInfo(
                        $"[Death] Rejected peer AllDeadTrigger from p{_net.CurrentReceivePlayerId}");
                return;
            }

            // Client: only accept morning resolve from host.
            if (_net.CurrentReceivePlayerId != hostId)
            {
                ModRuntime.LegacyInfo(
                    $"[Death] Rejected AllDeadTrigger from non-host p{_net.CurrentReceivePlayerId}");
                return;
            }

            ModRuntime.LegacyInfo("[Death] All dead at night — exiting spectator for morning");

            if (Spectator.SpectatorModeController.Instance != null)
                Spectator.SpectatorModeController.Instance.ExitAndRespawn();

            DeathStateTracker.Reset();
        }

        internal void HandleFinalDreamsceneDeath(FinalDreamsceneDeathMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            ModRuntime.LegacyInfo($"[FinalDreamscene] Received remote death notification for player {playerId}");

            RemotePlayerProxy diedProxy = _net.GetProxy(playerId);
            if (diedProxy != null)
            {
                CharBase cb = diedProxy.CachedCharBase;
                if (cb != null)
                {
                    cb.alive = false;
                    cb.Health = 0f;
                }
                var deathCols = diedProxy.CachedColliders;
                if (deathCols != null)
                {
                    for (int ci = 0; ci < deathCols.Length; ci++)
                    {
                        if (deathCols[ci] != null)
                            deathCols[ci].enabled = false;
                    }
                }
                _net.GetOrCreateState(playerId).IsDeadInDream = true;
            }

            Sync.FinalDreamsceneManager.OnRemoteDeathInDream(playerId);
        }
    }
}
