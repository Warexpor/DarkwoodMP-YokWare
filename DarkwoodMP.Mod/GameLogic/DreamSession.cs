using System.Collections.Generic;
using DarkwoodMP.Network;
using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Shared dream session (Horde DreamSession slim).
/// Authority picks preset; all peers enter; freeze proxies until DreamEntered.
/// </summary>
public static class DreamSession
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
    private static readonly HashSet<int> _enteredPlayerIds = new HashSet<int>();

    public static bool IsActive =>
        Current == State.Starting || Current == State.Active || Current == State.Ending;

    /// <summary>Reject new LAN joins while a dream is in progress.</summary>
    public static bool ShouldRejectNewConnections => IsActive;

    public static bool IsPresetCompleted(string preset) =>
        !string.IsNullOrEmpty(preset) && _completedPresets.Contains(preset);

    public static bool TryBegin(string presetName)
    {
        if (string.IsNullOrEmpty(presetName)) return false;
        if (presetName == "playerDeath") return false; // personal
        if (IsActive) return false;
        if (IsPresetCompleted(presetName)) return false;

        SessionId = _nextSessionId++;
        PresetName = presetName;
        Current = State.Starting;
        _enteredPlayerIds.Clear();
        FinalDreamsceneManager.OnDreamStarted();
        ModLogger.Msg($"[DreamSession] Starting session {SessionId} preset={presetName}");
        return true;
    }

    public static void MarkActive()
    {
        if (Current == State.Starting)
            Current = State.Active;
    }

    public static void OnPlayerEntered(int playerId)
    {
        if (!IsActive || playerId < 0) return;
        _enteredPlayerIds.Add(playerId);
        // Unfreeze that remote proxy so they can move in the dream
        var go = NetworkManager.Instance?.GetRemotePlayer(playerId);
        var proxy = go != null ? go.GetComponent<RemotePlayerProxy>() : null;
        if (proxy != null)
            proxy.FreezePosition = false;
    }

    public static void End(string outcomeName = "")
    {
        if (Current == State.Idle) return;
        Current = State.Ending;
        if (!string.IsNullOrEmpty(PresetName))
            _completedPresets.Add(PresetName);
        ModLogger.Msg($"[DreamSession] Ending session {SessionId} preset={PresetName} outcome={outcomeName}");

        // Unfreeze all remotes
        var manager = NetworkManager.Instance;
        if (manager != null)
        {
            foreach (var kvp in manager.RemotePlayers)
            {
                var proxy = kvp.Value != null ? kvp.Value.GetComponent<RemotePlayerProxy>() : null;
                if (proxy != null && !proxy.IsDead)
                    proxy.FreezePosition = false;
            }
        }

        FreezeTracker.Reset();
        FinalDreamsceneManager.OnDreamEnded();
        Current = State.Idle;
        PresetName = null;
        _enteredPlayerIds.Clear();
    }

    public static void Reset()
    {
        Current = State.Idle;
        PresetName = null;
        SessionId = 0;
        _enteredPlayerIds.Clear();
        FreezeTracker.Reset();
        FinalDreamsceneManager.Reset();
    }

    public static void ResetIncludingCompletions()
    {
        Reset();
        _completedPresets.Clear();
    }

    /// <summary>Freeze remote proxies while they load into the dream.</summary>
    public static void FreezeAllRemotes()
    {
        var manager = NetworkManager.Instance;
        if (manager == null) return;
        foreach (var kvp in manager.RemotePlayers)
        {
            var proxy = kvp.Value != null ? kvp.Value.GetComponent<RemotePlayerProxy>() : null;
            if (proxy != null)
                proxy.FreezePosition = true;
        }
    }
}
