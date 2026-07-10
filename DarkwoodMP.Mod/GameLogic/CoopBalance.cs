using System;
using System.Collections.Generic;
using DarkwoodMP.Network;

namespace DarkwoodMP.GameLogic;

public enum LootShareMode
{
    Off = 0,
    Double = 1,
    ScaleWithPlayers = 2
}

/// <summary>
/// Co-op balance helpers (Horde CoopBalance policy A):
/// progression sinks + barricade mats scaled by party size.
/// </summary>
public static class CoopBalance
{
    public static readonly HashSet<string> UpgradeItemTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "meat",
        "exp_mushroom",
        "exp_bio1_nightMushroom_01"
    };

    public static readonly HashSet<string> DefenseMatTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "wood",
        "nail"
    };

    public static int GetPartyMultiplier()
    {
        var mode = ModConfig.Load().GetLootShareMode();
        if (mode == LootShareMode.Off)
            return 1;

        var m = NetworkManager.Instance;
        if (m == null || !m.IsConnected)
            return 1;

        if (mode == LootShareMode.Double)
            return 2;

        // ScaleWithPlayers: 1 + remote count (host + N clients = 1+N)
        var remotes = 0;
        foreach (var id in m.ConnectedPlayers)
            if (id != m.LocalPlayerId) remotes++;
        return 1 + remotes;
    }

    public static bool IsScaledLootType(string type) =>
        !string.IsNullOrEmpty(type)
        && (UpgradeItemTypes.Contains(type) || DefenseMatTypes.Contains(type));

    /// <summary>Default allowlist for dream presence scale (ChomperBlack).</summary>
    public static readonly HashSet<string> DefaultNamedNpcAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ChomperBlack",
        "ChomperBlack(Clone)"
    };

    private static HashSet<string> _npcAllowlist;
    private static string _npcAllowlistRaw;

    public static void InvalidateAllowlistCache()
    {
        _npcAllowlist = null;
        _npcAllowlistRaw = null;
    }

    public static string NormalizeNpcName(string nameOrPrefab)
    {
        if (string.IsNullOrEmpty(nameOrPrefab)) return "";
        string s = nameOrPrefab.Trim();
        int slash = s.LastIndexOf('/');
        if (slash >= 0 && slash < s.Length - 1)
            s = s.Substring(slash + 1);
        int clone = s.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
        if (clone >= 0)
            s = s.Substring(0, clone).Trim();
        return s;
    }

    public static bool IsNamedNpcAllowlisted(string nameOrPrefab)
    {
        if (string.IsNullOrEmpty(nameOrPrefab)) return false;
        EnsureNpcAllowlist();
        return _npcAllowlist.Contains(NormalizeNpcName(nameOrPrefab));
    }

    private static void EnsureNpcAllowlist()
    {
        var raw = ModConfig.Load().NamedNpcAllowlist ?? "ChomperBlack";
        if (_npcAllowlist != null && raw == _npcAllowlistRaw) return;
        _npcAllowlistRaw = raw;
        _npcAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var n = NormalizeNpcName(part);
            if (n.Length > 0) _npcAllowlist.Add(n);
        }
        if (_npcAllowlist.Count == 0)
            foreach (var d in DefaultNamedNpcAllowlist)
                _npcAllowlist.Add(NormalizeNpcName(d));
    }
}
