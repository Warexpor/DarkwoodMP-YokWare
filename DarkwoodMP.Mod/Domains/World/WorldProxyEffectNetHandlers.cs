using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Remote proxy footstep / effect-sync / sound / scare handlers.</summary>
    internal sealed class WorldProxyEffectNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal WorldProxyEffectNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleProxyFootstep(int playerId, bool running)
        {
            if (!_net.RemoteProxies.TryGetValue(playerId, out var proxy)) return;
            Transform proxyT = proxy.transform;
            float range = running ? 350f : 150f;
            Character.alertInArea(proxyT.position, range, false, 1f);
            PlayProxyFootstepSound(proxy, running);
        }

        internal void SendPlayerEffects()
        {
            Player local = Player.Instance;
            if (local == null) return;

            CharBase localCb = local;
            var msg = new PlayerEffectSyncMessage
            {
                HasShadowWard = local.effects != null
                    && local.effects.hasEffectType(CharacterEffectType.shadowWard),
                HasForestSpiritWard = local.effects != null
                    && local.effects.hasEffectType(CharacterEffectType.forestSpiritWard),
                FriendOfTheForest = local.skills != null && local.skills.FriendOfTheForest,
                EnemyOfTheForest = local.skills != null && local.skills.EnemyOfTheForest,
                Invisible = local.invisible,
                IgnoreMe = local.ignoreMe,
                Poisoned = localCb != null && localCb.poisoned,
                Bleeding = localCb != null && localCb.bleeding
            };
            _net.Broadcast(NetMessageType.PlayerEffectSync, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void HandlePlayerEffectSync(PlayerEffectSyncMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) return;

            proxy.RemoteHasShadowWard = msg.HasShadowWard;
            proxy.RemoteHasForestSpiritWard = msg.HasForestSpiritWard;
            proxy.RemoteHasFriendOfTheForest = msg.FriendOfTheForest;
            proxy.RemoteHasEnemyOfTheForest = msg.EnemyOfTheForest;
            proxy.RemotePoisoned = msg.Poisoned;
            proxy.RemoteBleeding = msg.Bleeding;

            CharBase cb = proxy.CachedCharBase;
            if (cb != null)
            {
                cb.invisible = msg.Invisible;
                cb.ignoreMe = msg.IgnoreMe;
                // Visual flags only; DoT stays local on the owning player.
                cb.poisoned = msg.Poisoned;
                cb.bleeding = msg.Bleeding;
            }
        }

        /// <summary>
        /// Plays a 3D-positioned footstep sound at the proxy's transform.
        /// Player footstep AudioItems are authored 2D for local steps. Force spatialBlend
        /// and linear rolloff so peers do not sound like the listener's own feet, while distance
        /// fades via AudioSource.maxDistance instead of a hard IsNearListener cull.
        /// Out of hear range, do not play at all. This avoids allocating AudioObjects
        /// for distant footsteps.
        /// Gate matches maxDistance / AudioSuppression (DefaultMaxSpatialDistance).
        /// </summary>
        internal static void PlayProxyFootstepSound(RemotePlayerProxy proxy, bool running)
        {
            Transform proxyT = proxy.transform;
            if (proxyT == null) return;
            if (!LocalAudioService.IsNearListenerPeerBand(
                    proxyT.position, LocalAudioService.DefaultMaxSpatialDistance))
                return;

            Player local = Player.Instance;
            if (local == null) return;

            CharacterSounds cs = local.GetComponent<CharacterSounds>();
            if (cs == null) return;

            CharBase proxyCB = proxy.CachedCharBase;
            if (proxyCB != null)
                proxyCB.checkGround();
            GroundType gt = proxyCB != null ? proxyCB.groundType : GroundType.grass;

            string soundID = null;
            switch (gt)
            {
                case GroundType.grass: soundID = cs.footstepGrass; break;
                case GroundType.wood: soundID = cs.footstepWood; break;
                case GroundType.tiles: soundID = cs.footstepTiles; break;
                case GroundType.bridge: soundID = cs.footstepBridge; break;
                case GroundType.rug: soundID = cs.footstepCarpet; break;
                case GroundType.water: soundID = cs.footstepWater; break;
                case GroundType.infection: soundID = cs.footstepInfection; break;
                default: soundID = cs.footstepGrass; break;
            }

            float volumeModifier = running ? 1.3f : 0.7f;
            float vol = cs.footstepVolume * volumeModifier;

            if (!string.IsNullOrEmpty(soundID))
                ForceSpatialProxyOneShot(AudioController.Play(soundID, proxyT, vol), soundID);

            ForceSpatialProxyOneShot(AudioController.Play("walk_clothes_noises", proxyT, vol), "walk_clothes_noises");

            if (UnityEngine.Random.Range(0f, 1f) > 1f - cs.footHitGroundSoundChance)
            {
                string addSound = gt == GroundType.wood ? "footsteps_wood_add" : "footstep_branches_add";
                ForceSpatialProxyOneShot(AudioController.Play(addSound, proxyT, 1f), addSound);
            }
        }

        /// <summary>Force 3D rolloff on player-authored (often 2D) clips played at a proxy.</summary>
        internal static void ForceSpatialProxyOneShot(AudioObject audioObj, string soundId)
        {
            if (audioObj == null || audioObj.primaryAudioSource == null) return;
            audioObj.primaryAudioSource.spatialBlend = 1f;
            audioObj.primaryAudioSource.rolloffMode = AudioRolloffMode.Linear;
            AudioItem item = !string.IsNullOrEmpty(soundId) ? AudioController.GetAudioItem(soundId) : null;
            float itemMin = (item != null && item.overrideAudioSourceSettings)
                ? item.audioSource_MinDistance : LocalAudioService.DefaultMinSpatialDistance;
            float itemMax = (item != null && item.overrideAudioSourceSettings)
                ? item.audioSource_MaxDistance : LocalAudioService.DefaultMaxSpatialDistance;
            // Closer near-field than guns so steps attenuate across a room; silence at
            // DefaultMaxSpatialDistance (same as Play gate + AudioSuppression).
            audioObj.primaryAudioSource.minDistance = Mathf.Clamp(itemMin, 8f, 40f);
            audioObj.primaryAudioSource.maxDistance = Mathf.Clamp(
                itemMax, 80f, LocalAudioService.DefaultMaxSpatialDistance);
        }

        internal void HandlePlayerSound(PlayerSoundMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) return;

            Transform proxyT = proxy.transform;
            Vector3 proxyPos = proxyT.position;
            float range = msg.Range;

            // Primary: Physics.OverlapSphere-based alert (finds entities with active colliders)
            Character.alertInArea(proxyPos, range, msg.DangerousSound, msg.Volume, msg.Gunshot);

            // Fallback: directly alert all tracked characters within range, even if their
            // colliders or chunks are briefly inactive when the message arrives.
            int nAll = CharacterTracker.CopyAll(out Character[] all);
            for (int i = 0; i < nAll; i++)
            {
                Character c = all[i];
                if (c == null) continue;
                if (c.deaf || !c.alive) continue;
                if (c.name.Contains("Player") || c.name.Contains("RemotePlayer"))
                    continue;

                float dist = Vector3.Distance(c.transform.position, proxyPos);
                if (dist <= range)
                {
                    if (!c.gameObject.activeSelf)
                        c.gameObject.SetActive(true);
                    if (!c.enabled)
                        c.enabled = true;

                    c.heardSound(proxyPos, range, msg.DangerousSound, msg.Volume, msg.Gunshot);
                }
            }
        }

        internal void HandlePlayerScare(PlayerScareMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) return;

            Transform proxyT = proxy.transform;
            Character.scareInArea(proxyT.position, msg.Range);
        }

    }
}
