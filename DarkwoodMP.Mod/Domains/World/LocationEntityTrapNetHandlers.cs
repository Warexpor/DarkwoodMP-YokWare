using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Entity spawn relay + host-authoritative trap trigger handlers.</summary>
    internal sealed class LocationEntityTrapNetHandlers
    {
        private readonly LanNetworkManager _net;

        // Debounce duplicate trap triggers from multi-collider contacts or retries.
        private readonly Dictionary<string, float> _trapTriggerDebounce = new Dictionary<string, float>();
        private readonly List<string> _trapDebounceStaleKeys = new List<string>(8);
        private const float TrapTriggerDebounceSec = 0.4f;

        internal LocationEntityTrapNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleEntitySpawn(EntitySpawnMessage msg)
        {
            if (string.IsNullOrEmpty(msg.PrefabPath))
                return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);

            try
            {
                GameObject go = Core.AddPrefab(msg.PrefabPath, pos, rot, null);
                if (go != null)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[Physics] spawned: {msg.PrefabPath} at {pos}");
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[PhysicsSpawnSync] failed to spawn {msg.PrefabPath}: {ex}");
            }

            // Client-originated spawn: host already applied; relay to other clients (3+).
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0)
            {
                _net.SendToAllExcept(_net.CurrentReceivePlayerId, NetMessageType.EntitySpawn,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
        }

        internal void HandleTrapTriggered(TrapTriggeredMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            string debounceKey = msg.TrapNetId > 0
                ? "id:" + msg.TrapNetId
                : $"{msg.PosX:F1}_{msg.PosY:F1}_{msg.PosZ:F1}";
            float now = Time.time;
            if (_trapTriggerDebounce.TryGetValue(debounceKey, out float last) && now - last < TrapTriggerDebounceSec)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[TrapTrigger] Host: debounced duplicate at {pos}");
                return;
            }
            _trapTriggerDebounce[debounceKey] = now;
            if (_trapTriggerDebounce.Count > 64)
            {
                _trapDebounceStaleKeys.Clear();
                foreach (var kvp in _trapTriggerDebounce)
                    if (now - kvp.Value > 5f) _trapDebounceStaleKeys.Add(kvp.Key);
                for (int i = 0; i < _trapDebounceStaleKeys.Count; i++)
                    _trapTriggerDebounce.Remove(_trapDebounceStaleKeys[i]);
            }

            GameObject go = msg.TrapNetId > 0
                ? Sync.TrapNetworkId.FindById(msg.TrapNetId)
                : null;
            if (go == null)
                go = WorldPhysicsSyncService.FindTrapByPos(pos);
            if (go == null)
            {
                Sync.TrapNetworkId.QueuePending(msg.TrapNetId, pos, triggered: true);
                ModRuntime.LegacyInfo($"[TrapTrigger] Host: no trap at {pos} id={msg.TrapNetId} — queued pending");
                return;
            }

            int trapId = msg.TrapNetId > 0
                ? msg.TrapNetId
                : Sync.TrapNetworkId.GetOrMintHost(go);
            Sync.TrapNetworkId.Ensure(go, trapId);

            // Already disarmed or sprung; never re-trigger a late TrapTriggered after silent disarm.
            if (WorldPhysicsSyncService.ReadTrapTriggered(go))
            {
                ModRuntime.LegacyInfo(
                    $"[TrapTrigger] Host: trap already triggered id={trapId} at {pos} — skip");
                return;
            }

            try
            {
                // Client TrapTriggered is the stomp or walk path. Silent disarm
                // uses TrapState directly.
                WorldPhysicsSyncService.ApplyTrapState(go, triggered: true, silentDisarm: false);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogError(
                    $"[TrapTrigger] ApplyTrapState failed id={trapId}: {ex.Message}");
                return;
            }

            if (go == null) return;
            var triggerSnd = go.GetComponent<Trigger>();
            if (triggerSnd != null && !string.IsNullOrEmpty(triggerSnd.activateSound))
            {
                try { AudioController.Play(triggerSnd.activateSound, pos); }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[TrapTrigger] activateSound failed: " + ex.Message);
                }
            }

            Vector3 key = new Vector3(
                Mathf.Round(pos.x * 10f) / 10f,
                Mathf.Round(pos.y * 10f) / 10f,
                Mathf.Round(pos.z * 10f) / 10f);
            short occupant = WorldPhysicsSyncService.ResolveTrapOccupant(trapId, key);
            _net.SendTrapState(new TrapState
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                Triggered = true,
                TrapNetId = trapId,
                OccupantPlayerId = occupant
            });
            ModRuntime.LegacyInfo($"[TrapTrigger] Host: applied triggered=true to {go.name} id={trapId} at {pos}");
        }
    }
}
