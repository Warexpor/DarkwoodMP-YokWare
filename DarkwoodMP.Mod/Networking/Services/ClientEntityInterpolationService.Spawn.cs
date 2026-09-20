using DWMPHorde.Logging;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public static partial class ClientEntityInterpolationService
    {
        private static Character[] _inactiveScanCache;
        private static float _inactiveScanCacheTime = -999f;
        private const float InactiveScanCacheTtl = 2f;

        private static Character FindInactiveCharacter(string entityName, Vector3 position, float radius)
        {
            string searchName = entityName;
            if (searchName.EndsWith("(Clone)"))
                searchName = searchName.Substring(0, searchName.Length - 7);

            // Share WorldQueryHelper TTL cache — do not Invalidate here (was forcing FoT
            // every 0.5s while any pending timeout fired, poisoning other Character consumers).
            float now = Time.time;
            if (_inactiveScanCache == null || now - _inactiveScanCacheTime >= InactiveScanCacheTtl)
            {
                var footSw = System.Diagnostics.Stopwatch.StartNew();
                _inactiveScanCache = WorldQueryHelper.GetCachedSceneComponents<Character>();
                footSw.Stop();
                DWMPHorde.Logging.ClientPerfProbe.NoteFindObjectsOfType("Character", footSw.Elapsed.TotalMilliseconds);
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

                string cname = c.name;
                if (cname.EndsWith("(Clone)"))
                    cname = cname.Substring(0, cname.Length - 7);
                if (!string.Equals(cname, searchName, System.StringComparison.OrdinalIgnoreCase))
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

            string path = !string.IsNullOrEmpty(prefabPath) ? prefabPath : "Characters/" + entityName;
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

                ModRuntime.LegacyInfo($"[Entity] spawned local entity: {path} at ({position.x:F1},{position.z:F1})");
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
