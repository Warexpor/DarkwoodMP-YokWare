using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Shared story NPC reputation (Horde model C live + join bulk).
/// Night-traders (name contains "night") stay personal — not broadcast.
/// </summary>
public sealed class Reputation_Patch : IPatch
{
    private static readonly Dictionary<string, int> _sessionRep = new Dictionary<string, int>();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(Reputation_Patch).GetMethod(nameof(SetRepPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("NPC", "set_reputation");
        yield return ("NPCState", "set_reputation");
        yield return ("Flags", "modifyReputation");
    }

    public static void SetRepPostfix(object __instance, int value)
    {
        try
        {
            if (RemoteApply.Active) return;
            string name = null;
            if (__instance != null)
            {
                var t = __instance.GetType();
                name = t.GetField("name")?.GetValue(__instance) as string
                    ?? (__instance as UnityEngine.Object)?.name;
            }
            if (string.IsNullOrEmpty(name)) return;
            if (IsPerPlayerRep(name)) return;

            _sessionRep[name] = value;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected) return;

            network.SendReliable(new ReputationPacket
            {
                PlayerId = manager.LocalPlayerId,
                NpcName = name.Replace(":", "_"),
                Value = value
            });
        }
        catch (Exception ex) { ModLogger.Error($"[Rep] {ex.Message}"); }
    }

    public static bool IsPerPlayerRep(string name) =>
        !string.IsNullOrEmpty(name) && name.IndexOf("night", StringComparison.OrdinalIgnoreCase) >= 0;

    public static void ApplyRemote(string name, int value)
    {
        if (string.IsNullOrEmpty(name) || IsPerPlayerRep(name)) return;
        _sessionRep[name] = value;
        try
        {
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            RemoteApply.Active = true;
            try
            {
                var st = flags.getNPCState(name);
                if (st != null) st.reputation = value;
                else
                {
                    // create path varies — best effort via modifyReputation if exists
                    try
                    {
                        typeof(Flags).GetMethod("modifyReputation")
                            ?.Invoke(flags, new object[] { name, value });
                    }
                    catch { }
                }
            }
            finally { RemoteApply.Active = false; }
        }
        catch (Exception ex) { ModLogger.Error($"[Rep] Apply: {ex.Message}"); }
    }

    public static void CollectSnapshot(List<Packet> into)
    {
        if (into == null || _sessionRep.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var kvp in _sessionRep)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(kvp.Key.Replace(";", "_").Replace(":", "_")).Append(':').Append(kvp.Value);
        }
        var pid = NetworkManager.Instance?.LocalPlayerId ?? 0;
        into.Add(new ReputationBulkPacket { PlayerId = pid, Payload = sb.ToString() });
    }

    public static void ApplyBulk(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return;
        foreach (var e in payload.Split(';'))
        {
            var sep = e.LastIndexOf(':');
            if (sep <= 0) continue;
            var n = e.Substring(0, sep);
            if (int.TryParse(e.Substring(sep + 1), out var v))
                ApplyRemote(n, v);
        }
    }

    public static void Reset() => _sessionRep.Clear();
}
