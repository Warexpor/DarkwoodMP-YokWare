using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Co-op UI must not freeze the whole world via Core.pause (Horde NoWorldPause).
/// Map / journal / dialogue / padlock / leveling / interactive UI.
/// </summary>
public sealed class PauseSuppression_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var typeName = target.DeclaringType!.Name;
        var methodName = target.Name;

        if (typeName == "Core" && methodName == "pause")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(PauseSuppression_Patch).GetMethod(nameof(CorePausePrefix), statics)!));
        }
        else if (typeName == "Core" && methodName == "unpause")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(PauseSuppression_Patch).GetMethod(nameof(CoreUnpausePrefix), statics)!),
                postfix: new HarmonyMethod(typeof(PauseSuppression_Patch).GetMethod(nameof(CoreUnpausePostfix), statics)!));
        }
        else if (IsOpenMethod(typeName, methodName))
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(PauseSuppression_Patch).GetMethod(nameof(OpenPrefix), statics)!),
                postfix: new HarmonyMethod(typeof(PauseSuppression_Patch).GetMethod(nameof(OpenPostfix), statics)!));
        }
        else if (IsCloseMethod(typeName, methodName))
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(PauseSuppression_Patch).GetMethod(nameof(ClosePrefix), statics)!),
                postfix: new HarmonyMethod(typeof(PauseSuppression_Patch).GetMethod(nameof(ClosePostfix), statics)!));
        }

        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Core", "pause");
        yield return ("Core", "unpause");
        yield return ("Map", "open");
        yield return ("Map", "close");
        yield return ("Journal", "open");
        yield return ("Journal", "close");
        yield return ("Journal", "showNote");
        yield return ("Journal", "hideNote");
        yield return ("Padlock", "activate");
        yield return ("Padlock", "deactivate");
        yield return ("DialogueWindow", "SetDialogue");
        yield return ("DialogueWindow", "close");
        yield return ("LevelingMenu", "show");
        yield return ("LevelingMenu", "hide");
        yield return ("InteractiveItem", "open");
        yield return ("InteractiveItem", "close");
        yield return ("SkillPointsMenu", "show");
        yield return ("SkillPointsMenu", "hide");
        yield return ("SkillPointsMenu", "open");
        yield return ("SkillPointsMenu", "close");
        yield return ("SkillSlotsMenu", "show");
        yield return ("SkillSlotsMenu", "hide");
        yield return ("SkillSlotsMenu", "open");
        yield return ("SkillSlotsMenu", "close");
    }

    private static bool IsOpenMethod(string type, string method) =>
        method is "open" or "show" or "activate" or "SetDialogue" or "showNote";

    private static bool IsCloseMethod(string type, string method) =>
        method is "close" or "hide" or "deactivate" or "hideNote";

    public static bool CorePausePrefix()
    {
        if (PauseSuppression.MultiplayerActive && PauseSuppression.SuppressPause > 0)
            return false;
        return true;
    }

    public static bool CoreUnpausePrefix()
    {
        if (PauseSuppression.MultiplayerActive && PauseSuppression.SuppressUnpause > 0)
            return false;
        return true;
    }

    public static void CoreUnpausePostfix()
    {
        if (!PauseSuppression.MultiplayerActive) return;
        if (FreezeTracker.IsFrozen)
        {
            try { Core.pause(keepMusicAndEnviromental: true); } catch { }
        }
    }

    public static void OpenPrefix() => PauseSuppression.BeginNoPause();
    public static void OpenPostfix() => PauseSuppression.EndNoPause();
    public static void ClosePrefix() => PauseSuppression.BeginNoUnpause();
    public static void ClosePostfix() => PauseSuppression.EndNoUnpause();
}
