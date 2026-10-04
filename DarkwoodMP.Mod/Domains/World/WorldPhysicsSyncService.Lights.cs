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
        public static int PendingLightCount => _s.PendingLights.Count;

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

        private const float PendingLightFlushInterval = 3f;

        /// <summary>
        /// Retry queued LightState after location/grid load.
        /// Use OverlapSphere rather than a scene-wide search on this path.
        /// </summary>
        public static void TryFlushPendingLights()
        {
            if (_s.PendingLights.Count == 0)
            {
                _s.PendingLightQueuedAt = -1f;
                return;
            }
            if (Player.Instance == null || Core.mainMenu || Core.loadingGame) return;

            float now = Time.unscaledTime;
            // Immediate flush when just entered a dream (caller may invoke right after load).
            bool dreamPad = Dreams.Instance != null && Dreams.Instance.dreaming;
            if (!dreamPad && now < _s.NextPendingLightFlushTime) return;
            _s.NextPendingLightFlushTime = now + (dreamPad ? 0.25f : PendingLightFlushInterval);

            if (_s.PendingLightQueuedAt < 0f)
                _s.PendingLightQueuedAt = now;
            if (now - _s.PendingLightQueuedAt > 30f)
            {
                ModLog.Event(LogCat.World,
                    "[LightApply] dropping " + _s.PendingLights.Count
                    + " pending light(s) after timeout (not in loaded grid)");
                _s.PendingLights.Clear();
                _s.PendingLightQueuedAt = -1f;
                return;
            }

            for (int i = _s.PendingLights.Count - 1; i >= 0; i--)
            {
                LightStateMessage ls = _s.PendingLights[i];
                Vector3 p = new Vector3(ls.PosX, ls.PosY, ls.PosZ);
                // Far map lights may be unloaded; retry when the player returns.
                // Keep all while dreaming (bunker pad is far from forest listen pos).
                if (!dreamPad && !Networking.ClientEntityInterpolationService.IsInClientInterest(p))
                {
                    _s.PendingLights.RemoveAt(i);
                    continue;
                }
                if (ApplyLightStateCore(ls, "pending", queueIfMissing: false))
                    _s.PendingLights.RemoveAt(i);
            }
            if (_s.PendingLights.Count == 0)
                _s.PendingLightQueuedAt = -1f;
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
            for (int i = 0; i < _s.PendingLights.Count; i++)
            {
                var p = _s.PendingLights[i];
                string pk = (p.ItemName ?? "") + "@"
                    + Mathf.Round(p.PosX) + "," + Mathf.Round(p.PosY) + "," + Mathf.Round(p.PosZ);
                if (pk == key)
                {
                    _s.PendingLights[i] = ls;
                    return;
                }
            }
            if (_s.PendingLights.Count >= MaxPendingLights)
                _s.PendingLights.RemoveAt(0);
            _s.PendingLights.Add(ls);
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

        /// <summary>Called by NotifyBodyPushStopped on this peer. Clears tracking tables; actual
        /// audio stop is handled by SoftStopNetwork / ForceStopByName.</summary>
        public static void TryStopBodyPushSound(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            _s.BodyPushSoundActive.Remove(objectName);
            if (_s.PushNameToGid.TryGetValue(objectName, out int __gid))
            {
                _s.LastPushSoundTime.Remove(__gid);
                _s.PushStationaryCount.Remove(__gid);
                _s.PushSoundFade.Remove(__gid);
                _s.PushSoundSource.Remove(__gid);
                _s.BodyPushSoundTimer.Remove(__gid);
                _s.PushGidToName.Remove(__gid);
                _s.PushNameToGid.Remove(objectName);
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
            // Hand back every rigidbody this client froze for interpolation before forgetting it.
            foreach (var kv in _s.ClientKinematic)
            {
                var (rBody, _, objName) = kv.Value;
                if (rBody != null)
                    rBody.isKinematic = false;
                LanNetworkManager.NotifyBodyPushStopped(objName);
            }

            // One object holds the whole session; a promotion keeps the flares already burning.
            _s = new SessionState(keepThrownLights ? _s.Thrown : new ThrownLightState());

            TrapNetworkId.ResetSession();
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
                state.Sequence = ++_s.NextSnapshotSequence;
            return state;
        }

        /// <summary>
        /// True while ThrownItem is mid-arc (not landed). Physics free-body stream must
        /// not own these. Kinematic interpolation cancels velocity and lands short.
        /// </summary>
    }
}
