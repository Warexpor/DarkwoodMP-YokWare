using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Death loot drop sync (v0.6 audit find). Verified via IL: the onDeath
/// coroutine (&lt;onDeath&gt;d__435) calls Player.dropBody up to 3× - each spawns
/// an "Objects/_Unique/deathDrop" bag and moves a RANDOM subset of the dead
/// player's items into it (Inventory.getAllItems + Random.Range). The bag is a
/// plain world container; before this patch the dying machine had it but the
/// partner never saw it, and the random selection was unseeded.
///
/// This postfix runs on the local dying player, finds each newly-populated
/// deathDrop bag and broadcasts its ACTUAL contents (not a re-roll) plus the
/// prefab + position. The partner recreates an identical bag
/// (ContainerSync.OnRemoteDeathDrop); from then on it is an ordinary synced
/// container - looting reconciles through the existing name+position matching,
/// so items can be recovered by either player with no duplication.
/// </summary>
public sealed class DeathDrop_Patch : IPatch
{
    // Bags already broadcast this session (instance ids never repeat in a process)
    private static readonly HashSet<int> _broadcast = new();

    // Per-broadcast counter. onDeath calls dropBody 3x and EVERY bag spawns at
    // the SAME player-death position with the SAME name ("deathDrop") - verified
    // by IL. Without a discriminator the partner's spatial idempotency would
    // collapse all three into one (losing 2/3 of the loot) and container
    // reconciliation (keyed on name@x,z) would alias them. So each bag gets a
    // globally-unique token "<clientId>-<seq>" which is appended to the object
    // name on BOTH machines -> unique, collision-free container id per bag.
    private static int _dropSeq;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(DeathDrop_Patch).GetMethod(nameof(DropPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "dropBody");
    }

    public static void DropPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Player player || player != Player.Instance) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var containerSync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ContainerSync>();
            var inventoryType = GameTypes.GetType("Inventory");
            if (containerSync == null || inventoryType == null) return;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(inventoryType))
            {
                if (obj is not Component bag) continue;
                var name = bag.gameObject.name;
                if (!name.StartsWith("deathDrop", StringComparison.Ordinal)) continue;
                if (name.Contains("marker")) continue;       // map marker, not a loot bag
                if (name.Contains("#")) continue;            // already tokenised (re-scan)
                if (!_broadcast.Add(bag.gameObject.GetInstanceID())) continue; // already sent

                var contents = containerSync.ReadContents(bag);
                if (contents.Count == 0) continue; // marker / empty bag - nothing to loot

                var payload = new StringBuilder();
                foreach (var kvp in contents)
                {
                    if (payload.Length > 0) payload.Append(';');
                    payload.Append(kvp.Key).Append(',').Append(kvp.Value);
                }

                var prefab = name.Replace("(Clone)", "");
                var uid = Math.Max(network.LocalClientId, 0) + "-" + (++_dropSeq);

                // Rename our own bag so its container id (name@x,z) is unique and
                // matches the partner's copy - both sides append the same token.
                bag.gameObject.name = name + "#" + uid;

                var pos = bag.transform.position;
                containerSync.RecordDeathDrop(prefab, uid, pos, payload.ToString());
                network.SendReliable(new DeathDropSpawnPacket
                {
                    PlayerId = Math.Max(network.LocalClientId, 0),
                    Prefab = prefab,
                    Uid = uid,
                    X = pos.x, Y = pos.y, Z = pos.z,
                    PayloadCsv = payload.ToString()
                });
                ModLogger.Msg($"[DeathDrop_Patch] Ironbark DeathDropSpawn '{prefab}' #{uid} at {pos:F1} [{payload}]");
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DeathDrop_Patch] {ex.Message}");
        }
    }
}
