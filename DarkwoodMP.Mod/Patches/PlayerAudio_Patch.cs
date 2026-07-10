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
/// Forwards the LOCAL player's sound effects (item use, actions, etc.) so the
/// partner hears them from the remote player's position. Ported from the BepInEx
/// mod's PlayerSoundSyncPatches. Footsteps/status sounds are skipped (footsteps
/// play from the clone's own leg animation; status sounds are personal).
/// Receive side: NetworkManager "paudio:" dispatch replays at the clone.
/// </summary>
public sealed class PlayerAudio_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var prefix = new HarmonyMethod(typeof(PlayerAudio_Patch).GetMethod(nameof(Prefix), statics)!);
        // Patch every Play overload that takes (string audioID, Transform parentObj, ...).
        foreach (var m in target.DeclaringType!.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Play") continue;
            var ps = m.GetParameters();
            if (ps.Length >= 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(Transform))
            {
                try { baseHarmony.Patch(m, prefix: prefix); }
                catch (Exception ex) { ModLogger.Warning($"[PlayerAudio_Patch] skip overload: {ex.Message}"); }
            }
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("AudioController", "Play");
    }

    private static readonly HashSet<string> _loggedIds = new();

    public static void Prefix(string audioID, Transform parentObj)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (parentObj == null || string.IsNullOrEmpty(audioID)) return;
            var p = Player.Instance;
            if (p == null || parentObj != p.transform) return; // only the local player's own sounds

            // Status sounds are personal to the local player - don't forward.
            // (Footsteps/walk sounds ARE forwarded: the clone's own animation
            // footsteps were too quiet, so partners couldn't hear each other walk.)
            if (audioID.IndexOf("player_low_health", StringComparison.OrdinalIgnoreCase) >= 0) return;
            if (audioID.IndexOf("player_tired", StringComparison.OrdinalIgnoreCase) >= 0) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            if (_loggedIds.Add(audioID))
                ModLogger.Msg($"[PlayerAudio_Patch] forwarding player sound '{audioID}'");

            network.Send(new PlayerAudioPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                AudioId = audioID
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerAudio_Patch] {ex.Message}");
        }
    }
}
