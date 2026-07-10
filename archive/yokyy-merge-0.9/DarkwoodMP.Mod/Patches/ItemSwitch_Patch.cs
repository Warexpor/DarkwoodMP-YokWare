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
/// Syncs switchable world Items (lamps, standing torches - and the generator,
/// whose on/off runs through Item.turnOn/turnOff, verified via IL:
/// Item.turnOn -> Generator.turnOn). Same pattern as InteractiveItem_Patch;
/// the receive side lives in InteractiveSync (ObjectType "Item").
/// </summary>
public sealed class ItemSwitch_Patch : IPatch
{
    private static readonly Dictionary<string, bool> _lastSent = new();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _lastSent.Clear(); // don't carry state suppression across sessions
        var postfixName = target.Name == "turnOn" ? nameof(OnPostfix) : nameof(OffPostfix);
        var postfix = new HarmonyMethod(typeof(ItemSwitch_Patch).GetMethod(postfixName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Item", "turnOn");
        yield return ("Item", "turnOff");
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

            if (instance is not Item item) return;
            if (item.transform.root.name.StartsWith("RemotePlayer_")) return;
            // Only player-toggleable items. Game events (shadows, day/night)
            // flip hundreds of ambient lights in one frame on BOTH machines -
            // broadcasting those flooded the network and caused stutters.
            if (!item.switchable) return;

            var objectId = GameIds.ForComponent(item);
            if (_lastSent.TryGetValue(objectId, out var last) && last == isActive) return;
            _lastSent[objectId] = isActive;

            network.SendReliable(new InteractiveStatePacket
            {
                ObjectId = objectId,
                ObjectType = "Item",
                IsActive = isActive,
                PlayerId = Math.Max(network.LocalClientId, 0)
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemSwitch_Patch] {ex.Message}");
        }
    }
}

/// <summary>
/// Syncs generator refueling. Verified game API: Generator.addFuel(float) is
/// called when the player pours gasoline (Player.waitToSpillLiquid). The
/// resulting ABSOLUTE fuel level is broadcast so both tanks stay identical
/// regardless of packet timing.
/// </summary>
public sealed class GeneratorFuel_Patch : IPatch
{
    private static FieldInfo _fuelField;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var postfix = new HarmonyMethod(typeof(GeneratorFuel_Patch).GetMethod(nameof(Postfix), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Generator", "addFuel");
    }

    public static void Postfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;
            if (__instance is not Component generator) return;

            _fuelField ??= __instance.GetType().GetField("fuel",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_fuelField?.GetValue(__instance) is not float fuel) return;

            network.SendReliable(new GeneratorFuelPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                ObjectId = GameIds.ForComponent(generator),
                Fuel = fuel
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[GeneratorFuel_Patch] {ex.Message}");
        }
    }
}
