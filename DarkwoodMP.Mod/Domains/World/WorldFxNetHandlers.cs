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
            // Host already plays live AI audio; only remote peers apply.
            // (Keep host out even if a packet is mis-routed / Forwardable echo.)
            if (_net.Role == NetworkRole.Host) return;

            Character c = CharacterTracker.FindByStableId(msg.HostId);
            if (c == null || c.sounds == null)
            {
                EntitySyncLog.Reaction("snd:miss",
                    "[EntitySound] no char/sounds id=" + msg.HostId
                    + " type=" + msg.SoundType, 2f);
                return;
            }

            // Match entity visual interest + send cull (not the shorter DefaultMaxAudioDistance).
            Vector3 cpos = c.transform != null ? c.transform.position : Vector3.zero;
            if (!ClientEntityInterpolationService.IsInClientInterest(cpos))
            {
                EntitySyncLog.Reaction("snd:far",
                    "[EntitySound] cull interest id=" + msg.HostId
                    + " type=" + msg.SoundType, 3f);
                return;
            }

            EntitySyncLog.Reaction(msg.HostId + ":" + msg.SoundType,
                "[EntitySound] apply id=" + msg.HostId + " " + (c.name ?? "")
                + " type=" + msg.SoundType
                + (string.IsNullOrEmpty(msg.LoopName) ? "" : " loop=" + msg.LoopName), 0.35f);

            // Prevent CharacterSounds → AudioController patches from re-forwarding.
            TraverseHack.ApplyingFromNetwork = true;
            TraverseHack.InsideCharacterSounds = true;
            try
            {
                // Replay vanilla CharacterSounds API (decompile: play*, playIdleLoop, destroySounds).
                // Component may be disabled on host-synced entities; method calls still play one-shots.
                switch (msg.SoundType)
                {
                    case EntitySoundType.Growl:
                        c.sounds.playGrowl();
                        break;
                    case EntitySoundType.Curious:
                        if (!string.IsNullOrEmpty(c.sounds.curious))
                            c.sounds.playSingleInstance(c.sounds.curious);
                        break;
                    case EntitySoundType.Aggressive:
                        if (!string.IsNullOrEmpty(c.sounds.aggressive))
                            c.sounds.playSingleInstance(c.sounds.aggressive);
                        break;
                    case EntitySoundType.Defensive:
                        if (!string.IsNullOrEmpty(c.sounds.defensive))
                            c.sounds.playSingleInstance(c.sounds.defensive);
                        break;
                    case EntitySoundType.Idle:
                        // Empty LoopName = destroySounds stop (host idle stop / despawn).
                        if (string.IsNullOrEmpty(msg.LoopName))
                            c.sounds.destroySounds();
                        else
                            // forceReplace=true so idle→aggressive loop swaps like host.
                            c.sounds.playIdleLoop(msg.LoopName, true);
                        break;
                    case EntitySoundType.Escaping:
                        c.sounds.playEscapingLoop();
                        break;
                    case EntitySoundType.EscapingStart:
                        if (!string.IsNullOrEmpty(c.sounds.escapingStart))
                            c.sounds.playSingleInstance(c.sounds.escapingStart);
                        break;
                    case EntitySoundType.EscapingStart2:
                        if (!string.IsNullOrEmpty(c.sounds.escapingStart2))
                            c.sounds.play(c.sounds.escapingStart2);
                        break;
                    case EntitySoundType.Attack1:
                        if (!string.IsNullOrEmpty(c.sounds.attack1))
                            c.sounds.play(c.sounds.attack1);
                        break;
                    case EntitySoundType.Attack2:
                        if (!string.IsNullOrEmpty(c.sounds.attack2))
                            c.sounds.play(c.sounds.attack2);
                        break;
                    case EntitySoundType.Death:
                        // Same path as Alive->dead snap; play at most one death SFX.
                        ClientEntityInterpolationService.NoteLocalDeathPresentation(c, msg.HostId);
                        break;
                    case EntitySoundType.GetHit:
                        // Attacker already played the local hit presentation; skip the echo.
                        if (ClientEntityInterpolationService.ShouldIgnoreGetHitEcho(msg.HostId))
                        {
                            EntitySyncLog.Reaction("snd:echo",
                                "[EntitySound] GetHit echo skipped id=" + msg.HostId, 0.5f);
                            break;
                        }
                        c.sounds.playGetHitByAxe1();
                        break;
                    default:
                        ModRuntime.Log?.LogWarning($"[EntitySound] Unhandled EntitySoundType: {msg.SoundType}");
                        break;
                }
            }
            finally
            {
                TraverseHack.InsideCharacterSounds = false;
                TraverseHack.ApplyingFromNetwork = false;
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
            ModRuntime.LegacyInfo("[ObjectRemove] received destroy request for \"" + msg.ObjectName + "\" at " + pos);
            // Mark consumed before destroy so a same-frame local getDroppedItem Prefix loses.
            Sync.WorldPhysicsSyncService.TryConsumeWorldPickup(msg.PosX, msg.PosY, msg.PosZ, msg.ObjectName);
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

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            if (!Sync.WorldPhysicsSyncService.TryConsumeWorldPickup(msg.PosX, msg.PosY, msg.PosZ, msg.ObjectName))
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
                return;
            }

            Sync.WorldPhysicsSyncService.DestroyObjectByPos(pos, msg.ObjectName);

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
        }

        private void HandleWorldPickupClaimDeny(WorldObjectRemovedMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;
            if (Patches.WorldPickupClaimPending.TryTake(msg.PosX, msg.PosY, msg.PosZ, msg.ObjectName,
                out string type, out int amt, out int pre, out string recipeFor))
            {
                Patches.WorldPickupClaimPending.Refund(type, amt, pre, "claim deny", recipeFor);
                return;
            }
            // No pending entry: the host-won Remove (ClaimedBy=host) already refunded this claim.
            // A blind refund here removed the amount a second time and ate the client's own stock.
            ModLog.Event(LogCat.World,
                "[WorldPickup] deny for " + msg.ObjectName + " had no pending claim (already refunded)");
        }

        /// <summary>A name match is only trusted this close to the position the sender reported.</summary>
        private const float BodyPushNameMatchMaxDist = 8f;

        /// <summary>
        /// Body-push / scrape source: the same-named object nearest the reported position, on the
        /// same side (dream pad vs overworld) as that position. Never a scene-wide name search:
        /// <c>GameObject.Find(name)</c> returned the first same-named object anywhere, including
        /// the overworld twin of a dream-pad object, so the wrong body got the scrape sound.
        /// </summary>
        private static GameObject ResolveBodyPushObject(string objectName, Vector3 bodyPos)
        {
            if (string.IsNullOrEmpty(objectName) || float.IsNaN(bodyPos.x))
                return null;
            Component hit = WorldQueryHelper.FindNearestByName<ItemSounds>(
                bodyPos, objectName, BodyPushNameMatchMaxDist);
            if (hit == null)
                hit = WorldQueryHelper.FindNearestByName<Item>(
                    bodyPos, objectName, BodyPushNameMatchMaxDist);
            if (hit == null || !WorldPhysicsSyncService.IsOnSameWorldSide(bodyPos, hit.transform))
                return null;
            return hit.gameObject;
        }

        internal void HandlePlayerAudio(PlayerAudioMessage msg)
        {
            if (msg.IsStopSignal)
            {
                // Local pusher/dragger still owns native ItemSounds; host quiet or stop echo
                // must not kill our scrape mid-push (same double-scrape family).
                if (DWMPHorde.Audio.ItemMovingSoundHelper.IsLocalPushOrDragOwner(msg.ObjectName)
                    || DWMPHorde.Audio.ItemMovingSoundHelper.HasRecentClientPhysicsSent(msg.ObjectName))
                {
                    DWMPHorde.Audio.MovingObjectSoundService.StopImmediate(msg.ObjectName);
                    return;
                }
                // Remote quiet stop uses SoftStop without suppression so motion can re-arm instantly.
                DWMPHorde.Audio.ItemMovingSoundHelper.SoftStopNetwork(msg.ObjectName);
                Sync.WorldPhysicsSyncService.TryStopBodyPushSound(msg.ObjectName);
                return;
            }

            if (string.IsNullOrEmpty(msg.SoundId)) return;

            if (!msg.StickToSender && msg.Volume <= 0.001f)
            {
                AudioController.Stop(msg.SoundId, 0.2f);
                return;
            }

            // Defensive: never play world ambients that slipped past send-side filter.
            if (msg.StickToSender && LocalAudioService.IsWorldAmbientLocalOnly(msg.SoundId))
                return;

            // Body-push / scrape with ObjectName: single-owner path.
            if (!string.IsNullOrEmpty(msg.ObjectName))
            {
                if (DWMPHorde.Audio.ItemMovingSoundHelper.IsScrapeSuppressed(msg.ObjectName))
                    return;
                // Local free-body pusher hears native ItemSounds only; never arm MOS or PlayerAudio.
                if (DWMPHorde.Audio.ItemMovingSoundHelper.IsLocalOwnedScrape(msg.ObjectName)
                    || DWMPHorde.Audio.ItemMovingSoundHelper.HasRecentClientPhysicsSent(msg.ObjectName)
                    || DWMPHorde.Audio.ItemMovingSoundHelper.HasRecentPushAuthority(msg.ObjectName))
                    return;
                // Already playing via PhysicsState→MOS: ignore redundant start (T2).
                if (DWMPHorde.Audio.MovingObjectSoundService.IsPlaying(msg.ObjectName))
                    return;

                Vector3 bodyPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (!float.IsNaN(msg.PosX)
                    && !LocalAudioService.IsNearListenerPeerBand(bodyPos, LocalAudioService.DefaultMaxAudioDistance))
                    return;

                GameObject go = ResolveBodyPushObject(msg.ObjectName, bodyPos);
                if (go != null)
                {
                    ItemSounds sounds = go.GetComponent<ItemSounds>();
                    if (sounds != null)
                    {
                        DWMPHorde.Audio.MovingObjectSoundService.NoteMoving(go, msg.ObjectName, sounds);
                        return;
                    }
                    // Fallback when ItemSounds missing: MOS EnsurePlaying by SoundId.
                    float vol = Mathf.Clamp01(msg.Volume);
                    DWMPHorde.Audio.MovingObjectSoundService.EnsurePlaying(go, msg.ObjectName, msg.SoundId, vol);
                    return;
                }
                // Object not found locally; fall through to a positional one-shot.
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            bool hasPos = !float.IsNaN(msg.PosX);

            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);

            bool isHitFeedback = LocalAudioService.IsPlayerHitFeedbackSound(msg.SoundId);
            // Equip get/hide stay 2D. Flashlight/torch: spatial at proxy + keep reverb.
            bool prefer2d = LocalAudioService.IsPrefer2dNetworkOneShot(msg.SoundId);
            bool spatialTool = LocalAudioService.IsRemotePlayerSpatialToolSound(msg.SoundId);

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

            if (playerId > 0)
            {
                if (!LocalAudioService.IsPeerAudioInRange(playerId, pos, LocalAudioService.DefaultMaxAudioDistance))
                    return;
            }
            else if (!LocalAudioService.IsNearListenerPeerBand(pos, LocalAudioService.DefaultMaxAudioDistance))
                return;

            TraverseHack.ApplyingFromNetwork = true;
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
                            float itemMax = (item != null && item.overrideAudioSourceSettings)
                                ? item.audioSource_MaxDistance : LocalAudioService.DefaultMaxSpatialDistance;
                            audioObj.primaryAudioSource.minDistance = Mathf.Max(itemMin, LocalAudioService.DefaultMinSpatialDistance);
                            audioObj.primaryAudioSource.maxDistance = Mathf.Max(itemMax, 100f);
                        }
                    }
                }
            }
            finally { TraverseHack.ApplyingFromNetwork = false; }
        }

    }
}
