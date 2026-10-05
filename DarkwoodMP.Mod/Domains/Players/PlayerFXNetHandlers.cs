using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Player FX / anim / dropped-item handlers composed for 0.8.</summary>
    internal sealed partial class PlayerFXNetHandlers
    {
        private readonly LanNetworkManager _net;

        /// <summary>Anim library arrived before proxy existed (late join / race).</summary>
        private readonly Dictionary<int, string> _pendingAnimLibraries =
            new Dictionary<int, string>();

        internal PlayerFXNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingAnimLibrary(int playerId)
        {
            if (playerId > 0)
                _pendingAnimLibraries.Remove(playerId);
        }

        internal void ClearAllPendingAnimLibraries()
        {
            _pendingAnimLibraries.Clear();
        }

        /// <summary>Apply stashed library after <see cref="WorldProxyLifecycleNetHandlers.EnsureRemoteProxy"/>.</summary>
        internal void FlushPendingAnimLibrary(int playerId)
        {
            if (playerId <= 0) return;
            if (!_pendingAnimLibraries.TryGetValue(playerId, out string libName))
                return;
            _pendingAnimLibraries.Remove(playerId);
            if (string.IsNullOrEmpty(libName)) return;
            int prevRecv = _net.CurrentReceivePlayerId;
            _net.AssignCurrentReceivePlayerId(playerId);
            try
            {
                HandlePlayerAnimLibrary(new PlayerAnimLibraryMessage { LibraryName = libName });
            }
            finally
            {
                _net.AssignCurrentReceivePlayerId(prevRecv);
            }
            ModRuntime.Log?.LogDebug("[AnimLib] applied pending library for p" + playerId + ": " + libName);
        }

        internal void HandlePlayerAnimation(PlayerAnimationMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) return;
            var animComp = proxy.GetComponent<SecondPlayerAnimController>();
            if (animComp == null) return;

            EntitySyncLog.Anim("player:rx",
                "[PlayerAnim] RX p" + playerId
                + (string.IsNullOrEmpty(msg.TorsoClip) ? "" : " torso=" + msg.TorsoClip)
                + (string.IsNullOrEmpty(msg.LegsClip) ? "" : " legs=" + msg.LegsClip), 0.4f);

            var prev = Sync.WorldPhysicsSyncService._suppressBroadcast;
            Sync.WorldPhysicsSyncService._suppressBroadcast = true;
            try
            {
                if (!string.IsNullOrEmpty(msg.TorsoClip))
                {
                    try
                    {
                        animComp.PlayTorso(msg.TorsoClip);
                    }
                    catch (System.Exception ex)
                    {
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.Log?.LogWarning("[Network] swallowed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                // Don't restart walk legs while torso is vault/beartrap/etc. (vanilla hides legs).
                bool hideLegs = !string.IsNullOrEmpty(msg.TorsoClip)
                    && SecondPlayerAnimController.ShouldHideLegsForTorso(msg.TorsoClip);
                if (!hideLegs && !string.IsNullOrEmpty(msg.LegsClip))
                {
                    try
                    {
                        animComp.PlayLegs(msg.LegsClip);
                    }
                    catch (System.Exception ex)
                    {
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.Log?.LogWarning("[Network] swallowed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                Sync.WorldPhysicsSyncService._suppressBroadcast = prev;
            }
        }

        internal void HandlePlayerAnimLibrary(PlayerAnimLibraryMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            if (string.IsNullOrEmpty(msg.LibraryName)) return;

            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null)
            {
                // Early handshake / pre-proxy: stash like PendingPlayerLights.
                if (playerId > 0)
                    _pendingAnimLibraries[playerId] = msg.LibraryName;
                return;
            }

            tk2dSpriteAnimator anim = proxy.GetComponent<tk2dSpriteAnimator>();
            if (anim == null) return;
            // Skip no-op applies; Resources.Load for every duplicate packet caused hitches.
            if (anim.Library != null
                && string.Equals(anim.Library.name, msg.LibraryName, System.StringComparison.Ordinal))
                return;

            var lib = Resources.Load(msg.LibraryName, typeof(tk2dSpriteAnimation)) as tk2dSpriteAnimation;
            if (lib == null)
            {
                ModRuntime.Log?.LogWarning("[AnimLib] library not found: " + msg.LibraryName);
                return;
            }

            var prev = Sync.WorldPhysicsSyncService._suppressBroadcast;
            Sync.WorldPhysicsSyncService._suppressBroadcast = true;
            try
            {
                HarmonyLib.Traverse.Create(anim).Property("Library").SetValue(lib);
                ModRuntime.Log?.LogDebug("[AnimLib] applied library: " + msg.LibraryName);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogError("[AnimLib] failed to set library: " + ex);
            }
            finally
            {
                Sync.WorldPhysicsSyncService._suppressBroadcast = prev;
            }
        }

        /// <summary>
        /// Dispatches a player-specific message that was forwarded from another
        /// client. _net.CurrentReceivePlayerId has already been set to the original
        /// sender's PlayerId before this is called.
        /// </summary>
        internal void DispatchRemotePlayerForward(NetMessageType innerType, byte[] innerPayload)
        {
            switch (innerType)
            {
                case NetMessageType.PlayerLightState:
                    _net.PlayerLightFxApplyHandlers.HandlePlayerLightState(
                        PlayerLightStateMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerAudio:
                    _net.WorldFxHandlers.HandlePlayerAudio(
                        PlayerAudioMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerFiredWeapon:
                    HandlePlayerFiredWeapon(PlayerFiredWeaponMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerAnimation:
                    HandlePlayerAnimation(PlayerAnimationMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerBurning:
                    _net.CombatFxGasBurnHandlers.HandlePlayerBurning(
                        PlayerBurningMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerDied:
                    _net.CombatDeathStateHandlers.HandlePlayerDied(
                        PlayerDiedMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerEffectSync:
                    _net.WorldProxyEffectHandlers.HandlePlayerEffectSync(
                        PlayerEffectSyncMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.PlayerAnimLibrary:
                    HandlePlayerAnimLibrary(PlayerAnimLibraryMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.ThrowableSpawn:
                    _net.CombatFxImpactHandlers.HandleThrowableSpawn(
                        ThrowableSpawnMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.DreamEnded:
                    _net.DreamHandlers.HandleDreamEnded(DreamEndedMessage.Deserialize(new NetReader(innerPayload)));
                    break;
                case NetMessageType.FinalDreamsceneDeath:
                    _net.CombatDeathStateHandlers.HandleFinalDreamsceneDeath(
                        FinalDreamsceneDeathMessage.Deserialize(new NetReader(innerPayload)));
                    break;
            }
        }

        internal void HandleBulletImpact(BulletImpactMessage msg)
        {
            // Any prefab name would be instantiated on every peer: allow only the impact /
            // blood FX our senders emit, and never relay anything else.
            if (!ImpactFxPolicy.IsAllowedBulletImpact(msg.PoolName, msg.PrefabName)
                || !CombatAuthorityPolicy.IsFinitePosition(msg.PosX, msg.PosY, msg.PosZ))
            {
                _net.SuppressRelay();
                ModLog.WarnRate(LogCat.Combat, "impact-reject:" + _net.CurrentReceivePlayerId,
                    "[BulletFX] rejected BulletImpact '" + msg.PrefabName + "' pool='" + msg.PoolName
                    + "' from p" + _net.CurrentReceivePlayerId);
                return;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[BulletFX] HandleBulletImpact: {msg.PrefabName} pool={msg.PoolName} pos={pos}");

            bool isBlood = msg.PrefabName.IndexOf("Bloodsplat", System.StringComparison.OrdinalIgnoreCase) >= 0
                || msg.PrefabName.IndexOf("Shotsplat", System.StringComparison.OrdinalIgnoreCase) >= 0;

            // Wrap in ApplyingFromNetwork to prevent HitscanBloodPatch and similar
            // patches from re-forwarding this blood back to the sender.
            bool prevNet = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                if (string.IsNullOrEmpty(msg.PoolName))
                {
                    // worldSpace: true; blood must sit at the absolute world position (parent null).
                    Core.AddPrefab(msg.PrefabName, pos, rot, null, worldSpace: true);
                }
                else
                    Core.AddPooledPrefab(msg.PoolName, msg.PrefabName, pos, rot);
            }
            finally
            {
                TraverseHack.SetExplicitFlag(prevNet);
            }

            // Blood is visual-only; bullet_hit_1 is for walls / projectile impacts.
            if (!isBlood)
                AudioController.Play("bullet_hit_1", pos);
        }

        internal void HandlePlayerFiredWeapon(PlayerFiredWeaponMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) { ModRuntime.LegacyInfo($"[WeaponFire] handle: no proxy for player {playerId}"); return; }

            InvItem itemDef = null;
            try { itemDef = Singleton<ItemsDatabase>.Instance?.getItem(msg.ItemType, instantiate: false); }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[Network] getItem: " + ex.Message);
            }
            if (itemDef == null) { ModRuntime.LegacyInfo($"[WeaponFire] handle: item not found: {msg.ItemType}"); return; }
            if (!itemDef.isFirearm) { ModRuntime.LegacyInfo($"[WeaponFire] handle: not a firearm: {msg.ItemType}"); return; }

            Transform proxyT = proxy.transform;

            // Prefer fire-packet pose (serialized Pos + AimY). Proxy interp lags PlayerState
            // and used to place muzzle/flash/audio at the wrong body facing mid-strafe.
            Vector3 firePos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            if (firePos.sqrMagnitude < 1e-6f)
                firePos = proxyT.position;
            // Vanilla fireWeapon: Quaternion.Euler(90, transform.eulerAngles.y, 0) axes.
            Quaternion muzzleRot = Quaternion.Euler(90f, msg.AimY, 0f);
            Vector3 aimUp = muzzleRot * Vector3.up;
            Vector3 aimRight = muzzleRot * Vector3.right;
            Vector3 muzzlePos = firePos
                + aimUp * itemDef.muzzleOffset.y
                + aimRight * itemDef.muzzleOffset.x;

            ModRuntime.LegacyInfo($"[WeaponFire] handle: spawning muzzle for {msg.ItemType} count={msg.ProjectileCount} aimY={msg.AimY}");

            if (itemDef.muzzlePrefab != null)
            {
                string name = itemDef.muzzlePrefab.name;
                if (!string.IsNullOrEmpty(name))
                    Core.AddPooledPrefab("FX", name, muzzlePos, muzzleRot);
            }

            if (itemDef.muzzleParticles != null)
            {
                string name = itemDef.muzzleParticles.name;
                if (!string.IsNullOrEmpty(name))
                    Core.AddPooledPrefab("FX", name, muzzlePos, muzzleRot);
            }

            if (!itemDef.noMuzzleFlash)
            {
                Core.AddPrefab("FX/Muzzle/PistolFlash", firePos + aimUp, muzzleRot, null, worldSpace: true);
            }

            // Shot audio: local shooter plays parentless attackSound; peers need it here
            // (HandlePlayerFiredWeapon is VFX-only otherwise). ApplyingFromNetwork prevents
            // PlayerAudio re-forward loops.
            if (!string.IsNullOrEmpty(itemDef.attackSound))
            {
                Vector3 shotPos = firePos;
                if (LocalAudioService.IsNearListenerPeerBand(shotPos, LocalAudioService.AudibleRange(itemDef.attackSound)))
                {
                    bool prevNet = TraverseHack.GetExplicitFlag();
                    TraverseHack.SetExplicitFlag(true);
                    try
                    {
                        // Parented to the stand-in for its indoor reverb and the wall muffle;
                        // isInside is current only after checkGround.
                        WorldProxyEffectNetHandlers.RefreshStandInGround(proxy);
                        var audioObj = AudioController.Play(itemDef.attackSound, shotPos, proxyT, 1f);
                        if (audioObj != null && audioObj.primaryAudioSource != null)
                        {
                            audioObj.primaryAudioSource.spatialBlend = 1f;
                            audioObj.primaryAudioSource.minDistance =
                                Mathf.Max(audioObj.primaryAudioSource.minDistance, LocalAudioService.DefaultMinSpatialDistance);
                            audioObj.primaryAudioSource.maxDistance =
                                Mathf.Max(audioObj.primaryAudioSource.maxDistance, 100f);
                            audioObj.primaryAudioSource.rolloffMode = AudioRolloffMode.Linear;
                        }
                    }
                    finally { TraverseHack.SetExplicitFlag(prevNet); }
                }
            }

            // Friendly-fire damage is applied only via ProxyDamagePatch (real collider hits
            // on the remote proxy → DamagePlayer / FriendlyFire). Cone damage here used to
            // double-apply with that path and could hit every peer incorrectly.
        }

    }
}
