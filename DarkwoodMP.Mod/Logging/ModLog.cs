using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using DWMPHorde.Config;
using UnityEngine;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// Central logging facade. Default <see cref="LogPreset.Trace"/> = full logging
    /// (all categories + high-freq). Set Public in config for quiet play.
    /// </summary>
    public static class ModLog
    {
        private static ManualLogSource _log;
        private static LogLevel _minLevel = LogLevel.Trace;
        private static LogPreset _preset = LogPreset.Trace;
        private static readonly bool[] _catEvent = new bool[16];
        private static readonly bool[] _catTrace = new bool[16];
        private static bool _redactPaths = true;
        private static bool _redactIps = true;
        private static bool _includeStacks;
        private static float _defaultRateSec = 1f;

        private static readonly Dictionary<string, float> _rateLast = new Dictionary<string, float>(64);
        private const int RateCap = 256;

        /// <summary>Must match <see cref="LogCat"/> order and count.</summary>
        private static readonly string[] CatTags =
        {
            "[YokWare]",
            "[YokWare/Net]",
            "[YokWare/Session]",
            "[YokWare/Combat]",
            "[YokWare/Entity]",
            "[YokWare/Phys]",
            "[YokWare/Loot]",
            "[YokWare/World]",
            "[YokWare/AI]",
            "[YokWare/Dream]",
            "[YokWare/Death]",
            "[YokWare/Audio]",
            "[YokWare/UI]",
            "[YokWare/Save]"
        };

        public static LogPreset CurrentPreset => _preset;
        public static LogLevel MinLevel => _minLevel;

        /// <summary>Host LogOutput path (Steam install).</summary>
        public const string HostLogHint = @"…\Steam\steamapps\common\Darkwood\BepInEx\LogOutput.log";

        /// <summary>Typical second-install client log path for dual testing.</summary>
        public const string ClientLogHint = @"…\SecondDarkwood\Darkwood\BepInEx\LogOutput.log";

        public static void Init(ManualLogSource log)
        {
            _log = log;
            ApplyConfig();
        }

        /// <summary>Re-read BepInEx config values into live filters.</summary>
        public static void ApplyConfig()
        {
            _preset = ModConfig.GetLogPreset();
            _minLevel = ModConfig.GetLogMinLevel();
            _redactPaths = ModConfig.LogRedactPaths == null || ModConfig.LogRedactPaths.Value;
            _redactIps = ModConfig.LogRedactIPs == null || ModConfig.LogRedactIPs.Value;
            _includeStacks = ModConfig.LogIncludeStacks != null && ModConfig.LogIncludeStacks.Value;
            _defaultRateSec = ModConfig.LogRateLimitSeconds != null
                ? Mathf.Max(0.1f, ModConfig.LogRateLimitSeconds.Value)
                : 1f;

            // Legacy VerboseLogging forces Trace when preset is still Public
            if (ModConfig.VerboseLogging != null && ModConfig.VerboseLogging.Value
                && _preset == LogPreset.Public)
            {
                _preset = LogPreset.Trace;
            }

            // Bridge old gates that still check ModRuntime.VerboseLogging
            ModRuntime.VerboseLogging = _preset == LogPreset.Trace
                || (_preset == LogPreset.Dev && _minLevel >= LogLevel.Trace);

            Array.Clear(_catEvent, 0, _catEvent.Length);
            Array.Clear(_catTrace, 0, _catTrace.Length);

            switch (_preset)
            {
                case LogPreset.Public:
                    // Quiet playtest: session lifecycle only
                    EnableEvent(LogCat.Core, LogCat.Network, LogCat.Session, LogCat.Dream, LogCat.Death, LogCat.Save);
                    break;
                case LogPreset.Support:
                    // Bug reports: add world simulation categories without full Trace spam
                    EnableEvent(LogCat.Core, LogCat.Network, LogCat.Session, LogCat.Dream, LogCat.Death, LogCat.Save,
                        LogCat.Combat, LogCat.Entity, LogCat.World, LogCat.Container);
                    break;
                case LogPreset.Dev:
                    for (int i = 0; i < CatTags.Length; i++)
                        _catEvent[i] = true;
                    break;
                case LogPreset.Trace:
                    for (int i = 0; i < CatTags.Length; i++)
                    {
                        _catEvent[i] = true;
                        _catTrace[i] = true;
                    }
                    _minLevel = LogLevel.Trace;
                    break;
            }

            ParseExtraCategories(ModConfig.LogExtraCategories?.Value, eventLevel: true);
            ParseExtraCategories(ModConfig.LogTraceCategories?.Value, eventLevel: false);
        }

        private static void EnableEvent(params LogCat[] cats)
        {
            foreach (var c in cats)
            {
                int i = (int)c;
                if (i >= 0 && i < _catEvent.Length)
                    _catEvent[i] = true;
            }
        }

        private static void ParseExtraCategories(string raw, bool eventLevel)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
                return;
            foreach (var part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse(part.Trim(), true, out LogCat cat))
                {
                    int i = (int)cat;
                    if (i < 0 || i >= _catEvent.Length) continue;
                    if (eventLevel) _catEvent[i] = true;
                    else _catTrace[i] = true;
                }
            }
        }

        public static void ResetRateLimits()
        {
            _rateLast.Clear();
        }

        public static bool IsEnabled(LogCat cat, LogLevel level)
        {
            if (_log == null) return false;
            if (level > _minLevel) return false;
            if (level <= LogLevel.Warn) return true; // Error/Warn always on
            int i = (int)cat;
            if (i < 0 || i >= _catEvent.Length) return false;
            if (level == LogLevel.Trace)
                return _catTrace[i] || _preset == LogPreset.Trace;
            return _catEvent[i];
        }

        public static bool IsTrace(LogCat cat) => IsEnabled(cat, LogLevel.Trace);
        public static bool IsEvent(LogCat cat) => IsEnabled(cat, LogLevel.Event);

        public static void Event(LogCat cat, string message) => Write(cat, LogLevel.Event, message);
        public static void Info(LogCat cat, string message) => Write(cat, LogLevel.Info, message);
        public static void Warn(LogCat cat, string message) => Write(cat, LogLevel.Warn, message);
        public static void Error(LogCat cat, string message) => Write(cat, LogLevel.Error, message);

        public static void Error(LogCat cat, string message, Exception ex)
        {
            if (ex == null)
            {
                Error(cat, message);
                return;
            }
            string msg = message + " | " + ex.GetType().Name + ": " + ex.Message;
            if (_includeStacks || _preset == LogPreset.Trace || _preset == LogPreset.Dev)
                msg += "\n" + ex;
            Write(cat, LogLevel.Error, msg);
        }

        public static void Trace(LogCat cat, string message) => Write(cat, LogLevel.Trace, message);

        public static void Trace(LogCat cat, Func<string> messageFactory)
        {
            if (!IsEnabled(cat, LogLevel.Trace) || messageFactory == null) return;
            Write(cat, LogLevel.Trace, messageFactory());
        }

        public static void Event(LogCat cat, Func<string> messageFactory)
        {
            if (!IsEnabled(cat, LogLevel.Event) || messageFactory == null) return;
            Write(cat, LogLevel.Event, messageFactory());
        }

        public static void TraceRate(LogCat cat, string key, string message, float minIntervalSec = -1f)
        {
            if (!IsEnabled(cat, LogLevel.Trace)) return;
            if (!PassRate(key, minIntervalSec)) return;
            Write(cat, LogLevel.Trace, message);
        }

        public static void TraceRate(LogCat cat, string key, Func<string> messageFactory, float minIntervalSec = -1f)
        {
            if (!IsEnabled(cat, LogLevel.Trace) || messageFactory == null) return;
            if (!PassRate(key, minIntervalSec)) return;
            Write(cat, LogLevel.Trace, messageFactory());
        }

        public static void WarnRate(LogCat cat, string key, string message, float minIntervalSec = 5f)
        {
            if (!IsEnabled(cat, LogLevel.Warn)) return;
            if (!PassRate(key, minIntervalSec)) return;
            Write(cat, LogLevel.Warn, message);
        }

        private static bool PassRate(string key, float minIntervalSec)
        {
            if (string.IsNullOrEmpty(key)) return true;
            float interval = minIntervalSec > 0f ? minIntervalSec : _defaultRateSec;
            float now = Time.unscaledTime;
            if (_rateLast.TryGetValue(key, out float last) && now - last < interval)
                return false;
            if (_rateLast.Count >= RateCap)
                _rateLast.Clear();
            _rateLast[key] = now;
            return true;
        }

        private static void Write(LogCat cat, LogLevel level, string message)
        {
            if (!IsEnabled(cat, level)) return;
            if (_log == null) return;

            string body = message ?? "";
            if (_redactPaths) body = RedactPaths(body);
            if (_redactIps) body = RedactIps(body);

            int tagIdx = (int)cat;
            string tag = tagIdx >= 0 && tagIdx < CatTags.Length ? CatTags[tagIdx] : "[YokWare]";
            // Level tag so Trace vs Event is filterable in BepInEx LogOutput.log
            string lvl =
                level == LogLevel.Error ? "ERROR" :
                level == LogLevel.Warn ? "WARN" :
                level == LogLevel.Trace ? "TRACE" :
                level == LogLevel.Info ? "INFO" : "EVENT";
            string line = tag + "[" + lvl + "] " + body;

            switch (level)
            {
                case LogLevel.Error:
                    _log.LogError(line);
                    break;
                case LogLevel.Warn:
                    _log.LogWarning(line);
                    break;
                case LogLevel.Trace:
                    // Single write — dual Debug+Info doubled file weight for no gain.
                    _log.LogInfo(line);
                    break;
                default:
                    _log.LogInfo(line);
                    break;
            }
        }

        /// <summary>Dev-only legacy dumps: max ~2 lines/sec per message prefix.</summary>
        public static void LegacyRateLimited(string message)
        {
            if (_log == null || message == null) return;
            if (_preset != LogPreset.Dev) return;
            string key = message.Length > 48 ? message.Substring(0, 48) : message;
            if (!PassRate("legacy:" + key, 0.5f)) return;
            _log.LogInfo("[YokWare][LEGACY] " + message);
        }

        /// <summary>Always prints identity + bug-report instructions (forces Core Event for these lines).</summary>
        public static void BannerSessionStart()
        {
            if (_log == null) return;

            bool wasCore = _catEvent[(int)LogCat.Core];
            _catEvent[(int)LogCat.Core] = true;
            try
            {
                Event(LogCat.Core, "================================================");
                Event(LogCat.Core, "  " + PluginInfo.Name + " v" + PluginInfo.DisplayVersion);
                Event(LogCat.Core, "  Protocol=" + PluginInfo.ProtocolVersion
                    + " | LogPreset=" + _preset
                    + " | MinLevel=" + _minLevel
                    + " | RedactIP=" + _redactIps
                    + " | RedactPath=" + _redactPaths);
                Event(LogCat.Core, "  Unity=" + Application.unityVersion
                    + " | " + SystemInfo.operatingSystem);
                Event(LogCat.Core, "  Config: BepInEx/config/" + PluginInfo.Guid + ".cfg");
                Event(LogCat.Core, "  Title: MULTIPLAYER | F2=settings F3=save F4=spectate | Ctrl+C=chat | F5=spawner");
                Event(LogCat.Core, "  Host log:  BepInEx/LogOutput.log (this install)");
                Event(LogCat.Core, "  Client log: second install's BepInEx/LogOutput.log");
                Event(LogCat.Core, "  Bug report: quit cleanly, send BOTH host+client BepInEx/LogOutput.log");
                Event(LogCat.Core, "  Quiet logs: set [Logging] LogPreset=Public (default is full Trace)");
                Event(LogCat.Core, "  Path B: Horde host-authoritative sync | GPLv3 | " + PluginInfo.Authors);
                Event(LogCat.Core, "  Docs: docs/PATH_B_FEATURE_INVENTORY.md + DarkwoodMP.Mod/docs/LOGGING.md");
                Event(LogCat.Core, "================================================");
            }
            finally
            {
                // Restore prior Core Event enablement (do not force Core permanently on Public)
                _catEvent[(int)LogCat.Core] = wasCore;
            }
        }

        /// <summary>Session teardown line for testers (when network stops).</summary>
        public static void BannerSessionStop(string role, int localId, int peerCount)
        {
            Event(LogCat.Network, "Session stopped | wasRole=" + role
                + " localId=" + localId
                + " peers=" + peerCount
                + " preset=" + _preset);
        }

        private static string RedactPaths(string s)
        {
            if (string.IsNullOrEmpty(s) || (s.IndexOf(':') < 0 && s.IndexOf('\\') < 0 && s.IndexOf('/') < 0))
                return s;
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (i + 2 < s.Length && char.IsLetter(s[i]) && s[i + 1] == ':' && (s[i + 2] == '\\' || s[i + 2] == '/'))
                {
                    int start = i;
                    i += 3;
                    while (i < s.Length && s[i] != ' ' && s[i] != '"' && s[i] != '\'' && s[i] != '\n' && s[i] != '\r')
                        i++;
                    string path = s.Substring(start, i - start);
                    int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
                    sb.Append(slash >= 0 ? path.Substring(slash + 1) : path);
                    continue;
                }
                sb.Append(s[i]);
                i++;
            }
            return sb.ToString();
        }

        private static string RedactIps(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('.') < 0) return s;
            return System.Text.RegularExpressions.Regex.Replace(
                s,
                @"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b",
                "*.*.*.*");
        }
    }
}
