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
/// Vault/window jump sync (Horde VaultState): disable proxy collisions while vaulting.
/// </summary>
public sealed class Vault_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var isStart = target.Name.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0
            || target.Name.IndexOf("begin", StringComparison.OrdinalIgnoreCase) >= 0
            || target.Name == "vault";
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(Vault_Patch).GetMethod(
                isStart ? nameof(StartPostfix) : nameof(EndPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "startVault");
        yield return ("Player", "vault");
        yield return ("Player", "endVault");
        yield return ("Player", "stopVault");
        yield return ("Jumpable", "jump");
        yield return ("Jumpable", "startJump");
    }

    public static void StartPostfix()
    {
        SendVault(true);
        SetLocalVaultColliders(false);
    }

    public static void EndPostfix()
    {
        SendVault(false);
        SetLocalVaultColliders(true);
    }

    private static void SendVault(bool vaulting)
    {
        try
        {
            if (RemoteApply.Active) return;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected) return;
            network.SendReliable(new VaultStatePacket
            {
                PlayerId = manager.LocalPlayerId,
                Vaulting = vaulting
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Vault] {ex.Message}");
        }
    }

    public static void ApplyRemote(int playerId, bool vaulting)
    {
        var go = NetworkManager.Instance?.GetRemotePlayer(playerId);
        if (go == null) return;
        var proxy = go.GetComponent<RemotePlayerProxy>() ?? go.AddComponent<RemotePlayerProxy>();
        proxy.PlayerId = playerId;
        foreach (var col in go.GetComponentsInChildren<Collider>(true))
        {
            if (col != null) col.enabled = !vaulting;
        }
    }

    private static void SetLocalVaultColliders(bool enabled)
    {
        try
        {
            var manager = NetworkManager.Instance;
            if (manager == null) return;
            foreach (var kvp in manager.RemotePlayers)
            {
                if (kvp.Value == null) continue;
                foreach (var col in kvp.Value.GetComponentsInChildren<Collider>(true))
                    if (col != null && !enabled) col.enabled = false;
                    else if (col != null && enabled)
                    {
                        var p = kvp.Value.GetComponent<RemotePlayerProxy>();
                        if (p == null || !p.IsDead) col.enabled = true;
                    }
            }
        }
        catch { }
    }
}
