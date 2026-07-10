using System;
using System.IO;
using DarkwoodMP.GameLogic;

namespace DarkwoodMP.Network;

/// <summary>
/// Configuration for DarkwoodMP mod - keeps backward compatibility with INI config
/// </summary>
public class ModConfig
{
    private static ModConfig? _cached;

    public string ConfigPath => _configPath;
    private readonly string _configPath;

    public string ServerIp { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 7777;
    public string SessionPassword { get; set; } = string.Empty;
    public string PlayerName { get; set; } = "Player";

    /// <summary>
    /// Shared world generation seed. When non-zero, world generation is made
    /// deterministic so that two players using the SAME seed who each start a
    /// NEW GAME get identical worlds (same buildings, objects, spawns).
    /// 0 = disabled (vanilla random generation).
    /// </summary>
    /// <summary>Default non-zero seed for first-run comfort (override in config.ini).</summary>
    public int WorldSeed { get; set; } = 12345;

    /// <summary>Key that toggles the multiplayer menu (UnityEngine.KeyCode name).</summary>
    public string MenuKey { get; set; } = "F1";

    /// <summary>
    /// High-frequency network/entity logs for playtest triage (default off).
    /// Lines use [YokWare][VERBOSE][…]. Also sets NetworkLayer.VerboseLogging.
    /// </summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>
    /// Co-op loot share: Off / Double / ScaleWithPlayers (default ScaleWithPlayers).
    /// Scales meat/mushrooms/wood/nail on personal pickup only.
    /// </summary>
    public string LootShareModeSetting { get; set; } = "ScaleWithPlayers";

    /// <summary>Scale allowlisted dream NPC presence (default ChomperBlack) by party size.</summary>
    public bool NamedNpcScaleEnabled { get; set; } = true;

    /// <summary>Comma-separated short names for dream NPC scale allowlist.</summary>
    public string NamedNpcAllowlist { get; set; } = "ChomperBlack";

    public GameLogic.LootShareMode GetLootShareMode()
    {
        switch ((LootShareModeSetting ?? "ScaleWithPlayers").Trim().ToLowerInvariant())
        {
            case "off": case "0": case "false": case "none":
                return GameLogic.LootShareMode.Off;
            case "double": case "2":
                return GameLogic.LootShareMode.Double;
            default:
                return GameLogic.LootShareMode.ScaleWithPlayers;
        }
    }

    private ModConfig()
    {
        var baseDir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "DarkwoodMP");
        _configPath = Path.Combine(baseDir, "config.ini");

        if (!File.Exists(_configPath))
        {
            var fallback = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".config", "DarkwoodMP", "config.ini"
            );
            if (File.Exists(fallback))
                _configPath = fallback;
        }
    }

    public static ModConfig Load()
    {
        if (_cached != null) return _cached;

        var config = new ModConfig();
        try
        {
            if (!File.Exists(config._configPath))
            {
                config.Save();
                _cached = config;
                return _cached;
            }

            var lines = File.ReadAllLines(config._configPath);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(";") || trimmed.Length == 0) continue;

                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx < 0) continue;

                var key = trimmed.Substring(0, eqIdx).Trim().ToLower().Replace(" ", "_");
                var value = trimmed.Substring(eqIdx + 1).Trim().Trim('"');

                switch (key)
                {
                    case "serverip": case "server_ip":
                        config.ServerIp = value;
                        break;
                    case "serverport": case "server_port":
                        if (int.TryParse(value, out var port)) config.ServerPort = port;
                        break;
                    case "sessionpassword": case "session_password":
                        config.SessionPassword = value;
                        break;
                    case "playername": case "player_name":
                        config.PlayerName = value;
                        break;
                    case "worldseed": case "world_seed":
                        if (int.TryParse(value, out var seed)) config.WorldSeed = seed;
                        break;
                    case "menukey": case "menu_key":
                        if (!string.IsNullOrEmpty(value)) config.MenuKey = value;
                        break;
                    case "verboselogging": case "verbose_logging": case "verbose":
                        config.VerboseLogging = value is "1" or "true" or "yes" or "on";
                        break;
                    case "lootsharemode": case "loot_share_mode":
                        if (!string.IsNullOrEmpty(value)) config.LootShareModeSetting = value;
                        break;
                    case "namednpcscaleenabled": case "named_npc_scale_enabled":
                        config.NamedNpcScaleEnabled = value is "1" or "true" or "yes" or "on";
                        break;
                    case "namednpcallowlist": case "named_npc_allowlist":
                        if (!string.IsNullOrEmpty(value)) config.NamedNpcAllowlist = value;
                        break;
                }
            }
            ModLogger.Msg("Config", $"Loaded ← {config._configPath}");
        }
        catch (System.Exception ex)
        {
            ModLogger.Error("Config", $"Failed to load: {ex.Message}");
        }
        _cached = config;
        return _cached;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var ini = $@"; YokWare Branch Configuration
; Generated: {System.DateTime.Now:u}

[Network]
ServerIp = ""{ServerIp}""
ServerPort = {ServerPort}
SessionPassword = ""{SessionPassword}""

[Player]
PlayerName = ""{PlayerName}""

[World]
; Same non-zero seed on all peers + NEW game each
WorldSeed = {WorldSeed}

[Gameplay]
LootShareMode = ""{LootShareModeSetting}""
NamedNpcScaleEnabled = {(NamedNpcScaleEnabled ? "true" : "false")}
NamedNpcAllowlist = ""{NamedNpcAllowlist}""

[UI]
MenuKey = ""{MenuKey}""

[Debug]
; true = high-frequency net/entity lines [YokWare][VERBOSE][…]
VerboseLogging = {(VerboseLogging ? "true" : "false")}
";
        File.WriteAllText(_configPath, ini);
        ModLogger.Msg("Config", $"Saved → {_configPath}");
    }
}
