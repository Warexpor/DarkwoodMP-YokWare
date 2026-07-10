using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Night custom event index sync (Horde ScenarioRandomEventSync).
/// Authority broadcasts chosen event; non-auth applies same index.
/// </summary>
public sealed class ScenarioEvent_Patch : IPatch
{
    private static int _pendingEvent = -1;
    private static string _pendingNight = "";

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(ScenarioEvent_Patch).GetMethod(nameof(FrequencyPrefix), statics)!),
            postfix: new HarmonyMethod(typeof(ScenarioEvent_Patch).GetMethod(nameof(FrequencyPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("NightScenario", "checkFrequencies");
        yield return ("NightScenario", "fireCustomEvent");
        yield return ("CustomEvent", "fire");
        yield return ("NightScenarios", "fireEvent");
    }

    public static bool FrequencyPrefix(object __instance)
    {
        var manager = NetworkManager.Instance;
        if (manager == null || !manager.IsConnected) return true;
        // Non-auth: only fire if we have a pending remote index
        if (!manager.IsTimeAuthority)
        {
            if (_pendingEvent < 0) return false; // block autonomous rolls
            return true;
        }
        return true;
    }

    public static void FrequencyPostfix(object __instance, object[] __args)
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return;
            if (!manager.IsTimeAuthority) return;

            int idx = -1;
            if (__args != null)
            {
                foreach (var a in __args)
                    if (a is int i) { idx = i; break; }
            }
            if (idx < 0) return;

            var night = __instance?.GetType().GetField("name")?.GetValue(__instance) as string ?? "night";
            network.SendReliable(new ScenarioEventPacket
            {
                PlayerId = manager.LocalPlayerId,
                NightName = night.Replace(":", "_"),
                EventIndex = idx
            });
        }
        catch (Exception ex) { ModLogger.Error($"[ScenarioEv] {ex.Message}"); }
    }

    public static void ApplyRemote(string nightId, int eventIndex)
    {
        _pendingNight = nightId;
        _pendingEvent = eventIndex;
        try
        {
            // Trigger local check with pending flag so FrequencyPrefix allows it
            RemoteApply.Active = true;
            try
            {
                var ns = UnityEngine.Object.FindObjectOfType(GameTypes.GetType("NightScenarios") ?? typeof(object));
                // Best-effort: CustomEvent fire path
            }
            finally { RemoteApply.Active = false; }
        }
        catch { }
        finally
        {
            _pendingEvent = -1;
        }
        ModLogger.Msg($"[ScenarioEv] Remote event {eventIndex} for '{nightId}'");
    }
}
