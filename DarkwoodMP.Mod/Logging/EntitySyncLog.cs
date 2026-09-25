using System;
using DWMPHorde.Config;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// Deep entity-path logging (anim / interp / reaction / damage / spawn).
    /// Gated by <see cref="ModConfig.VerboseEntitySync"/> (or full Trace preset /
    /// <c>LogTraceCategories=Entity</c>). Hot paths use <see cref="ModLog.TraceRate"/>.
    /// Lifecycle transitions use <see cref="ModLog.Event"/>.
    /// </summary>
    public static class EntitySyncLog
    {
        public static bool On =>
            ModConfig.IsVerboseEntitySync
            || ModLog.IsTrace(LogCat.Entity);

        public static bool CombatOn =>
            ModConfig.IsVerboseEntitySync
            || ModLog.IsTrace(LogCat.Combat)
            || ModLog.IsTrace(LogCat.Entity);

        /// <summary>Rate-limited Trace under Entity.</summary>
        public static void Trace(string key, string message, float intervalSec = -1f)
        {
            if (!On) return;
            ModLog.TraceRate(LogCat.Entity, key, message, intervalSec);
        }

        public static void Trace(string key, Func<string> factory, float intervalSec = -1f)
        {
            if (!On || factory == null) return;
            ModLog.TraceRate(LogCat.Entity, key, factory, intervalSec);
        }

        /// <summary>Unrate-limited Trace (caller must ensure rarity).</summary>
        public static void TraceNow(string message)
        {
            if (!On) return;
            ModLog.Trace(LogCat.Entity, message);
        }

        public static void TraceNow(Func<string> factory)
        {
            if (!On || factory == null) return;
            ModLog.Trace(LogCat.Entity, factory);
        }

        /// <summary>Lifecycle / one-shot (spawn, death, despawn, match, clip change).</summary>
        public static void Event(string message)
        {
            if (!On && !ModLog.IsEvent(LogCat.Entity)) return;
            // When VerboseEntitySync is on, always emit even under Public.
            if (On || ModLog.IsEvent(LogCat.Entity))
                ModLog.Event(LogCat.Entity, message);
        }

        public static void Event(Func<string> factory)
        {
            if (factory == null) return;
            if (!On && !ModLog.IsEvent(LogCat.Entity)) return;
            ModLog.Event(LogCat.Entity, factory);
        }

        public static void Combat(string message)
        {
            if (!CombatOn && !ModLog.IsEvent(LogCat.Combat)) return;
            ModLog.Event(LogCat.Combat, message);
        }

        public static void Combat(Func<string> factory)
        {
            if (factory == null) return;
            if (!CombatOn && !ModLog.IsEvent(LogCat.Combat)) return;
            ModLog.Event(LogCat.Combat, factory);
        }

        public static void CombatTrace(string key, string message, float intervalSec = -1f)
        {
            if (!CombatOn) return;
            ModLog.TraceRate(LogCat.Combat, key, message, intervalSec);
        }

        public static void CombatTrace(string key, Func<string> factory, float intervalSec = -1f)
        {
            if (!CombatOn || factory == null) return;
            ModLog.TraceRate(LogCat.Combat, key, factory, intervalSec);
        }

        public static void Reaction(string key, string message, float intervalSec = 0.5f)
        {
            if (!On) return;
            ModLog.TraceRate(LogCat.Entity, "react:" + key, message, intervalSec);
        }

        public static void Reaction(string key, Func<string> factory, float intervalSec = 0.5f)
        {
            if (!On || factory == null) return;
            ModLog.TraceRate(LogCat.Entity, "react:" + key, factory, intervalSec);
        }

        public static void Anim(string key, string message, float intervalSec = 0.75f)
        {
            if (!On) return;
            ModLog.TraceRate(LogCat.Entity, "anim:" + key, message, intervalSec);
        }

        public static void Anim(string key, Func<string> factory, float intervalSec = 0.75f)
        {
            if (!On || factory == null) return;
            ModLog.TraceRate(LogCat.Entity, "anim:" + key, factory, intervalSec);
        }

        public static void Interp(string key, string message, float intervalSec = 1f)
        {
            if (!On) return;
            ModLog.TraceRate(LogCat.Entity, "interp:" + key, message, intervalSec);
        }

        public static void Interp(string key, Func<string> factory, float intervalSec = 1f)
        {
            if (!On || factory == null) return;
            ModLog.TraceRate(LogCat.Entity, "interp:" + key, factory, intervalSec);
        }

        public static void Damage(string message)
        {
            Combat(message);
        }
    }
}
