using System.Collections;
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
    /// Dual path: Core.AddPrefab (runtime / CharacterSpawnPoint) + delayed scan (pre-placed).
    /// Night hideout scenarios are intentionally not scaled.
    /// </summary>
    public static class NamedNpcScalePatch
    {
        private static bool _spawningExtra;
        private static Coroutine _delayedScan;
        private static int _scanGeneration;

        public static void Reset()
        {
            _spawningExtra = false;
            _scanGeneration++;
            _delayedScan = null;
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

            Vector3 basePos = original.transform.position;
            Quaternion rot = original.transform.rotation;

            // Parent null (not under dream Location): EntityStateBroadcastService skips
            // Characters parented under dreamLocation; unparented dream spawns sync like ChomperHalf.
            var net = LanNetworkManager.Instance;
            List<Vector3> anchors = new List<Vector3>(4) { basePos };
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy != null)
                        anchors.Add(proxy.transform.position);
                }
            }

            _spawningExtra = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3 anchor = anchors[Mathf.Min(i + 1, anchors.Count - 1)];
                    Vector3 spawnPos = anchor + new Vector3(
                        UnityEngine.Random.Range(-80f, 80f),
                        0f,
                        UnityEngine.Random.Range(-80f, 80f));
                    try
                    {
                        spawnPos = Core.randomPosAround(anchor, 40f, 120f, canBeInside: true, mustBeInsideGraph: false);
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

        /// <summary>Scan dream location for pre-placed allowlisted characters.</summary>
        public static void ScanDreamLocation(string reason)
        {
            if (!CanScaleDreamNpcs())
                return;

            Location loc = Dreams.Instance != null ? Dreams.Instance.dreamLocation : null;
            if (loc == null)
                return;

            Character[] chars = loc.GetComponentsInChildren<Character>(true);
            int processed = 0;
            for (int i = 0; i < chars.Length; i++)
            {
                Character c = chars[i];
                if (c == null || c.gameObject == null)
                    continue;
                if (c.GetComponent<DreamBalanceProcessedMarker>() != null)
                    continue;
                if (!CoopBalance.IsNamedNpcAllowlisted(c.name))
                    continue;

                ProcessOriginal(c.gameObject, "scan:" + reason);
                processed++;
            }

            if (processed > 0 || ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[DreamNpcScale] scan ({reason}) processed={processed}");
        }

        private static IEnumerator DelayedScanRoutine(int generation)
        {
            yield return new WaitForSeconds(2f);
            if (generation != _scanGeneration)
                yield break;
            ScanDreamLocation("delayed");
            // Second pass for CharacterSpawnPoint (0.1–1s) stragglers after first delay
            yield return new WaitForSeconds(1.5f);
            if (generation != _scanGeneration)
                yield break;
            ScanDreamLocation("delayed2");
            _delayedScan = null;
        }

        private static void ScheduleDelayedScan()
        {
            if (!CanScaleDreamNpcs())
                return;

            _scanGeneration++;
            int gen = _scanGeneration;
            MonoBehaviour runner = LanNetworkManager.Instance;
            if (runner == null && Singleton<Controller>.Instance != null)
                runner = Singleton<Controller>.Instance;
            if (runner == null)
                return;

            if (_delayedScan != null)
                runner.StopCoroutine(_delayedScan);
            _delayedScan = runner.StartCoroutine(DelayedScanRoutine(gen));
        }

        // ─── Core.AddPrefab: runtime / spawn-point dream NPCs ────────────

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

        // ─── Dreams.onLocationSpawned: schedule pre-placed scan ──────────

        [HarmonyPatch(typeof(Dreams), "onLocationSpawned")]
        public static class DreamLocationSpawnedScalePatch
        {
            private static void Postfix()
            {
                if (!CanScaleDreamNpcs())
                    return;
                ScheduleDelayedScan();
            }
        }
    }
}
