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
/// Broadcasts firearm impact FX (bullet wall hits, character splats, blood)
/// so partners see where shots land. Cosmetic only - the receive side
/// (RangedSync.OnRemoteBulletFx) replays the exact prefab under RemoteApply so
/// it is not re-forwarded.
///
/// Two hooks:
///  - Core.AddPooledPrefab(pool="FX", "bullet_hit_1"/"Shotsplat1", ...) - the
///    hitscan/pellet impact splats, forwarded only while WE are firing a gun.
///  - Core.AddPrefab("FX/Bloodsplats/...", ...) - blood, forwarded only when it
///    happens near a player (avoids replaying save-loaded environmental blood).
/// </summary>
public sealed class BulletFX_Patch : IPatch
{
    private const float NearPlayerRange = 60f;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var coreType = target.DeclaringType!;

        if (target.Name == "AddPooledPrefab")
        {
            var m = coreType.GetMethod("AddPooledPrefab",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                        null, new[] { typeof(string), typeof(string), typeof(Vector3), typeof(Quaternion) }, null)
                    ?? target;
            baseHarmony.Patch(m, prefix: new HarmonyMethod(typeof(BulletFX_Patch).GetMethod(nameof(PooledPrefix), statics)!));
            return (baseHarmony, m);
        }

        // Core.AddPrefab(string, Vector3, Quaternion, GameObject, bool)
        var addPrefab = coreType.GetMethod("AddPrefab",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance,
                            null, new[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) }, null)
                        ?? target;
        baseHarmony.Patch(addPrefab, prefix: new HarmonyMethod(typeof(BulletFX_Patch).GetMethod(nameof(PrefabPrefix), statics)!));
        return (baseHarmony, addPrefab);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Core", "AddPooledPrefab");
        yield return ("Core", "AddPrefab");
    }

    public static void PooledPrefix(string pool, string prefab, Vector3 position, Quaternion quaternion)
    {
        try
        {
            // Suppress under RemoteApply.Active (bulletfx replays must not
            // loop, mirror-side die() gore must not echo back) UNLESS the
            // authority is applying a client-forwarded enemy hit - the blood
            // born from THAT getHit is first-hand FX and must broadcast, or
            // the attacking client never sees blood for its own hits.
            if (RemoteApply.Active && !RemoteApply.BroadcastFxFromApply) return;
            if (pool != "FX") return;

            // Bleed-effect drips under a wounded character: only the machine
            // running the damage sim (the authority for enemies, each machine
            // for its own player) ever spawns these, so forwarding can't dupe.
            if (prefab == "BleedSplat")
            {
                if (NearAnyPlayer(position))
                    Broadcast("FX", prefab, position, quaternion.eulerAngles);
                return;
            }

            if (prefab != "bullet_hit_1" && prefab != "Shotsplat1") return;

            // Forward these when the local player is firing a gun (the same FX
            // names are reused by other systems), OR when we are the authority
            // applying a remote player's forwarded hit.
            if (!RemoteApply.BroadcastFxFromApply)
            {
                var p = Player.Instance;
                if (p == null) return;
                if (InvItemClass.isNull(p.currentItem) || p.currentItem.baseClass == null) return;
                if (!p.currentItem.baseClass.isFirearm) return;
            }

            Broadcast("FX", prefab, position, quaternion.eulerAngles);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[BulletFX_Patch] pooled: {ex.Message}");
        }
    }

    public static void PrefabPrefix(string prefab, Vector3 position, Quaternion quaternion)
    {
        try
        {
            // See PooledPrefix: blood spawned while the authority applies a
            // forwarded hit must travel; every other apply stays suppressed.
            if (RemoteApply.Active && !RemoteApply.BroadcastFxFromApply) return;
            if (prefab == null || !prefab.StartsWith("FX/Bloodsplats/")) return;
            if (!NearAnyPlayer(position)) return;

            Broadcast("", prefab, position, quaternion.eulerAngles);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[BulletFX_Patch] prefab: {ex.Message}");
        }
    }

    private static bool NearAnyPlayer(Vector3 pos)
    {
        var local = Player.Instance;
        if (local != null && (local.transform.position - pos).sqrMagnitude <= NearPlayerRange * NearPlayerRange)
            return true;

        var manager = NetworkManager.Instance;
        if (manager != null)
        {
            foreach (var kvp in manager.RemotePlayers)
            {
                if (kvp.Value != null
                    && (kvp.Value.transform.position - pos).sqrMagnitude <= NearPlayerRange * NearPlayerRange)
                    return true;
            }
        }
        return false;
    }

    private static void Broadcast(string pool, string prefab, Vector3 pos, Vector3 rotEuler)
    {
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        if (network == null || !network.IsConnected) return;

        // bulletfx:<pool>:<prefab>:<x,y,z>:<rx,ry,rz>  (pool empty for AddPrefab)
        network.Send(new BulletFxPacket
        {
            PlayerId = Math.Max(network.LocalClientId, 0),
            Pool = pool ?? "",
            Prefab = prefab ?? "",
            X = pos.x, Y = pos.y, Z = pos.z,
            Rx = rotEuler.x, Ry = rotEuler.y, Rz = rotEuler.z
        });
    }
}
