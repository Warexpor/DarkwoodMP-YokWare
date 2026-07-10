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
/// Secondary objects from Explodes (fire clouds, debris) — Horde ExplosionSpawnSync.
/// Main explosion prefab is skipped (already local); remote throws land via ThrownArmed.
/// </summary>
public sealed class ExplosionSpawn_Patch : IPatch
{
    private static int _activationDepth;
    private static bool _insideSpawnObjects;
    private static Explodes _currentExplodes;
    private static bool _skipBecauseRemoteThrow;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        switch (target.Name)
        {
            case "onActivate":
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(ExplosionSpawn_Patch).GetMethod(nameof(OnActivatePrefix), statics)!),
                    postfix: new HarmonyMethod(typeof(ExplosionSpawn_Patch).GetMethod(nameof(OnActivatePostfix), statics)!));
                break;
            case "AddPrefab":
                var postfix = new HarmonyMethod(typeof(ExplosionSpawn_Patch).GetMethod(nameof(AddPrefabPostfix), statics)!);
                foreach (var m in target.DeclaringType!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "AddPrefab") continue;
                    var ps = m.GetParameters();
                    if (ps.Length >= 3 && ps[0].ParameterType == typeof(UnityEngine.Object))
                        baseHarmony.Patch(m, postfix: postfix);
                }
                break;
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Explodes", "onActivate");
        yield return ("Core", "AddPrefab");
    }

    public static void OnActivatePrefix(object __instance)
    {
        _activationDepth++;
        _currentExplodes = __instance as Explodes;
        _insideSpawnObjects = true;
        _skipBecauseRemoteThrow = false;

        try
        {
            if (__instance is not Explodes exp) return;
            var ti = exp.GetComponent<ThrownItem>();
            if (ti == null || ti.objectThatSpawnedMe == null) return;
            // Remote throw replay: objectThatSpawnedMe is the remote proxy.
            if (ti.objectThatSpawnedMe.root != null
                && ti.objectThatSpawnedMe.root.name.StartsWith("RemotePlayer_"))
                _skipBecauseRemoteThrow = true;
        }
        catch { }
    }

    public static void OnActivatePostfix()
    {
        _activationDepth--;
        if (_activationDepth > 0) return;
        _insideSpawnObjects = false;
        _skipBecauseRemoteThrow = false;
        _currentExplodes = null;
    }

    public static void AddPrefabPostfix(ref GameObject __result, object[] __args)
    {
        try
        {
            if (!_insideSpawnObjects || _skipBecauseRemoteThrow) return;
            if (RemoteApply.Active) return;
            if (__result == null || __args == null || __args.Length < 3) return;

            var prefab = __args[0] as UnityEngine.Object;
            if (prefab == null) return;
            if (__args[1] is not Vector3 position) return;
            if (__args[2] is not Quaternion quaternion) return;

            // Skip the main explosion visual (already present on both machines for local detonations).
            if (_currentExplodes != null && _currentExplodes.explosionPrefab != null
                && prefab == _currentExplodes.explosionPrefab)
                return;

            var name = prefab.name;
            if (string.IsNullOrEmpty(name)) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var euler = quaternion.eulerAngles;
            network.SendReliable(new ExplosionSpawnObjectPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                PrefabName = name,
                X = position.x, Y = position.y, Z = position.z,
                Rx = euler.x, Ry = euler.y, Rz = euler.z
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ExplosionSpawn] {ex.Message}");
        }
    }

    public static void ApplyRemote(string prefabName, Vector3 pos, Vector3 rotEuler)
    {
        if (string.IsNullOrEmpty(prefabName)) return;
        try
        {
            var rot = Quaternion.Euler(rotEuler.x, rotEuler.y, rotEuler.z);
            RemoteApply.Active = true;
            try
            {
                string[] prefixes = { "", "Items/", "FX/", "Environment/", "Particles/", "Dummies/", "Fire/", "Weapons/" };
                UnityEngine.Object prefab = null;
                foreach (var prefix in prefixes)
                {
                    prefab = Resources.Load("Prefabs/" + prefix + prefabName);
                    if (prefab != null) break;
                }
                if (prefab != null)
                    Core.AddPrefab(prefab, pos, rot, null, false);
                else
                    Core.AddPrefab(prefabName, pos, rot, null, false);
            }
            finally { RemoteApply.Active = false; }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ExplosionSpawn] ApplyRemote: {ex.Message}");
        }
    }
}
