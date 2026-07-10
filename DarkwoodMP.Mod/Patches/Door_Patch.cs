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
/// Patches Darkwood's Door.open/Door.close to broadcast door state changes.
/// Verified game API: Door.open(Vector3 openerPosition, Transform openerTransform, float OpenForce),
/// Door.close(Transform openerTransform), field `opened` (bool).
/// The opener position is sent along because Door.open() uses it to decide
/// which way the door swings - without it doors open in random directions remotely.
/// </summary>
public sealed class Door_Patch : IPatch
{
    private static readonly Dictionary<string, bool> _lastSent = new();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _lastSent.Clear(); // don't carry state suppression across sessions
        var postfixName = target.Name == "open" ? nameof(OpenPostfix) : nameof(ClosePostfix);
        var postfix = new HarmonyMethod(typeof(Door_Patch).GetMethod(postfixName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Door", "open");
        yield return ("Door", "close");
    }

    // Harmony binds `openerPosition` / `openerTransform` to the original method arguments
    public static void OpenPostfix(object __instance, Vector3 openerPosition)
        => Send(__instance, true, openerPosition);

    public static void ClosePostfix(object __instance, Transform openerTransform)
    {
        var opener = openerTransform != null && __instance is Component c && openerTransform != c.transform
            ? openerTransform.position
            : (__instance as Component)?.transform.position ?? Vector3.zero;
        Send(__instance, false, opener);
    }

    private static void Send(object doorInstance, bool isOpen, Vector3 openerPosition)
    {
        try
        {
            if (RemoteApply.Active) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            if (doorInstance is not Component door) return;
            var doorId = GameIds.ForComponent(door);

            if (_lastSent.TryGetValue(doorId, out var last) && last == isOpen) return;
            _lastSent[doorId] = isOpen;

            network.SendReliable(new DoorStatePacket
            {
                DoorId = doorId,
                IsOpen = isOpen,
                PlayerId = Math.Max(network.LocalClientId, 0),
                OpenerX = openerPosition.x,
                OpenerY = openerPosition.y,
                OpenerZ = openerPosition.z
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Door_Patch] {ex.Message}");
        }
    }
}
