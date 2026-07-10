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
/// Authoritative enemy SPAWNING (v0.4). Before this, each machine spawned its
/// enemy population independently (unseeded RNG in CharacterSpawner /
/// CharacterSpawnPoint) and EnemySync could only mirror whatever happened to
/// exist on both sides - counts and timing drifted. Verified in IL:
///
/// - CharacterSpawner.spawnCharacterAround is the single choke point for all
///   DYNAMIC spawns (night scenario chars via spawnNightChar, random events,
///   game events). It spawns via Core.AddPrefab("Characters/" + type) around
///   the LOCAL player. -> postfix broadcasts the spawn ("espawn"), the other
///   machine instantiates the same prefab at the same spot and immediately
///   adopts it as a frozen mirror owned by the spawner (EnemySync).
///
/// - CharacterSpawner.spawnNightChar spawns the night scenario around
///   Player.Instance on EVERY machine. When players spend the night together
///   both machines would spawn + mirror each other = double population.
///   -> prefix suppresses local night spawning while a SENIOR player (host,
///   or lower id) is within SeniorRadius - their machine runs the night and
///   our copy arrives via espawn.
///
/// - CharacterSpawnPoint.actuallySpawn rolls Random.Range for its spawnChance
///   at a FIXED world position. -> prefix re-seeds the RNG from
///   (worldSeed ^ position hash) so both machines roll the same outcome for
///   the same spawn point, regardless of when each machine activates the
///   location. No broadcast needed - the spawn itself is deterministic.
/// </summary>
public sealed class EnemySpawn_Patch : IPatch
{
    private const float SeniorRadius = 60f; // matches EnemySync.YieldRadius

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        switch (target.Name)
        {
            case "spawnCharacterAround":
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(EnemySpawn_Patch).GetMethod(nameof(SpawnAroundPrefix), statics)!),
                    postfix: new HarmonyMethod(typeof(EnemySpawn_Patch).GetMethod(nameof(SpawnAroundPostfix), statics)!));
                break;
            case "spawnNightChar":
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(EnemySpawn_Patch).GetMethod(nameof(NightSpawnPrefix), statics)!));
                break;
            case "actuallySpawn":
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(EnemySpawn_Patch).GetMethod(nameof(SpawnPointSeedPrefix), statics)!));
                break;
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("CharacterSpawner", "spawnCharacterAround");
        yield return ("CharacterSpawner", "spawnNightChar");
        yield return ("CharacterSpawnPoint", "actuallySpawn");
    }

    /// <summary>Remote replays must never trigger a real spawn on this machine.</summary>
    public static bool SpawnAroundPrefix(ref Character __result)
    {
        if (!RemoteApply.Active) return true;
        __result = null; // all game callers null-check the result
        return false;
    }

    /// <summary>
    /// Host/auth night: ~50% of spawns recenter near a far remote (Horde NightSpawnRedirect).
    /// </summary>
    public static void SpawnAroundPrefix_Redirect(ref Vector3 __0)
    {
        // Optional: some overloads use different param names — handled in SpawnAroundPostfix origin
    }

    // Parameter name matches spawnCharacterAround(..., String type, ...)
    public static void SpawnAroundPostfix(Character __result, string type)
    {
        try
        {
            if (RemoteApply.Active || __result == null) return;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || !network.IsConnected) return;

            // ~50% redirect near a far remote so night pressure hits the whole party
            if (manager != null && manager.IsTimeAuthority && manager.RemotePlayers.Count > 0
                && UnityEngine.Random.value < 0.5f)
            {
                try
                {
                    Transform best = null;
                    float bestD = 0f;
                    var local = Player.Instance != null ? Player.Instance.transform.position : Vector3.zero;
                    foreach (var kvp in manager.RemotePlayers)
                    {
                        if (kvp.Value == null) continue;
                        var d = (kvp.Value.transform.position - local).sqrMagnitude;
                        if (d > bestD) { bestD = d; best = kvp.Value.transform; }
                    }
                    if (best != null && bestD > 40f * 40f)
                    {
                        var near = best.position + new Vector3(
                            UnityEngine.Random.Range(-15f, 15f), 0f, UnityEngine.Random.Range(-15f, 15f));
                        __result.transform.position = near;
                    }
                }
                catch { }
            }

            // Reliable one-shot spawn (EntityState continues motion after bind).
            var id = CharacterTracker.GetStableId(__result);
            if (id == 0) return;
            var pos = __result.transform.position;
            var name = type ?? __result.gameObject.name;
            if (name.EndsWith("(Clone)")) name = name.Substring(0, name.Length - 7);
            var path = "Characters/" + name;

            network.SendReliable(new EntitySpawnPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                EntityId = id,
                EntityType = name,
                PrefabPath = path,
                X = pos.x, Y = pos.y, Z = pos.z,
                RotY = __result.transform.eulerAngles.y
            });
            ModLogger.Msg($"[EnemySpawn] Reliable spawn id={id} '{name}' at {pos}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[EnemySpawn_Patch] {ex.Message}");
        }
    }

    /// <summary>
    /// Co-location dedupe: only the senior machine (host, else lowest player
    /// id) runs the night scenario when players are together.
    /// </summary>
    public static bool NightSpawnPrefix()
    {
        return !SeniorPlayerNearby();
    }

    /// <summary>
    /// Is a senior player (host, else lower id) within SeniorRadius of our
    /// player? Shared authority rule for night spawns and night events.
    /// Errs on "no" so game logic keeps running if anything is unresolved.
    /// </summary>
    internal static bool SeniorPlayerNearby()
    {
        try
        {
            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected) return false;

            var myId = Math.Max(manager.LocalPlayerId, 0);
            if (myId == 0) return false; // the host is senior everywhere

            var playerTransform = DarkwoodMP.DependencyInjection.ServiceLocator
                .Resolve<GameLogic.PlayerSync>()?.LocalPlayerTransform;
            if (playerTransform == null) return false;

            foreach (var kvp in manager.RemotePlayers)
            {
                if (kvp.Value == null || Math.Max(kvp.Key, 0) >= myId) continue;
                if ((kvp.Value.transform.position - playerTransform.position).sqrMagnitude
                    < SeniorRadius * SeniorRadius)
                    return true;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[EnemySpawn_Patch] Senior check failed (assuming alone): {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Deterministic spawn-point rolls: same seed + same spot = same outcome
    /// on every machine, no matter when the location activates locally.
    /// </summary>
    public static void SpawnPointSeedPrefix(object __instance)
    {
        var seed = WorldGenSeed_Patch.BaseSeed;
        if (seed == 0) return;
        if (__instance is not Component c) return;
        var p = c.transform.position;
        var hash = ((int)Mathf.Round(p.x) * 73856093) ^ ((int)Mathf.Round(p.z) * 19349663);
        UnityEngine.Random.InitState(seed ^ hash);
    }
}
