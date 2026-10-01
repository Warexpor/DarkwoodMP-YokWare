using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    public static partial class WorldPhysicsSyncService
    {
        private const int MaxPendingLights = 64;

        /// <summary>Pending light applies (CoopPerfProbe).</summary>
        public static int PendingLightCount => _pendingLights.Count;

        /// <summary>
        /// Applies a received light on/off state change from a remote peer.
        /// Looks up the light Item by position and name, then toggles it if needed.
        /// </summary>
        /// <param name="ls">The light state message.</param>
        /// <param name="fromPeer">Sender identifier for logging.</param>
        public static void ApplyLightState(LightStateMessage ls, string fromPeer)
        {
            ApplyLightStateCore(ls, fromPeer, queueIfMissing: true);
        }

        private static float _nextPendingLightFlushTime;
        private const float PendingLightFlushInterval = 3f;
        private static float _pendingLightQueuedAt = -1f;

        /// <summary>
        /// Retry queued LightState after location/grid load.
        /// Use OverlapSphere rather than a scene-wide search on this path.
        /// </summary>
        public static void TryFlushPendingLights()
        {
            if (_pendingLights.Count == 0)
            {
                _pendingLightQueuedAt = -1f;
                return;
            }
            if (Player.Instance == null || Core.mainMenu || Core.loadingGame) return;

            float now = Time.unscaledTime;
            // Immediate flush when just entered a dream (caller may invoke right after load).
            bool dreamPad = Dreams.Instance != null && Dreams.Instance.dreaming;
            if (!dreamPad && now < _nextPendingLightFlushTime) return;
            _nextPendingLightFlushTime = now + (dreamPad ? 0.25f : PendingLightFlushInterval);

            if (_pendingLightQueuedAt < 0f)
                _pendingLightQueuedAt = now;
            if (now - _pendingLightQueuedAt > 30f)
            {
                ModLog.Event(LogCat.World,
                    "[LightApply] dropping " + _pendingLights.Count
                    + " pending light(s) after timeout (not in loaded grid)");
                _pendingLights.Clear();
                _pendingLightQueuedAt = -1f;
                return;
            }

            for (int i = _pendingLights.Count - 1; i >= 0; i--)
            {
                LightStateMessage ls = _pendingLights[i];
                Vector3 p = new Vector3(ls.PosX, ls.PosY, ls.PosZ);
                // Far map lights may be unloaded; retry when the player returns.
                // Keep all while dreaming (bunker pad is far from forest listen pos).
                if (!dreamPad && !Networking.ClientEntityInterpolationService.IsInClientInterest(p))
                {
                    _pendingLights.RemoveAt(i);
                    continue;
                }
                if (ApplyLightStateCore(ls, "pending", queueIfMissing: false))
                    _pendingLights.RemoveAt(i);
            }
            if (_pendingLights.Count == 0)
                _pendingLightQueuedAt = -1f;
        }

        /// <returns>True if applied or already matching; false if still missing.</returns>
        private static bool ApplyLightStateCore(LightStateMessage ls, string fromPeer, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(ls.PosX, ls.PosY, ls.PosZ);

            // Late-join bulk sends every on-lamp on the map. Far ones are not in the client
            // grid, so do not queue it. Dream pads remain in interest while dreaming.
            bool clientSide = ModRuntime.Network != null
                && ModRuntime.Network.Role == Networking.NetworkRole.Client;
            bool dreamPad = Dreams.Instance != null && Dreams.Instance.dreaming;
            if (clientSide && !dreamPad
                && !Networking.ClientEntityInterpolationService.IsInClientInterest(pos))
                return true; // Host will resend when the player enters the area.

            Item item = FindLightByPos(pos, ls.ItemName, ls.ItemType);
            if (item == null)
            {
                // Apply to scene lamps only; do not spawn a prefab from ItemType.
                if (queueIfMissing && clientSide
                    && Networking.ClientEntityInterpolationService.IsInClientInterest(pos))
                {
                    QueuePendingLight(ls);
                    ModRuntime.LegacyInfo($"[LightApply] {ls.ItemName} not found at {pos} — queued");
                }
                else if (queueIfMissing && !clientSide)
                {
                    QueuePendingLight(ls);
                    ModRuntime.LegacyInfo($"[LightApply] {ls.ItemName} not found at {pos} — queued");
                }
                return false;
            }

            // Already matching: treat as success (drop pending).
            if (ls.IsOn == item.isOn)
                return true;

            ModRuntime.LegacyInfo($"[LightApply] {item.name} isOn={ls.IsOn} from {fromPeer}");

            // Vanilla player path is Item.switchMe(): playSwitch() then turnOn/turnOff.
            // Remote state only had turnOn/turnOff. Many lamps put the click
            // in switchSound only.
            // (startSound/endSound empty), so peers saw the light change with no SFX.
            TraverseHack.ApplyingFromNetwork = true;
            try
            {
                ItemSounds sounds = item.GetComponent<ItemSounds>();
                if (sounds != null)
                    sounds.playSwitch();

                if (ls.IsOn)
                    DialogHostApplyGuard.RunHostWorldFanout(() => item.turnOn());
                else
                    DialogHostApplyGuard.RunHostWorldFanout(() => item.turnOff());

                // turnOff only calls playStop when hasPower. Unpowered lamps still need
                // the end one-shot if switchSound was empty and endSound is set.
                if (!ls.IsOn && sounds != null && !item.hasPower
                    && !string.IsNullOrEmpty(sounds.endSound)
                    && string.IsNullOrEmpty(sounds.switchSound))
                {
                    AudioController.Play(sounds.endSound, item.transform, sounds.volumeModifier);
                }
            }
            finally
            {
                TraverseHack.ApplyingFromNetwork = false;
            }
            return true;
        }

        private static void QueuePendingLight(LightStateMessage ls)
        {
            // Dedupe by name and rounded position, keeping the latest isOn value.
            string key = (ls.ItemName ?? "") + "@"
                + Mathf.Round(ls.PosX) + "," + Mathf.Round(ls.PosY) + "," + Mathf.Round(ls.PosZ);
            for (int i = 0; i < _pendingLights.Count; i++)
            {
                var p = _pendingLights[i];
                string pk = (p.ItemName ?? "") + "@"
                    + Mathf.Round(p.PosX) + "," + Mathf.Round(p.PosY) + "," + Mathf.Round(p.PosZ);
                if (pk == key)
                {
                    _pendingLights[i] = ls;
                    return;
                }
            }
            if (_pendingLights.Count >= MaxPendingLights)
                _pendingLights.RemoveAt(0);
            _pendingLights.Add(ls);
        }

        /// <summary>
        /// Finds a light Item by position (+ optional name/type). OverlapSphere first;
        /// inactive hierarchy fallback (dream door-swap / unloaded hideout lamps).
        /// </summary>
        private static Item FindLightByPos(Vector3 pos, string name, string itemType = null)
        {
            Item best = FindLightInSphere(pos, name, itemType, 5f);
            if (best != null) return best;
            best = FindLightInSphere(pos, name, itemType, 20f);
            if (best != null) return best;
            return FindLightInactiveScan(pos, name, itemType, 25f);
        }

        private static Item FindLightInSphere(Vector3 pos, string name, string itemType, float radius)
        {
            int nearbyN = OverlapNear(pos, radius);
            Item best = null;
            float bestDistSq = radius * radius;
            for (int i = 0; i < nearbyN; i++)
            {
                if (_overlap3D[i] == null) continue;
                Item item = _overlap3D[i].GetComponentInParent<Item>();
                if (item == null) continue;
                if (!LightNameOrTypeMatches(item, name, itemType))
                    continue;
                if (!item.isLight && !item.switchable)
                    continue;
                float dSq = XzDistSq(item.transform.position, pos);
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = item;
                }
            }
            return best;
        }

        /// <summary>Include inactive GOs (dream pad lamps under closed door hierarchy).</summary>
        private static Item FindLightInactiveScan(Vector3 pos, string name, string itemType, float maxDist)
        {
            float maxSq = maxDist * maxDist;
            Item[] all = WorldQueryHelper.GetCachedSceneComponents<Item>();
            Item best = null;
            float bestSq = maxSq;
            for (int i = 0; i < all.Length; i++)
            {
                Item item = all[i];
                if (item == null || !item.gameObject.scene.IsValid()) continue;
                if (!item.isLight && !item.switchable) continue;
                if (!LightNameOrTypeMatches(item, name, itemType)) continue;
                float dSq = XzDistSq(item.transform.position, pos);
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    best = item;
                }
            }
            return best;
        }

        private static bool LightNameOrTypeMatches(Item item, string name, string itemType)
        {
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(itemType))
                return true;
            string iname = item.name ?? "";
            string bare = iname.Replace("(Clone)", "").Trim();
            if (!string.IsNullOrEmpty(name))
            {
                if (iname.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || bare.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (!string.IsNullOrEmpty(itemType))
            {
                string t = item.invItem != null ? item.invItem.type : null;
                if (!string.IsNullOrEmpty(t)
                    && t.Equals(itemType, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (bare.Equals(itemType, StringComparison.OrdinalIgnoreCase)
                    || iname.IndexOf(itemType, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            // If the name is missing but the type is empty, allow the nearest
            // switchable object found by sphere callers.
            // only when both filters empty (handled above). With name set and no match: fail.
            return string.IsNullOrEmpty(name) && string.IsNullOrEmpty(itemType);
        }

        /// <summary>Called from HandlePlayerAudio's IsStopSignal handler when the
        /// host broadcasts NotifyBodyPushStopped. Clears tracking tables; actual
        /// audio stop is handled by SoftStopNetwork / ForceStopByName.</summary>
        public static void TryStopBodyPushSound(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            _bodyPushSoundActive.Remove(objectName);
            if (_pushNameToGid.TryGetValue(objectName, out int __gid))
            {
                _lastPushSoundTime.Remove(__gid);
                _pushStationaryCount.Remove(__gid);
                _pushSoundFade.Remove(__gid);
                _pushSoundSource.Remove(__gid);
                _bodyPushSoundTimer.Remove(__gid);
                _pushGidToName.Remove(__gid);
                _pushNameToGid.Remove(objectName);
            }
        }

        /// <summary>Resets all cached state, tracked objects, and interpolation tables (used on scene change or disconnect).</summary>
        public static void Reset() => ResetCore(keepThrownLights: false);

        /// <summary>
        /// Host promotion (HostMigration promote handoff): same reset, but the thrown-light tables
        /// survive. Reset() cleared them, so a flare already burning in the world was no longer
        /// tracked by the new host and never faded or expired.
        /// </summary>
        public static void ResetForPromote() => ResetCore(keepThrownLights: true);

        private static void ResetCore(bool keepThrownLights)
        {
            _lastPos.Clear();
            _lastMoveTime.Clear();
            _lastDoorOpen.Clear();
            _lastTrapTriggered.Clear();
            _knownTraps.Clear();
            _trapResultCache.Clear();
            _doorKeyAge.Clear();
            _trapKeyAge.Clear();
            _generatorKeyAge.Clear();
            if (!keepThrownLights)
            {
                _thrownLights.Clear();
                _thrownById.Clear();
                _flareBurnStarts.Clear();
                _thrownLightFades.Clear();
            }
            TrapNetworkId.ResetSession();
            _lastGeneratorOn.Clear();
            _scanCenters.Clear();
            _scannedObjectIds.Clear();
            _objectInterp.Clear();
            _lastResolvedByName.Clear();
            _lastFullRbScanTime = -999f;
            // Release all client-kinematic rigidbodies on reset
            foreach (var kv in _clientKinematic)
            {
                var (rBody, _, objName) = kv.Value;
                if (rBody != null)
                    rBody.isKinematic = false;
                LanNetworkManager.NotifyBodyPushStopped(objName);
            }
            _clientKinematic.Clear();
            _bodyPushSoundTimer.Clear();
            _bodyPushSoundActive.Clear();
            _pushSoundAO.Clear();
            _pushSoundSource.Clear();
            _pushSoundFade.Clear();
            _lastPushSoundTime.Clear();
            _pushStationaryCount.Clear();
            _pushNameToGid.Clear();
            _pushGidToName.Clear();
            _clientKinematicGate.Clear();
            _lastGeneratorFuel.Clear();
            _lastClientUpdateTime.Clear();
            _nextSnapshotSequence = 0;
            _destroyDebounce.Clear();
            _outboundRemoveDebounce.Clear();
            ResetConsumedWorldPickups();
            _pendingLights.Clear();
            MovingObjectSoundService.Reset();
            ListTracker<Door>.Clear();
            ListTracker<Generator>.Clear();
            // Promotion runs only this reset (no network stop follows), so the tracker must be
            // rescanned there: an empty tracker left the new host broadcasting no NPCs.
            if (keepThrownLights)
                CharacterTracker.ResetForNetworkStop();
            else
                CharacterTracker.Clear();
        }

        /// <summary>
        /// Stamps one-off reliable physics/event snapshots that do not pass through
        /// <see cref="TryBuildWorldSnapshot"/>.
        /// </summary>
        public static PhysicsStateMessage StampSnapshot(PhysicsStateMessage state)
        {
            state.Reliable = true;
            if (state.Sequence == 0)
                state.Sequence = ++_nextSnapshotSequence;
            return state;
        }

        /// <summary>
        /// True while ThrownItem is mid-arc (not landed). Physics free-body stream must
        /// not own these. Kinematic interpolation cancels velocity and lands short.
        /// </summary>
    }
}
