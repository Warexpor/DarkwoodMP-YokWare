using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public static partial class ClientEntityInterpolationService
    {
        private static Character[] _inactiveScanCache; // process-scoped: short TTL scene-scan cache
        private static float _inactiveScanCacheTime = -999f; // process-scoped: short TTL scene-scan cache
        private const float InactiveScanCacheTtl = 2f;

        /// <summary>
        /// A save body that never woke (inactive since the world loaded, so the tracker never
        /// listed it) at the host's position. Reads the Character scene registry, filled once
        /// when the world finished loading and kept by Character.Awake: the scene-wide search it
        /// used to run cost about 47 ms each time a host-spawned creature (no save body to match)
        /// waited out its pending match, a hitch per dog of a pack.
        /// </summary>
        private static Character FindInactiveCharacter(string entityName, Vector3 position, float radius)
        {
            // Share WorldQueryHelper's array — do not Invalidate here (was forcing FoT
            // every 0.5s while any pending timeout fired, poisoning other Character consumers).
            // Before the world is seeded that is still the cached search; the probe is noted there.
            float now = Time.time;
            if (_inactiveScanCache == null || now - _inactiveScanCacheTime >= InactiveScanCacheTtl)
            {
                _inactiveScanCache = WorldQueryHelper.GetCachedSceneComponents<Character>();
                _inactiveScanCacheTime = now;
            }

            Character[] all = _inactiveScanCache;
            float radiusSq = radius * radius;
            Character best = null;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < all.Length; i++)
            {
                Character c = all[i];
                if (c == null) continue;

                // Skip the local player and remote proxy
                if (c.name.Contains("Player"))
                    continue;

                // Skip if already host-synced or a phantom
                if (CharacterTracker.TryGetStableId(c, out short sid))
                {
                    if (_hostSyncedIds.Contains(sid) || _spawnedPhantomIds.Contains(sid))
                        continue;
                }

                if (!CharacterTracker.BaseNameEquals(c.name, entityName))
                    continue;
                // Another host body's save twin (its own pending row finds it by id).
                if (RejectOtherSaveTwin(c))
                    continue;

                float dx = c.transform.position.x - position.x;
                float dz = c.transform.position.z - position.z;
                float dSq = dx * dx + dz * dz;
                if (dSq < radiusSq && dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>Prefab path (under Resources/Prefabs) → its background load, held so the prefab stays loaded.</summary>
        private static readonly Dictionary<string, ResourceRequest> _phantomPrefabWarm = new Dictionary<string, ResourceRequest>(16); // process-scoped: creature prefabs, loaded once per run

        private static string PhantomPrefabPath(string entityName, string prefabPath)
            => !string.IsNullOrEmpty(prefabPath) ? prefabPath : "Characters/" + entityName;

        /// <summary>
        /// A body the host spawned at runtime (it carries its prefab path) normally has no save
        /// twin here and becomes a phantom when its pending match times out. Start loading its prefab in the
        /// background now: the first Resources.Load of a creature prefab cost 20-35 ms inside the
        /// spawn frame, on top of the instantiate.
        /// </summary>
        private static void WarmPhantomPrefab(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath) || _phantomPrefabWarm.ContainsKey(prefabPath))
                return;
            _phantomPrefabWarm[prefabPath] = Resources.LoadAsync("Prefabs/" + prefabPath);
        }

        private static Character SpawnEntityLocally(string entityName, string prefabPath, Vector3 position, float rotY)
        {
            if (string.IsNullOrEmpty(entityName) && string.IsNullOrEmpty(prefabPath))
                return null;

            // During shared dream, ignore overworld-distance host spawns (stale EntityState).
            if (DreamSyncManager.IsLocalDreamActive || DreamSession.IsActive)
            {
                var dreamTf = DreamSyncManager.GetDreamLocationTransform();
            // During entry, wait for dreamLocation before creating phantoms.
                if (DreamSession.IsActive && dreamTf == null)
                    return null;
                if (dreamTf != null)
                {
                    // Dream pads sit far off-map; 5km radius around pad is generous.
                    const float maxDistSq = 5000f * 5000f;
                    if ((position - dreamTf.position).sqrMagnitude > maxDistSq)
                        return null;
                }
            }

            string path = PhantomPrefabPath(entityName, prefabPath);
            try
            {
                Quaternion rotation = Quaternion.Euler(90f, rotY, 0f);

                GameObject go = Core.AddPrefab(path, position, rotation, null);
                if (go == null) return null;

                Character c = go.GetComponent<Character>();
                if (c == null)
                {
                    Object.Destroy(go);
                    return null;
                }

                // Force idle animation so the entity doesn't appear in T-pose
                // until the next host snapshot provides the correct clip.
                tk2dSpriteAnimator anim = c.GetComponent<tk2dSpriteAnimator>();
                if (anim != null)
                {
                    string idleClip = Traverse.Create(c).Field("idleAni").GetValue<string>();
                    if (!string.IsNullOrEmpty(idleClip) && anim.GetClipByName(idleClip) != null)
                    {
                        if (anim.CurrentClip == null || anim.CurrentClip.name != idleClip)
                            anim.Play(idleClip);
                    }
                }

                EntitySyncLog.Event(() =>
                    "[ClientSpawn] phantom " + path
                    + " at (" + position.x.ToString("F0") + "," + position.z.ToString("F0") + ")");
                return c;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogError($"[Entity] failed to spawn {path}: {ex}");
                return null;
            }
        }

    }
}
