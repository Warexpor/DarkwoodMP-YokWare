using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Keeps CharacterTracker in sync with the live scene: registers a Character on
/// Start, deregisters it on OnDestroy. Applied on every machine (the registry is
/// the backbone of host-authoritative enemy sync AND removes the per-frame
/// FindObjectsOfType(Character) scans).
/// </summary>
public sealed class CharacterRegistry_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.Name == "OnDestroy")
            baseHarmony.Patch(target, prefix: new HarmonyMethod(typeof(CharacterRegistry_Patch).GetMethod(nameof(OnDestroyPrefix), statics)!));
        else
            baseHarmony.Patch(target, postfix: new HarmonyMethod(typeof(CharacterRegistry_Patch).GetMethod(nameof(StartPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Character", "Start");
        yield return ("Character", "OnDestroy");
    }

    public static void StartPostfix(object __instance)
    {
        try { if (__instance is Character c) CharacterTracker.Add(c); }
        catch (Exception ex) { ModLogger.Error($"[CharacterRegistry_Patch] add: {ex.Message}"); }
    }

    public static void OnDestroyPrefix(object __instance)
    {
        try { if (__instance is Character c) CharacterTracker.Remove(c); }
        catch (Exception ex) { ModLogger.Error($"[CharacterRegistry_Patch] remove: {ex.Message}"); }
    }
}
