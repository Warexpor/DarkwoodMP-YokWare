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

        private static int _nextHostId = 1;
        private static readonly Dictionary<int, GameObject> ById = new Dictionary<int, GameObject>(64);
        private static readonly List<PendingTrapApply> Pending = new List<PendingTrapApply>(16);
        private static readonly List<int> _deadKeys = new List<int>(8);
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
            int id = _nextHostId++;
            if (_nextHostId <= 0) _nextHostId = 1;
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

        /// <summary>
        /// Resolve which trap a trapped player occupies: nearest Trigger with trap name near player.
        /// Host stamps NetId when missing.
        /// </summary>
        public static int ResolveOccupyingTrapId(Vector3 playerPos, bool hostMint)
        {
            // Trap Y is often ~-10 while the player stands at ~16 — 3D OverlapSphere(2.5)
            // never hits. Use a tall sphere then filter by XZ (same as ReleaseLocalBearTrapIfNear).
            GameObject best = null;
            float bestSq = 2.5f * 2.5f;
            const float overlapR = 30f;
            int hitN = Physics.OverlapSphereNonAlloc(playerPos, overlapR, WorldQueryHelper.SharedOverlapBuf);
            for (int i = 0; i < hitN; i++)
            {
                if (WorldQueryHelper.SharedOverlapBuf[i] == null) continue;
                GameObject root = WorldQueryHelper.SharedOverlapBuf[i].attachedRigidbody != null
                    ? WorldQueryHelper.SharedOverlapBuf[i].attachedRigidbody.gameObject
                    : WorldQueryHelper.SharedOverlapBuf[i].gameObject;
                if (root == null) continue;
                if (!TrapNetworkId.IsOccupancyTrap(root))
                    continue;
                Vector3 tp = root.transform.position;
                float dx = tp.x - playerPos.x;
                float dz = tp.z - playerPos.z;
                float sq = dx * dx + dz * dz;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = root;
                }
            }

            if (best == null) return 0;
            if (hostMint || GetId(best) > 0)
                return hostMint ? GetOrMintHost(best) : GetId(best);
            return GetId(best);
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
