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

        private struct PendingTrapApply
        {
            public int NetId;
            public Vector3 Pos;
            public bool Triggered;
            public float QueuedAt;
        }

        public static void ResetSession()
        {
            _nextHostId = 1;
            ById.Clear();
            Pending.Clear();
        }

        public static int GetId(GameObject go)
        {
            if (go == null) return 0;
            var c = go.GetComponent<TrapNetworkId>();
            return c != null ? c.NetId : 0;
        }

        public static void Ensure(GameObject go, int netId)
        {
            if (go == null || netId <= 0) return;
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
                ById[id] = go;
        }

        /// <summary>
        /// Resolve which trap a trapped player occupies: nearest Trigger with trap name near player.
        /// Host stamps NetId when missing.
        /// </summary>
        public static int ResolveOccupyingTrapId(Vector3 playerPos, bool hostMint)
        {
            GameObject best = null;
            float bestSq = 2.5f * 2.5f;
            Collider[] hits = Physics.OverlapSphere(playerPos, 2.5f);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] == null) continue;
                GameObject root = hits[i].attachedRigidbody != null
                    ? hits[i].attachedRigidbody.gameObject
                    : hits[i].gameObject;
                if (root == null) continue;
                string n = root.name.ToLowerInvariant();
                if (!n.Contains("trap") && !n.Contains("bear") && !n.Contains("snap") && !n.Contains("animal"))
                    continue;
                float sq = (root.transform.position - playerPos).sqrMagnitude;
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

        public static void QueuePending(int netId, Vector3 pos, bool triggered)
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
                        QueuedAt = Time.time
                    };
                    return;
                }
            }
            Pending.Add(new PendingTrapApply
            {
                NetId = netId,
                Pos = pos,
                Triggered = triggered,
                QueuedAt = Time.time
            });
        }

        private static float _nextPendingFlushTime;
        private const float PendingFlushInterval = 3f;

        public static int FlushPending(System.Func<Vector3, string, GameObject> findByPos, System.Action<GameObject, bool> apply)
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

                apply?.Invoke(go, p.Triggered);
                Pending.RemoveAt(i);
                applied++;
            }
            return applied;
        }

        public static IEnumerable<KeyValuePair<int, GameObject>> EnumerateRegistered()
        {
            // prune dead
            List<int> dead = null;
            foreach (var kv in ById)
            {
                if (kv.Value == null)
                {
                    if (dead == null) dead = new List<int>();
                    dead.Add(kv.Key);
                }
            }
            if (dead != null)
                foreach (int id in dead)
                    ById.Remove(id);

            return ById;
        }
    }
}
