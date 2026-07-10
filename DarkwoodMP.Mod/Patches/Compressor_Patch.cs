using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Oxygen tank give/convert via compressor (Horde CompressorSync).
/// Broadcasts inventory mutations so peers keep tank state.
/// </summary>
public sealed class Compressor_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(Compressor_Patch).GetMethod(nameof(Postfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Compressor", "convert");
        yield return ("Compressor", "use");
        yield return ("Compressor", "OnUse");
        yield return ("InvItemClass", "use"); // fallback if oxygen items use this
    }

    public static void Postfix(object __instance, MethodBase __originalMethod)
    {
        try
        {
            if (RemoteApply.Active) return;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected) return;

            // Heuristic: compressor convert empties empty tanks → full
            var typeName = __instance?.GetType().Name ?? "";
            if (typeName.IndexOf("Compressor", StringComparison.OrdinalIgnoreCase) < 0
                && (__originalMethod?.Name ?? "").IndexOf("convert", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            network.SendReliable(new OxygenConvertPacket
            {
                PlayerId = manager.LocalPlayerId
            });
            ModLogger.Msg("[Compressor] Broadcast oxyconvert");
        }
        catch (Exception ex) { ModLogger.Error($"[Compressor] {ex.Message}"); }
    }

    public static void ApplyRemoteConvert()
    {
        // Peer: convert empty oxygen tanks in inventory to full (best-effort type names)
        try
        {
            var player = Player.Instance;
            if (player?.Inventory == null) return;
            RemoteApply.Active = true;
            try
            {
                // Common Darkwood oxygen item type names
                string[] empty = { "oxygen_empty", "oxygenTankEmpty", "o2_empty", "oxygen_tank_empty" };
                string[] full = { "oxygen", "oxygenTank", "o2", "oxygen_tank" };
                for (int i = 0; i < empty.Length; i++)
                {
                    try
                    {
                        player.Inventory.removeItemAmount(empty[i], 99);
                        player.Inventory.addItemType(full[Math.Min(i, full.Length - 1)], 1);
                    }
                    catch { }
                }
            }
            finally { RemoteApply.Active = false; }
        }
        catch (Exception ex) { ModLogger.Error($"[Compressor] Apply: {ex.Message}"); }
    }
}
