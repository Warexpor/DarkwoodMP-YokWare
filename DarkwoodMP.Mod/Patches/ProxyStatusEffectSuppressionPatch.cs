using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(CharacterEffect), "initialize", typeof(InvItemEffect))]
    public static class ProxyStatusEffectSuppressionPatch
    {
        private static bool Prefix(CharacterEffect __instance)
        {
            if (__instance.GetComponent<RemotePlayerProxy>() != null)
                return false;
            return true;
        }
    }
}
