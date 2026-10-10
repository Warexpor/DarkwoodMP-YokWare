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
    /// <summary>
    /// Object locate/spawn and pose-interp helpers (split from Apply for ownership).
    /// </summary>
    public static partial class WorldPhysicsSyncService
    {
        /// <summary>Removes an object from the interpolation dictionary so it stops being smoothed.</summary>
        /// <param name="go">The GameObject to remove.</param>
        public static void RemoveObjectFromInterpolation(GameObject go)
        {
            if (go == null) return;
            _s.ObjectInterp.Remove(go.GetInstanceID());
        }

        /// <summary>
        /// After remote E-drag ends (or local drag ends on host): drop client-push
        /// kinematic hold + interp by object name so the free body is draggable again.
        /// Without this, host kept isKinematic / claim side-effects and could not re-grab.
        /// </summary>
        public static void ReleaseClientPushHoldByName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;

            List<int> dropIds = null;
            foreach (var kv in _s.ClientKinematic)
            {
                if (string.Equals(kv.Value.objName, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    if (dropIds == null)
                    {
                        _snapStaleIntKeys.Clear();
                        dropIds = _snapStaleIntKeys;
                    }
                    dropIds.Add(kv.Key);
                }
            }

            if (dropIds != null)
            {
                for (int i = 0; i < dropIds.Count; i++)
                {
                    int id = dropIds[i];
                    if (_s.ClientKinematic.TryGetValue(id, out var kin))
                        ReleaseKinematic(kin.rb);
                    _s.ClientKinematic.Remove(id);
                    _s.ClientKinematicGate[id] = Time.time;
                    _s.ObjectInterp.Remove(id);
                    _s.BodyPushSoundActive.Remove(objectName);
                    _s.BodyPushSoundTimer.Remove(id);
                    if (_s.PushGidToName.TryGetValue(id, out string n) && string.Equals(n, objectName, StringComparison.OrdinalIgnoreCase))
                        _s.PushGidToName.Remove(id);
                    _s.PushNameToGid.Remove(objectName);
                }
            }

            // Name-only cleanup when gid maps already gone.
            _s.BodyPushSoundActive.Remove(objectName);
            _s.PushNameToGid.Remove(objectName);
        }

        /// <summary>
        /// Sets a new position/rotation target for an object and resets the interpolation
        /// state so it smoothly moves from its current position to the target over <paramref name="durationSec"/>.
        /// </summary>
        private static void SetObjectTarget(GameObject go, Vector3 targetPos, Vector3 targetRot, float durationSec = -1f)
        {
            int id = go.GetInstanceID();
            float now = Time.time;
            float duration = durationSec > 0.001f ? durationSec : InterpFixedDuration;

            if (_s.ObjectInterp.TryGetValue(id, out var state))
            {
                float dur = state.TargetTime - state.PrevTime;
                if (dur > 0.001f)
                {
                    // Catch up the previous state to the current render-time lerp position
                    float t = Mathf.Clamp01((now - state.PrevTime) / dur);
                    state.PrevPos = Vector3.Lerp(state.PrevPos, state.TargetPos, t);
                    Quaternion prevRotQ = Quaternion.Euler(state.PrevRot);
                    Quaternion targetRotQ = Quaternion.Euler(state.TargetRot);
                    state.PrevRot = Quaternion.Slerp(prevRotQ, targetRotQ, t).eulerAngles;
                }
                else
                {
                    state.PrevPos = state.TargetPos;
                    state.PrevRot = state.TargetRot;
                }
            }
            else
            {
                state.Target = go;
                // Prefer live rigidbody pose (kinematic drive) over transform if available.
                Rigidbody rb0 = go.GetComponent<Rigidbody>();
                if (rb0 != null)
                {
                    state.PrevPos = rb0.position;
                    state.PrevRot = rb0.rotation.eulerAngles;
                }
                else
                {
                    state.PrevPos = go.transform.position;
                    state.PrevRot = go.transform.eulerAngles;
                }
            }

            state.Target = go;

            // Large jumps (trap/door/object teleports across locations): hard-snap like
            // the client-push path (ClientPushSnapDistance). Generic 0.2s lerp left
            // furniture sliding across the map after a pad teleport.
            float jump = Vector3.Distance(state.PrevPos, targetPos);
            if (jump >= ClientPushSnapDistance)
            {
                state.PrevPos = targetPos;
                state.PrevRot = targetRot;
                Rigidbody rbSnap = go.GetComponent<Rigidbody>();
                if (rbSnap != null)
                {
                    rbSnap.position = targetPos;
                    rbSnap.rotation = Quaternion.Euler(targetRot);
                    rbSnap.velocity = Vector3.zero;
                    rbSnap.angularVelocity = Vector3.zero;
                }
                else
                {
                    go.transform.position = targetPos;
                    go.transform.rotation = Quaternion.Euler(targetRot);
                }
                // Short residual interp so a late packet does not re-lerp from far Prev.
                duration = Mathf.Min(duration, 0.05f);
            }

            state.TargetPos = targetPos;
            state.TargetRot = targetRot;
            state.PrevTime = now;
            state.TargetTime = now + duration;
            _s.ObjectInterp[id] = state;

            if (IsSceneFixedLightItem(go))
                return;

            // Lock to the host position during active sync to prevent proxy collisions.
            // on the client from pushing the object away from the host's position.
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Host must stay kinematic too while interpolating client-pushed free-bodies.
                LockKinematic(rb);
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Wall and fixed lamps use LightState for on/off; PhysicsState must not
        /// kinematic-lock (client walk-blocker).
        /// Floor / pushable lamps (<c>draggable</c> or ItemSounds moving scrape) are
        /// free bodies: stream pose so observers hear body-push MOS (DragSync already
        /// covered E-drag). Blanket <c>isLight</c> skip made Lamp_old_yellow_01 silent
        /// on push while chairs/wardrobe scraped normally.
        /// </summary>
        private static bool IsSceneFixedLightItem(GameObject go)
        {
            if (go == null) return false;
            Item item = go.GetComponent<Item>();
            // Movable lamps: same free-body path as stools / wardrobes.
            if (item != null && item.draggable)
                return false;
            ItemSounds sounds = go.GetComponent<ItemSounds>();
            if (sounds != null
                && (!string.IsNullOrEmpty(sounds.movingSound)
                    || !string.IsNullOrEmpty(sounds.movingSound_grass)))
                return false;

            if (item != null && item.isLight)
                return true;
            if (go.GetComponent<ItemLight>() != null)
                return true;
            string n = go.name ?? "";
            if (n.IndexOf("Lamp", StringComparison.OrdinalIgnoreCase) >= 0
                && (n.IndexOf("dream", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("switch", StringComparison.OrdinalIgnoreCase) >= 0
                    || (item != null && item.isLight)))
                return true;
            return false;
        }

        /// <summary>
        /// Drop kinematic/interp hold. Collider isTrigger is owned by host
        /// <c>DreamPropCollider</c> parity (GE isColliderTrigger), not guessed here.
        /// </summary>
        private static void RepairSceneFixedLightPhysics(GameObject go)
        {
            if (go == null) return;
            RemoveObjectFromInterpolation(go);
            ReleaseKinematic(go.GetComponent<Rigidbody>());
        }

        /// <summary>
        /// Look up a GameObject by name, then by proximity, then by spawning it
        /// from the <see cref="ItemsDatabase"/> if the <see cref="WorldObjectState.ItemType"/>
        /// is known.  Returns null only when every strategy fails.
        /// </summary>
        private static bool IsUsableResolveCandidate(
            GameObject candidate, string wantName, Vector3 targetPos, float maxDist)
        {
            if (candidate == null) return false;
            if (candidate.name != wantName) return false;
            if (candidate.GetComponent<Character>() != null) return false;
            if (candidate.GetComponent<DroppedItemIdentifier>() != null) return false;
            if (candidate.GetComponent<DeathDrop>() != null) return false;
            if (maxDist < float.MaxValue
                && Vector3.Distance(candidate.transform.position, targetPos) > maxDist)
                return false;
            return true;
        }

        private static GameObject RememberResolved(string name, GameObject go)
        {
            if (string.IsNullOrEmpty(name) || go == null)
                return go;
            if (!_s.LastResolvedByName.ContainsKey(name)
                && _s.LastResolvedByName.Count >= MaxResolvedByName)
            {
                // Drop one entry so long sessions cannot grow unboundedly.
                string drop = null;
                foreach (var k in _s.LastResolvedByName.Keys)
                {
                    drop = k;
                    break;
                }
                if (drop != null)
                    _s.LastResolvedByName.Remove(drop);
            }
            _s.LastResolvedByName[name] = go;
            return go;
        }

        /// <summary>Largest distance a wide-resolve (strategy 2) match may sit from the reported pose.</summary>
        private const float FullScanMaxDist = 50f;

        /// <summary>
        /// While a dream is active, a pad-side target may only resolve to a pad body and an
        /// overworld-side target never to one (the pad is a clone of overworld locations).
        /// </summary>
        private static bool IsSameWorldAsTarget(Transform candidate, Vector3 targetPos)
        {
            Transform pad = DreamSyncManager.GetDreamLocationTransform();
            if (pad == null) return true;
            bool targetOnPad = Vector3.Distance(targetPos, pad.position) <= 250f;
            bool candOnPad = candidate.IsChildOf(pad)
                || Vector3.Distance(candidate.position, pad.position) <= 250f;
            return targetOnPad == candOnPad;
        }

        /// <param name="allowSpawn">False for client-origin snapshots: the host owns item existence
        /// and must never mint a prefab because a client still reports an object the host removed.</param>
        private static GameObject FindOrSpawnObject(WorldObjectState obj, bool allowSpawn)
        {
            if (string.IsNullOrEmpty(obj.Name))
                return null;

            Vector3 targetPos = new Vector3(obj.PosX, obj.PosY, obj.PosZ);

            // Last successful resolve for this name (skip if destroyed / too far). Only taken when
            // no other object of that name sits closer to the reported spot: two identical chairs
            // a few steps apart swapped, the update for one moving the other.
            GameObject cached = null;
            if (_s.LastResolvedByName.TryGetValue(obj.Name, out GameObject c0)
                && IsUsableResolveCandidate(c0, obj.Name, targetPos, ResolvedNameMaxDist))
                cached = c0;

            // Strategy 1: overlap sphere near the reported position (avoids teleporting
            // objects with non-unique names because GameObject.Find can match any instance)
            int nearbyN = OverlapNear(targetPos, 1.5f);
            for (int i = 0; i < nearbyN; i++)
            {
                if (_overlap3D[i] == null || _overlap3D[i].attachedRigidbody == null) continue;
                GameObject candidate = _overlap3D[i].attachedRigidbody.gameObject;
                if (!IsUsableResolveCandidate(candidate, obj.Name, targetPos, float.MaxValue)) continue;
                return RememberResolved(obj.Name, candidate);
            }

            // The copy this name is driving trails the sender by the interpolation, more than
            // the resolve radii when pushed fast: its last target is where it is headed. Missed,
            // every state failed until the rate-limited full scan, and the copy stood, then jumped.
            if (c0 != null && IsUsableResolveCandidate(c0, obj.Name, targetPos, float.MaxValue)
                && _s.ObjectInterp.TryGetValue(c0.GetInstanceID(), out ObjectInterpState driving)
                && driving.Target == c0
                && Vector3.Distance(driving.TargetPos, targetPos) <= ResolvedNameMaxDist)
                return c0;

            // Strategy 1b: wider sphere before the wide resolve (client stutter when host
            // pushes objects away and can miss a small OverlapSphere query.
            {
                int wideN = OverlapNear(targetPos, 15f);
                GameObject bestWide = null;
                float bestWideDist = float.MaxValue;
                for (int i = 0; i < wideN; i++)
                {
                    if (_overlap3D[i] == null || _overlap3D[i].attachedRigidbody == null) continue;
                    GameObject candidate = _overlap3D[i].attachedRigidbody.gameObject;
                    if (candidate.name != obj.Name) continue;
                    if (!IsUsableResolveCandidate(candidate, obj.Name, targetPos, 15f)) continue;
                    float d = Vector3.Distance(candidate.transform.position, targetPos);
                    if (d < bestWideDist)
                    {
                        bestWideDist = d;
                        bestWide = candidate;
                    }
                }
                if (cached != null
                    && (bestWide == null || Vector3.Distance(cached.transform.position, targetPos) <= bestWideDist))
                    return cached;
                if (bestWide != null)
                    return RememberResolved(obj.Name, bestWide);
            }

            // Strategy 2 (rate-limited): every body within FullScanMaxDist. Bodies with a live
            // collider come from one wide OverlapSphere; pushable props without one (collider off,
            // object inactive) are Items, read from the Item registry. This used to be a
            // scene-wide Rigidbody FindObjectsOfType (35-50 ms), a dual-box hitch source.
            float nowScan = Time.time;
            if (nowScan - _s.LastFullRbScanTime >= FullRbScanMinInterval)
            {
                _s.LastFullRbScanTime = nowScan;
                DWMPHorde.Logging.ClientPerfProbe.NoteFullRbScan();
                GameObject best = null;
                float bestDist = float.MaxValue;

                void Consider(GameObject candidate)
                {
                    // Bounded: an unbounded name match found the overworld twin of a dream-pad
                    // crate (≈70k units away) and the hard snap then dragged it to -75000.
                    float d = Vector3.Distance(candidate.transform.position, targetPos);
                    if (d > FullScanMaxDist || d >= bestDist) return;
                    if (!IsUsableResolveCandidate(candidate, obj.Name, targetPos, FullScanMaxDist)) return;
                    if (!IsSameWorldAsTarget(candidate.transform, targetPos)) return;
                    bestDist = d;
                    best = candidate;
                }

                int farN = OverlapNear(targetPos, FullScanMaxDist);
                for (int i = 0; i < farN; i++)
                {
                    Rigidbody rb = _overlap3D[i] != null ? _overlap3D[i].attachedRigidbody : null;
                    if (rb != null)
                        Consider(rb.gameObject);
                }
                Item[] items = WorldQueryHelper.GetCachedSceneComponents<Item>();
                for (int i = 0; i < items.Length; i++)
                {
                    Item it = items[i];
                    if (it == null) continue;
                    // Distance first: GetComponent and name reads only for the few close ones.
                    if ((it.transform.position - targetPos).sqrMagnitude > FullScanMaxDist * FullScanMaxDist)
                        continue;
                    if (it.GetComponent<Rigidbody>() != null)
                        Consider(it.gameObject);
                }
                if (best != null)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[ObjectApply] found \"{best.name}\" via wide resolve ({bestDist.ToString("F1")} u from target)");
                    return RememberResolved(obj.Name, best);
                }
            }

            // Strategy 3: spawn from ItemsDatabase (cross-world-chunk support)
            if (!allowSpawn)
                return null;

            // Do not spawn a duplicate inside the active dream pad.
            // (client solid lamp / ghost bell) while the real prop already exists.
            if (DreamSyncManager.IsDreamActive
                || (Dreams.Instance != null && Dreams.Instance.dreaming))
                return null;

            string nameLower = obj.Name != null ? obj.Name.ToLowerInvariant() : "";
            if (nameLower.Contains("droppeditem") || nameLower.Contains("deathdrop"))
                return null;

            if (string.IsNullOrEmpty(obj.ItemType))
                return null;

            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(obj.ItemType))
                return null;

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(obj.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
                return null;

            GameObject prefab = itemDef.item as GameObject;
            if (prefab == null)
                return null;

            Quaternion rot = Quaternion.Euler(obj.RotX, obj.RotY, obj.RotZ);
            GameObject spawned;
            bool prevNet = TraverseHack.GetExplicitFlag();
            try
            {
                TraverseHack.SetExplicitFlag(true);
                spawned = Core.AddPrefab(prefab, targetPos, rot, null);
                if (spawned == null)
                    spawned = UnityEngine.Object.Instantiate(prefab, targetPos, rot);
            }
            finally
            {
                TraverseHack.SetExplicitFlag(prevNet);
            }

            if (spawned != null)
            {
                Rigidbody rb = spawned.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.position = targetPos;
                    rb.rotation = rot;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                ModRuntime.LegacyInfo($"[ObjectApply] spawned \"{obj.Name}\" type={obj.ItemType} at {targetPos}");
            }

            return spawned;
        }
    }
}
