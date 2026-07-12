using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Shared dream session model (night parity):
    /// all connected players enter the initiated dream; death → spectate until session ends.
    /// Host alone decides when the session ends (story outcome or all dead).
    /// Sole authority for completed-preset set + level dream flags snapshot on the wire.
    /// </summary>
    internal static class DreamSession
    {
        public enum State
        {
            Idle,
            Starting,
            Active,
            Ending
        }

        public static State Current { get; private set; } = State.Idle;
        public static string PresetName { get; private set; }
        public static int SessionId { get; private set; }

        private static int _nextSessionId = 1;
        private static readonly HashSet<string> _completedPresets =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Host-resolved random pick for clients still in getPreset("") before DreamStarted.
        /// Vanilla sleep/skill dreams call prepareDream("") and roll inside getPreset.
        /// </summary>
        private static string _pendingHostPreset;

        /// <summary>Bit0=lvl2,1=lvl3,2=lvl5,3=lvl6,4=lvl7 (matches Dreams.hadDreamAtLvl*).</summary>
        public const byte LvlFlag2 = 1 << 0;
        public const byte LvlFlag3 = 1 << 1;
        public const byte LvlFlag5 = 1 << 2;
        public const byte LvlFlag6 = 1 << 3;
        public const byte LvlFlag7 = 1 << 4;

        public static bool IsActive =>
            Current == State.Starting || Current == State.Active || Current == State.Ending;

        public static bool IsStarting => Current == State.Starting;

        public static string PendingHostPreset => _pendingHostPreset;

        public static void SetPendingHostPreset(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            _pendingHostPreset = presetName;
        }

        /// <summary>Peek without clear — getPreset may run more than once for the same pick.</summary>
        public static bool TryGetPendingHostPreset(out string presetName)
        {
            presetName = _pendingHostPreset;
            return !string.IsNullOrEmpty(presetName);
        }

        public static void ClearPendingHostPreset()
        {
            _pendingHostPreset = null;
        }

        /// <summary>
        /// Mirror vanilla one-shot random pool: empty getPreset removes the pick from presetList.
        /// Named / Resources.Load paths do not — remotes and host named prepare must remove by name.
        /// </summary>
        public static void MirrorPoolRemove(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            var d = Dreams.Instance;
            if (d?.presetList == null || d.presetList.Count == 0) return;

            for (int i = d.presetList.Count - 1; i >= 0; i--)
            {
                var p = d.presetList[i];
                if (p == null) continue;
                string n = p.gameObject != null ? p.gameObject.name : p.name;
                if (string.Equals(n, presetName, StringComparison.OrdinalIgnoreCase))
                {
                    d.presetList.RemoveAt(i);
                    ModLog.Event(LogCat.Dream, "MirrorPoolRemove: " + presetName);
                }
            }
        }

        public static string ResolvePresetName(DreamPreset preset)
        {
            if (preset == null) return null;
            if (preset.gameObject != null && !string.IsNullOrEmpty(preset.gameObject.name))
                return preset.gameObject.name;
            return preset.name;
        }

        public static bool IsPresetCompleted(string preset)
        {
            return !string.IsNullOrEmpty(preset) && _completedPresets.Contains(preset);
        }

        public static string[] GetCompletedPresets()
        {
            if (_completedPresets.Count == 0)
                return Array.Empty<string>();
            var arr = new string[_completedPresets.Count];
            _completedPresets.CopyTo(arr);
            return arr;
        }

        public static void MarkCompleted(string preset)
        {
            if (!string.IsNullOrEmpty(preset))
                _completedPresets.Add(preset);
        }

        /// <summary>Host (or local solo) begins a session. Returns false if blocked.</summary>
        public static bool TryBegin(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return false;
            if (IsActive)
            {
                // Same preset while Starting = duplicate prepare; treat as success for caller ignore.
                if (Current == State.Starting
                    && string.Equals(PresetName, presetName, StringComparison.OrdinalIgnoreCase))
                {
                    ModLog.Event(LogCat.Dream, "TryBegin no-op — already Starting " + presetName);
                    return false;
                }
                ModLog.Event(LogCat.Dream, $"Reject begin — already active ({Current}): {presetName}");
                return false;
            }
            if (IsPresetCompleted(presetName))
            {
                ModLog.Event(LogCat.Dream, $"Reject begin — already completed: {presetName}");
                return false;
            }

            SessionId = _nextSessionId++;
            PresetName = presetName;
            Current = State.Starting;
            SetPendingHostPreset(presetName);
            FinalDreamsceneManager.OnDreamStarted();
            ModLog.Event(LogCat.Dream, $"Starting session {SessionId} preset={presetName}");
            return true;
        }

        /// <summary>
        /// transferToDream: mark previous pocket completed, stay in session, swap preset (no Idle).
        /// </summary>
        public static void SetChainedPreset(string nextPreset)
        {
            if (string.IsNullOrEmpty(nextPreset)) return;
            if (!IsActive)
            {
                TryBegin(nextPreset);
                SetPendingHostPreset(nextPreset);
                return;
            }
            if (!string.IsNullOrEmpty(PresetName))
                MarkCompleted(PresetName);
            PresetName = nextPreset;
            Current = State.Starting;
            SetPendingHostPreset(nextPreset);
            // Refresh death tracking for the new pocket (clear mid-dream death for survivors).
            FinalDreamsceneManager.OnDreamEnded();
            FinalDreamsceneManager.OnDreamStarted();
            ModLog.Event(LogCat.Dream, $"Chained preset → {nextPreset} (session {SessionId})");
        }

        public static void MarkActive()
        {
            if (Current == State.Starting)
                Current = State.Active;
        }

        public static void End(string outcomeName = "")
        {
            if (Current == State.Idle) return;
            Current = State.Ending;
            MarkCompleted(PresetName);
            ModLog.Event(LogCat.Dream,
                $"Ending session {SessionId} preset={PresetName} outcome={outcomeName}");
            FinalDreamsceneManager.OnDreamEnded();
            Current = State.Idle;
            PresetName = null;
            _pendingHostPreset = null;
        }

        /// <summary>Abort Starting if prepare failed.</summary>
        public static void AbortStarting(string reason)
        {
            if (Current != State.Starting) return;
            ModLog.Event(LogCat.Dream, "Abort Starting: " + reason);
            FinalDreamsceneManager.OnDreamEnded();
            Current = State.Idle;
            PresetName = null;
            _pendingHostPreset = null;
        }

        public static void Reset()
        {
            Current = State.Idle;
            PresetName = null;
            SessionId = 0;
            _pendingHostPreset = null;
        }

        public static void ResetIncludingCompletions()
        {
            Reset();
            _completedPresets.Clear();
        }

        public static bool ShouldRejectNewConnections => IsActive;

        // ── Snapshot (level flags + completed) ───────────────────────────

        public static byte ReadLocalLvlFlags()
        {
            var d = Dreams.Instance;
            if (d == null) return 0;
            byte b = 0;
            if (d.hadDreamAtLvl2) b |= LvlFlag2;
            if (d.hadDreamAtLvl3) b |= LvlFlag3;
            if (d.hadDreamAtLvl5) b |= LvlFlag5;
            if (d.hadDreamAtLvl6) b |= LvlFlag6;
            if (d.hadDreamAtLvl7) b |= LvlFlag7;
            return b;
        }

        public static void ApplyLvlFlags(byte flags)
        {
            var d = Dreams.Instance;
            if (d == null) return;
            if ((flags & LvlFlag2) != 0) d.hadDreamAtLvl2 = true;
            if ((flags & LvlFlag3) != 0) d.hadDreamAtLvl3 = true;
            if ((flags & LvlFlag5) != 0) d.hadDreamAtLvl5 = true;
            if ((flags & LvlFlag6) != 0) d.hadDreamAtLvl6 = true;
            if ((flags & LvlFlag7) != 0) d.hadDreamAtLvl7 = true;
        }

        /// <summary>Merge host snapshot into local completed set + lvl flags (union, never clear remote-unknown).</summary>
        public static void ApplySnapshot(string[] completed, byte lvlFlags)
        {
            if (completed != null)
            {
                for (int i = 0; i < completed.Length; i++)
                {
                    if (!string.IsNullOrEmpty(completed[i]))
                        _completedPresets.Add(completed[i]);
                }
            }
            ApplyLvlFlags(lvlFlags);
            ModLog.Event(LogCat.Dream,
                "Applied session snapshot completed=" + _completedPresets.Count
                + " lvlFlags=" + lvlFlags);
        }

        public static void WriteSnapshot(NetWriter w)
        {
            w.Put(SessionId);
            w.Put(ReadLocalLvlFlags());
            string[] done = GetCompletedPresets();
            w.Put(done.Length);
            for (int i = 0; i < done.Length; i++)
                w.Put(done[i] ?? "");
        }

        public static void ReadSnapshotInto(NetReader r, out int sessionId, out byte lvlFlags, out string[] completed)
        {
            sessionId = 0;
            lvlFlags = 0;
            completed = Array.Empty<string>();
            if (r.AvailableBytes < 1) return;
            sessionId = r.GetInt();
            lvlFlags = r.GetByte();
            int n = r.GetInt();
            if (n < 0 || n > 256) n = 0;
            completed = new string[n];
            for (int i = 0; i < n; i++)
                completed[i] = r.GetString();
        }
    }
}
