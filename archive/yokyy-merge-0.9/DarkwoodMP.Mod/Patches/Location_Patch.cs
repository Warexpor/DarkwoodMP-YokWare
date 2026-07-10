using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// OutsideLocations enter/exit → LocationSync (Horde LocationEnter/Exit).
/// Targets common method names; fails soft if signatures differ.
/// </summary>
public sealed class Location_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.Name.IndexOf("enter", StringComparison.OrdinalIgnoreCase) >= 0
            || target.Name.IndexOf("goTo", StringComparison.OrdinalIgnoreCase) >= 0
            || target.Name == "loadLocation")
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(Location_Patch).GetMethod(nameof(EnterPostfix), statics)!));
        }
        else
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(Location_Patch).GetMethod(nameof(ExitPostfix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        // Multiple candidates — PatchRegistry skips missing
        yield return ("OutsideLocations", "goToLocation");
        yield return ("OutsideLocations", "enterLocation");
        yield return ("OutsideLocations", "loadLocation");
        yield return ("OutsideLocations", "leaveLocation");
        yield return ("OutsideLocations", "exitLocation");
        yield return ("OutsideLocations", "leaveAllLocations");
    }

    public static void EnterPostfix(object __instance, object[] __args)
    {
        try
        {
            if (RemoteApply.Active) return;
            string name = null;
            if (__args != null)
            {
                foreach (var a in __args)
                {
                    if (a is string s && !string.IsNullOrEmpty(s)) { name = s; break; }
                    if (a != null && a.GetType().Name.IndexOf("Location", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var nf = a.GetType().GetField("name") ?? a.GetType().GetField("locationName");
                        name = nf?.GetValue(a) as string ?? a.ToString();
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    var cur = __instance.GetType().GetField("currentLocation",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var loc = cur?.GetValue(__instance);
                    name = loc?.GetType().GetField("name")?.GetValue(loc) as string ?? loc?.ToString();
                }
                catch { }
            }
            if (string.IsNullOrEmpty(name)) return;
            var pos = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;
            DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<LocationSync>()?.OnLocalEnter(name, pos);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Location] Enter: {ex.Message}");
        }
    }

    public static void ExitPostfix()
    {
        try
        {
            if (RemoteApply.Active) return;
            var pos = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;
            DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<LocationSync>()?.OnLocalExit(pos);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Location] Exit: {ex.Message}");
        }
    }
}
