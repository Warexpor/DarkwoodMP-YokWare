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
/// Patches Darkwood's InteractiveItem (levers, generators, switches) to broadcast
/// on/off state changes. Verified game API: InteractiveItem.switchOn(),
/// InteractiveItem.switchOff(), field `isOn` (bool).
/// </summary>
public sealed class InteractiveItem_Patch : IPatch
{
    private static readonly Dictionary<string, bool> _lastSent = new();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _lastSent.Clear(); // don't carry state suppression across sessions
        var postfixName = target.Name == "switchOn" ? nameof(OnPostfix) : nameof(OffPostfix);
        var postfix = new HarmonyMethod(typeof(InteractiveItem_Patch).GetMethod(postfixName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("InteractiveItem", "switchOn");
        yield return ("InteractiveItem", "switchOff");
    }

    public static void OnPostfix(object __instance) => Send(__instance, true);
    public static void OffPostfix(object __instance) => Send(__instance, false);

    private static void Send(object instance, bool isActive)
    {
        try
        {
            if (RemoteApply.Active) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            if (instance is not Component item) return;
            var objectId = GameIds.ForComponent(item);

            if (_lastSent.TryGetValue(objectId, out var last) && last == isActive) return;
            _lastSent[objectId] = isActive;

            network.SendReliable(new InteractiveStatePacket
            {
                ObjectId = objectId,
                ObjectType = "InteractiveItem",
                IsActive = isActive,
                PlayerId = Math.Max(network.LocalClientId, 0)
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractiveItem_Patch] {ex.Message}");
        }
    }
}
