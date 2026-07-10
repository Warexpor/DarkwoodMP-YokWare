using BepInEx.Configuration;
using DWMPHorde.Logging;

namespace DWMPHorde.Config
{
    public enum LootShareMode
    {
        Off = 0,
        Double = 1,
        ScaleWithPlayers = 2
    }

    public static class ModConfig
    {
        public static ConfigEntry<string> ConnectAddress { get; private set; }
        public static ConfigEntry<int> ConnectPort { get; private set; }
        public static ConfigEntry<string> HostPassword { get; private set; }
        /// <summary>Display name in chat (Yokyy product port).</summary>
        public static ConfigEntry<string> PlayerName { get; private set; }
        public static ConfigEntry<bool> FriendlyFireEnabled { get; private set; }
        public static ConfigEntry<bool> DoubleItemsEnabled { get; private set; }
        public static ConfigEntry<string> LootShareModeSetting { get; private set; }
        /// <summary>Host: scale allowlisted dream NPC presence by party multiplier.</summary>
        public static ConfigEntry<bool> NamedNpcScaleEnabled { get; private set; }
        /// <summary>Comma-separated short names (e.g. ChomperBlack). Dream presence only.</summary>
        public static ConfigEntry<string> NamedNpcAllowlist { get; private set; }
        public static ConfigEntry<bool> VerboseLogging { get; private set; }
        /// <summary>Transition-only light sync logs (flare/flash on/off, join bulk). Not 30 Hz spam.</summary>
        public static ConfigEntry<bool> VerboseLightSync { get; private set; }
        public static ConfigEntry<int> MaxPlayers { get; private set; }
        public static ConfigEntry<bool> AllowJoinDuringDream { get; private set; }
        public static ConfigEntry<int> MaxPeerDamage { get; private set; }

        /// <summary>
        /// LiteNetLib connection key shared by host accept and client connect.
        /// Empty password → open LAN (key = plugin name only). Non-empty → name:password.
        /// </summary>
        public static string GetConnectionKey()
        {
            string pw = HostPassword?.Value?.Trim() ?? "";
            if (string.IsNullOrEmpty(pw))
                return PluginInfo.Name;
            return PluginInfo.Name + ":" + pw;
        }

        // --- Logging (public testing) ---
        public static ConfigEntry<string> LogPresetSetting { get; private set; }
        public static ConfigEntry<string> LogMinLevelSetting { get; private set; }
        public static ConfigEntry<string> LogExtraCategories { get; private set; }
        public static ConfigEntry<string> LogTraceCategories { get; private set; }
        public static ConfigEntry<float> LogRateLimitSeconds { get; private set; }
        public static ConfigEntry<bool> LogRedactPaths { get; private set; }
        public static ConfigEntry<bool> LogRedactIPs { get; private set; }
        public static ConfigEntry<bool> LogIncludeStacks { get; private set; }

        public static LootShareMode GetLootShareMode()
        {
            if (DoubleItemsEnabled != null && !DoubleItemsEnabled.Value)
                return LootShareMode.Off;
            if (LootShareModeSetting == null)
                return LootShareMode.ScaleWithPlayers;
            switch ((LootShareModeSetting.Value ?? "ScaleWithPlayers").Trim().ToLowerInvariant())
            {
                case "off":
                case "0":
                    return LootShareMode.Off;
                case "double":
                case "1":
                    return LootShareMode.Double;
                default:
                    return LootShareMode.ScaleWithPlayers;
            }
        }

        public static LogPreset GetLogPreset()
        {
            string raw = (LogPresetSetting?.Value ?? "Trace").Trim();
            if (System.Enum.TryParse(raw, true, out LogPreset p))
                return p;
            return LogPreset.Trace;
        }

        public static LogLevel GetLogMinLevel()
        {
            string raw = (LogMinLevelSetting?.Value ?? "Trace").Trim();
            if (System.Enum.TryParse(raw, true, out LogLevel l))
                return l;
            return LogLevel.Trace;
        }

        public static void Bind(ConfigFile config)
        {
            ConnectAddress = config.Bind("Network", "ConnectAddress", "127.0.0.1", "Default IP address shown in the connect field.");
            ConnectPort = config.Bind("Network", "ConnectPort", PluginInfo.DefaultPort, "Default UDP port for LAN connections.");
            HostPassword = config.Bind("Network", "HostPassword", "",
                "Optional join password. Empty = open LAN (trusted subnet). Host and every client must match.");
            PlayerName = config.Bind("Network", "PlayerName", "Player",
                "Name shown in co-op chat (Ctrl+C).");
            MaxPlayers = config.Bind("Network", "MaxPlayers", 8, "Maximum players including host.");
            AllowJoinDuringDream = config.Bind("Network", "AllowJoinDuringDream", false, "If false, reject joins during dream session.");
            FriendlyFireEnabled = config.Bind("Gameplay", "FriendlyFireEnabled", true, "Players can damage each other.");
            DoubleItemsEnabled = config.Bind("Gameplay", "DoubleItemsEnabled", true,
                "Master switch for loot sharing (hideout fuels + barricade wood/nail).");
            LootShareModeSetting = config.Bind("Gameplay", "LootShareMode", "ScaleWithPlayers",
                "Off | Double | ScaleWithPlayers (1+remote peers). Scales exp/meat/mushrooms and wood/nail pickups.");
            NamedNpcScaleEnabled = config.Bind("Gameplay", "NamedNpcScaleEnabled", true,
                "Host: multiply allowlisted dream NPC presence by LootShareMode party multiplier (default ChomperBlack).");
            NamedNpcAllowlist = config.Bind("Gameplay", "NamedNpcAllowlist", "ChomperBlack",
                "Comma-separated character short names scaled in dreams only (not night hideout trash).");
            MaxPeerDamage = config.Bind("Gameplay", "MaxPeerDamage", 200,
                "Host clamps peer-reported attack/FF damage to this max (anti-grief). No per-message rate limit — multi-hit weapons need every pellet to apply.");
            // Entity spawner moved to standalone plugin YokWare.EntitySpawner (F5).

            // Support = join/session/combat Events without Physics/entity frame spam (Trace/Dev can fill 10MB+).
            LogPresetSetting = config.Bind("Logging", "LogPreset", "Support",
                "Public=quiet. Support=session/join/combat Events (default playtest). Trace=all cats + high-freq. Dev=LegacyInfo dumps (huge logs). Restart after change.");
            LogMinLevelSetting = config.Bind("Logging", "LogMinLevel", "Event",
                "Error | Warn | Event | Info | Trace. Levels above this are dropped.");
            LogExtraCategories = config.Bind("Logging", "LogExtraCategories", "",
                "Optional Event categories on top of preset (comma): Core,Network,Session,Combat,Entity,Physics,Container,World,AI,Dream,Death,Audio,UI,Save.");
            LogTraceCategories = config.Bind("Logging", "LogTraceCategories", "none",
                "Categories allowed for Trace when not full Trace preset, or 'none'.");
            LogRateLimitSeconds = config.Bind("Logging", "LogRateLimitSeconds", 1f,
                "Minimum seconds between identical TraceRate keys (spam guard).");
            // Local dual-box default: full lines in BepInEx/LogOutput.log (set true for public pastebins).
            LogRedactPaths = config.Bind("Logging", "LogRedactPaths", false,
                "Strip absolute paths to filenames in log lines (safer pastebins).");
            LogRedactIPs = config.Bind("Logging", "LogRedactIPs", false,
                "Mask IPv4 in logs (Public always; leave true when sharing Support logs).");
            LogIncludeStacks = config.Bind("Logging", "LogIncludeStacks", true,
                "Include full exception stacks on Error (also on for Dev/Trace).");

            VerboseLogging = config.Bind("Debug", "VerboseLogging", false,
                "If true and LogPreset=Public, forces Trace. Prefer LogPreset=Support for join tests (not Trace/Dev).");
            VerboseLightSync = config.Bind("Debug", "VerboseLightSync", false,
                "Transition light sync logs. Leave false unless debugging lights.");
        }

        /// <summary>True when VerboseLightSync is enabled (safe if unbound).</summary>
        public static bool IsVerboseLightSync =>
            VerboseLightSync != null && VerboseLightSync.Value;
    }
}
