using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Story volume authority (Horde EventTriggersProxyPatches).
/// Vanilla only reacts to Player.Instance — client walk-ins never fire host GameEvents.
/// Non-authority: suppress local enter/exit. Authority: treat RemotePlayerProxy as player.
/// </summary>
public sealed class EventTriggers_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var isEnter = target.Name == "OnTriggerEnter";
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(EventTriggers_Patch).GetMethod(
                isEnter ? nameof(EnterPrefix) : nameof(ExitPrefix), statics)!),
            postfix: new HarmonyMethod(typeof(EventTriggers_Patch).GetMethod(
                isEnter ? nameof(EnterPostfix) : nameof(ExitPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("EventTriggers", "OnTriggerEnter");
        yield return ("EventTriggers", "OnTriggerExit");
    }

    private static bool IsAuthorityConnected()
    {
        var m = NetworkManager.Instance;
        return m != null && m.IsConnected && m.IsTimeAuthority;
    }

    private static bool IsConnectedNonAuthority()
    {
        var m = NetworkManager.Instance;
        return m != null && m.IsConnected && !m.IsTimeAuthority;
    }

    /// <summary>Non-authority: skip entire volume handling (authority owns story zones).</summary>
    public static bool EnterPrefix()
    {
        return !IsConnectedNonAuthority();
    }

    public static bool ExitPrefix()
    {
        return !IsConnectedNonAuthority();
    }

    public static void EnterPostfix(object __instance, Collider other)
    {
        try
        {
            if (!IsAuthorityConnected()) return;
            if (__instance is not EventTriggers et || other == null) return;
            if (!et.reactsToPlayer) return;
            if (!CanFire(et)) return;

            var proxy = other.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null) return;

            // Mirror vanilla multi-collider guard when already entered
            if (et.entered != 0)
            {
                try
                {
                    var pos = proxy.transform.position;
                    int mask = 1 << et.gameObject.layer;
                    if (Helpers.isComponentAtPos(pos, mask, et))
                        return;
                }
                catch { /* Helpers may differ */ }
            }

            et.fireEventTrigger(EventTrigger.Type.area);
            et.entered++;
            if (NetworkLayer.VerboseLogging)
                ModLogger.Msg($"[EventTriggers] proxy enter p{proxy.PlayerId} on {et.name}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[EventTriggers] EnterPostfix: {ex.Message}");
        }
    }

    public static void ExitPostfix(object __instance, Collider other)
    {
        try
        {
            if (!IsAuthorityConnected()) return;
            if (__instance is not EventTriggers et || other == null) return;
            if (!et.reactsToPlayer) return;
            if (!CanFire(et)) return;

            var proxy = other.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null) return;

            try
            {
                var pos = proxy.transform.position;
                int mask = 1 << et.gameObject.layer;
                if (Helpers.isComponentAtPos(pos, mask, et))
                    return;
            }
            catch { }

            et.exited++;
            if (et.exited >= et.entered)
            {
                try { et.fireEventTriggerExit(EventTrigger.Type.area); }
                catch (Exception ex)
                {
                    ModLogger.Error($"[EventTriggers] exit fire: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[EventTriggers] ExitPostfix: {ex.Message}");
        }
    }

    private static bool CanFire(EventTriggers et)
    {
        try
        {
            if ((Core.loadingGame || Singleton<SaveManager>.Instance.dontFireTriggers) && !et.canFireWhenLoadingGame)
                return false;
            if (!Core.worldGenFinished())
                return false;
            if (Singleton<OutsideLocations>.Instance != null && Singleton<OutsideLocations>.Instance.loading)
                return false;
        }
        catch { return true; }
        return true;
    }
}
