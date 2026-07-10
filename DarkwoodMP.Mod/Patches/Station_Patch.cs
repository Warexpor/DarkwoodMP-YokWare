using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Shared world stations (v0.4):
/// - Saw: convert (woodLog -&gt; wood into the saw's own inventory) and addFuel
///   (Player.waitToSpillLiquid, like the generator). Fuel travels as an
///   ABSOLUTE value; the output inventory goes over the container channel.
/// - Feeder: activating it consumes it (activate -&gt; makeInactive) and buffs
///   the activator - the state flip replicates, the buff stays personal.
/// - Lure: animals eating the bait chip its health - absolute value, only
///   broadcast by the machine simulating the eating animal.
/// </summary>
public sealed class Station_Patch : IPatch
{
    private static FieldInfo _sawInventoryField; // Saw.inventory (private)

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var name = target.DeclaringType!.Name switch
        {
            "Saw" => target.Name == "convert" ? nameof(SawConvertPostfix) : nameof(SawFuelPostfix),
            "Feeder" => nameof(FeederPostfix),
            _ => nameof(LurePostfix)
        };
        // Lure loot rolls (Random.Range inside removeHealth) are seeded per
        // lure position so both machines roll the same drops (v0.5)
        var lurePrefix = target.DeclaringType.Name == "Lure"
            ? new HarmonyMethod(typeof(Station_Patch).GetMethod(nameof(LureSeedPrefix), statics)!)
            : null;
        baseHarmony.Patch(target,
            prefix: lurePrefix,
            postfix: new HarmonyMethod(typeof(Station_Patch).GetMethod(name, statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Saw", "convert");
        yield return ("Saw", "addFuel");
        yield return ("Feeder", "activate");
        yield return ("Lure", "removeHealth");
    }

    public static void SawConvertPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Saw saw) return;

            SendSawFuel(saw);

            // The produced planks live in the saw's own (private) inventory
            _sawInventoryField ??= typeof(Saw).GetField("inventory",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_sawInventoryField?.GetValue(saw) is UnityEngine.Component inventory)
                DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ContainerSync>()?.FlushExternal(inventory);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Station_Patch] {ex.Message}");
        }
    }

    public static void SawFuelPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is Saw saw)
                SendSawFuel(saw);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Station_Patch] {ex.Message}");
        }
    }

    private static void SendSawFuel(Saw saw)
    {
        SendSawFuelPacket(GameIds.ForComponent(saw), saw.fuel);
    }

    public static void FeederPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is Feeder feeder)
                SendFeeder(GameIds.ForComponent(feeder));
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Station_Patch] {ex.Message}");
        }
    }

    public static void LureSeedPrefix(object __instance)
    {
        var seed = WorldGenSeed_Patch.BaseSeed;
        if (seed == 0) return;
        if (__instance is not Lure lure) return;
        var p = lure.transform.position;
        // Pre-bite health is synced, so both machines derive the same seed
        UnityEngine.Random.InitState(seed
            ^ ((int)UnityEngine.Mathf.Round(p.x) * 73856093)
            ^ ((int)UnityEngine.Mathf.Round(p.z) * 19349663)
            ^ lure.health);
    }

    // Parameter name matches Lure.removeHealth(int amountToRemove, Character eatingCharacter)
    public static void LurePostfix(object __instance, Character eatingCharacter)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Lure lure) return;

            // Only the machine simulating the eating animal reports the bite -
            // frozen mirrors never eat, but the border band could double-count
            if (eatingCharacter != null)
            {
                var enemySync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<GameLogic.EnemySync>();
                if (enemySync != null && !enemySync.IsLocallySimulated(eatingCharacter)) return;
            }

            // Via the coalescing outbox, NOT a direct send: scripted eaters call
            // removeHealth every frame, and one reliable packet per bite flooded
            // the channel and spam-froze the partner's console (v0.7 playtest).
            DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<GameLogic.StationSync>()
                ?.QueueLureHealth(GameIds.ForComponent(lure), lure.health);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Station_Patch] {ex.Message}");
        }
    }

    private static void SendSawFuelPacket(string objectId, float fuel)
    {
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        if (network == null || !network.IsConnected) return;
        network.SendReliable(new StationSawFuelPacket
        {
            PlayerId = Math.Max(network.LocalClientId, 0),
            ObjectId = objectId ?? "",
            Fuel = fuel
        });
    }

    private static void SendFeeder(string objectId)
    {
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        if (network == null || !network.IsConnected) return;
        network.SendReliable(new StationFeederPacket
        {
            PlayerId = Math.Max(network.LocalClientId, 0),
            ObjectId = objectId ?? "",
            Payload = ""
        });
    }
}
