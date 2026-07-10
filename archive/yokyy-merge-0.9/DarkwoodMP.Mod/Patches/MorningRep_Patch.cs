using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Skip morning reputation bonuses after night death hold (Horde MorningRepPatch).
/// </summary>
public sealed class MorningRep_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(MorningRep_Patch).GetMethod(nameof(Prefix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Controller", "startAfterNight");
        yield return ("Controller", "giveAfterNightRewards");
        yield return ("Player", "giveAfterNightRewards");
    }

    public static bool Prefix()
    {
        if (DeathStateTracker.SkipMorningRepBonus)
        {
            ModLogger.Msg("[MorningRep] Skipping after-night rewards (night death hold)");
            return false;
        }
        return true;
    }
}
