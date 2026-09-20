using System;
using System.Collections.Generic;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Periodically snapshots nearby entity positions and states, then broadcasts them
    /// to connected peers (LAN LiteNetLib or Steam P2P) on an unreliable channel.
    /// </summary>
    public static class EntityStateBroadcastService
    {
        private static float _sendTimer;
        private static uint _nextSnapshotSequence;
        private const float SendInterval = 0.1f;

        /// <summary>Keep the batch large enough that dense night scenes do not starve later entities.</summary>
        private const int MaxEntitiesPerPacket = 256;
        /// <summary>Near-player band filled first so far wildlife cannot starve combat NPCs.</summary>
        private const float PriorityDistance = 1400f;
        private static EntitySnapshotNet[] _buffer = new EntitySnapshotNet[MaxEntitiesPerPacket];
        private static readonly Dictionary<short, EntitySnapshotNet> _lastSent = new Dictionary<short, EntitySnapshotNet>();
        /// <summary>Stable stripped name / prefab path — avoid Unity <c>name</c> + Substring + GetComponent every 10 Hz.</summary>
        private static readonly Dictionary<short, string> _cachedEntityNames = new Dictionary<short, string>(128);
        private static readonly Dictionary<short, string> _cachedPrefabPaths = new Dictionary<short, string>(128);
        /// <summary>Round-robin start index so a full tracker list is not starved by the per-packet cap.</summary>
        private static int _scanStart;

        /// <summary>
        /// Called every frame; accumulates time and sends a snapshot when the interval elapses.
        /// </summary>
        public static void Tick()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            if (_paused) return;

            _sendTimer += Time.deltaTime;
            if (_sendTimer < SendInterval)
                return;

            _sendTimer = 0f;
            SendSnapshot(net);
        }

        /// <summary>
        /// Collects snapshots of entities within range of host or any remote player,
        /// and sends to all connected peers (unreliable, ~10 Hz).
        /// </summary>
        private static readonly NetWriter _snapWriter = new NetWriter();
        private static byte[] _snapSendBuf = Array.Empty<byte>();

        private static void SendSnapshot(LanNetworkManager net)
        {
            // CopyAll: no ToArray alloc every 100ms (dual-box host hitch with 100+ tracked AI).
            int nAll = CharacterTracker.CopyAll(out Character[] all);
            if (nAll == 0)
                return;

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
            // Matches WorldGrid proxy cull / client interest (XZ).
            float maxDistSq = GameplayConstants.EntityActivationRange * GameplayConstants.EntityActivationRange;
            float priorityDistSq = PriorityDistance * PriorityDistance;

            if (_scanStart < 0 || _scanStart >= nAll)
                _scanStart = 0;

            // Pass 0: near any player (combat / presentation critical).
            // Pass 1: rest of host broadcast radius (fills remaining slots).
            for (int pass = 0; pass < 2 && count < maxEntities; pass++)
            {
                bool nearOnly = pass == 0;
                for (int n = 0; n < nAll && count < maxEntities; n++)
                {
                    int i = (_scanStart + n) % nAll;
                    Character c = all[i];
                    if (c == null) continue;

                    // During dreams, stream dream NPCs only; skip frozen overworld AI.
                    if (Sync.DreamSyncManager.IsDreamActive
                        && Sync.DreamSyncManager.IsWorldFrozenForComponent(c))
                        continue;

                    Vector3 cPos = c.transform.position;
                    float dxh = cPos.x - hostPos.x;
                    float dzh = cPos.z - hostPos.z;
                    float dHost = dxh * dxh + dzh * dzh;
                    bool nearHost = dHost <= priorityDistSq;
                    bool nearRemote = PlayerPositionManager.IsAnyRemoteWithinSq(cPos, priorityDistSq);
                    bool inPriority = nearHost || nearRemote;
                    if (nearOnly != inPriority)
                        continue;

                    // Skip entities too far from both the host and all remote players
                    if (dHost > maxDistSq && !PlayerPositionManager.IsAnyRemoteWithinSq(cPos, maxDistSq))
                        continue;

                    if (!TryBuildSnapshot(c, cPos, out EntitySnapshotNet snap))
                        continue;

                    // Dirty-check: skip if nothing changed since last send
                    if (_lastSent.TryGetValue(snap.Index, out var last) && !HasChanged(last, snap))
                        continue;

                    _lastSent[snap.Index] = snap;
                    _buffer[count] = snap;
                    count++;
                }
            }

            // Advance scan window for next tick
            _scanStart = (_scanStart + Mathf.Max(1, maxEntities / 2)) % nAll;

            if (count == 0)
                return;

            _snapWriter.Reset();
            _snapWriter.Put((byte)NetMessageType.EntityState);

            int entityCount = count;
            _snapWriter.Put(++_nextSnapshotSequence);
            _snapWriter.Put(entityCount);
            for (int i = 0; i < entityCount; i++)
                _buffer[i].Serialize(_snapWriter);

            _snapWriter.CopyDataInto(ref _snapSendBuf, out int sendLen);
            // Walk the connected peers directly; ConnectedPlayerIds allocated a List every tick.
            net.SendRawToReadyPeers(_snapSendBuf, sendLen, DeliveryMethod.Unreliable);
            DWMPHorde.Logging.ClientPerfProbe.NoteEntityBroadcast(entityCount);

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

        private static bool TryBuildSnapshot(Character c, Vector3 cPos, out EntitySnapshotNet snap)
        {
            snap = default;

            short id = CharacterTracker.GetStableId(c);
            if (id == 0)
                return false;

            // Near a remote: WorldGrid edge cases can leave isActive/animator off while the
            // GO is still tracked; otherwise the client gets empty clips and sliding sprites. Wake
            // presentation components so processAnims can own Walk/Idle again.
            if (c.alive
                && PlayerPositionManager.IsAnyRemoteWithinSq(cPos, PriorityDistance * PriorityDistance)
                && (!c.isActive || (c.animator != null && !c.animator.enabled)))
            {
                try
                {
                    if (!c.gameObject.activeSelf)
                        c.gameObject.SetActive(true);
                    c.enableComponents(true);
                }
                catch { /* dismantled mid-frame */ }
            }

            // Prefer Character.animator (cached body) over raw GetComponent for presentation.
            tk2dSpriteAnimator anim = null;
            try { anim = c.animator; } catch { /* dismantled */ }
            if (anim == null)
                anim = c.GetComponent<tk2dSpriteAnimator>();

            // clipToPlay is what processAnims decided this frame; CurrentClip can be null
            // after enableComponents / SetActive cycles while clipToPlay is still Walk/Idle.
            string clip = "";
            try
            {
                if (!string.IsNullOrEmpty(c.clipToPlay))
                    clip = c.clipToPlay;
            }
            catch { /* odd prefab */ }
            if (string.IsNullOrEmpty(clip) && anim != null && anim.CurrentClip != null)
                clip = anim.CurrentClip.name;

            short clipFrame = anim != null && anim.CurrentClip != null ? (short)anim.CurrentFrame : (short)-1;
            Vector3 rot = c.transform.eulerAngles;
            byte healthPct = (byte)Mathf.Clamp((c.Health / Mathf.Max(c.maxHealth, 1f)) * 100f, 0, 100);
            byte flags = 0;
            if (c.sleeping) flags |= EntitySnapshotNet.FlagSleeping;
            if (c.eating) flags |= EntitySnapshotNet.FlagEating;
            bool alive = c.alive;

            // Cheap dirty gate before Unity name / PrefabPathComponent work.
            if (_lastSent.TryGetValue(id, out EntitySnapshotNet last)
                && last.PosX == cPos.x && last.PosY == cPos.y && last.PosZ == cPos.z
                && last.RotY == rot.y
                && last.ClipFrame == clipFrame
                && last.Alive == alive
                && last.HealthPct == healthPct
                && last.Flags == flags
                && string.Equals(last.Clip, clip, StringComparison.Ordinal))
            {
                return false;
            }

            if (!_cachedEntityNames.TryGetValue(id, out string entityName) || entityName == null)
            {
                entityName = c.name ?? "";
                if (entityName.EndsWith("(Clone)", StringComparison.Ordinal))
                    entityName = entityName.Substring(0, entityName.Length - 7);
                _cachedEntityNames[id] = entityName;
            }

            if (!_cachedPrefabPaths.TryGetValue(id, out string prefabPath))
            {
                prefabPath = "";
                var ppc = c.GetComponent<PrefabPathComponent>();
                if (ppc != null && ppc.Path != null)
                    prefabPath = ppc.Path;
                _cachedPrefabPaths[id] = prefabPath;
            }

            snap = new EntitySnapshotNet
            {
                Index = id,
                PosX = cPos.x,
                PosY = cPos.y,
                PosZ = cPos.z,
                RotY = rot.y,
                Clip = clip,
                ClipFrame = clipFrame,
                Alive = alive,
                HealthPct = healthPct,
                EntityName = entityName,
                PrefabPath = prefabPath,
                Flags = flags
            };
            return true;
        }

        private static int _sendCount;
        private static int _fullResyncCounter;
        private static bool _paused;

        /// <summary>Pauses broadcasting (positions frozen on receiver).</summary>
        public static void Pause() => _paused = true;

        /// <summary>Resumes broadcasting.</summary>
        public static void Resume() => _paused = false;

        /// <summary>Stops broadcasting and clears dirty cache.</summary>
        public static void Stop()
        {
            _sendTimer = 0f;
            _nextSnapshotSequence = 0;
            _lastSent.Clear();
            _cachedEntityNames.Clear();
            _cachedPrefabPaths.Clear();
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
                || last.EntityName != current.EntityName || last.PrefabPath != current.PrefabPath
                || last.Flags != current.Flags;
        }
    }
}
