using System.Collections.Generic;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Shared dream session model (night parity):
    /// all connected players enter the initiated dream; death → spectate until session ends.
    /// Host alone decides when the session ends (story outcome or all dead).
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
        private static readonly HashSet<string> _completedPresets = new HashSet<string>();

        public static bool IsActive => Current == State.Starting || Current == State.Active || Current == State.Ending;

        public static bool IsPresetCompleted(string preset)
        {
            return !string.IsNullOrEmpty(preset) && _completedPresets.Contains(preset);
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
            FinalDreamsceneManager.OnDreamStarted();
            ModLog.Event(LogCat.Dream, $"Starting session {SessionId} preset={presetName}");
            return true;
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
            ModLog.Event(LogCat.Dream, $"Ending session {SessionId} preset={PresetName} outcome={outcomeName}");
            FinalDreamsceneManager.OnDreamEnded();
            Current = State.Idle;
            PresetName = null;
        }

        public static void Reset()
        {
            Current = State.Idle;
            PresetName = null;
            SessionId = 0;
            // Keep _completedPresets across disconnect within same game load;
            // clear only on full network stop if desired — cleared in OnNetworkStop.
        }

        public static void ResetIncludingCompletions()
        {
            Reset();
            _completedPresets.Clear();
        }

        /// <summary>Reject new LAN joins while a dream is in progress (P3.7).</summary>
        public static bool ShouldRejectNewConnections => IsActive;
    }
}
