using System;
using System.IO;
using DarkwoodMP.Network;

namespace DarkwoodMP;

/// <summary>
/// Loader-agnostic logging tuned for playtest triage.
/// Every line is filterable: <c>[YokWare][LEVEL][Category]</c>.
/// Verbose noise is off unless config <c>VerboseLogging=true</c> or <see cref="Verbose"/> is set.
/// </summary>
public static class ModLogger
{
    public const string Prefix = "[YokWare]";

    private static Action<string> _info = _ => { };
    private static Action<string> _warn = _ => { };
    private static Action<string> _error = _ => { };
    private static bool _ready;

    /// <summary>When true, <see cref="Verbose"/> / high-frequency net diag emit.</summary>
    public static bool Verbose
    {
        get => NetworkLayer.VerboseLogging;
        set => NetworkLayer.VerboseLogging = value;
    }

    public static void Initialize(Action<string> info, Action<string> warning, Action<string> error)
    {
        _info = info ?? (_ => { });
        _warn = warning ?? (_ => { });
        _error = error ?? (_ => { });
        _ready = true;
    }

#if BEPINEX
    public static void Initialize(BepInEx.Logging.ManualLogSource source)
    {
        if (source == null)
        {
            Initialize(Console.WriteLine, Console.WriteLine, Console.WriteLine);
            return;
        }
        Initialize(
            m => source.LogInfo(m),
            m => source.LogWarning(m),
            m => source.LogError(m));
    }
#endif

    /// <summary>Apply VerboseLogging from loaded config (call after <see cref="ModConfig.Load"/>).</summary>
    public static void ApplyConfig(ModConfig config)
    {
        if (config == null) return;
        Verbose = config.VerboseLogging;
        if (Verbose)
            Msg("Log", "VerboseLogging ON — high-frequency net/entity lines enabled");
    }

    /// <summary>One-shot session banner for testers (filter: [YokWare][INFO][Boot]).</summary>
    public static void BootBanner(string loaderName, string configPath, int worldSeed)
    {
        Msg("Boot", "======== session ========");
        Msg("Boot", $"{ProductInfo.Name} v{ProductInfo.Version} | {ProductInfo.Authors}");
        Msg("Boot", $"Loader={loaderName} | Wire={Packets.Ironbark.Banner} | Product={ProductInfo.Version}");
        Msg("Boot", $"Config={configPath}");
        Msg("Boot", worldSeed != 0
            ? $"WorldSeed={worldSeed} (must match all peers; use NEW game)"
            : "WorldSeed=0 WARNING — worlds will diverge");
        Msg("Boot", $"VerboseLogging={Verbose} (set VerboseLogging=true in config.ini for deep net/entity logs)");
        Msg("Boot", "Filter tips: [Session] [Join] [World] [Combat] [Reliable] [SyncCheck] [ERROR]");
        Msg("Boot", "======== ready ========");
    }

    public static void Msg(string message) => Write("INFO", "Log", message);
    public static void Msg(string category, string message) => Write("INFO", category, message);

    public static void Warning(string message) => Write("WARN", "Log", message);
    public static void Warning(string category, string message) => Write("WARN", category, message);

    public static void Error(string message) => Write("ERROR", "Log", message);
    public static void Error(string category, string message) => Write("ERROR", category, message);

    /// <summary>Only when VerboseLogging is on (datagram spam, entity stream, etc.).</summary>
    public static void VerboseMsg(string category, string message)
    {
        if (!Verbose) return;
        Write("VERBOSE", category, message);
    }

    /// <summary>Legacy bridge: still works; prefer category overload.</summary>
    public static void VerboseMsg(string message) => VerboseMsg("Log", message);

    private static void Write(string level, string category, string message)
    {
        var cat = string.IsNullOrEmpty(category) ? "Log" : Sanitize(category);
        var line = $"{Prefix}[{level}][{cat}] {message}";
        if (!_ready)
        {
            if (level == "ERROR") Console.Error.WriteLine(line);
            else Console.WriteLine(line);
            return;
        }
        switch (level)
        {
            case "WARN": _warn(line); break;
            case "ERROR": _error(line); break;
            default: _info(line); break;
        }
    }

    private static string Sanitize(string category)
    {
        // Keep tags short for filter UIs
        if (category.Length > 24) return category.Substring(0, 24);
        return category.Trim().Trim('[', ']');
    }
}
