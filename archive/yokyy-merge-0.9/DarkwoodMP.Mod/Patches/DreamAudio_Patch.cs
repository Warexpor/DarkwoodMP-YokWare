using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Forward dream SFX with world position so peers/spectators hear them
/// (Horde DreamAudioPatches slim). Uses AudioController.Play(string, Vector3, …).
/// </summary>
public sealed class DreamAudio_Patch : IPatch
{
    private static float _lastSend;
    private static string _lastId = "";

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        // Patch Play overloads that include Vector3 world position
        foreach (var m in target.DeclaringType!.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Play") continue;
            var ps = m.GetParameters();
            if (ps.Length >= 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(Vector3))
            {
                try
                {
                    baseHarmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(DreamAudio_Patch).GetMethod(nameof(PlayAtPosPrefix), statics)!));
                }
                catch { }
            }
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("AudioController", "Play");
    }

    public static void PlayAtPosPrefix(string audioID, Vector3 worldPosition)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (string.IsNullOrEmpty(audioID)) return;
            if (!ShouldForward(worldPosition)) return;

            // Debounce identical spam
            if (audioID == _lastId && Time.unscaledTime - _lastSend < 0.05f) return;
            _lastId = audioID;
            _lastSend = Time.unscaledTime;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected) return;

            network.Send(new DreamAudioPacket
            {
                PlayerId = manager.LocalPlayerId,
                AudioId = audioID.Replace(":", "_"),
                X = worldPosition.x, Y = worldPosition.y, Z = worldPosition.z
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DreamAudio] {ex.Message}");
        }
    }

    private static bool ShouldForward(Vector3 worldPosition)
    {
        var manager = NetworkManager.Instance;
        if (manager == null || !manager.IsConnected) return false;
        try
        {
            if (Dreams.Instance == null || !Dreams.Instance.dreaming) return false;
        }
        catch { return false; }
        return true;
    }

    public static void ApplyRemote(string audioID, Vector3 pos)
    {
        if (string.IsNullOrEmpty(audioID)) return;
        try
        {
            RemoteApply.Active = true;
            try
            {
                AudioController.Play(audioID, pos, null);
            }
            finally
            {
                RemoteApply.Active = false;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DreamAudio] Apply: {ex.Message}");
        }
    }
}
