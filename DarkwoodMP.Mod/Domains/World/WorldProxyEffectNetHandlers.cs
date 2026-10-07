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
            // Vanilla Player footsteps alert nobody while invisible (chameleon / ninja), and a
            // walking step alerts nobody while aiming (Player FootHitGround: !aiming && !invisible).
            bool aiming = _net.RemotePlayers.TryGetValue(playerId, out RemotePlayerState st) && st.Aiming;
            CharBase cb = proxy.CachedCharBase;
            if ((cb == null || !cb.invisible) && (running || !aiming))
                Character.alertInArea(proxyT.position, range, false, 1f);
            PlayProxyFootstepSound(proxy, running, aiming);
        }

        /// <summary>
        /// Vanilla <c>Player.setInvisible</c> for a peer: going invisible makes everything attacking
        /// it stop, and the body shows at 30% alpha.
        /// </summary>
        private static void ApplyInvisible(RemotePlayerProxy proxy, bool invisible)
        {
            Transform proxyT = proxy.transform;
            if (invisible && ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Host)
            {
                Character[] all;
                int n = CharacterTracker.CopyAll(out all);
                for (int i = 0; i < n; i++)
                {
                    Character c = all[i];
                    if (c != null && (c.target == proxyT || c.superTarget == proxyT))
                        c.stopAttacking(proxyT);
                }
            }
            Color tint = new Color(1f, 1f, 1f, invisible ? 0.3f : 1f);
            tk2dBaseSprite torso = proxyT.GetComponent<tk2dBaseSprite>();
            if (torso != null)
                torso.color = tint;
            Transform legs = proxyT.Find("PlayerLegs");
            tk2dBaseSprite legsSprite = legs != null ? legs.GetComponent<tk2dBaseSprite>() : null;
            if (legsSprite != null)
                legsSprite.color = tint;
        }

        private int _lastEffectFlags = -1;
        private string _lastSkills;
        private bool _lastHasHome;
        private Vector3 _lastHome;

        internal static string LearnedSkillNames(Player local)
        {
            if (local.skills == null || local.skills.skills == null || local.skills.skills.Count == 0)
                return "";
            var sb = new System.Text.StringBuilder(128);
            for (int i = 0; i < local.skills.skills.Count; i++)
            {
                PlayerSkill sk = local.skills.skills[i];
                if (sk == null || sk.gameObject == null)
                    continue;
                if (sb.Length > 0)
                    sb.Append('|');
                sb.Append(sk.gameObject.name);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Every tick: a change (ward, ninja, forest skills, ignoreMe) goes out at once, since host
        /// AI and the night worm act on it; otherwise a keepalive every 2 s for new peers.
        /// </summary>
        internal void SendPlayerEffects(bool keepalive)
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
                Bleeding = localCb != null && localCb.bleeding,
                Burning = local.GetComponent<Burn>() != null,
                BurnSpecial = local.effects != null && local.effects.hasEffectType(CharacterEffectType.burnSpecial),
                InEpilogue = EpilogueNetHandlers.IsLocalInEpilogue(),
                InPrologue = Sync.PersonalPrologue.LocalInPrologue
            };
            float maxHp = local.maxHealth > 0f ? local.maxHealth : 1f;
            msg.HealthPct = (byte)Mathf.Clamp(Mathf.RoundToInt(local.health / maxHp * 100f), 0, 100);
            msg.DarknessPct = (byte)Mathf.Clamp(Mathf.RoundToInt(local.darknessCounter * 100f), 0, 100);
            string skills = LearnedSkillNames(local);
            bool skillsChanged = !string.Equals(skills, _lastSkills, System.StringComparison.Ordinal);
            ExperienceMachine home = local.experienceMachine;
            Vector3 homePos = home != null ? home.transform.position : Vector3.zero;
            msg.HasHome = home != null;
            msg.HomeX = homePos.x;
            msg.HomeY = homePos.y;
            msg.HomeZ = homePos.z;
            bool homeChanged = msg.HasHome != _lastHasHome || (homePos - _lastHome).sqrMagnitude > 0.01f;
            _lastHasHome = msg.HasHome;
            _lastHome = homePos;
            int flags = msg.Flags | (msg.Flags2 << 8) | (msg.HealthPct << 16) | (msg.DarknessPct << 24);
            if (!keepalive && flags == _lastEffectFlags && !skillsChanged && !homeChanged)
                return;
            _lastEffectFlags = flags;
            _lastSkills = skills;
            // The skill list rides only on a change and the keepalive, not on every health tick.
            msg.HasSkills = skillsChanged || keepalive;
            msg.Skills = skills;
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
            proxy.RemoteHomeOven = msg.HasHome ? new Vector3(msg.HomeX, msg.HomeY, msg.HomeZ) : (Vector3?)null;
            proxy.RemoteInEpilogue = msg.InEpilogue;
            proxy.RemoteInPrologue = msg.InPrologue;
            proxy.RemoteHealthPct = msg.HealthPct;
            proxy.RemoteDarknessPct = msg.DarknessPct;
            if (msg.HasSkills)
            {
                proxy.RemoteSkills.Clear();
                if (!string.IsNullOrEmpty(msg.Skills))
                {
                    string[] names = msg.Skills.Split('|');
                    for (int i = 0; i < names.Length; i++)
                        proxy.RemoteSkills.Add(names[i]);
                }
            }

            CharBase cb = proxy.CachedCharBase;
            if (cb != null)
            {
                if (msg.Invisible != cb.invisible)
                    ApplyInvisible(proxy, msg.Invisible);
                cb.invisible = msg.Invisible;
                cb.ignoreMe = msg.IgnoreMe;
                // Visual flags only; DoT stays local on the owning player.
                cb.poisoned = msg.Poisoned;
                cb.bleeding = msg.Bleeding;
            }
            // A joiner, or a missed burn message: converge on the owner's fire.
            CombatFxGasBurnNetHandlers.ApplyProxyBurn(proxy, playerId, msg.Burning, msg.BurnSpecial, 0f);
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
        internal static void PlayProxyFootstepSound(RemotePlayerProxy proxy, bool running, bool aiming)
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

            // Vanilla Player: playFootHitGround(1.5) running, (0.5) aiming, () walking.
            float volumeModifier = running ? 1.5f : (aiming ? 0.5f : 1f);
            float vol = cs.footstepVolume * volumeModifier;

            // Local playback of a remote peer's steps must not re-enter AudioController
            // forward patches (ForwardWorldObjectSound would echo feet back to the runner
            // → doubled footsteps on the client).
            // Use explicit flag (same pattern as NetworkApplyGuard) — ApplyingFromNetwork
            // getter ORs NetworkApplyGuard.IsActive and must not be used as prev/restore.
            bool prevNet = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                if (!string.IsNullOrEmpty(soundID))
                    PlayProxyOneShot(soundID, proxyT, vol);

                PlayProxyOneShot("walk_clothes_noises", proxyT, vol);

                if (UnityEngine.Random.Range(0f, 1f) > 1f - cs.footHitGroundSoundChance)
                {
                    string addSound = gt == GroundType.wood ? "footsteps_wood_add" : "footstep_branches_add";
                    PlayProxyOneShot(addSound, proxyT, volumeModifier);
                }
            }
            finally
            {
                TraverseHack.SetExplicitFlag(prevNet);
            }
        }

        /// <summary>
        /// The stand-in has no CharacterSounds tick: refresh its ground before a play parented to
        /// it, so AudioController reads the current CharBase.isInside (indoor reverb).
        /// </summary>
        internal static void RefreshStandInGround(RemotePlayerProxy proxy)
        {
            CharBase cb = proxy != null ? proxy.CachedCharBase : null;
            if (cb == null) return;
            try { cb.checkGround(); }
            catch { /* stand-in torn down mid-frame */ }
        }

        /// <summary>
        /// A peer's sound on its stand-in: fully 3D, linear out to the range the game gives this
        /// id (its prefab's when not overridden; the peer range for a 2D-authored id), the same
        /// range the PlayerAudio hear gate uses.
        /// </summary>
        internal static void ApplyStandInRolloff(AudioSource src, string soundId)
        {
            if (src == null) return;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            AudioItem item = !string.IsNullOrEmpty(soundId) ? AudioController.GetAudioItem(soundId) : null;
            float itemMin = (item != null && item.overrideAudioSourceSettings)
                ? item.audioSource_MinDistance : LocalAudioService.DefaultMinSpatialDistance;
            src.minDistance = Mathf.Max(itemMin, LocalAudioService.DefaultMinSpatialDistance);
            src.maxDistance = Mathf.Max(LocalAudioService.SpatialMaxDistance(soundId), 100f);
        }

        /// <summary>Force 3D rolloff on player-authored (often 2D) clips played at a proxy.</summary>
        internal static void ForceSpatialProxyOneShot(AudioSource src, string soundId)
        {
            if (src == null) return;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            AudioItem item = !string.IsNullOrEmpty(soundId) ? AudioController.GetAudioItem(soundId) : null;
            float itemMin = (item != null && item.overrideAudioSourceSettings)
                ? item.audioSource_MinDistance : LocalAudioService.DefaultMinSpatialDistance;
            float itemMax = (item != null && item.overrideAudioSourceSettings)
                ? item.audioSource_MaxDistance : LocalAudioService.DefaultMaxSpatialDistance;
            // Closer near-field than guns so steps attenuate across a room; silence at
            // DefaultMaxSpatialDistance (same as Play gate + AudioSuppression).
            src.minDistance = Mathf.Clamp(itemMin, 8f, 40f);
            src.maxDistance = Mathf.Clamp(itemMax, 80f, LocalAudioService.DefaultMaxSpatialDistance);
        }

        /// <summary>A peer's one-shot at its stand-in, 3D from its first moment (<see cref="PeerSpatialPlay"/>).</summary>
        private static void PlayProxyOneShot(string soundId, Transform at, float volume)
        {
            PeerSpatialPlay.Play(() => AudioController.Play(soundId, at, volume),
                src => ForceSpatialProxyOneShot(src, soundId));
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

            // Vanilla Character.alertInArea, as for the local player's own sounds. Characters culled
            // out of the world (inactive) do not hear, exactly as they would not for the host.
            Character.alertInArea(proxyPos, range, msg.DangerousSound, msg.Volume, msg.Gunshot);
        }

        internal void HandlePlayerScare(PlayerScareMessage msg)
        {
            if (_net.Role != NetworkRole.Host)
            {
                // Host relay of a peer's scary face: the effect only.
                if (msg.ScaryFace && _net.RemoteProxies.TryGetValue(msg.CasterId, out var caster) && caster != null)
                    Core.AddPrefab("FX/skills/scaryface_prefab", caster.transform.position, Quaternion.Euler(90f, 0f, 0f), null);
                return;
            }
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) return;

            Transform proxyT = proxy.transform;
            if (!msg.ScaryFace)
            {
                // The client's aim scare is always 350 (ClientAimScarePatch).
                Character.scareInArea(proxyT.position, Mathf.Min(msg.Range, 350f));
                return;
            }

            // Vanilla PlayerSkill.activate "scaryFace" around the caster's body.
            Vector3 pos = proxyT.position;
            Collider[] buf = WorldQueryHelper.SharedOverlapBuf;
            int hits = Physics.OverlapSphereNonAlloc(pos, 500f, buf);
            for (int i = 0; i < hits; i++)
            {
                Collider col = buf[i];
                if (col == null || col.gameObject.layer != 11 && col.gameObject.layer != 21)
                    continue;
                Character c = col.gameObject.GetComponent<Character>();
                if (c != null && c.GetComponent<NPC>() == null)
                    c.runAway(pos);
            }
            Core.AddPrefab("FX/skills/scaryface_prefab", pos, Quaternion.Euler(90f, 0f, 0f), null);
            var relay = new PlayerScareMessage { ScaryFace = true, CasterId = (short)playerId };
            _net.SendToAllExcept(playerId, NetMessageType.PlayerScare, w => relay.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

    }
}
