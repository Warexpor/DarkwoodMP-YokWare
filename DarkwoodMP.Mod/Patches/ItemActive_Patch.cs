using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Broadcasts light-item activation (torch lit, flashlight on, flare struck).
/// Verified game API: InvItemClass.switchActive(bool destActive, bool isDeselect)
/// is the single toggle point (Player.activateItem, switchToItem, deselect,
/// durability burn-out all go through it). Only items that actually emit light
/// (lightEmitter prefab, flashlight or natural light) are synced.
/// </summary>
public sealed class ItemActive_Patch : IPatch
{
    private static readonly Dictionary<string, bool> _lastSent = new();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _lastSent.Clear();
        var postfix = new HarmonyMethod(typeof(ItemActive_Patch).GetMethod(nameof(Postfix), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("InvItemClass", "switchActive");
    }

    /// <summary>
    /// Re-announce all currently active light items. Called when a player
    /// joins so they see torches/flashlights lit before they connected.
    /// </summary>
    public static void RebroadcastActive()
    {
        try
        {
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            foreach (var kvp in _lastSent)
            {
                if (!kvp.Value) continue;
                network.SendReliable(new ItemLightPacket
                {
                    PlayerId = Math.Max(network.LocalClientId, 0),
                    ItemType = kvp.Key,
                    Active = true
                });
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemActive_Patch] {ex.Message}");
        }
    }

    // Parameter name must match the game method for Harmony binding
    private static readonly HashSet<string> _loggedTypes = new();

    public static void Postfix(object __instance, bool destActive)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not InvItemClass item) return;
            if (item.baseClass == null || string.IsNullOrEmpty(item.type)) return;

            // Only light sources are interesting to other players
            var emitsLight = item.baseClass.lightEmitter != null
                             || item.baseClass.isFlashlight
                             || item.baseClass.isNaturalLight
                             || item.baseClass.lightRadius > 0f;

            // One diagnostic line per item type so playtest logs show exactly
            // which items pass the light gate and with which flags
            if (_loggedTypes.Add(item.type))
                ModLogger.Msg($"[ItemActive] '{item.type}' active={destActive} emitter={(item.baseClass.lightEmitter != null)} flashlight={item.baseClass.isFlashlight} natural={item.baseClass.isNaturalLight} radius={item.baseClass.lightRadius:F1} -> synced={emitsLight}");

            if (!emitsLight) return;

            if (_lastSent.TryGetValue(item.type, out var last) && last == destActive) return;
            _lastSent[item.type] = destActive;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            ModLogger.Msg($"[ItemActive] Broadcasting itemlight:{item.type}:{(destActive ? 1 : 0)}");
            network.SendReliable(new ItemLightPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                ItemType = item.type,
                Active = destActive
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemActive_Patch] {ex.Message}");
        }
    }
}
