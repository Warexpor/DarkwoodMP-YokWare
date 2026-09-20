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
                ModRuntime.LegacyInfo("[ObjInterp] active=" + _objectInterp.Count);
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
            List<int> __staleSrcKeys = null;
            foreach (var __kv in _lastPushSoundTime)
            {
                if ((__srcCleanupNow - __kv.Value) > 0.15f)
                {
                    if (__staleSrcKeys == null) __staleSrcKeys = new List<int>();
                    __staleSrcKeys.Add(__kv.Key);
                }
            }
            if (__staleSrcKeys != null)
                foreach (int __k in __staleSrcKeys)
                {
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
            List<int> staleKin = null;
            foreach (var kv in _clientKinematic)
            {
                if (nowK >= kv.Value.releaseTime)
                {
                    var (rBody, _, oName) = kv.Value;
                    if (rBody != null)
                        rBody.isKinematic = false;
                    if (!string.IsNullOrEmpty(oName) && _bodyPushSoundActive.Remove(oName))
                        LanNetworkManager.NotifyBodyPushStopped(oName);
                    if (staleKin == null) staleKin = new List<int>();
                    staleKin.Add(kv.Key);
                }
            }
            if (staleKin != null)
                foreach (int id in staleKin)
                {
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
                    // Prefer Item.turnOn (playStart + particles + Generator.turnOn).
                    // Generator.turnOn alone does not start ItemSounds.
                    if (item != null)
                        item.turnOn();
                    else
                        gen.turnOn();
                }
                else
                {
                    if (item != null)
                        item.turnOff();
                    else
                        gen.turnOff();
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

                ModRuntime.LegacyInfo("[GeneratorApply] isOn " + wasOn + "→" + gen.isOn
                    + " fuel=" + fuel.ToString("F0") + " at " + gen.transform.position);
            }

            if (gen.lowPower != lowPower)
                gen.setLowPower(lowPower);
        }

        private static float _nextDreamPropColliderBroadcast;
        private const float DreamPropColliderMinInterval = 0.35f;

        /// <summary>
        /// Host: snapshot Item collider isTrigger under the dream pad and fan-out so
        /// clients match walk-through lamps / solid bells after isColliderTrigger GEs.
        /// </summary>
        public static void HostBroadcastDreamPropColliders(bool force = false)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            if (!DreamSyncManager.IsDreamActive && (Dreams.Instance == null || !Dreams.Instance.dreaming))
                return;
            float now = Time.unscaledTime;
            if (!force && now < _nextDreamPropColliderBroadcast)
                return;
            _nextDreamPropColliderBroadcast = now + DreamPropColliderMinInterval;

            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            if (dreamRoot == null) return;

            Item[] items = dreamRoot.GetComponentsInChildren<Item>(true);
            if (items == null || items.Length == 0) return;

            var list = new System.Collections.Generic.List<DreamPropColliderMessage.Entry>(32);
            for (int i = 0; i < items.Length && list.Count < 64; i++)
            {
                Item item = items[i];
                if (item == null) continue;
                Collider col = item.GetComponent<Collider>();
                if (col == null) continue;
                // Skip huge static environment; keep lights, bells, push props.
                string n = item.name ?? "";
                bool interesting = item.isLight
                    || item.GetComponent<ItemLight>() != null
                    || n.IndexOf("Lamp", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Bell", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("bell", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("karuzela", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("SWITCH", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || item.draggable;
                if (!interesting) continue;

                Vector3 p = item.transform.position;
                list.Add(new DreamPropColliderMessage.Entry
                {
                    Name = n,
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    IsTrigger = col.isTrigger
                });
            }

            if (list.Count == 0) return;

            var msg = new DreamPropColliderMessage { Entries = list.ToArray() };
            net.Broadcast(NetMessageType.DreamPropCollider,
                w => msg.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                "[DreamPropCollider] host broadcast " + list.Count + " collider(s)");
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
                    "[DreamPropCollider] " + go.name + " isTrigger→" + e.IsTrigger);
            }
            if (applied > 0)
                ModRuntime.LegacyInfo("[DreamPropCollider] applied " + applied);
        }

        private static GameObject FindDreamPropForCollider(string name, Vector3 pos)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Collider[] near = Physics.OverlapSphere(pos, 8f);
            GameObject best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < near.Length; i++)
            {
                if (near[i] == null) continue;
                Transform t = near[i].transform;
                GameObject root = near[i].attachedRigidbody != null
                    ? near[i].attachedRigidbody.gameObject
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
            Transform[] all = dreamRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                string n = all[i].name ?? "";
                if (n.Equals(name, System.StringComparison.OrdinalIgnoreCase)
                    || n.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return all[i].gameObject;
            }
            return null;
        }

        /// <summary>
        /// LightState that arrived before the Item existed (unloaded location grid).
        /// Flushed by <see cref="TryFlushPendingLights"/> once the world is ready.
        /// </summary>
        private static readonly List<LightStateMessage> _pendingLights = new List<LightStateMessage>(32);
    }
}
