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
/// Sound-based AI alerting (ported from the BepInEx mod). Loud noises made by
/// the local player - gunshots and aim-scares - are broadcast so the PARTNER'S
/// machine alerts the enemies IT simulates.
///
/// Why it matters in the distributed model: when two players fight the same
/// pack, exactly one machine owns those enemies. The non-owning player's
/// gunshot is heard natively only on their own (mirrored) copies, which are
/// frozen - so without this the shared enemies never react to that player.
/// The receiver replays the noise via Character.alertInArea/scareInArea at the
/// shooter's clone position, waking the enemies it actually simulates.
///
/// Symmetric: no host/client role gating - each side alerts its own enemies to
/// the other side's noise. The sender's own enemies are already alerted
/// natively by the vanilla fireWeapon/aimScare call.
/// </summary>
public sealed class PlayerNoise_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var name = target.Name == "aimScare" ? nameof(ScarePostfix) : nameof(GunshotPostfix);
        baseHarmony.Patch(target, postfix: new HarmonyMethod(typeof(PlayerNoise_Patch).GetMethod(name, statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "fireWeapon");
        yield return ("Player", "aimScare");
    }

    public static void GunshotPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Player player) return;
            if (player.transform.root.name.StartsWith("RemotePlayer_")) return;
            if (player.invisible) return;
            if (InvItemClass.isNull(player.currentItem) || player.currentItem.baseClass == null) return;
            if (!player.currentItem.baseClass.isFirearm) return;

            Send("gun", player.currentItem.baseClass.attackSoundRange);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerNoise_Patch] gunshot: {ex.Message}");
        }
    }

    public static void ScarePostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Player player) return;
            if (player.transform.root.name.StartsWith("RemotePlayer_")) return;

            Send("scare", 350f);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerNoise_Patch] scare: {ex.Message}");
        }
    }

    private static void Send(string kind, float range)
    {
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        if (network == null || !network.IsConnected) return;

        network.SendReliable(new PlayerNoisePacket
        {
            PlayerId = Math.Max(network.LocalClientId, 0),
            Kind = kind ?? "",
            Range = range
        });
    }

    /// <summary>Receive side: alert the enemies this machine simulates around a partner's noise.</summary>
    public static void ApplyRemoteNoise(int playerId, string kind, float range)
    {
        try
        {
            var proxy = NetworkManager.Instance?.GetRemotePlayer(playerId);
            if (proxy == null) return;
            var pos = proxy.transform.position;

            if (kind == "scare")
                Character.scareInArea(pos, range);
            else
                Character.alertInArea(pos, range, true, 1f, true);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerNoise_Patch] apply '{kind}': {ex.Message}");
        }
    }
}
