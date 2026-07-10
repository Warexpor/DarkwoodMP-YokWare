using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Dream presence scale for allowlisted NPCs (Horde NamedNpcScale — ChomperBlack).
/// Authority only, during dreams, when party multiplier &gt; 1.
/// </summary>
public sealed class NamedNpcScale_Patch : IPatch
{
    private static bool _spawningExtra;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        // Core.AddPrefab has many overloads — patch the common string path one
        foreach (var m in target.DeclaringType!.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "AddPrefab") continue;
            var ps = m.GetParameters();
            if (ps.Length >= 1 && ps[0].ParameterType == typeof(string))
            {
                try
                {
                    baseHarmony.Patch(m,
                        postfix: new HarmonyMethod(typeof(NamedNpcScale_Patch).GetMethod(nameof(AddPrefabPostfix), statics)!));
                }
                catch { /* overload mismatch */ }
            }
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Core", "AddPrefab");
    }

    public static void AddPrefabPostfix(string __0, GameObject __result)
    {
        try
        {
            if (_spawningExtra || RemoteApply.Active) return;
            if (__result == null || string.IsNullOrEmpty(__0)) return;
            if (!CanScale()) return;
            if (!CoopBalance.IsNamedNpcAllowlisted(__0) && !CoopBalance.IsNamedNpcAllowlisted(__result.name))
                return;
            if (__result.GetComponent<DreamBalanceProcessedMarker>() != null) return;

            int mult = CoopBalance.GetPartyMultiplier();
            if (mult <= 1) return;

            __result.AddComponent<DreamBalanceProcessedMarker>();
            int extras = mult - 1;
            SpawnExtras(__result, __0, extras);
            ModLogger.Msg($"[DreamNpcScale] '{CoopBalance.NormalizeNpcName(__0)}' mult={mult} extras={extras}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DreamNpcScale] {ex.Message}");
        }
    }

    private static bool CanScale()
    {
        if (!ModConfig.Load().NamedNpcScaleEnabled) return false;
        var m = NetworkManager.Instance;
        if (m == null || !m.IsConnected || !m.IsTimeAuthority) return false;
        try
        {
            if (Dreams.Instance == null || !Dreams.Instance.dreaming) return false;
        }
        catch { return false; }
        return CoopBalance.GetPartyMultiplier() > 1;
    }

    private static void SpawnExtras(GameObject original, string prefabPath, int count)
    {
        if (count <= 0 || original == null) return;
        var anchors = new List<Vector3> { original.transform.position };
        var manager = NetworkManager.Instance;
        if (manager != null)
        {
            foreach (var kvp in manager.RemotePlayers)
                if (kvp.Value != null)
                    anchors.Add(kvp.Value.transform.position);
        }

        string path = prefabPath;
        if (!path.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("characters/", StringComparison.OrdinalIgnoreCase))
            path = "Characters/" + CoopBalance.NormalizeNpcName(original.name);

        _spawningExtra = true;
        try
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 anchor = anchors[Mathf.Min(i + 1, anchors.Count - 1)];
                Vector3 spawnPos = anchor + new Vector3(
                    UnityEngine.Random.Range(-40f, 40f), 0f, UnityEngine.Random.Range(-40f, 40f));
                try
                {
                    spawnPos = Core.randomPosAround(anchor, 40f, 120f, canBeInside: true, mustBeInsideGraph: false);
                }
                catch { }

                var go = Core.AddPrefab(path, spawnPos, original.transform.rotation, null);
                if (go != null)
                    go.AddComponent<DreamBalanceProcessedMarker>();
            }
        }
        finally
        {
            _spawningExtra = false;
        }
    }
}

/// <summary>Marks dream NPCs already presence-scaled.</summary>
public sealed class DreamBalanceProcessedMarker : MonoBehaviour
{
}
