using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Patches the local Player's health changes so other clients can update health bars.
/// Verified game API: Player.setHealth(float), Player.die(),
/// inherited public field CharBase.Health (float).
/// </summary>
public sealed class PlayerHealth_Patch : IPatch
{
    private static float _lastSentHealth = float.MinValue;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var postfixName = target.Name == "die" ? nameof(DiePostfix) : nameof(SetHealthPostfix);
        var postfix = new HarmonyMethod(typeof(PlayerHealth_Patch).GetMethod(postfixName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "setHealth");
        yield return ("Player", "die");
    }

    private static FieldInfo _healthField;

    public static void SetHealthPostfix(object __instance)
    {
        try
        {
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;
            if (!IsLocalPlayer(__instance)) return;

            // Cached: setHealth fires for every regen tick
            _healthField ??= __instance.GetType().GetField("Health",
                BindingFlags.Public | BindingFlags.Instance);
            if (_healthField?.GetValue(__instance) is not float health) return;

            // setHealth also fires for regen ticks - only send meaningful changes
            if (Mathf.Abs(health - _lastSentHealth) < 1f) return;
            _lastSentHealth = health;

            network.Send(new HealthUpdatePacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                TargetEntityId = "",
                Health = health,
                IsDead = health <= 0f
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerHealth_Patch] {ex.Message}");
        }
    }

    public static void DiePostfix(object __instance)
    {
        try
        {
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;
            if (!IsLocalPlayer(__instance)) return;

            _lastSentHealth = 0f;
            network.SendReliable(new HealthUpdatePacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                TargetEntityId = "",
                Health = 0f,
                IsDead = true
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerHealth_Patch] {ex.Message}");
        }
    }

    /// <summary>
    /// Remote-player clones carry a Player component too - their health changes
    /// must never be broadcast under OUR player id.
    /// </summary>
    private static bool IsLocalPlayer(object instance)
    {
        return instance is Component c && !c.transform.root.name.StartsWith("RemotePlayer_");
    }
}
