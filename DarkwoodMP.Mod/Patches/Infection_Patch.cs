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
/// Infection ground splat authority (Horde InfectionStatusSyncPatches).
/// Non-authority does not auto-spread; authority broadcasts spawn/disappear.
/// </summary>
public sealed class Infection_Patch : IPatch
{
    private const string PrefabPath = "Traps/infection_splat";

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        switch (target.Name)
        {
            case "waitToSpread":
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(Infection_Patch).GetMethod(nameof(WaitToSpreadPrefix), statics)!));
                break;
            case "spawnInfection":
                baseHarmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(Infection_Patch).GetMethod(nameof(SpawnPostfix), statics)!));
                break;
            case "disappear":
                baseHarmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(Infection_Patch).GetMethod(nameof(DisappearPostfix), statics)!));
                break;
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Infection", "waitToSpread");
        yield return ("Infection", "spawnInfection");
        yield return ("Infection", "disappear");
    }

    public static bool WaitToSpreadPrefix()
    {
        var m = NetworkManager.Instance;
        if (m == null || !m.IsConnected) return true;
        // Only time-authority spreads
        return m.IsTimeAuthority;
    }

    public static void SpawnPostfix(object __instance, Vector3 position)
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return;
            if (!manager.IsTimeAuthority) return;

            Vector3 pos = position;
            try { pos = Core.getYPos(position, PosType.low1); } catch { }

            network.SendReliable(new InfectionSplatPacket
            {
                PlayerId = manager.LocalPlayerId,
                Spawn = true,
                X = pos.x, Y = pos.y, Z = pos.z
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Infection] SpawnPostfix: {ex.Message}");
        }
    }

    public static void DisappearPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Component c) return;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return;
            if (!manager.IsTimeAuthority) return;

            var pos = c.transform.position;
            network.SendReliable(new InfectionSplatPacket
            {
                PlayerId = manager.LocalPlayerId,
                Spawn = false,
                X = pos.x, Y = pos.y, Z = pos.z
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Infection] DisappearPostfix: {ex.Message}");
        }
    }

    public static void ApplyRemoteSpawn(Vector3 pos)
    {
        try
        {
            RemoteApply.Active = true;
            try
            {
                Core.AddPrefab(PrefabPath, pos, Quaternion.Euler(90f, 0f, 0f), null);
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[Infection] Remote splat at {pos}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Infection] ApplyRemoteSpawn: {ex.Message}");
        }
    }

    public static void ApplyRemoteGone(Vector3 pos)
    {
        try
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Infection)))
            {
                if (obj is not Component c) continue;
                if ((c.transform.position - pos).sqrMagnitude > 2.5f * 2.5f) continue;
                RemoteApply.Active = true;
                try
                {
                    try { ((Infection)c).disappear(); }
                    catch { UnityEngine.Object.Destroy(c.gameObject); }
                }
                finally
                {
                    RemoteApply.Active = false;
                }
                return;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Infection] ApplyRemoteGone: {ex.Message}");
        }
    }
}
