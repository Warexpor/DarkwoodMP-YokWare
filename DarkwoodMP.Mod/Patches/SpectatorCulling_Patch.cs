using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// WorldGrid culls around spectated camera, not immobilized corpse (Horde SpectatorCullingPatch).
/// </summary>
public sealed class SpectatorCulling_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(SpectatorCulling_Patch).GetMethod(nameof(Prefix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("WorldGrid", "refreshPosition");
    }

    public static void Prefix(object[] __args)
    {
        var spec = SpectatorModeController.Instance;
        if (spec == null || !spec.IsSpectating) return;
        var targetPos = spec.FollowTargetPosition;
        if (targetPos.HasValue && __args != null && __args.Length > 0 && __args[0] is Vector3)
            __args[0] = targetPos.Value;
    }
}
