using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>Marks a dream NPC that already received co-op presence scaling.</summary>
    public sealed class DreamBalanceProcessedMarker : MonoBehaviour
    {
    }

    /// <summary>
    /// Host-only: scale presence of allowlisted NPCs (default ChomperBlack) during dreams.
    /// Only on real spawn (Core.AddPrefab) — CharacterSpawnPoint, GameEvent.spawnCharacter,
    /// CharacterSpawner, etc. Pre-placed / event-gated characters are NOT doubled at load;
    /// extras fire when the same spawn path that vanilla uses runs.
    /// Night hideout scenarios are intentionally not scaled.
    /// </summary>
    public static class NamedNpcScalePatch
    {
        private static bool _spawningExtra;

        public static void Reset()
        {
            _spawningExtra = false;
            CoopBalance.InvalidateAllowlistCache();
        }

        private static bool CanScaleDreamNpcs()
        {
            if (ModConfig.NamedNpcScaleEnabled != null && !ModConfig.NamedNpcScaleEnabled.Value)
                return false;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return false;

            if (Dreams.Instance == null || !Dreams.Instance.dreaming)
                return false;

            return CoopBalance.GetPartyMultiplier() > 1;
        }

        private static void ProcessOriginal(GameObject go, string label)
        {
            if (go == null || _spawningExtra)
                return;
            if (go.GetComponent<DreamBalanceProcessedMarker>() != null)
                return;

            string nameKey = CoopBalance.NormalizeNpcName(go.name);
            if (!CoopBalance.IsNamedNpcAllowlisted(nameKey))
                return;

            int mult = CoopBalance.GetPartyMultiplier();
            if (mult <= 1)
                return;

            go.AddComponent<DreamBalanceProcessedMarker>();

            string prefabPath = ResolvePrefabPath(go, nameKey);
            int extras = mult - 1;
            SpawnExtras(go, prefabPath, extras);

            ModRuntime.LegacyInfo(
                $"[DreamNpcScale] {label}: '{nameKey}' mult={mult} extras={extras} path={prefabPath}");
        }

        private static string ResolvePrefabPath(GameObject go, string shortName)
        {
            var pathComp = go.GetComponent<Sync.PrefabPathComponent>();
            if (pathComp != null && !string.IsNullOrEmpty(pathComp.Path))
                return pathComp.Path;
            return "Characters/" + shortName;
        }

        private static void SpawnExtras(GameObject original, string prefabPath, int count)
        {
            if (count <= 0 || original == null)
                return;

            // Anchor extras near the original spawn (event position), not near remote
            // proxies — party mult is extra bodies at the same trigger, not free spawns on peers.
            Vector3 basePos = original.transform.position;
            Quaternion rot = original.transform.rotation;

            // Parent null (not under dream Location): EntityStateBroadcastService skips
            // Characters parented under dreamLocation; unparented dream spawns sync like ChomperHalf.
            _spawningExtra = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3 spawnPos = basePos + new Vector3(
                        UnityEngine.Random.Range(-60f, 60f),
                        0f,
                        UnityEngine.Random.Range(-60f, 60f));
                    try
                    {
                        spawnPos = Core.randomPosAround(basePos, 30f, 90f, canBeInside: true, mustBeInsideGraph: false);
                    }
                    catch
                    {
                        // keep offset fallback
                    }

                    GameObject extra = Core.AddPrefab(prefabPath, spawnPos, rot, null);
                    if (extra == null)
                    {
                        ModRuntime.Log?.LogWarning($"[DreamNpcScale] AddPrefab failed for {prefabPath}");
                        continue;
                    }

                    if (extra.GetComponent<DreamBalanceProcessedMarker>() == null)
                        extra.AddComponent<DreamBalanceProcessedMarker>();

                    var ch = extra.GetComponent<Character>();
                    if (ch != null)
                        ch.isActive = true;
                }
            }
            finally
            {
                _spawningExtra = false;
            }
        }

        // ─── Core.AddPrefab only: event / spawn-point / spawner dream NPCs ───

        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(Core), "AddPrefab", new[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) })]
        public static class DreamNpcAddPrefabScalePatch
        {
            private static void Postfix(GameObject __result, object[] __args)
            {
                if (__result == null || _spawningExtra)
                    return;
                if (!CanScaleDreamNpcs())
                    return;
                string prefab = __args != null && __args.Length > 0 ? __args[0] as string : null;
                if (!CoopBalance.IsAllowlistedPrefabPath(prefab))
                    return;

                ProcessOriginal(__result, "AddPrefab");
            }
        }
    }
}
