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
/// Client world melee on doors/windows/items → authority apply (Horde MeleeWorldHit).
/// </summary>
public sealed class WorldMelee_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(WorldMelee_Patch).GetMethod(nameof(GetHitPrefix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Door", "getHit");
        yield return ("Window", "getHit");
        yield return ("Item", "getHit");
    }

    public static bool GetHitPrefix(object __instance, float damage, MethodBase __originalMethod)
    {
        try
        {
            if (RemoteApply.Active) return true;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return true;
            // Authority applies locally
            if (manager.IsTimeAuthority) return true;
            if (__instance is not Component c) return true;

            // Only player-originated hits (melee on world objects)
            if (Player.Instance == null) return true;

            var pos = c.transform.position;
            byte type = 0;
            var tn = c.GetType().Name;
            if (tn == "Window") type = 1;
            else if (tn == "Item") type = 2;

            damage = DamageSync.SanitizePeerDamage(damage);
            network.SendReliable(new MeleeWorldHitPacket
            {
                PlayerId = manager.LocalPlayerId,
                HitType = type,
                X = pos.x, Y = pos.y, Z = pos.z,
                Damage = damage
            });
            // Suppress local apply — authority will apply once
            return false;
        }
        catch { return true; }
    }

    public static void ApplyRemote(byte type, Vector3 pos, float damage)
    {
        try
        {
            Type t = type == 1 ? typeof(Window) : type == 2 ? typeof(Item) : typeof(Door);
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(t))
            {
                if (obj is not Component c) continue;
                if ((c.transform.position - pos).sqrMagnitude > 2.25f) continue;
                RemoteApply.Active = true;
                try
                {
                    var m = c.GetType().GetMethod("getHit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (m == null) return;
                    var ps = m.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType == typeof(float))
                    {
                        var args = new object[ps.Length];
                        args[0] = damage;
                        for (int i = 1; i < ps.Length; i++)
                            args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                        m.Invoke(c, args);
                    }
                }
                finally { RemoteApply.Active = false; }
                return;
            }
        }
        catch (Exception ex) { ModLogger.Error($"[WorldMelee] Apply: {ex.Message}"); }
    }
}
