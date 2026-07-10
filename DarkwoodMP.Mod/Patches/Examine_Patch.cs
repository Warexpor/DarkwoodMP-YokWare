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
/// Examine + HidingPlace (Horde ExaminableSyncPatches slim).
/// Non-auth examine requests authority run; authority fans examined state.
/// Clients suppress HidingPlace AI spawn (host entity sync owns characters).
/// </summary>
public sealed class Examine_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.DeclaringType!.Name == "Examinable")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Examine_Patch).GetMethod(nameof(ExaminePrefix), statics)!),
                postfix: new HarmonyMethod(typeof(Examine_Patch).GetMethod(nameof(ExaminePostfix), statics)!));
        }
        else if (target.DeclaringType!.Name == "HidingPlace")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Examine_Patch).GetMethod(nameof(HidingPlacePrefix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Examinable", "examine");
        yield return ("HidingPlace", "OnEnable");
    }

    public static void ExaminePrefix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return;
            if (manager.IsTimeAuthority) return;
            if (__instance is not Component c) return;

            var pos = c.transform.position;
            network.SendReliable(new ExamineRequestPacket
            {
                PlayerId = manager.LocalPlayerId,
                ObjectName = c.gameObject.name,
                X = pos.x, Y = pos.y, Z = pos.z
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Examine] prefix: {ex.Message}");
        }
    }

    public static void ExaminePostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return;
            if (!manager.IsTimeAuthority) return;
            if (__instance is not Examinable ex) return;

            BroadcastState(ex, network, manager.LocalPlayerId);
        }
        catch (Exception e)
        {
            ModLogger.Error($"[Examine] postfix: {e.Message}");
        }
    }

    public static bool HidingPlacePrefix()
    {
        var manager = NetworkManager.Instance;
        if (manager == null || !manager.IsConnected) return true;
        // Non-authority: no second AI in the cabinet
        if (!manager.IsTimeAuthority)
            return false;
        return true;
    }

    public static void ApplyRemoteState(string name, Vector3 pos, bool examined, int pool)
    {
        try
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Examinable)))
            {
                if (obj is not Examinable ex) continue;
                if (ex.gameObject.name != name) continue;
                if ((ex.transform.position - pos).sqrMagnitude > 4f) continue;
                RemoteApply.Active = true;
                try
                {
                    ex.examined = examined;
                    // pool may be int or unused depending on build — set via reflection if needed
                    try
                    {
                        var f = typeof(Examinable).GetField("displayedDescriptionPool",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (f != null && f.FieldType == typeof(int))
                            f.SetValue(ex, pool);
                        else if (f != null && f.FieldType == typeof(bool))
                            f.SetValue(ex, pool != 0);
                    }
                    catch { }
                }
                finally
                {
                    RemoteApply.Active = false;
                }
                return;
            }
        }
        catch (Exception e)
        {
            ModLogger.Error($"[Examine] ApplyRemoteState: {e.Message}");
        }
    }

    public static void ApplyRemoteRequest(int requesterId, string name, Vector3 pos)
    {
        try
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Examinable)))
            {
                if (obj is not Examinable ex) continue;
                if (ex.gameObject.name != name) continue;
                if ((ex.transform.position - pos).sqrMagnitude > 4f) continue;
                RemoteApply.Active = true;
                try { ex.examine(); }
                finally { RemoteApply.Active = false; }

                var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
                var manager = NetworkManager.Instance;
                if (network != null && manager != null)
                    BroadcastState(ex, network, manager.LocalPlayerId);
                return;
            }
        }
        catch (Exception e)
        {
            ModLogger.Error($"[Examine] ApplyRemoteRequest: {e.Message}");
        }
    }

    private static void BroadcastState(Examinable ex, NetworkLayer network, int playerId)
    {
        var pos = ex.transform.position;
        network.SendReliable(new ExamineStatePacket
        {
            PlayerId = playerId,
            ObjectName = ex.gameObject.name,
            X = pos.x, Y = pos.y, Z = pos.z,
            Examined = ex.examined,
            DescriptionPool = GetDescriptionPool(ex)
        });
    }

    private static int GetDescriptionPool(Examinable ex)
    {
        try
        {
            var f = typeof(Examinable).GetField("displayedDescriptionPool",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return 0;
            var v = f.GetValue(ex);
            if (v is int i) return i;
            if (v is bool b) return b ? 1 : 0;
        }
        catch { }
        return 0;
    }
}
