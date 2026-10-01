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
        public static void UpdateObjectInterpolation()
        {
            if (ModRuntime.Network == null)
                return;

            // Periodic door tracker cleanup on both host and client rescans
            // for any doors whose Awake was missed by the Harmony patch.
            ListTracker<Door>.Cleanup();

            _objectInterpDeadKeys.Clear();
            _objectInterpKeys.Clear();
            _objectInterpKeys.AddRange(_objectInterp.Keys);
            float now = Time.time;

            if (_objectInterp.Count > 0 && now - _objInterpLastLogTime >= 30f)
            {
                _objInterpLastLogTime = now;
                ModRuntime.LegacyInfo($"[ObjInterp] active={_objectInterp.Count}");
            }

            for (int oi = 0; oi < _objectInterpKeys.Count; oi++)
            {
                int key = _objectInterpKeys[oi];
                var s = _objectInterp[key];

                if (s.Target == null)
                {
                    _objectInterpDeadKeys.Add(key);
                    continue;
                }

                if (!s.CachedComps || s.CachedRb == null && s.CachedItem == null)
                {
                    s.CachedRb = s.Target.GetComponent<Rigidbody>();
                    s.CachedItem = s.Target.GetComponent<Item>();
                    s.CachedComps = true;
                    _objectInterp[key] = s;
                }

                // Skip objects being dragged by the local player; local physics
                // (HingeJoint) already drives the correct position. Interpolation
                // would fight the joint and cause jitter.
                Item item = s.CachedItem;
                if (item != null && item.beingDragged)
                {
                    // Don't remove from interpolation yet; the drag might end
                    // and we want to resume smoothly. Just skip position update.
                    // Release kinematic so the local HingeJoint can drive position.
                    Rigidbody dragRb = s.CachedRb;
                    if (dragRb != null && dragRb.isKinematic)
                        dragRb.isKinematic = false;
                    continue;
                }

                // Remove stale entries: no new snapshot received for a while.
                // TargetTime is in the future (PrevTime + InterpFixedDuration), so
                // the effective timeout is InterpFixedDuration + 1.5s since PrevTime.
                if (s.PrevTime > 0 && now - s.PrevTime > (InterpFixedDuration + 1.5f))
                {
                    _objectInterpDeadKeys.Add(key);
                    continue;
                }

                // Fixed-duration interpolation moves from PrevPos to TargetPos.
                // smoothly over InterpFixedDuration seconds.
                float duration = s.TargetTime - s.PrevTime; // always InterpFixedDuration
                float elapsed = now - s.PrevTime;
                float t = duration > 0.001f ? Mathf.Clamp01(elapsed / duration) : 1f;

                Vector3 lerpPos = Vector3.Lerp(s.PrevPos, s.TargetPos, t);
                Quaternion prevRotQ = Quaternion.Euler(s.PrevRot);
                Quaternion targetRotQ = Quaternion.Euler(s.TargetRot);
                Quaternion lerpRot = Quaternion.Slerp(prevRotQ, targetRotQ, t);

                Rigidbody rb = s.CachedRb;
                bool hostClientPush = rb != null && _clientKinematic.ContainsKey(key);
                if (t < 1f || hostClientPush)
                {
                    // Active lerp, or hold last target on host while waiting for next
                    // client PhysicsState (avoids freeze-then-teleport between packets).
                    Vector3 pos = t < 1f ? lerpPos : s.TargetPos;
                    Quaternion rot = t < 1f ? lerpRot : Quaternion.Euler(s.TargetRot);
                    if (rb != null)
                    {
                        if (!rb.isKinematic)
                            rb.isKinematic = true;
                        rb.position = pos;
                        rb.rotation = rot;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    else
                    {
                        s.Target.transform.position = pos;
                        s.Target.transform.rotation = rot;
                    }
                }
                else
                {
                    // Interpolation complete (non-client-push). Release kinematic so the
                    // local peer can push / interact. Next snapshot re-locks.
                    if (rb != null && ModRuntime.Network != null && ModRuntime.Network.Role != NetworkRole.Host)
                        rb.isKinematic = false;
                    // Non-rigidbody objects still need transform driven.
                    if (rb == null)
                    {
                        s.Target.transform.position = lerpPos;
                        s.Target.transform.rotation = lerpRot;
                    }
                }
            }

            foreach (int key in _objectInterpDeadKeys)
            {
                if (_objectInterp.TryGetValue(key, out var deadState) && deadState.Target != null
                    && ModRuntime.Network != null && ModRuntime.Network.Role != NetworkRole.Host)
                {
                    Rigidbody rb = deadState.Target.GetComponent<Rigidbody>();
                    if (rb != null)
                        rb.isKinematic = false;
                    // Stop drag sound when the remote object falls out of sync
                    // (host walked away, object out of scan range, etc.) so
                    // the sound doesn't play forever on the client.
                    LanNetworkManager.NotifyBodyPushStopped(deadState.Target.name);
                }
                _objectInterp.Remove(key);
            }

            // Unified scrape-loop fades + occlusion (drag + body-push).
            MovingObjectSoundService.Tick();

            // Local free-body push: ForceStop when player stops / leaves contact (T3).
            ItemMovingSoundHelper.TickLocalPushScrapeStop();

            // Safety net if PhysicsState packets stop entirely: soft-stop after ~1 missed
            // 10Hz tick + margin. Decision lag is the bug; SoftStop fade is vanilla 0.5s.
            float __srcCleanupNow = Time.time;
            _stalePushSrcKeys.Clear();
            foreach (var __kv in _lastPushSoundTime)
            {
                if ((__srcCleanupNow - __kv.Value) > 0.15f)
                    _stalePushSrcKeys.Add(__kv.Key);
            }
            for (int __si = 0; __si < _stalePushSrcKeys.Count; __si++)
            {
                int __k = _stalePushSrcKeys[__si];
                if (_pushGidToName.TryGetValue(__k, out var __akn))
                {
                    ItemMovingSoundHelper.SoftStopNetwork(__akn);
                    _pushNameToGid.Remove(__akn);
                }
                _lastPushSoundTime.Remove(__k);
                _pushStationaryCount.Remove(__k);
                _pushGidToName.Remove(__k);
                _pushSoundSource.Remove(__k);
                _pushSoundFade.Remove(__k);
            }

            // Release path for client-kinematic objects. This runs every frame
            // in LateUpdate, including when TryBuildWorldSnapshot is paused.
            float nowK = Time.time;
            _staleKinematicKeys.Clear();
            foreach (var kv in _clientKinematic)
            {
                if (nowK >= kv.Value.releaseTime)
                {
                    var (rBody, _, oName) = kv.Value;
                    if (rBody != null)
                        rBody.isKinematic = false;
                    if (!string.IsNullOrEmpty(oName) && _bodyPushSoundActive.Remove(oName))
                        LanNetworkManager.NotifyBodyPushStopped(oName);
                    _staleKinematicKeys.Add(kv.Key);
                }
            }
            for (int ski = 0; ski < _staleKinematicKeys.Count; ski++)
            {
                int id = _staleKinematicKeys[ski];
                _clientKinematic.Remove(id);
                _bodyPushSoundTimer.Remove(id);
                _pushSoundAO.Remove(id);
                _pushSoundSource.Remove(id);
                _lastPushSoundTime.Remove(id);
                _pushStationaryCount.Remove(id);
                if (_pushGidToName.TryGetValue(id, out var __skn))
                {
                    _bodyPushSoundActive.Remove(__skn);
                    _pushNameToGid.Remove(__skn);
                }
                _pushGidToName.Remove(id);
            }
        }

        /// <summary>
        /// Applies a generator's on/off state and fuel level to the local world.
        /// Uses Item.turnOn/turnOff when present so ItemSounds (start + loop / stop)
        /// match the local player path. Generator.turnOn alone does not start audio.
        /// </summary>
        private static void ApplyGeneratorState(Generator gen, bool isOn, float fuel, bool lowPower = false)
        {
            // Fuel first: Generator.turnOn no-ops when fuel <= 0.
            gen.fuel = fuel;
            bool wasOn = gen.isOn;

            if (gen.isOn != isOn)
            {
                Item item = gen.GetComponent<Item>();
                if (isOn)
                {
                    if (item != null)
                        DialogHostApplyGuard.RunHostWorldFanout(() => item.turnOn());
                    else
                        DialogHostApplyGuard.RunHostWorldFanout(() => gen.turnOn());
                }
                else
                {
                    if (item != null)
                        DialogHostApplyGuard.RunHostWorldFanout(() => item.turnOff());
                    else
                        DialogHostApplyGuard.RunHostWorldFanout(() => gen.turnOff());
                }

                // Belt: if item.turnOn early-out (fuel/disabled) left isOn wrong, force gen + SFX.
                ItemSounds snd = item != null ? item.GetComponent<ItemSounds>() : null;
                if (isOn && !gen.isOn && fuel > 0f)
                {
                    gen.turnOn();
                    if (item != null) item.isOn = true;
                }
                if (wasOn != gen.isOn && snd != null)
                {
                    if (gen.isOn)
                        snd.playStart();
                    else
                        snd.playStop();
                }

                ModRuntime.LegacyInfo($"[GeneratorApply] isOn {wasOn}→{gen.isOn} fuel={fuel.ToString("F0")} at {gen.transform.position}");
            }

            if (gen.lowPower != lowPower)
                gen.setLowPower(lowPower);
        }

        private static float _nextDreamPropColliderBroadcast; // process-scoped: rate limit
        private const float DreamPropColliderMinInterval = 0.35f;
        private static Item[] _dreamPropItemsCache; // reset-in: ResetCore
        private static int _dreamPropItemsRootId; // reset-in: ResetCore
        private static readonly System.Collections.Generic.List<DreamPropColliderMessage.Entry> _dreamPropEntries = // process-scoped: scratch
            new System.Collections.Generic.List<DreamPropColliderMessage.Entry>(64);
        private static DreamPropColliderMessage.Entry[] _dreamPropEntryBuf = // process-scoped: scratch
            System.Array.Empty<DreamPropColliderMessage.Entry>();

        /// <summary>Drop pad Item cache on dream enter/exit so collider fan-out rescans.</summary>
        public static void InvalidateDreamPropColliderCache()
        {
            _dreamPropItemsCache = null;
            _dreamPropItemsRootId = 0;
        }

        private static Item[] GetDreamPropItems(Transform dreamRoot, bool forceRefresh)
        {
            int rootId = dreamRoot.GetInstanceID();
            if (forceRefresh
                || _dreamPropItemsCache == null
                || _dreamPropItemsRootId != rootId)
            {
                _dreamPropItemsCache = dreamRoot.GetComponentsInChildren<Item>(true)
                    ?? System.Array.Empty<Item>();
                _dreamPropItemsRootId = rootId;
            }
            return _dreamPropItemsCache;
        }

        /// <summary>
        /// Host: snapshot Item collider isTrigger under the dream pad and fan-out so
        /// clients match walk-through lamps / solid bells after isColliderTrigger GEs.
        /// </summary>
        public static void HostBroadcastDreamPropColliders(bool force = false)
        {
            if (!NetGuard.ConnectedHost(out var net))
                return;
            if (!DreamSyncManager.IsDreamActive && (Dreams.Instance == null || !Dreams.Instance.dreaming))
                return;
            float now = Time.unscaledTime;
            if (!force && now < _nextDreamPropColliderBroadcast)
                return;
            _nextDreamPropColliderBroadcast = now + DreamPropColliderMinInterval;

            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            if (dreamRoot == null) return;

            Item[] items = GetDreamPropItems(dreamRoot, force);
            if (items == null || items.Length == 0) return;

            _dreamPropEntries.Clear();
            for (int i = 0; i < items.Length && _dreamPropEntries.Count < 64; i++)
            {
                Item item = items[i];
                if (item == null) continue;
                Collider col = item.GetComponent<Collider>();
                if (col == null) continue;
                // Skip huge static environment; keep lights, bells, push props.
                string n = item.name ?? "";
                bool interesting = item.isLight
                    || item.draggable
                    || n.IndexOf("Lamp", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Bell", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("bell", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("karuzela", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("SWITCH", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || item.GetComponent<ItemLight>() != null;
                if (!interesting) continue;

                Vector3 p = item.transform.position;
                _dreamPropEntries.Add(new DreamPropColliderMessage.Entry
                {
                    Name = n,
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    IsTrigger = col.isTrigger
                });
            }

            if (_dreamPropEntries.Count == 0) return;

            int nEntries = _dreamPropEntries.Count;
            if (_dreamPropEntryBuf.Length != nEntries)
                _dreamPropEntryBuf = new DreamPropColliderMessage.Entry[nEntries];
            for (int i = 0; i < nEntries; i++)
                _dreamPropEntryBuf[i] = _dreamPropEntries[i];

            var msg = new DreamPropColliderMessage { Entries = _dreamPropEntryBuf };
            net.Broadcast(NetMessageType.DreamPropCollider,
                w => msg.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[DreamPropCollider] host broadcast {nEntries} collider(s)");
        }

        /// <summary>Client: apply host dream collider isTrigger flags.</summary>
        public static void ApplyDreamPropColliders(DreamPropColliderMessage msg)
        {
            if (msg.Entries == null || msg.Entries.Length == 0) return;
            int applied = 0;
            for (int i = 0; i < msg.Entries.Length; i++)
            {
                var e = msg.Entries[i];
                Vector3 pos = new Vector3(e.PosX, e.PosY, e.PosZ);
                GameObject go = FindDreamPropForCollider(e.Name, pos);
                if (go == null) continue;
                Collider col = go.GetComponent<Collider>();
                if (col == null) continue;
                if (col.isTrigger == e.IsTrigger) continue;
                col.isTrigger = e.IsTrigger;
                if (IsSceneFixedLightItem(go))
                    RepairSceneFixedLightPhysics(go);
                applied++;
                ModRuntime.LegacyInfo(
                    $"[DreamPropCollider] {go.name} isTrigger→{e.IsTrigger}");
            }
            if (applied > 0)
                ModRuntime.LegacyInfo($"[DreamPropCollider] applied {applied}");
        }

        private static GameObject FindDreamPropForCollider(string name, Vector3 pos)
        {
            if (string.IsNullOrEmpty(name)) return null;
            int nearN = OverlapNear(pos, 8f);
            GameObject best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < nearN; i++)
            {
                if (_overlap3D[i] == null) continue;
                Transform t = _overlap3D[i].transform;
                GameObject root = _overlap3D[i].attachedRigidbody != null
                    ? _overlap3D[i].attachedRigidbody.gameObject
                    : t.gameObject;
                if (root == null) continue;
                string n = root.name ?? "";
                if (!n.Equals(name, System.StringComparison.OrdinalIgnoreCase)
                    && n.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                float d = Vector3.Distance(root.transform.position, pos);
                if (d < bestD)
                {
                    bestD = d;
                    best = root;
                }
            }
            if (best != null) return best;

            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            if (dreamRoot == null) return null;
            // Prefer cached pad Items (same set host broadcasts) over Transform FoT.
            Item[] items = GetDreamPropItems(dreamRoot, forceRefresh: false);
            for (int i = 0; i < items.Length; i++)
            {
                Item item = items[i];
                if (item == null) continue;
                string n = item.name ?? "";
                if (n.Equals(name, System.StringComparison.OrdinalIgnoreCase)
                    || n.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return item.gameObject;
            }
            return null;
        }

        /// <summary>
        /// LightState that arrived before the Item existed (unloaded location grid).
        /// Flushed by <see cref="TryFlushPendingLights"/> once the world is ready.
        /// </summary>
        private static readonly List<LightStateMessage> _pendingLights = new List<LightStateMessage>(32); // reset-in: ResetCore
    }
}
