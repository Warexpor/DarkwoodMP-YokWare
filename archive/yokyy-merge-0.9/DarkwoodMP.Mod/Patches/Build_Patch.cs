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
/// Syncs player-built world changes:
///  - PLACED items (beartraps, lures, ...). Verified in IL:
///    Player.progressBarCompleted, `placingItem` branch, spawns
///    currentItem.baseClass.item at the proxy ghost's position via
///    Core.AddPrefab and marks Trigger.setByPlayer.
///  - CONSTRUCTIONS. Constructible.construct(bool manual, int forceOption):
///    manual=true consumes ingredients from the open ConstructionMenu, so the
///    remote replay uses manual=false (the same path save-loading takes).
/// </summary>
public sealed class Build_Patch : IPatch
{
    // Captured before the original runs - progressBarCompleted clears the state
    private static string _placeType;
    private static Vector3 _placePos;
    private static Quaternion _placeRot;
    private static bool _placeCaptured;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.Name == "progressBarCompleted")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Build_Patch).GetMethod(nameof(PlacePrefix), statics)!),
                postfix: new HarmonyMethod(typeof(Build_Patch).GetMethod(nameof(PlacePostfix), statics)!));
        }
        else
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(Build_Patch).GetMethod(nameof(ConstructPostfix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "progressBarCompleted");
        yield return ("Constructible", "construct");
    }

    // ------------------------------------------------------------------
    // Placed items
    // ------------------------------------------------------------------

    public static void PlacePrefix(object __instance)
    {
        _placeCaptured = false;
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Player player) return;
            if (!player.placingItem || player.proxyItem == null) return;
            if (InvItemClass.isNull(player.currentItem)) return;

            _placeType = player.currentItem.type;
            _placePos = player.proxyItem.transform.position;
            _placeRot = player.proxyItem.transform.rotation;
            _placeCaptured = true;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Build_Patch] {ex.Message}");
        }
    }

    public static void PlacePostfix()
    {
        try
        {
            if (!_placeCaptured) return;
            _placeCaptured = false;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var buildSync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<BuildSync>();
            if (network == null || !network.IsConnected) return;

            buildSync?.RecordLocalPlacement(_placeType, _placePos, _placeRot);

            network.SendReliable(new BuildPlacedPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                ItemType = _placeType ?? "",
                X = _placePos.x, Y = _placePos.y, Z = _placePos.z,
                Rx = _placeRot.x, Ry = _placeRot.y, Rz = _placeRot.z, Rw = _placeRot.w
            });
            ModLogger.Msg($"[Build_Patch] Ironbark BuildPlaced '{_placeType}' at {_placePos:F1}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Build_Patch] {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Constructions
    // ------------------------------------------------------------------

    // Harmony binds the original arguments by name
    public static void ConstructPostfix(object __instance, bool manual, int forceOption)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (!manual) return; // save-load path - not a player action
            if (__instance is not Component constructible) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var option = forceOption;
            if (option < 0 && __instance is Constructible c)
                option = c.chosenOption;

            var objectId = GameIds.ForComponent(constructible);
            ModLogger.Msg($"[Build_Patch] Constructed '{objectId}' (option {option})");

            // Session history for late joiners (v0.5) - the component
            // destroys itself after this, so record it now or never
            DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<BuildSync>()
                ?.RecordConstruct(objectId, option);

            network.SendReliable(new BuildConstructPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                ObjectId = objectId,
                Option = option
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Build_Patch] {ex.Message}");
        }
    }
}
