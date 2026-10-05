using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Entity / world ambient / player audio FX composed for 0.8.</summary>
    internal sealed class WorldFxNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal WorldFxNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleEntitySound(EntitySoundMessage msg)
        {
            // The host plays its creatures' sounds itself.
            if (_net.Role == NetworkRole.Host) return;
            if (string.IsNullOrEmpty(msg.SoundId)) return;

            Character c = CharacterTracker.FindByStableId(msg.HostId);
            CharacterSounds s = c != null ? c.sounds : null;
            if (s == null)
            {
                EntitySyncLog.Reaction("snd:miss",
                    "[EntitySound] no char/sounds id=" + msg.HostId + " kind=" + msg.Kind, 2f);
                return;
            }
            // Outside interest the copy is not driven: it stands where it was last seen.
            if (!ClientEntityInterpolationService.IsInClientInterest(c.transform.position))
                return;

            EntitySyncLog.Reaction(msg.HostId + ":" + msg.Kind,
                "[EntitySound] apply id=" + msg.HostId + " " + (c.name ?? "")
                + " kind=" + msg.Kind + " id=" + msg.SoundId, 0.35f);

            if (msg.Kind == EntitySoundKind.Death)
            {
                ClientEntityInterpolationService.NoteLocalDeathPresentation(c, msg.HostId);
                return;
            }
            if (msg.Kind == EntitySoundKind.GetHit
                && ClientEntityInterpolationService.ConsumeLocalHitEcho(msg.HostId, msg.AttackerId))
            {
                EntitySyncLog.Reaction("snd:echo",
                    "[EntitySound] GetHit already shown locally id=" + msg.HostId, 0.5f);
                return;
            }

            // The host already applied vanilla's guards (underwater, underground); play as its call did.
            bool prevNet = TraverseHack.GetExplicitFlag();
            bool prevInside = TraverseHack.InsideCharacterSounds;
            TraverseHack.SetExplicitFlag(true);
            TraverseHack.InsideCharacterSounds = true;
            try
            {
                // Reverb on the copy comes from CharBase.isInside, which only checkGround refreshes.
                c.checkGround();
                switch (msg.Kind)
                {
                    case EntitySoundKind.Play:
                        s.playedAO = AudioController.Play(msg.SoundId, s.transform);
                        break;
                    case EntitySoundKind.Single:
                        if (s.playedAO != null && s.playedAO.IsPlaying() && s.playedAO.transform.parent == s.transform)
                            s.playedAO.Stop();
                        s.playedAO = AudioController.Play(msg.SoundId, s.transform);
                        break;
                    case EntitySoundKind.Attached:
                        AudioController.Play(msg.SoundId, s.transform, Mathf.Clamp01(msg.Volume));
                        break;
                    case EntitySoundKind.GetHit:
                        AudioController.Play(msg.SoundId, s.transform);
                        break;
                    default:
                        EntitySyncLog.Reaction("snd:kind", "[EntitySound] unknown kind " + msg.Kind, 5f);
                        break;
                }
            }
            finally
            {
                TraverseHack.InsideCharacterSounds = prevInside;
                TraverseHack.SetExplicitFlag(prevNet);
            }
        }

        /// <summary>
        /// A banshee screams at a player or stops: its sight light on every peer; the scream on
        /// the victim's own body, the shake and the overlay only for the victim (vanilla
        /// bansheeAgitated / onBansheeSeePlayer / onBansheeOutOfSightOfPlayer, Character.cs).
        /// </summary>
        internal void HandleBansheeAgitation(BansheeAgitationMessage msg)
        {
            if (_net.Role == NetworkRole.Host) return;
            Character banshee = CharacterTracker.FindByStableId(msg.HostId);
            BansheeVictims.SetSightLight(banshee, msg.Agitated);
            if (msg.VictimId != _net.LocalPlayerId)
                return;
            Player p = Player.Instance;
            if (p == null || p._transform == null)
                return;

            bool prevNet = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                if (msg.Agitated)
                {
                    if (p.bansheeAgitatedSoundAO == null)
                        p.bansheeAgitatedSoundAO = AudioController.Play("banshee_agitated_player", p._transform);
                    if (banshee == null)
                        return;
                    float dist = Mathf.Max(1f, Core.trueDistance(banshee.transform, p._transform));
                    Singleton<CamMain>.Instance.shake(0.5f, 1200f / dist);
                    if (msg.Overlay)
                    {
                        Singleton<UI>.Instance.initBansheeOverlay();
                        Core.tweenAlpha(Singleton<UI>.Instance.bansheeOverlay.gameObject,
                            Mathf.Clamp(70f / dist, 0f, 0.5f), 0.5f, timeScaleDependent: true);
                    }
                }
                else
                {
                    if (p.bansheeAgitatedSoundAO != null)
                    {
                        p.bansheeAgitatedSoundAO.Stop(1f);
                        p.bansheeAgitatedSoundAO = null;
                    }
                    Singleton<UI>.Instance.initBansheeOverlay();
                    Core.tweenAlpha(Singleton<UI>.Instance.bansheeOverlay.gameObject, 0f, 0.5f, timeScaleDependent: true);
                    Singleton<UI>.Instance.wantToRemoveBansheeOverlay();
                }
            }
            finally
            {
                TraverseHack.SetExplicitFlag(prevNet);
            }
        }

        internal void HandleWorldObjectRemoved(WorldObjectRemovedMessage msg)
        {
            if (msg.Mode == WorldObjectRemovedMessage.ModeClaimRequest)
            {
                HandleWorldPickupClaimRequest(msg);
                return;
            }
            if (msg.Mode == WorldObjectRemovedMessage.ModeClaimDeny)
            {
                HandleWorldPickupClaimDeny(msg);
                return;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            ModRuntime.LegacyInfo($"[ObjectRemove] received destroy request for \"{msg.ObjectName}\" at {pos}");
            // Mark consumed before destroy so a same-frame local getDroppedItem Prefix loses.
            Sync.WorldPhysicsSyncService.TryConsumeWorldPickup(msg.PosX, msg.PosY, msg.PosZ, msg.ObjectName);
            // The host's grant Remove also reaches the claimer, whose own pickup already
            // destroyed its copy: searching again could only hit some other object.
            bool ownGrant = _net.Role == NetworkRole.Client
                && msg.ClaimedByPlayerId > 0 && msg.ClaimedByPlayerId == _net.LocalPlayerId;
            if (!ownGrant)
                Sync.WorldPhysicsSyncService.DestroyObjectByPos(pos, msg.ObjectName);

            // Optimistic client lost the host-auth race: refund once via pending.
            if (_net.Role == NetworkRole.Client
                && msg.ClaimedByPlayerId != _net.LocalPlayerId)
            {
                Patches.WorldPickupClaimPending.TryRefundIfPending(
                    msg.PosX, msg.PosY, msg.PosZ, msg.ObjectName, "remove claimedBy=" + msg.ClaimedByPlayerId);
            }
            else
            {
                Patches.WorldPickupClaimPending.Clear(msg.PosX, msg.PosY, msg.PosZ, msg.ObjectName);
            }

            // Forward client-originated removal to other clients (3+ support).
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0
                && msg.Mode == WorldObjectRemovedMessage.ModeRemove)
                _net.SendToAllExcept(_net.CurrentReceivePlayerId, NetMessageType.WorldObjectRemoved, w => msg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Host: first ClaimRequest wins — consume, destroy local, fan Remove with ClaimedBy.
        /// Loser gets ClaimDeny (optimistic grant refund on client).
        /// </summary>
        private void HandleWorldPickupClaimRequest(WorldObjectRemovedMessage msg)
        {
            if (_net.Role != NetworkRole.Host)
                return;
            int claimer = _net.CurrentReceivePlayerId;
            if (claimer <= 0)
                return;
            ProcessPickupClaim(msg, claimer, mayHold: true);
        }

        /// <summary>
        /// Claims for a location the host has not spawned yet (the claimer entered it moments ago):
        /// the item is not here to take yet, but it will be. Denied, the client gave the item back
        /// and its own copy was already gone: the item vanished for it.
        /// </summary>
        private readonly System.Collections.Generic.List<KeyValuePair<WorldObjectRemovedMessage, KeyValuePair<int, float>>> _heldClaims =
            new System.Collections.Generic.List<KeyValuePair<WorldObjectRemovedMessage, KeyValuePair<int, float>>>();
        private float _nextHeldClaimRetry;
        private const float HoldClaimSec = 20f;

        internal void ClearHeldClaims() => _heldClaims.Clear();

        internal void TickHeldClaims()
        {
            if (_heldClaims.Count == 0 || _net.Role != NetworkRole.Host || Time.unscaledTime < _nextHeldClaimRetry)
                return;
            _nextHeldClaimRetry = Time.unscaledTime + 1f;
            var batch = new System.Collections.Generic.List<KeyValuePair<WorldObjectRemovedMessage, KeyValuePair<int, float>>>(_heldClaims);
            _heldClaims.Clear();
            for (int i = 0; i < batch.Count; i++)
            {
                bool expired = Time.unscaledTime - batch[i].Value.Value > HoldClaimSec;
                if (!ProcessPickupClaim(batch[i].Key, batch[i].Value.Key, mayHold: !expired, heldSince: batch[i].Value.Value))
                    continue;
            }
        }

        private bool ClaimerLocationPending(int claimer)
        {
            var ol = Singleton<OutsideLocations>.Instance;
            return ol != null && ol.spawnedLocations != null
                && _net.RemoteOutsideLocation.TryGetValue(claimer, out string loc)
                && !string.IsNullOrEmpty(loc)
                && !ol.spawnedLocations.ContainsKey(Core.getTrueLocationName(loc));
        }

        /// <returns>False when the claim was put on hold.</returns>
        private bool ProcessPickupClaim(WorldObjectRemovedMessage msg, int claimer, bool mayHold, float heldSince = -1f)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            // First claim wins, and only for an object the host actually has: a claim for a
            // pickup the host never had (or already lost) is denied instead of granted.
            bool first = Sync.WorldPhysicsSyncService.TryConsumeWorldPickup(msg.PosX, msg.PosY, msg.PosZ, msg.ObjectName);
            bool granted = first && Sync.WorldPhysicsSyncService.TryDestroyClaimedWorldPickup(pos, msg.ObjectName);
            if (first && !granted)
            {
                // Not taken by anyone: leave it takeable (a failed claim used to mark it gone).
                Sync.WorldPhysicsSyncService.UnconsumeWorldPickup(msg.PosX, msg.PosY, msg.PosZ, msg.ObjectName);
                if (mayHold && ClaimerLocationPending(claimer))
                {
                    _heldClaims.Add(new KeyValuePair<WorldObjectRemovedMessage, KeyValuePair<int, float>>(
                        msg, new KeyValuePair<int, float>(claimer, heldSince >= 0f ? heldSince : Time.unscaledTime)));
                    ModLog.Event(LogCat.World, "[WorldPickup] hold p" + claimer + " " + msg.ObjectName + " — location still spawning here");
                    return false;
                }
            }
            if (!granted)
            {
                var deny = new WorldObjectRemovedMessage
                {
                    PosX = msg.PosX,
                    PosY = msg.PosY,
                    PosZ = msg.PosZ,
                    ObjectName = msg.ObjectName,
                    Mode = WorldObjectRemovedMessage.ModeClaimDeny,
                    ClaimedByPlayerId = 0,
                    ItemType = msg.ItemType ?? "",
                    Amount = msg.Amount,
                    Durability = msg.Durability,
                    Ammo = msg.Ammo
                };
                _net.SendToPlayer(claimer, NetMessageType.WorldObjectRemoved, w => deny.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModLog.Event(LogCat.World,
                    "[WorldPickup] deny p" + claimer + " " + msg.ObjectName + " at " + pos);
                return true;
            }

            var remove = new WorldObjectRemovedMessage
            {
                PosX = msg.PosX,
                PosY = msg.PosY,
                PosZ = msg.PosZ,
                ObjectName = msg.ObjectName,
                Mode = WorldObjectRemovedMessage.ModeRemove,
                ClaimedByPlayerId = claimer,
                ItemType = msg.ItemType ?? "",
                Amount = msg.Amount,
                Durability = msg.Durability,
                Ammo = msg.Ammo
            };
            // Broadcast includes claimer (GO already gone — destroy is idempotent).
            _net.Broadcast(NetMessageType.WorldObjectRemoved, w => remove.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.World,
                "[WorldPickup] grant p" + claimer + " " + msg.ObjectName + " at " + pos);
            return true;
        }

        private void HandleWorldPickupClaimDeny(WorldObjectRemovedMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;
            if (Patches.WorldPickupClaimPending.TryTake(msg.PosX, msg.PosY, msg.PosZ, msg.ObjectName,
                out string type, out int amt, out int pre, out string recipeFor, out float dur, out int ammo))
            {
                Patches.WorldPickupClaimPending.Refund(type, amt, pre, "claim deny", recipeFor, dur, ammo);
                return;
            }
            // No pending entry: the host-won Remove (ClaimedBy=host) already refunded this claim.
            // A blind refund here removed the amount a second time and ate the client's own stock.
            ModLog.Event(LogCat.World,
                "[WorldPickup] deny for " + msg.ObjectName + " had no pending claim (already refunded)");
        }

        internal void HandlePlayerAudio(PlayerAudioMessage msg)
        {
            if (string.IsNullOrEmpty(msg.SoundId)) return;

            // A silent play is not a stop: stopping the id here killed every instance of that
            // sound on this peer, including its own unrelated ones.
            if (msg.Volume <= 0.001f)
                return;

            // Defensive: never play world ambients that slipped past send-side filter.
            if (msg.StickToSender && LocalAudioService.IsWorldAmbientLocalOnly(msg.SoundId))
                return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            bool hasPos = !float.IsNaN(msg.PosX);

            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);

            // Player-origin rules (sender's body, 2D equip, per-peer hear gate) apply only to the
            // sender's own player sounds. A world/enemy sound the host forwards (StickToSender
            // false) stays where it happened: a door's "door_hit_metal" or a lamp "activate" was
            // being moved onto the host's body and an "_get"/"_hide" id played 2D.
            bool fromPlayer = msg.StickToSender;
            bool isHitFeedback = fromPlayer && LocalAudioService.IsPlayerHitFeedbackSound(msg.SoundId);
            // Equip get/hide stay 2D. Flashlight/torch: spatial at proxy + keep reverb.
            bool prefer2d = fromPlayer && LocalAudioService.IsPrefer2dNetworkOneShot(msg.SoundId);
            bool spatialTool = fromPlayer && LocalAudioService.IsRemotePlayerSpatialToolSound(msg.SoundId);

            // Hit SFX: always prefer the victim proxy (who was hit), not the local player.
            // Never call getHit, red-screen, or BloodOverlay here; this path is audio only.
            if (isHitFeedback && proxy != null)
                pos = proxy.transform.position;
            else if (!hasPos || spatialTool)
            {
                // Flashlight: always use proxy position even if packet has local coords.
                if (proxy != null)
                    pos = proxy.transform.position;
                else if (!hasPos)
                    return;
            }

            // The sticky per-peer gate tracks that peer's body; world sounds from the same sender
            // are all over the map and would flip it, so they use the stateless band.
            float range = LocalAudioService.AudibleRange(msg.SoundId);
            if (fromPlayer && playerId > 0)
            {
                if (!LocalAudioService.IsPeerAudioInRange(playerId, pos, range))
                    return;
            }
            else if (!LocalAudioService.IsNearListenerPeerBand(pos, range))
                return;

            // Explicit flag saved and restored: an outer apply scope must survive this replay.
            bool prevNet = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                Transform parent = null;
                // Inventory open/close and other stick-to-sender presence SFX must parent to
                // the proxy so AudioController can read CharBase.isInside for reverb.
                bool presenceSpatial = LocalAudioService.IsRemotePlayerPresenceSound(msg.SoundId);
                if ((!prefer2d || presenceSpatial) && proxy != null && (msg.StickToSender || presenceSpatial))
                {
                    parent = proxy.transform;
                    // Proxy has no CharacterSounds tick — refresh indoor ground before Play
                    // or open_drawer arrives with isInside=false and skips AudioReverbFilter.
                    CharBase pcb = proxy.CachedCharBase;
                    if (pcb != null)
                    {
                        try { pcb.checkGround(); }
                        catch { /* ignore */ }
                    }
                    prefer2d = false;
                }
                else if (!prefer2d && proxy != null && msg.StickToSender)
                {
                    parent = proxy.transform;
                    CharBase pcb = proxy.CachedCharBase;
                    if (pcb != null)
                    {
                        try { pcb.checkGround(); }
                        catch { /* ignore */ }
                    }
                }

                AudioObject audioObj;
                if (prefer2d)
                {
                    audioObj = AudioController.Play(msg.SoundId);
                    if (audioObj != null && audioObj.primaryAudioSource != null
                        && msg.Volume > 0f && msg.Volume < 0.999f)
                        audioObj.volume = Mathf.Clamp01(msg.Volume);
                }
                else
                {
                    // Parent to proxy so vanilla indoor reverb (isInside) applies in bunker.
                    audioObj = AudioController.Play(msg.SoundId, pos, parent, Mathf.Clamp01(msg.Volume));
                }

                if (audioObj != null)
                {
                    if (prefer2d)
                    {
                        // UI/equip: strip world filters; fully 2D.
                        var reverb = audioObj.GetComponent<AudioReverbFilter>();
                        if (reverb != null) UnityEngine.Object.Destroy(reverb);
                        var lowPass = audioObj.GetComponent<AudioLowPassFilter>();
                        if (lowPass != null) UnityEngine.Object.Destroy(lowPass);
                        if (audioObj.primaryAudioSource != null)
                        {
                            audioObj.primaryAudioSource.spatialBlend = 0f;
                            audioObj.primaryAudioSource.reverbZoneMix = 0f;
                        }
                    }
                    else if (audioObj.primaryAudioSource != null)
                    {
                        // Spatial remote SFX (flashlight, hits, etc.): 3D at proxy.
                        // Keep reverb/lowpass from AudioController (bunker wetness).
                        audioObj.primaryAudioSource.spatialBlend = 1f;

                        if (isHitFeedback)
                        {
                            audioObj.primaryAudioSource.rolloffMode = AudioRolloffMode.Linear;
                            audioObj.primaryAudioSource.minDistance = 8f;
                            audioObj.primaryAudioSource.maxDistance = 80f;
                        }
                        else if (spatialTool)
                        {
                            // Flashlight/torch: Log + full peer range. Tiny minDistance buried
                            // the soft click tail under attenuation while the attack still
                            // read; keep near-field at DefaultMinSpatialDistance.
                            audioObj.primaryAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
                            audioObj.primaryAudioSource.minDistance =
                                LocalAudioService.DefaultMinSpatialDistance;
                            audioObj.primaryAudioSource.maxDistance =
                                LocalAudioService.DefaultMaxSpatialDistance;
                        }
                        else
                        {
                            audioObj.primaryAudioSource.rolloffMode = AudioRolloffMode.Linear;
                            AudioItem item = AudioController.GetAudioItem(msg.SoundId);
                            float itemMin = (item != null && item.overrideAudioSourceSettings)
                                ? item.audioSource_MinDistance : LocalAudioService.DefaultMinSpatialDistance;
                            // The range the game gives this id (its prefab's when not overridden),
                            // the same range the hear gate above used.
                            float itemMax = LocalAudioService.SpatialMaxDistance(msg.SoundId);
                            audioObj.primaryAudioSource.minDistance = Mathf.Max(itemMin, LocalAudioService.DefaultMinSpatialDistance);
                            audioObj.primaryAudioSource.maxDistance = Mathf.Max(itemMax, 100f);
                        }
                    }
                }
            }
            finally { TraverseHack.SetExplicitFlag(prevNet); }
        }

    }
}
