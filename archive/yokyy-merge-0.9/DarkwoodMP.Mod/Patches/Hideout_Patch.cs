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
/// Hideout oven / ExperienceMachine enable-disable + join bulk (Horde HideoutUpgrade).
/// </summary>
public sealed class Hideout_Patch : IPatch
{
    private static readonly Dictionary<string, bool> _session = new Dictionary<string, bool>();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var on = target.Name.IndexOf("enable", StringComparison.OrdinalIgnoreCase) >= 0
            || target.Name == "OnEnable" || target.Name == "activate";
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(Hideout_Patch).GetMethod(
                on ? nameof(EnablePostfix) : nameof(DisablePostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("ExperienceMachine", "enable");
        yield return ("ExperienceMachine", "disable");
        yield return ("ExperienceMachine", "Activate");
        yield return ("ExperienceMachine", "Deactivate");
        yield return ("Workbench", "upgrade");
    }

    public static void EnablePostfix(object __instance) => Send(__instance, true);
    public static void DisablePostfix(object __instance) => Send(__instance, false);

    private static void Send(object instance, bool enabled)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (instance is not Component c) return;
            var id = GameIds.ForComponent(c);
            _session[id] = enabled;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected) return;
            network.SendReliable(new HideoutStatePacket
            {
                PlayerId = manager.LocalPlayerId,
                ComponentId = id,
                Enabled = enabled
            });
        }
        catch (Exception ex) { ModLogger.Error($"[Hideout] {ex.Message}"); }
    }

    public static void ApplyRemote(string id, bool enabled)
    {
        _session[id] = enabled;
        try
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Component)))
            {
                if (obj is not Component c) continue;
                if (GameIds.ForComponent(c) != id) continue;
                RemoteApply.Active = true;
                try
                {
                    c.gameObject.SetActive(enabled);
                    var m = c.GetType().GetMethod(enabled ? "enable" : "disable",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    m?.Invoke(c, null);
                }
                finally { RemoteApply.Active = false; }
                return;
            }
        }
        catch (Exception ex) { ModLogger.Error($"[Hideout] Apply: {ex.Message}"); }
    }

    public static void CollectSnapshot(List<Packet> into)
    {
        if (into == null) return;
        var pid = NetworkManager.Instance?.LocalPlayerId ?? 0;
        foreach (var kvp in _session)
            into.Add(new HideoutStatePacket
            {
                PlayerId = pid,
                ComponentId = kvp.Key,
                Enabled = kvp.Value
            });
    }

    public static void Reset() => _session.Clear();
}
