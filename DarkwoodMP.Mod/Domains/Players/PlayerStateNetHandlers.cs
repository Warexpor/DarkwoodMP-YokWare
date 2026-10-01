using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Player pose / movement / trap flags from PlayerState.</summary>
    internal sealed class PlayerStateNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal PlayerStateNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandlePlayerState(PlayerStateMessage state)
        {
            int playerId = _net.CurrentReceivePlayerId; // For host: which client sent state
            if (playerId <= 0) return;

            // The host validates the receiving peer; clients key forwarded state
            // by the original player id carried in the trusted host relay.
            int sequenceSender = _net.Role == NetworkRole.Host
                ? playerId
                : (state.PlayerId > 0 ? state.PlayerId : playerId);
            if (!_net.AcceptSnapshotSequence(
                _net.LastPlayerStateSequence, sequenceSender, state.Sequence, "PlayerState"))
                return;

            if (_net.Role == NetworkRole.Host)
            {
                // First in-world state from a joiner → dump deferred bulk (not during their LoadScene)
                _net.TryFlushLateJoinBulkForPeer(playerId);

                // Host: update position manager per-player
                PlayerPositionManager.UpdateRemotePlayer(playerId,
                    new Vector3(state.PosX, state.PosY, state.PosZ),
                    state.TorsoFacingY);
                if (Patches.MorningHideoutHold.HasPending)
                    Patches.MorningHideoutHold.Observe(playerId, new Vector3(state.PosX, state.PosY, state.PosZ));

                if (playerId > 0)
                {
                    var hostSt = _net.GetOrCreateState(playerId);
                    hostSt.InBearTrap = state.InBearTrap;
                    hostSt.BearTrapPos = new Vector3(state.PosX, state.PosY, state.PosZ);
                    hostSt.TrapNetId = state.InBearTrap ? state.TrapNetId : 0;
                    hostSt.HasLightProtection = state.HasLightProtection;
                    hostSt.HasNightShadows = state.HasNightShadows;
                    if (state.InBearTrap)
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Trap] host: player {playerId} trapped id={hostSt.TrapNetId} at {hostSt.BearTrapPos}");

                    // Relay to the other clients first: the host has no proxy for this peer while it
                    // loads a location, but the peers that do must keep seeing it.
                    // PlayerId is the transport peer (authoritative), never the embedded one.
                    state.PlayerId = playerId;
                    _net.BroadcastHot(NetMessageType.PlayerState, w => state.Serialize(w),
                        excludePlayerId: playerId);

                    _net.EnsureRemoteProxy(playerId);
                    RemotePlayerProxy proxy = _net.GetProxy(playerId);
                    if (proxy == null) return;

                    // Revive only after real respawn. Night-dead peers still send PlayerState
                    // (get-up clips while spectating) which previously re-alive'd the proxy →
                    // dogs re-aggro'd a "zombie" corpse and damage felt completely broken.
                    if (state.TorsoClip != "Death1" && state.TorsoClip != "Death2"
                        && !DeathStateTracker.IsRemoteNightDead(playerId))
                    {
                        CharBase reviveCB = proxy.CachedCharBase;
                        if (reviveCB != null && !reviveCB.alive)
                        {
                            reviveCB.alive = true;
                            reviveCB.Health = reviveCB.maxHealth;
                            var reviveCols = proxy.CachedColliders;
                            if (reviveCols != null)
                            {
                                for (int ci = 0; ci < reviveCols.Length; ci++)
                                {
                                    if (reviveCols[ci] != null)
                                        reviveCols[ci].enabled = true;
                                }
                            }
                            var animComp = proxy.GetComponent<Players.SecondPlayerAnimController>();
                            if (animComp != null)
                                animComp.ResetDeathState();
                            var rb = proxy.GetComponent<Rigidbody>();
                            if (rb != null)
                                rb.position = new Vector3(state.PosX, state.PosY, state.PosZ);
                            ModRuntime.LegacyInfo($"[Death] Remote proxy revived for player {playerId}");
                        }
                    }
                    else if (DeathStateTracker.IsRemoteNightDead(playerId))
                    {
                        CharBase deadCB = proxy.CachedCharBase;
                        if (deadCB != null && deadCB.alive)
                        {
                            deadCB.alive = false;
                            deadCB.Health = 0f;
                            var deadCols = proxy.CachedColliders;
                            if (deadCols != null)
                            {
                                for (int ci = 0; ci < deadCols.Length; ci++)
                                {
                                    if (deadCols[ci] != null)
                                        deadCols[ci].enabled = false;
                                }
                            }
                        }
                    }

                    var netState = new PlayerStateNet
                    {
                        Position = new Vector3(state.PosX, state.PosY, state.PosZ),
                        Locomotion = (SecondPlayerAnimController.LocomotionState)state.LocomotionState,
                        FlipX = state.FlipX,
                        LegFacingY = state.LegFacingY,
                        ReverseLegs = state.ReverseLegs,
                        TorsoFacingY = state.TorsoFacingY,
                        TorsoClip = state.TorsoClip,
                        LegsClip = state.LegsClip,
                        CurrentFrame = state.CurrentFrame
                    };

                    proxy.RemoteRunning = state.Running;
                    proxy.RemoteLocomotion = (SecondPlayerAnimController.LocomotionState)state.LocomotionState;
                    proxy.ApplyNetworkState(netState);
                    _net.PlayerHeldLightApplyHandlers.HandleRemoteContinuousLights(state, playerId);

                    // DISABLED on join path: removeAfterNightEffect() is a full-screen native
                    // morning/event sequence. A joining client's AfterNightActive=false packet
                    // was firing it on the HOST mid-session ("unique event" + hitch).
                    // Live hideout leave sync can return later with an explicit reliable message.
                }
                return;
            }

            // Ignore host or forwarded state while the client is loading a scene.
            // or title (EnsureRemoteProxy was spamming 500+ "Player is inactive" / frame).
            if (!LanNetworkManager.CanSpawnRemoteProxies())
                return;

            {
                int remotePlayerId = state.PlayerId > 0
                    ? state.PlayerId
                    : (_net.CurrentReceivePlayerId > 0 ? _net.CurrentReceivePlayerId : 1);
                PlayerPositionManager.UpdateRemotePlayer(remotePlayerId,
                    new Vector3(state.PosX, state.PosY, state.PosZ),
                    state.TorsoFacingY);
                _net.EnsureRemoteProxy(remotePlayerId);
                RemotePlayerProxy proxy = _net.GetProxy(remotePlayerId);

                var cliSt = _net.GetOrCreateState(remotePlayerId);
                cliSt.InBearTrap = state.InBearTrap;
                cliSt.BearTrapPos = new Vector3(state.PosX, state.PosY, state.PosZ);
                cliSt.TrapNetId = state.InBearTrap ? state.TrapNetId : 0;
                cliSt.HasLightProtection = state.HasLightProtection;
                cliSt.HasNightShadows = state.HasNightShadows;
                if (state.InBearTrap)
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[Trap] client: player {remotePlayerId} trapped id={cliSt.TrapNetId} at {cliSt.BearTrapPos}");

                if (proxy == null) return;

                // Mirror host: do not revive while peer is still night-dead.
                CharBase reviveCB = proxy.CachedCharBase;
                if (reviveCB != null && !reviveCB.alive
                    && state.TorsoClip != "Death1" && state.TorsoClip != "Death2"
                    && !DeathStateTracker.IsRemoteNightDead(remotePlayerId))
                {
                    reviveCB.alive = true;
                    reviveCB.Health = reviveCB.maxHealth;
                    var reviveCols = proxy.CachedColliders;
                    if (reviveCols != null)
                    {
                        for (int ci = 0; ci < reviveCols.Length; ci++)
                        {
                            if (reviveCols[ci] != null)
                                reviveCols[ci].enabled = true;
                        }
                    }
                    var animComp = proxy.GetComponent<Players.SecondPlayerAnimController>();
                    if (animComp != null)
                        animComp.ResetDeathState();
                    var rb = proxy.GetComponent<Rigidbody>();
                    if (rb != null)
                        rb.position = new Vector3(state.PosX, state.PosY, state.PosZ);
                    ModRuntime.LegacyInfo($"[Death] Remote proxy revived for player {remotePlayerId}");
                }
                else if (DeathStateTracker.IsRemoteNightDead(remotePlayerId) && reviveCB != null && reviveCB.alive)
                {
                    reviveCB.alive = false;
                    reviveCB.Health = 0f;
                    var deadCols = proxy.CachedColliders;
                    if (deadCols != null)
                    {
                        for (int ci = 0; ci < deadCols.Length; ci++)
                        {
                            if (deadCols[ci] != null)
                                deadCols[ci].enabled = false;
                        }
                    }
                }

                proxy.RemoteRunning = state.Running;
                proxy.RemoteLocomotion = (SecondPlayerAnimController.LocomotionState)state.LocomotionState;

                var remoteState = new PlayerStateNet
                {
                    Position = new Vector3(state.PosX, state.PosY, state.PosZ),
                    Locomotion = (SecondPlayerAnimController.LocomotionState)state.LocomotionState,
                    FlipX = state.FlipX,
                    LegFacingY = state.LegFacingY,
                    ReverseLegs = state.ReverseLegs,
                    TorsoFacingY = state.TorsoFacingY,
                    TorsoClip = state.TorsoClip,
                    LegsClip = state.LegsClip,
                    CurrentFrame = state.CurrentFrame
                };

                proxy.ApplyNetworkState(remoteState);
                _net.PlayerHeldLightApplyHandlers.HandleRemoteContinuousLights(state, remotePlayerId);
            }
        }
    }
}
