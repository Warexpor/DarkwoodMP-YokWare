using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Single detour on <c>Core.AddPrefab(string, …)</c>. Every spawn in the game goes
    /// through here, so the features run from one typed prefix/postfix in a fixed order
    /// instead of one patch class (and one <c>object[]</c> box) per feature.
    /// Order mirrors the former priorities: prefix gas-trail gate (First) → blood relay
    /// (Last); postfix gas trail (First) → physics / prefab path (Normal) →
    /// shadow / dream NPC / night worm (Last).
    /// </summary>
    [HarmonyPatch(typeof(Core), "AddPrefab", new[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) })]
    public static class CoreAddPrefabStringPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(string prefab, Vector3 position, Quaternion quaternion)
        {
            if (!GasolineTrailSpawnPatch.AllowSpawn(prefab, position))
                return false;
            HitscanBloodPatch.TryForwardBlood(prefab, position, quaternion);
            return true;
        }

        private static void Postfix(GameObject __result, string prefab, Vector3 position, Quaternion quaternion)
        {
            GasolineTrailSpawnPatch.OnAddPrefab(__result, prefab, position);
            CoreAddPrefabPhysicsSyncPatch.OnAddPrefab(__result, prefab, position, quaternion);
            AddPrefabRecordPathPatch.OnAddPrefab(__result, prefab);
            ShadowCaptureOnSpawnPatch.OnAddPrefab(__result, prefab);
            NightWormPostSpawnPatch.OnAddPrefab(__result, prefab);
        }
    }

    /// <summary>
    /// Single detour on <c>Core.AddPrefab(Object, …)</c>: gas-trail gate/relay (First),
    /// explosion spawn-object relay, then the enemy ranged-attack capture (EnemyAttack).
    /// Arguments bound by position (__0..__2).
    /// </summary>
    [HarmonyPatch(typeof(Core), "AddPrefab", new[] { typeof(Object), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) })]
    public static class CoreAddPrefabObjectPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Object __0)
        {
            return GasolineTrailObjectSpawnPatch.AllowSpawn(__0);
        }

        private static void Postfix(GameObject __result, Object __0, Vector3 __1, Quaternion __2)
        {
            GasolineTrailObjectSpawnPatch.OnAddPrefab(__result, __0, __1);
            ExplosionObjectSpawnSyncPatch.OnAddPrefab(__result, __0, __1, __2);
            DefenderAttackContext.NoteSpawned(__result);
        }
    }
}
