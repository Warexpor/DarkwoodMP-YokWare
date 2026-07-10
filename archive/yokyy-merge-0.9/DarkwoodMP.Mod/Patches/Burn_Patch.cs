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
/// Entity + player burn start/stop (Horde FireSync). Mirrors get visual Burn;
/// DoT damage stays on the authority / local player machine only.
/// </summary>
public sealed class Burn_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        switch (target.Name)
        {
            case "initialize":
                baseHarmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(Burn_Patch).GetMethod(nameof(EffectInitPostfix), statics)!));
                break;
            case "stop":
                baseHarmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(Burn_Patch).GetMethod(nameof(BurnStopPostfix), statics)!));
                break;
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("CharacterEffect", "initialize");
        yield return ("Burn", "stop");
    }

    public static void EffectInitPostfix(object __instance, object[] __args)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__args == null || __args.Length < 1 || __args[0] is not InvItemEffect effect) return;
            if (effect.type != CharacterEffectType.burn && effect.type != CharacterEffectType.burnSpecial)
                return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;
            // CharacterEffect is not Character — resolve entity root for mirror/player checks.
            if (__instance is not Component comp) return;
            var character = comp as Character
                ?? comp.GetComponent<Character>()
                ?? comp.GetComponentInParent<Character>();
            if (character == null) return;

            // Mirror entities: authority owns DoT — stop local burn damage coroutine.
            var enemySync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<EnemySync>();
            if (enemySync != null && enemySync.IsClientMirroring(character))
            {
                var burn = character.GetComponent<Burn>() ?? character.GetComponentInChildren<Burn>();
                if (burn != null) burn.StopAllCoroutines();
                return;
            }

            var player = character.GetComponent<Player>();
            if (player != null)
            {
                if (player.transform.root.name.StartsWith("RemotePlayer_")) return;
                network.SendReliable(new PlayerBurningPacket
                {
                    PlayerId = Math.Max(network.LocalClientId, 0),
                    IsBurning = true,
                    BurnTime = effect.duration
                });
                return;
            }

            if (character.transform.root.name.StartsWith("RemotePlayer_")) return;

            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsTimeAuthority) return;

            var id = CharacterTracker.GetStableId(character);
            if (id == 0) return;

            network.SendReliable(new EntityBurningPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                EntityId = id,
                IsBurning = true,
                BurnTime = effect.duration,
                Modifier = effect.modifier,
                Interval = effect.interval
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Burn_Patch] EffectInit: {ex.Message}");
        }
    }

    public static void BurnStopPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Component burnComp) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var character = burnComp as Character
                ?? burnComp.GetComponent<Character>()
                ?? burnComp.GetComponentInParent<Character>();
            if (character == null) return;

            var player = character.GetComponent<Player>();
            if (player != null)
            {
                if (player.transform.root.name.StartsWith("RemotePlayer_")) return;
                network.SendReliable(new PlayerBurningPacket
                {
                    PlayerId = Math.Max(network.LocalClientId, 0),
                    IsBurning = false,
                    BurnTime = 0f
                });
                return;
            }

            if (character.transform.root.name.StartsWith("RemotePlayer_")) return;

            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsTimeAuthority) return;
            if (!CharacterTracker.TryGetStableId(character, out var id) || id == 0) return;

            network.SendReliable(new EntityBurningPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                EntityId = id,
                IsBurning = false
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Burn_Patch] BurnStop: {ex.Message}");
        }
    }

    public static void ApplyRemoteEntityBurn(short entityId, bool isBurning, float burnTime)
    {
        try
        {
            var entity = CharacterTracker.FindByStableId(entityId);
            if (entity == null) return;
            RemoteApply.Active = true;
            try
            {
                if (isBurning)
                {
                    var burn = entity.GetComponent<Burn>();
                    if (burn == null)
                    {
                        burn = entity.gameObject.AddComponent<Burn>();
                        burn.burnTime = burnTime;
                    }
                    // Mirror: visual only — authority already ticks DoT.
                    try { burn.StopAllCoroutines(); } catch { }
                }
                else
                {
                    var burn = entity.GetComponent<Burn>();
                    if (burn != null) burn.stop();
                }
            }
            finally { RemoteApply.Active = false; }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Burn_Patch] ApplyEntity: {ex.Message}");
        }
    }

    public static void ApplyRemotePlayerBurn(int playerId, bool isBurning, float burnTime)
    {
        try
        {
            var proxy = NetworkManager.Instance?.GetRemotePlayer(playerId);
            if (proxy == null) return;
            RemoteApply.Active = true;
            try
            {
                if (isBurning)
                {
                    var burn = proxy.GetComponent<Burn>();
                    if (burn == null)
                    {
                        burn = proxy.AddComponent<Burn>();
                        burn.burnTime = burnTime;
                    }
                }
                else
                {
                    var burn = proxy.GetComponent<Burn>();
                    if (burn != null) burn.stop();
                }
            }
            finally { RemoteApply.Active = false; }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Burn_Patch] ApplyPlayer: {ex.Message}");
        }
    }
}
