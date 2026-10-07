using DWMPHorde.Logging;

namespace DWMPHorde.Config
{
    public enum LootShareMode
    {
        Off = 0,
        ScaleWithPlayers = 1
    }

    public static class ModConfig
    {
        public static ModSetting<string> ConnectAddress { get; private set; }
        public static ModSetting<int> ConnectPort { get; private set; }
        public static ModSetting<string> HostPassword { get; private set; }
        /// <summary>Last Steam lobby id (ulong) for Steam join field / host display.</summary>
        public static ModSetting<string> SteamLobbyId { get; private set; }
        /// <summary>Steam lobby visibility: friends | public | private.</summary>
        public static ModSetting<string> SteamLobbyType { get; private set; }
        /// <summary>
        /// Override Unity LocalLow save root. Empty = default, except SecondDarkwood install
        /// auto-uses sibling folder Darkwood_Second (dual-box isolation).
        /// </summary>
        public static ModSetting<string> SaveRootOverride { get; private set; }
        /// <summary>Last profile slot used for a permanent co-op world copy (1-5). 0 means none.</summary>
        public static ModSetting<int> PreferredCoopCopySlot { get; private set; }
        /// <summary>Display name in chat (Yokyy product port).</summary>
        public static ModSetting<string> PlayerName { get; private set; }
        /// <summary>Master switch for the Ctrl+C co-op chat HUD (see <see cref="ChatHud"/>).</summary>
        public static ModSetting<bool> ChatEnabled { get; private set; }
        public static ModSetting<bool> FriendlyFireEnabled { get; private set; }
        public static ModSetting<bool> DoubleItemsEnabled { get; private set; }
        public static ModSetting<string> LootShareModeSetting { get; private set; }
        /// <summary>Host: scale allowlisted dream NPC presence by party multiplier.</summary>
        public static ModSetting<bool> NamedNpcScaleEnabled { get; private set; }
        /// <summary>Comma-separated short names (e.g. ChomperBlack). Dream presence only.</summary>
        public static ModSetting<string> NamedNpcAllowlist { get; private set; }
        public static ModSetting<bool> VerboseLogging { get; private set; }
        /// <summary>Transition-only light sync logs (flare/flash on/off, join bulk). Not 30 Hz spam.</summary>
        public static ModSetting<bool> VerboseLightSync { get; private set; }
        /// <summary>
        /// Deep entity path Trace/Event: host snapshot, client interp/anim/clip, hit reaction,
        /// damage/FF, spawn/despawn. Rate-limited on hot paths. Prefer this over full LogPreset=Trace.
        /// </summary>
        public static ModSetting<bool> VerboseEntitySync { get; private set; }
        /// <summary>
        /// Force free OS pointer (no Confined/ClipCursor). Off by default (vanilla confine);
        /// Linux dual-box testers (Wayland + Wine) set it true in their cfg.
        /// </summary>
        public static ModSetting<bool> FreeCursorForDualBox { get; private set; }
        /// <summary>Co-op frame-cost probe ([Perf] lines) outside Dev/Trace presets. Off by default.</summary>
        public static ModSetting<bool> PerfProbe { get; private set; }
        /// <summary>Host↔client world fingerprint compare; differences logged as DESYNC lines.</summary>
        public static ModSetting<bool> DesyncCheck { get; private set; }
        public static ModSetting<int> DesyncCheckIntervalSec { get; private set; }
        public static ModSetting<int> MaxPlayers { get; private set; }
        public static ModSetting<bool> AllowJoinDuringDream { get; private set; }
        public static ModSetting<int> MaxPeerDamage { get; private set; }
        public static ModSetting<float> PeerMovementVolume { get; private set; }
        /// <summary>
        /// On host crash/timeout, survivors elect lowest player id as new host (LAN + Steam).
        /// Requires PeerRoster gossip; keep true for dual-box / friends crash recovery.
        /// </summary>
        public static ModSetting<bool> HostMigrationEnabled { get; private set; }
        public static ModSetting<bool> VoiceEnabled { get; private set; }
        public static ModSetting<string> VoiceMode { get; private set; }
        public static ModSetting<string> VoicePttKey { get; private set; }
        public static ModSetting<float> VoiceVolume { get; private set; }
        public static ModSetting<float> VoiceGain { get; private set; }
        public static ModSetting<float> VoiceFullVolumeDistance { get; private set; }
        public static ModSetting<float> VoiceMaxDistance { get; private set; }
        public static ModSetting<string> WalkieItemName { get; private set; }

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
        public static ModSetting<string> LogPresetSetting { get; private set; }
        public static ModSetting<string> LogMinLevelSetting { get; private set; }
        public static ModSetting<string> LogExtraCategories { get; private set; }
        public static ModSetting<string> LogTraceCategories { get; private set; }
        public static ModSetting<float> LogRateLimitSeconds { get; private set; }
        public static ModSetting<bool> LogRedactPaths { get; private set; }
        public static ModSetting<bool> LogRedactIPs { get; private set; }
        public static ModSetting<bool> LogIncludeStacks { get; private set; }

        public const int MinPort = 1;
        public const int MaxPort = 65535;

        private static int _lastWarnedPort = int.MinValue;

        /// <summary>
        /// Configured default port, clamped to a bindable UDP range. A hand-edited cfg with
        /// 0 / 99999 would otherwise reach LiteNetLib as-is; warn once per bad value.
        /// </summary>
        public static int GetConnectPort()
        {
            int raw = ConnectPort != null ? ConnectPort.Value : PluginInfo.DefaultPort;
            if (raw >= MinPort && raw <= MaxPort)
                return raw;

            int clamped = raw < MinPort ? MinPort : MaxPort;
            if (raw != _lastWarnedPort)
            {
                _lastWarnedPort = raw;
                ModLog.Warn(LogCat.Core,
                    "ConnectPort " + raw + " is outside " + MinPort + "-" + MaxPort + " — using " + clamped
                    + ". Fix [Network] ConnectPort in the config file.");
            }
            return clamped;
        }

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
                case "none":
                    return LootShareMode.Off;
                // "double" was removed; old configs fall through to ScaleWithPlayers.
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

        public static void Bind(ModConfigStore config)
        {
            ConnectAddress = config.Bind("Network", "ConnectAddress", "127.0.0.1", "Default IP address shown in the connect field.");
            ConnectPort = config.Bind("Network", "ConnectPort", PluginInfo.DefaultPort,
                "Default UDP port for LAN connections (1-65535; out-of-range values are clamped with a warning).");
            HostPassword = config.Bind("Network", "HostPassword", "",
                "Optional join password. Empty = open LAN (trusted subnet). Host and every client must match. Also used as Steam lobby conn key.");
            SteamLobbyId = config.Bind("Network", "SteamLobbyId", "",
                "Steam lobby id (ulong) for Steam join. Host auto-fills when creating a Steam lobby. Friends can also use the Steam invite overlay.");
            SteamLobbyType = config.Bind("Network", "SteamLobbyType", "friends",
                "Steam host lobby visibility: friends | public | private.");
            SaveRootOverride = config.Bind("Saves", "SaveRootOverride", "",
                "Optional absolute path for save data (1_4Save/profs). Empty = Unity default. "
                + "SecondDarkwood install auto-isolates to LocalLow/.../Darkwood_Second when empty. "
                + "Set manually if dual-box still shares a tree.");
            PreferredCoopCopySlot = config.Bind("Saves", "PreferredCoopCopySlot", 0,
                "Last local profile slot (1-5) used for a permanent co-op world copy. 0 = none. "
                + "Join picker highlights this; empty slots are still preferred when free.");
            PlayerName = config.Bind("Network", "PlayerName", "Player",
                "Name shown in co-op chat (Ctrl+C) and speech bubbles.");
            ChatEnabled = config.Bind("Network", "ChatEnabled", true,
                "Co-op text chat: Ctrl+C opens the input, Enter sends, Esc closes. "
                + "Gameplay input is locked while typing. Restart after change.");
            MaxPlayers = config.Bind("Network", "MaxPlayers", 8, "Maximum players including host.");
            AllowJoinDuringDream = config.Bind("Network", "AllowJoinDuringDream", false, "If false, reject joins during dream session.");
            FriendlyFireEnabled = config.Bind("Gameplay", "FriendlyFireEnabled", true, "Players can damage each other.");
            DoubleItemsEnabled = config.Bind("Gameplay", "DoubleItemsEnabled", true,
                "Master switch for loot sharing (hideout furnace fuels / isExpItem).");
            LootShareModeSetting = config.Bind("Gameplay", "LootShareMode", "ScaleWithPlayers",
                "Off | ScaleWithPlayers (1+remote peers). Scales hideout fuels (mushrooms, odd meat, eggs, etc.). Not wood/nail or regular dog meat.");
            NamedNpcScaleEnabled = config.Bind("Gameplay", "NamedNpcScaleEnabled", true,
                "Host: multiply allowlisted dream NPC presence by LootShareMode party multiplier (default ChomperBlack).");
            NamedNpcAllowlist = config.Bind("Gameplay", "NamedNpcAllowlist", "ChomperBlack",
                "Comma-separated character short names scaled in dreams only (not night hideout trash).");
            MaxPeerDamage = config.Bind("Gameplay", "MaxPeerDamage", 200,
                "Host clamps peer-reported attack/FF damage to this max per hit (anti-grief). A per-peer budget (20 hits/s, burst 40; 1000 damage/s, burst 3000) caps sustained spam while multi-hit bursts like shotgun pellets still apply in full.");
            PeerMovementVolume = config.Bind("Gameplay", "PeerMovementVolume", 0.75f,
                "Volume of other players' movement here (footsteps, clothes, dodge and landing steps), 0..1. Their other sounds (shots, hits, tools) stay at full volume.");
            HostMigrationEnabled = config.Bind("Network", "HostMigrationEnabled", true,
                "If true, host crash/timeout elects lowest remaining player id as new host (LAN n+). Peers reconnect to elected listen port.");
            VoiceEnabled = config.Bind("Voice", "VoiceEnabled", true,
                "Steam Voice proximity/walkie chat when Steam client is logged on.");
            VoiceMode = config.Bind("Voice", "VoiceMode", "ptt",
                "ptt = push-to-talk (VoicePttKey). open = always transmit while connected.");
            VoicePttKey = config.Bind("Voice", "VoicePttKey", "V",
                "Unity KeyCode name for push-to-talk (default V).");
            VoiceVolume = config.Bind("Voice", "VoiceVolume", 1f,
                "Playback volume multiplier for remote voice.");
            VoiceGain = config.Bind("Voice", "VoiceGain", 1.4f,
                "Gain applied after Steam DecompressVoice.");
            // Game units, like every other range here (a body is about 40 across).
            VoiceFullVolumeDistance = config.Bind("Voice", "VoiceFullVolumeDistance", 150f,
                "Distance (game units) within which proximity voice is at full volume.");
            VoiceMaxDistance = config.Bind("Voice", "VoiceMaxDistance", 650f,
                "Distance (game units) beyond which proximity voice is silent (same as other peer sounds).");
            WalkieItemName = config.Bind("Voice", "WalkieItemName", "walkie_talkie",
                "InvItem type for walkie radio (hold + RMB to TX; inventory enables radio RX).");
            // Entity spawner moved to standalone plugin YokWare.EntitySpawner.

            // Support = join/session/combat Events without Legacy flood ([Perf] via Debug.PerfProbe).
            // Dev = LegacyInfo dumps. Trace = max capture (LegacyInfo + VerboseLogging gates).
            LogPresetSetting = config.Bind("Logging", "LogPreset", "Support",
                "Public=quiet. Support=session/join/combat (default; [Perf] needs Debug.PerfProbe). Dev=LegacyInfo dumps + [Perf]. Trace=max capture (Legacy + high-freq Verbose). Dual-box bug packs: Trace on both. Restart after change.");
            LogMinLevelSetting = config.Bind("Logging", "LogMinLevel", "Event",
                "Error | Warn | Event | Info | Trace. LegacyInfo runs on LogPreset=Dev or Trace.");
            LogExtraCategories = config.Bind("Logging", "LogExtraCategories", "",
                "Optional Event categories on top of preset (comma): Core,Network,Session,Combat,Entity,Physics,Container,World,AI,Dream,Death,Audio,UI,Save.");
            LogTraceCategories = config.Bind("Logging", "LogTraceCategories", "none",
                "Categories allowed for Trace when not full Trace preset, or 'none'.");
            LogRateLimitSeconds = config.Bind("Logging", "LogRateLimitSeconds", 1f,
                "Minimum seconds between identical TraceRate keys (spam guard).");
            // Local dual-box default: full lines in BepInEx/LogOutput.log (set true for public pastebins).
            LogRedactPaths = config.Bind("Logging", "LogRedactPaths", false,
                "Strip absolute paths to filenames in log lines (safer pastebins).");
            LogRedactIPs = config.Bind("Logging", "LogRedactIPs", true,
                "Mask IPv4 in log lines. On by default so shared logs do not leak addresses; "
                + "set false to see full IPs while debugging your own LAN.");
            LogIncludeStacks = config.Bind("Logging", "LogIncludeStacks", true,
                "Include full exception stacks on Error (also on for Dev/Trace).");

            VerboseLogging = config.Bind("Debug", "VerboseLogging", false,
                "If true and LogPreset=Public, forces Trace. Prefer LogPreset=Support for join tests (not Trace/Dev).");
            VerboseLightSync = config.Bind("Debug", "VerboseLightSync", false,
                "Transition light sync logs. Leave false unless debugging lights.");
            VerboseEntitySync = config.Bind("Debug", "VerboseEntitySync", false,
                "Deep entity sync logs (anim/interp/reaction/damage/spawn). Rate-limited Trace + "
                + "lifecycle Events. Off by default; set true for dual-box diagnosis. "
                + "Also enables Entity/Combat/AI Trace under Support without full Trace preset.");
            FreeCursorForDualBox = config.Bind("Debug", "FreeCursorForDualBox", false,
                "Force Cursor.lockState=None (skip vanilla Confined). Off by default (vanilla confine). "
                + "Linux dual-box testers (Hyprland/Wayland, Wine/Proton SecondDarkwood) set this true: "
                + "Confined ClipCursor traps the mouse and blur-release can freeze the Wine window.");
            PerfProbe = config.Bind("Debug", "PerfProbe", false,
                "Co-op frame-cost probe: per-frame timing + [Perf] line every 2 s while connected. "
                + "Always on under LogPreset=Dev/Trace; set true to capture it under Support/Public.");
            DesyncCheck = config.Bind("Debug", "DesyncCheck", true,
                "Host sends each client a fingerprint of the world state they should share; the client "
                + "compares it with its own and logs any difference that lasts two checks as a DESYNC "
                + "line (both logs). Needs the host's setting on; a client with it off ignores digests.");
            DesyncCheckIntervalSec = config.Bind("Debug", "DesyncCheckIntervalSec", 15,
                "Seconds between desync checks (clamped 5..300).");

            // A read-only or locked cfg must not abort startup: every setting is already bound
            // with its in-memory value, the file is only a convenience.
            try
            {
                config.Save();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("Config save failed (" + config.ConfigFilePath + "): " + ex.Message);
            }
        }

        /// <summary>True when VerboseLightSync is enabled (safe if unbound).</summary>
        public static bool IsVerboseLightSync =>
            VerboseLightSync != null && VerboseLightSync.Value;

        /// <summary>True when the PerfProbe flag is enabled (safe if unbound — defaults off).</summary>
        public static bool IsPerfProbe => PerfProbe != null && PerfProbe.Value;

        /// <summary>True when VerboseEntitySync is enabled (safe if unbound — defaults off).</summary>
        public static bool IsVerboseEntitySync =>
            VerboseEntitySync != null && VerboseEntitySync.Value;
    }
}
