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
/// Broadcasts firearm shots so partners see the muzzle flash on the shooter's
/// clone. Cosmetic only - ranged DAMAGE is already synced (PvpHit_Patch for
/// clones, CharDamage_Patch for enemies). Receive side: RangedSync.OnRemoteFired.
/// </summary>
public sealed class WeaponFire_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(WeaponFire_Patch).GetMethod(nameof(Postfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "fireWeapon");
    }

    public static void Postfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Player player) return;
            if (player.transform.root.name.StartsWith("RemotePlayer_")) return;
            if (InvItemClass.isNull(player.currentItem) || player.currentItem.baseClass == null) return;
            if (!player.currentItem.baseClass.isFirearm) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var pos = player.transform.position;
            var aimY = player.transform.eulerAngles.y;

            // firedweapon:<type>:<aimY>:<x,y,z>
            network.Send(new FiredWeaponPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                ItemType = player.currentItem.type,
                AimY = aimY,
                X = pos.x, Y = pos.y, Z = pos.z
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WeaponFire_Patch] {ex.Message}");
        }
    }
}
