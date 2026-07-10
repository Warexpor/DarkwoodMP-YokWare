using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Enemy damage to barricades (v0.4). Barricade_Patch covers the PLAYER
/// actions (build/destroy), but during a night siege enemies chip barricade
/// health through Door.getHit / Window.getHit - without sync, one player
/// sees an intact barricade while the other watches it splinter.
///
/// Postfixes broadcast the ABSOLUTE remaining health over the existing
/// idempotent "doorbar"/"winbar" channels (BarricadeSync replay). Once the
/// barricade breaks, getHit runs destroyBarricade internally and the
/// existing Barricade_Patch postfix broadcasts the destruction - this patch
/// deliberately stays silent then (barricaded is already false).
///
/// Double-count safety: enemies frozen as remote mirrors never attack, so
/// each swing is only ever simulated - and broadcast - on one machine.
/// </summary>
public sealed class SiegeDamage_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var name = target.DeclaringType!.Name == "Door" ? nameof(DoorHitPostfix) : nameof(WindowHitPostfix);
        baseHarmony.Patch(target, postfix: new HarmonyMethod(typeof(SiegeDamage_Patch).GetMethod(name, statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Door", "getHit");
        yield return ("Window", "getHit");
    }

    public static void DoorHitPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Door door || !door.barricaded || door.barricadeHealth <= 0) return;
            Send(BarricadeStatePacket.KindDoor, door, door.barricadeHealth);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[SiegeDamage_Patch] {ex.Message}");
        }
    }

    public static void WindowHitPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Window window || !window.barricaded || window.barricadeHealth <= 0) return;
            Send(BarricadeStatePacket.KindWindow, window, window.barricadeHealth);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[SiegeDamage_Patch] {ex.Message}");
        }
    }

    private static void Send(byte kind, Component target, int health)
    {
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        if (network == null || !network.IsConnected) return;
        network.SendReliable(new BarricadeStatePacket
        {
            PlayerId = Math.Max(network.LocalClientId, 0),
            Kind = kind,
            ObjectId = GameIds.ForComponent(target),
            Barricaded = true,
            Health = health
        });
    }
}
