using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Stable multiplayer id for a trap instance. Host mints; peers stamp from messages.
    /// Avoids float-rounded position key collisions for occupancy / bulk apply.
    /// </summary>
    public sealed class TrapNetworkId : MonoBehaviour
    {
        public int NetId;

        private static int _nextHostId = 1; // process-scoped: not rewound, ids stamped on traps survive a re-host (see ResetSession)
        /// <summary>Each host epoch mints from its own 2^24 block (a promoted host starts a new one).</summary>
        private const int EpochShift = 24;
        /// <summary>Ids at or above this are treated as junk wire values (see NoteSeenId).</summary>
        private const int MaxSaneId = 1 << 30;
        private static readonly Dictionary<int, GameObject> ById = new Dictionary<int, GameObject>(64);
        private static readonly List<PendingTrapApply> Pending = new List<PendingTrapApply>(16);
        private static readonly List<int> _deadKeys = new List<int>(8); // process-scoped: scratch
        private const int MaxPending = 64;

        /// <summary>Pending trap applies waiting for scene objects (CoopPerfProbe).</summary>
        public static int PendingCount => Pending.Count;

        private struct PendingTrapApply
        {
            public int NetId;
            public Vector3 Pos;
            public bool Triggered;
            public bool SilentDisarm;
            public float QueuedAt;
        }

        /// <summary>
        /// Session boundary. The id counter is deliberately NOT rewound: trap components
        /// (and the ids on them) survive a re-host / host promotion, so restarting at 1
        /// would mint ids that collide with traps that already carry them.
        /// </summary>
        public static void ResetSession()
        {
            ById.Clear();
            Pending.Clear();
            _nextPendingFlushTime = 0f;
        }

        /// <summary>
        /// Call right after host promotion (after the world reset). The promoted host only saw the
        /// ids of traps it had loaded; other peers can hold higher ids from the old host for traps
        /// it never saw. Re-register every id already stamped in the scene, then move minting into
        /// a fresh epoch block above anything the previous host could have handed out.
        /// </summary>
        public static void OnPromotedToHost()
        {
            TrapNetworkId[] stamped = null;
            try
            {
                WorldQueryHelper.InvalidateSceneScanCache<TrapNetworkId>();
                stamped = WorldQueryHelper.GetCachedSceneComponents<TrapNetworkId>();
            }
            catch { /* scene not ready: fall back to the ids already noted */ }
            if (stamped != null)
            {
                for (int i = 0; i < stamped.Length; i++)
                {
                    TrapNetworkId t = stamped[i];
                    if (t == null || t.NetId <= 0) continue;
                    NoteSeenId(t.NetId);
                    if (!ById.TryGetValue(t.NetId, out var cur) || cur == null)
                        ById[t.NetId] = t.gameObject;
                }
            }

            long highest = (long)_nextHostId - 1;
            long nextBlock = ((highest >> EpochShift) + 1) << EpochShift;
            if (nextBlock < MaxSaneId)
                _nextHostId = (int)nextBlock;
            ModRuntime.LegacyInfo($"[TrapId] promoted host mints from {_nextHostId}");
        }

        public static int GetId(GameObject go)
        {
            if (go == null) return 0;
            var c = go.GetComponent<TrapNetworkId>();
            return c != null ? c.NetId : 0;
        }

        /// <summary>
        /// True for a trap the other player should see spring, break, or disappear.
        /// Name lists miss prefabs that only set <see cref="Trigger.isBearTrap"/> or chain/mutated flags.
        /// </summary>
        public static bool IsWorldTrap(GameObject go)
        {
            if (go == null) return false;
            Trigger trig = go.GetComponent<Trigger>();
            if (trig != null && (trig.isBearTrap || trig.isChainTrap || trig.isMutatedTrap))
                return true;
            string name = go.name != null ? go.name.ToLowerInvariant() : "";
            if (name.Contains("trap") || name.Contains("snap") || name.Contains("mushroom"))
                return true;
            return name.Contains("brokenglass") || name.Contains("broken_glass");
        }

        /// <summary>Vanilla only sets inBearTrap on isBearTrap. Name fragments were grabbing the wrong prop.</summary>
        public static bool IsOccupancyTrap(GameObject go)
        {
            if (go == null) return false;
            Trigger trig = go.GetComponent<Trigger>();
            return trig != null && trig.isBearTrap;
        }

        /// <summary>Keep the host mint counter above every id this peer has seen on a trap.</summary>
        private static void NoteSeenId(int netId)
        {
            // Ignore absurd wire values: a counter near int.MaxValue would wrap back into live ids.
            if (netId <= 0 || netId >= 1 << 30) return;
            if (netId >= _nextHostId)
                _nextHostId = netId + 1;
        }

        public static void Ensure(GameObject go, int netId)
        {
            if (go == null || netId <= 0) return;
            NoteSeenId(netId);
            var c = go.GetComponent<TrapNetworkId>();
            if (c == null)
                c = go.AddComponent<TrapNetworkId>();
            if (c.NetId != netId && c.NetId > 0 && ById.TryGetValue(c.NetId, out var old) && old == go)
                ById.Remove(c.NetId);
            c.NetId = netId;
            ById[netId] = go;
        }

        /// <summary>Host: mint or return existing id for a trap GO.</summary>
        public static int GetOrMintHost(GameObject go)
        {
            if (go == null) return 0;
            int existing = GetId(go);
            if (existing > 0)
            {
                NoteSeenId(existing);
                ById[existing] = go;
                return existing;
            }
            // Skip ids another live trap already holds (never mint a duplicate).
            int id = 0;
            for (int attempts = 0; attempts < 1024; attempts++)
            {
                int candidate = _nextHostId;
                _nextHostId = candidate >= MaxSaneId - 1 ? 1 : candidate + 1;
                if (candidate <= 0) continue;
                if (ById.TryGetValue(candidate, out var holder) && holder != null && holder != go)
                    continue;
                id = candidate;
                break;
            }
            if (id <= 0) return 0;
            Ensure(go, id);
            return id;
        }

        public static GameObject FindById(int netId)
        {
            if (netId <= 0) return null;
            if (ById.TryGetValue(netId, out var go) && go != null)
                return go;
            ById.Remove(netId);
            return null;
        }

        public static void RegisterKnown(GameObject go)
        {
            if (go == null) return;
            int id = GetId(go);
            if (id > 0)
            {
                NoteSeenId(id);
                ById[id] = go;
            }
        }

        public static void QueuePending(int netId, Vector3 pos, bool triggered, bool silentDisarm = false)
        {
            for (int i = 0; i < Pending.Count; i++)
            {
                if (Pending[i].NetId == netId || (netId <= 0 && (Pending[i].Pos - pos).sqrMagnitude < 0.25f))
                {
                    Pending[i] = new PendingTrapApply
                    {
                        NetId = netId > 0 ? netId : Pending[i].NetId,
                        Pos = pos,
                        Triggered = triggered,
                        SilentDisarm = silentDisarm || Pending[i].SilentDisarm,
                        QueuedAt = Time.time
                    };
                    return;
                }
            }
            while (Pending.Count >= MaxPending)
                Pending.RemoveAt(0);
            Pending.Add(new PendingTrapApply
            {
                NetId = netId,
                Pos = pos,
                Triggered = triggered,
                SilentDisarm = silentDisarm,
                QueuedAt = Time.time
            });
        }

        private static float _nextPendingFlushTime;
        private const float PendingFlushInterval = 3f;

        public static int FlushPending(
            System.Func<Vector3, string, GameObject> findByPos,
            System.Action<GameObject, bool, bool> apply)
        {
            if (Pending.Count == 0) return 0;
            // findByPos is OverlapSphere-only now, but still rate-limit retries.
            float now = Time.unscaledTime;
            if (now < _nextPendingFlushTime) return 0;
            _nextPendingFlushTime = now + PendingFlushInterval;

            int applied = 0;
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                var p = Pending[i];
                if (Time.time - p.QueuedAt > 30f)
                {
                    Pending.RemoveAt(i);
                    continue;
                }

                GameObject go = FindById(p.NetId);
                if (go == null)
                    go = findByPos != null ? findByPos(p.Pos, null) : null;
                if (go == null) continue;

                if (p.NetId > 0)
                    Ensure(go, p.NetId);
                else if (ModRuntime.Network != null && ModRuntime.Network.Role == Networking.NetworkRole.Host)
                    GetOrMintHost(go);

                apply?.Invoke(go, p.Triggered, p.SilentDisarm);
                Pending.RemoveAt(i);
                applied++;
            }
            return applied;
        }

        public static IEnumerable<KeyValuePair<int, GameObject>> EnumerateRegistered()
        {
            // prune dead
            _deadKeys.Clear();
            foreach (var kv in ById)
            {
                if (kv.Value == null)
                    _deadKeys.Add(kv.Key);
            }
            for (int i = 0; i < _deadKeys.Count; i++)
                ById.Remove(_deadKeys[i]);

            return ById;
        }
    }
}
