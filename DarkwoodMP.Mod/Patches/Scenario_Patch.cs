using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Night scenario identity sync (Horde ScenarioSync).
/// Time-authority (or host) broadcasts the chosen NightScenarios name;
/// others apply by name under RemoteApply so both get the same night cast.
/// </summary>
public sealed class Scenario_Patch : IPatch
{
    private static string _lastSent = "";

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _lastSent = "";
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(Scenario_Patch).GetMethod(nameof(SetScenarioPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("NightScenarios", "setCurrentScenario");
    }

    public static void SetScenarioPostfix(object __instance, object __0)
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected) return;
            // Only authority publishes (host, or elected time-authority on dedicated)
            if (!manager.IsTimeAuthority) return;

            var name = ExtractScenarioName(__0);
            if (string.IsNullOrEmpty(name) || name == _lastSent) return;
            _lastSent = name;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            network.SendReliable(new ScenarioStatePacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                ScenarioName = name
            });
            ModLogger.Msg($"[Scenario_Patch] Broadcast night scenario '{name}'");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Scenario_Patch] {ex.Message}");
        }
    }

    private static string ExtractScenarioName(object scenarioOrName)
    {
        if (scenarioOrName == null) return null;
        if (scenarioOrName is string s) return s;
        try
        {
            var t = scenarioOrName.GetType();
            var f = t.GetField("name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? t.GetField("scenarioName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(scenarioOrName) is string n) return n;
            var p = t.GetProperty("name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p?.GetValue(scenarioOrName, null) is string pn) return pn;
            return scenarioOrName.ToString();
        }
        catch { return null; }
    }

    /// <summary>Non-authority: adopt host/authority scenario by name.</summary>
    public static void ApplyRemoteScenario(string scenarioName)
    {
        if (string.IsNullOrEmpty(scenarioName)) return;
        try
        {
            var nsType = GameTypes.GetType("NightScenarios");
            if (nsType == null) return;
            var ns = UnityEngine.Object.FindObjectOfType(nsType);
            if (ns == null) return;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var getScenario = nsType.GetMethod("getScenario", flags);
            object scenario = null;
            if (getScenario != null)
            {
                foreach (var m in nsType.GetMethods(flags))
                {
                    if (m.Name != "getScenario") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    {
                        scenario = m.Invoke(ns, new object[] { scenarioName });
                        break;
                    }
                }
            }

            if (scenario == null)
            {
                ModLogger.Warning($"[Scenario_Patch] Unknown scenario '{scenarioName}'");
                return;
            }

            // Set currentScenario field directly to avoid re-entrancy through setCurrentScenario
            var field = nsType.GetField("currentScenario", flags);
            RemoteApply.Active = true;
            try
            {
                if (field != null)
                    field.SetValue(ns, scenario);
                else
                {
                    var set = nsType.GetMethod("setCurrentScenario", flags);
                    set?.Invoke(ns, new object[] { scenario });
                }
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[Scenario_Patch] Applied remote scenario '{scenarioName}'");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Scenario_Patch] ApplyRemoteScenario: {ex.Message}");
        }
    }
}
