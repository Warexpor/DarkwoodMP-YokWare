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
/// Door.open during active dream session → peers (Horde DreamDoorSync).
/// Normal doors already use DoorSync; dreams pause entity bulk so doors need this.
/// </summary>
public sealed class DreamDoor_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(DreamDoor_Patch).GetMethod(nameof(OpenPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Door", "open");
    }

    public static void OpenPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (!DreamSession.IsActive) return;
            if (__instance is not Door door) return;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected) return;

            var pos = door.transform.position;
            network.SendReliable(new DreamDoorPacket
            {
                PlayerId = manager.LocalPlayerId,
                DoorName = door.gameObject.name.Replace(":", "_"),
                X = pos.x, Y = pos.y, Z = pos.z
            });
        }
        catch (Exception ex) { ModLogger.Error($"[DreamDoor] {ex.Message}"); }
    }

    public static void ApplyRemote(string name, Vector3 pos)
    {
        try
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Door)))
            {
                if (obj is not Door d) continue;
                if (d.gameObject.name != name && !d.gameObject.name.StartsWith(name)) continue;
                if ((d.transform.position - pos).sqrMagnitude > 9f) continue;
                RemoteApply.Active = true;
                try
                {
                    if (!d.opened)
                        d.open(pos, d.transform, 1f);
                }
                finally { RemoteApply.Active = false; }
                return;
            }
        }
        catch (Exception ex) { ModLogger.Error($"[DreamDoor] Apply: {ex.Message}"); }
    }
}
