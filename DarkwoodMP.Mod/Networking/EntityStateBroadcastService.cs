using System.Collections.Generic;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Periodically snapshots nearby entity positions and states, then broadcasts them
    /// to the connected client over an unreliable channel.
    /// </summary>
    public static class EntityStateBroadcastService
    {
        private static Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();
        private static float _sendTimer;
        private const float SendInterval = 0.1f;

        private const int MaxEntitiesPerPacket = 192;
        private static EntitySnapshotNet[] _buffer = new EntitySnapshotNet[MaxEntitiesPerPacket];
        private static readonly Dictionary<short, EntitySnapshotNet> _lastSent = new Dictionary<short, EntitySnapshotNet>();
        /// <summary>Round-robin start index so a full tracker list is not starved by the per-packet cap.</summary>
        private static int _scanStart;

        /// <summary>Sets the peers to broadcast to.</summary>
        public static void SetPeers(Dictionary<int, NetPeer> peers) => _peers = peers;

        /// <summary>
        /// Called every frame; accumulates time and sends a snapshot when the interval elapses.
        /// </summary>
        public static void Tick()
        {
            if (_peers == null || _peers.Count == 0)
                return;
            if (_paused) return;

            _sendTimer += Time.deltaTime;
            if (_sendTimer < SendInterval)
                return;

            _sendTimer = 0f;
            SendSnapshot();
        }

        /// <summary>
        /// Collects snapshots of entities within range of host or any remote player,
        /// and sends to all connected peers (unreliable, ~10 Hz).
        /// </summary>
        private static void SendSnapshot()
        {
            Character[] all = CharacterTracker.GetAll();
            if (all == null || all.Length == 0)
                return;

            int nAll = all.Length;
            int maxEntities = Mathf.Min(nAll, MaxEntitiesPerPacket);
            if (_buffer.Length < maxEntities)
                _buffer = new EntitySnapshotNet[maxEntities];

            // Full resync every ~1s (10 ticks) to correct drift
            if (++_fullResyncCounter >= 10)
            {
                _fullResyncCounter = 0;
                _lastSent.Clear();
            }

            int count = 0;

            Vector3 hostPos = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;
            // 3500-unit radius around host or any remote — matches WorldGrid proxy cull.
            float maxDistSq = 3500f * 3500f;

            if (_scanStart < 0 || _scanStart >= nAll)
                _scanStart = 0;

            // Round-robin so entities at the end of the tracker list still get updates
            // when more than MaxEntitiesPerPacket are dirty/in-range.
            for (int n = 0; n < nAll && count < maxEntities; n++)
            {
                int i = (_scanStart + n) % nAll;
                Character c = all[i];
                if (c == null) continue;

                // During dreams, skip entities that belong to the dream scene
                // (parented under the dream Location). Regular-world entities,
                // including ones spawned during the dream (ChomperHalf, Meat_marker,
                // etc.), continue to broadcast so the client sees them.
                if (Sync.DreamSyncManager.IsDreamActive)
                {
                    Transform dreamLoc = Sync.DreamSyncManager.GetDreamLocationTransform();
                    if (dreamLoc != null && c.transform.IsChildOf(dreamLoc))
                        continue;
                }

                Vector3 cPos = c.transform.position;
                float d1 = Vector3.SqrMagnitude(cPos - hostPos);
                // Skip entities too far from both the host and all remote players
                if (d1 > maxDistSq && !PlayerPositionManager.IsAnyRemoteWithinSq(cPos, maxDistSq))
                    continue;

                // Prefer Character.animator (cached body) over raw GetComponent for presentation.
                tk2dSpriteAnimator anim = null;
                try { anim = c.animator; } catch { /* dismantled */ }
                if (anim == null)
                    anim = c.GetComponent<tk2dSpriteAnimator>();
                string clip = anim != null && anim.CurrentClip != null ? anim.CurrentClip.name : "";
                short clipFrame = anim != null && anim.CurrentClip != null ? (short)anim.CurrentFrame : (short)-1;
                Vector3 rot = c.transform.eulerAngles;

                string entityName = c.name;
                // Strip "(Clone)" suffix added by Unity when instantiating prefabs
                if (entityName.EndsWith("(Clone)"))
                    entityName = entityName.Substring(0, entityName.Length - 7);

                string prefabPath = "";
                var ppc = c.GetComponent<PrefabPathComponent>();
                if (ppc != null)
                    prefabPath = ppc.Path;

                short id = CharacterTracker.GetStableId(c);
                if (id == 0)
                    continue;

                var snap = new EntitySnapshotNet
                {
                    Index = id,
                    PosX = cPos.x,
                    PosY = cPos.y,
                    PosZ = cPos.z,
                    RotY = rot.y,
                    Clip = clip,
                    ClipFrame = clipFrame,
                    Alive = c.alive,
                    HealthPct = (byte)Mathf.Clamp((c.Health / Mathf.Max(c.maxHealth, 1f)) * 100f, 0, 100),
                    EntityName = entityName,
                    PrefabPath = prefabPath
                };

                // Dirty-check: skip if nothing changed since last send
                if (_lastSent.TryGetValue(id, out var last) && !HasChanged(last, snap))
                    continue;

                _lastSent[id] = snap;
                _buffer[count] = snap;
                count++;
            }

            // Advance scan window for next tick
            _scanStart = (_scanStart + Mathf.Max(1, maxEntities / 2)) % nAll;

            if (count == 0)
                return;

            var writer = new NetWriter();
            writer.Put((byte)NetMessageType.EntityState);

            int entityCount = count;
            writer.Put(entityCount);
            for (int i = 0; i < entityCount; i++)
                _buffer[i].Serialize(writer);

            byte[] data = writer.CopyData();
            var net = LanNetworkManager.Instance;
            foreach (var kvp in _peers)
            {
                if (kvp.Value.ConnectionState != ConnectionState.Connected)
                    continue;
                // Skip joiners mid world LoadScene — dual-box host freeze when they stop PollEvents.
                if (net != null && !net.IsPeerReadyForGameplay(kvp.Key))
                    continue;
                kvp.Value.Send(data, DeliveryMethod.Unreliable);
            }

            _sendCount++;
            if (_sendCount % 10 == 0 && ModRuntime.VerboseLogging)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append($"[HostEntitySync] sending {entityCount} entities: ");
                for (int i = 0; i < entityCount; i++)
                {
                    Character c = CharacterTracker.FindByStableId(_buffer[i].Index);
                    if (c != null)
                    {
                        sb.Append(c.name);
                        sb.Append("(id=");
                        sb.Append(_buffer[i].Index);
                        sb.Append(") ");
                    }
                }
                ModRuntime.LegacyInfo(sb.ToString());
            }
        }

        private static int _sendCount;
        private static int _fullResyncCounter;
        private static bool _paused;

        /// <summary>Pauses broadcasting (positions frozen on receiver).</summary>
        public static void Pause() => _paused = true;

        /// <summary>Resumes broadcasting.</summary>
        public static void Resume() => _paused = false;

        /// <summary>Stops broadcasting and clears the peer reference.</summary>
        public static void Stop()
        {
            _peers?.Clear();
            _sendTimer = 0f;
            _lastSent.Clear();
            _fullResyncCounter = 0;
            _paused = false;
            _scanStart = 0;
        }

        private static bool HasChanged(EntitySnapshotNet last, EntitySnapshotNet current)
        {
            return last.PosX != current.PosX || last.PosY != current.PosY || last.PosZ != current.PosZ
                || last.RotY != current.RotY
                || last.Clip != current.Clip || last.ClipFrame != current.ClipFrame
                || last.Alive != current.Alive || last.HealthPct != current.HealthPct
                || last.EntityName != current.EntityName || last.PrefabPath != current.PrefabPath;
        }
    }
}
