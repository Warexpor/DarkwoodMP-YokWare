using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// World object destroy (harvest/barrel/trap) → peers (Horde ObjectDestroyTrapPatch).
/// </summary>
public sealed class WorldHarvest_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        // UnityEngine.Object.Destroy(Object) — may need to patch via AccessTools
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(WorldHarvest_Patch).GetMethod(nameof(DestroyPrefix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        // PatchRegistry resolves type by name from game assemblies — Object may fail.
        // Also hook Item.destroy / Item.disappear if present.
        yield return ("Item", "destroy");
        yield return ("Item", "Destroy");
        yield return ("Trigger", "destroy");
        yield return ("Explodes", "explode");
    }

    public static void DestroyPrefix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Component c) return;
            var go = c.gameObject;
            if (go == null) return;
            string name = go.name.ToLowerInvariant();
            bool isTrap = name.Contains("trap") || name.Contains("bear") || name.Contains("snap");
            bool isDestructible = name.Contains("barrel") || name.Contains("tank") || name.Contains("glass");
            bool isHarvestable = name.Contains("mushroom") || name.Contains("_exp") || name.Contains("bio_");
            if (!isTrap && !isDestructible && !isHarvestable) return;

            var p = go.transform.position;
            var key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected) return;

            network.SendReliable(new WorldObjectGonePacket
            {
                PlayerId = manager.LocalPlayerId,
                ObjectName = go.name.Replace(":", "_"),
                X = key.x, Y = key.y, Z = key.z
            });
        }
        catch (Exception ex) { ModLogger.Error($"[WorldHarvest] {ex.Message}"); }
    }

    public static void ApplyRemote(string name, Vector3 pos)
    {
        try
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Transform)))
            {
                if (obj is not Transform t) continue;
                if (t.gameObject.name != name && !t.gameObject.name.StartsWith(name.Replace("(Clone)", "")))
                    continue;
                if ((t.position - pos).sqrMagnitude > 2.25f) continue;
                RemoteApply.Active = true;
                try { UnityEngine.Object.Destroy(t.gameObject); }
                finally { RemoteApply.Active = false; }
                return;
            }
        }
        catch (Exception ex) { ModLogger.Error($"[WorldHarvest] Apply: {ex.Message}"); }
    }
}
