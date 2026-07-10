using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Replicates story flags between players. Verified game API:
/// Flags.setFlag(string flagName, bool activeModifier) and
/// Flags.setFlag(string flagName, int amount) - all story decisions (dream
/// outcomes, dialogue choices, world events) go through these. Karma follows
/// automatically: Controller.startBeforeDay derives karmaPoints from flags.
/// </summary>
public sealed class Flags_Patch : IPatch
{
    // Instance flag: a fresh Flags_Patch is created on every (re)connect, so the
    // overloads are patched again after RemovePatches. A static flag here used to
    // permanently kill flag sync after the first disconnect.
    private bool _patchedAll;

    // Per-player transient flags (position tracking etc.) must NOT be synced -
    // they describe where THIS player is, not shared story state
    private static readonly string[] _localOnlyPrefixes = { "player_in" };

    // Only broadcast actual value changes; the game re-sets many flags
    // repeatedly with the same value
    private static readonly Dictionary<string, int> _lastSent = new();

    // Convergent session store for the desync check (v0.6): every synced flag
    // change lands here - local sends AND applied remote flags. If flag sync
    // works, both machines' stores converge to the same content; SyncCheck
    // compares a hash of it. Also the source for the flag re-broadcast
    // corrective ("setFlag with the same value" is a no-op in the game).
    private static readonly Dictionary<string, int> _sessionFlags = new();
    private static readonly Dictionary<string, bool> _sessionFlagIsInt = new();

    public static void RecordForDigest(string flagName, bool isInt, int value)
    {
        if (IsLocalOnly(flagName)) return;
        lock (_sessionFlags)
        {
            _sessionFlags[flagName] = value;
            _sessionFlagIsInt[flagName] = isInt;
        }
    }

    /// <summary>Order-independent digest of all flags synced this session.</summary>
    public static void GetDigest(out int count, out uint hash)
    {
        lock (_sessionFlags)
        {
            count = _sessionFlags.Count;
            hash = 0;
            foreach (var kvp in _sessionFlags)
            {
                unchecked { hash += GameLogic.SyncCheck.Fnv1a(kvp.Key + "=" + kvp.Value); }
            }
        }
    }

    /// <summary>Snapshot of the session flags (for the corrective re-broadcast).</summary>
    public static List<(string name, bool isInt, int value)> GetSessionFlags()
    {
        var result = new List<(string, bool, int)>();
        lock (_sessionFlags)
        {
            foreach (var kvp in _sessionFlags)
                result.Add((kvp.Key, _sessionFlagIsInt.TryGetValue(kvp.Key, out var isInt) && isInt, kvp.Value));
        }
        return result;
    }

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        // Both setFlag overloads must be patched; the registry only hands us
        // the first match, so resolve and patch them explicitly here.
        if (!_patchedAll)
        {
            _patchedAll = true;
            _lastSent.Clear(); // stale values from a previous session must not suppress sends
            lock (_sessionFlags)
            {
                _sessionFlags.Clear();
                _sessionFlagIsInt.Clear();
            }
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var m in target.DeclaringType!.GetMethods(flags))
            {
                if (m.Name != "setFlag") continue;
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;

                string postfixName;
                if (ps[1].ParameterType == typeof(bool)) postfixName = nameof(BoolPostfix);
                else if (ps[1].ParameterType == typeof(int)) postfixName = nameof(IntPostfix);
                else continue;

                var postfix = new HarmonyMethod(typeof(Flags_Patch).GetMethod(postfixName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
                baseHarmony.Patch(m, postfix: postfix);
            }
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Flags", "setFlag");
    }

    public static void BoolPostfix(string flagName, bool activeModifier)
        => Send(flagName, false, activeModifier ? 1 : 0);

    public static void IntPostfix(string flagName, int amount)
        => Send(flagName, true, amount);

    private static void Send(string flagName, bool isInt, int value)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (IsLocalOnly(flagName)) return;

            if (_lastSent.TryGetValue(flagName, out var last) && last == value) return;
            _lastSent[flagName] = value;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            RecordForDigest(flagName, isInt, value);

            network.SendReliable(new FlagDeltaPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                IsInt = isInt,
                FlagName = flagName ?? "",
                Value = value
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Flags_Patch] {ex.Message}");
        }
    }

    public static bool IsLocalOnly(string flagName)
    {
        if (string.IsNullOrEmpty(flagName)) return true;
        foreach (var prefix in _localOnlyPrefixes)
        {
            if (flagName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
