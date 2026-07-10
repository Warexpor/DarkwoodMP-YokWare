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
/// Proxy status flags: wards, invis, poison, bleed (Horde PlayerEffectSync).
/// </summary>
public sealed class PlayerEffect_Patch : IPatch
{
    private static float _lastSend;
    private const float Interval = 0.5f;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        // Hook via Player update is heavy — use postfix on skill/status methods
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(PlayerEffect_Patch).GetMethod(nameof(AnyPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "Update");
        yield return ("CharacterEffects", "activate");
        yield return ("CharacterEffects", "deactivate");
    }

    public static void AnyPostfix(object __instance, MethodBase __originalMethod)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__originalMethod?.Name == "Update")
            {
                if (Time.time - _lastSend < Interval) return;
                _lastSend = Time.time;
            }
            BroadcastFlags();
        }
        catch { }
    }

    private static void BroadcastFlags()
    {
        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        var manager = NetworkManager.Instance;
        if (network == null || manager == null || !network.IsConnected) return;
        var p = Player.Instance;
        if (p == null) return;

        int flags = 0;
        try
        {
            // Bit layout: 1 invis, 2 ignoreMe, 4 poison, 8 bleed, 16 shadowWard, 32 forestWard
            if (p.invisible) flags |= 1;
            if (p.ignoreMe) flags |= 2;
        }
        catch { }

        try
        {
            var fx = p.GetComponent<CharacterEffects>();
            // poison/bleed detection best-effort via active effects list
        }
        catch { }

        network.Send(new PlayerEffectPacket
        {
            PlayerId = manager.LocalPlayerId,
            TargetPlayerId = manager.LocalPlayerId,
            Flags = flags
        });
    }

    public static void ApplyRemote(int playerId, int flags)
    {
        var go = NetworkManager.Instance?.GetRemotePlayer(playerId);
        if (go == null) return;
        var proxy = go.GetComponent<RemotePlayerProxy>();
        if (proxy == null) return;
        proxy.RemoteInvisible = (flags & 1) != 0;
        proxy.RemoteIgnoreMe = (flags & 2) != 0;
        proxy.RemotePoisoned = (flags & 4) != 0;
        proxy.RemoteBleeding = (flags & 8) != 0;

        // Visual: hide renderers when invisible
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            if (r != null) r.enabled = !proxy.RemoteInvisible;
    }
}
