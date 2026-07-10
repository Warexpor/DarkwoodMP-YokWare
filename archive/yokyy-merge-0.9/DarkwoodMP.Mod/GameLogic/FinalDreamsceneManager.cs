using System.Collections.Generic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Shared dream death set (Horde FinalDreamsceneManager slim).
/// When all participants die in a shared dream, ends the dream for everyone.
/// Epilogue crawl deaths are skipped. Lightweight follow-cam instead of full spectator UI.
/// </summary>
public static class FinalDreamsceneManager
{
    private static bool _active;
    private static bool _localDead;
    private static bool _ending;
    private static readonly HashSet<int> _deadIds = new HashSet<int>();
    private static readonly HashSet<int> _participants = new HashSet<int>();

    public static bool IsActive => _active;
    public static bool IsLocalDead => _localDead;

    public static bool AllDead
    {
        get
        {
            if (!_active || !_localDead || _participants.Count == 0) return false;
            foreach (var id in _participants)
                if (!_deadIds.Contains(id)) return false;
            return true;
        }
    }

    public static void OnDreamStarted()
    {
        _active = true;
        _localDead = false;
        _ending = false;
        _deadIds.Clear();
        RefreshParticipants();
        ModLogger.Msg($"[FinalDream] Dream death tracking on ({_participants.Count} participants)");
    }

    public static void OnDreamEnded()
    {
        if (!_active && _participants.Count == 0) return;
        _active = false;
        _localDead = false;
        _ending = false;
        _deadIds.Clear();
        _participants.Clear();
        RestoreLocalIfSpectating();
        ModLogger.Msg("[FinalDream] Tracking off");
    }

    public static void RefreshParticipants()
    {
        _participants.Clear();
        var manager = NetworkManager.Instance;
        if (manager == null) return;
        if (manager.LocalPlayerId >= 0)
            _participants.Add(manager.LocalPlayerId);
        foreach (var id in manager.ConnectedPlayers)
            if (id >= 0) _participants.Add(id);
    }

    public static void OnLocalDeathInDream()
    {
        if (!_active || _localDead || _ending) return;

        try
        {
            if (Player.Instance != null && Player.Instance.inEpilogue)
            {
                ModLogger.Msg("[FinalDream] Epilogue death — vanilla path");
                return;
            }
        }
        catch { /* inEpilogue may not exist on all builds */ }

        RefreshParticipants();
        if (_participants.Count <= 1)
        {
            ModLogger.Msg("[FinalDream] Solo dream death — vanilla");
            return;
        }

        var manager = NetworkManager.Instance;
        _localDead = true;
        if (manager != null && manager.LocalPlayerId >= 0)
            _deadIds.Add(manager.LocalPlayerId);

        var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
        if (network != null && network.IsConnected && manager != null)
        {
            network.SendReliable(new FinalDreamDeathPacket
            {
                PlayerId = manager.LocalPlayerId
            });
        }

        EnterLightweightSpectate();

        if (AllDead)
            EndDreamForAll();
    }

    public static void OnRemoteDeathInDream(int playerId)
    {
        if (!_active || _ending || playerId < 0) return;
        RefreshParticipants();
        _participants.Add(playerId);
        _deadIds.Add(playerId);
        ModLogger.Msg($"[FinalDream] Remote {playerId} dead in dream ({_deadIds.Count}/{_participants.Count})");

        var go = NetworkManager.Instance?.GetRemotePlayer(playerId);
        go?.GetComponent<RemotePlayerProxy>()?.ApplyDeathState();

        if (AllDead)
            EndDreamForAll();
    }

    public static void OnRemoteDisconnected(int playerId)
    {
        _participants.Remove(playerId);
        _deadIds.Remove(playerId);
        if (_active && !_ending && _localDead && AllDead)
            EndDreamForAll();
    }

    public static void Reset() => OnDreamEnded();

    private static void EnterLightweightSpectate()
    {
        try
        {
            SpectatorModeController.EnsureExists();
            Transform follow = null;
            var manager = NetworkManager.Instance;
            if (manager != null)
            {
                foreach (var kvp in manager.RemotePlayers)
                {
                    if (kvp.Value == null) continue;
                    var proxy = kvp.Value.GetComponent<RemotePlayerProxy>();
                    if (proxy != null && proxy.IsDead) continue;
                    follow = kvp.Value.transform;
                    break;
                }
            }
            if (follow == null) return;
            SpectatorModeController.Instance.ForceEnter(follow);
        }
        catch (System.Exception ex)
        {
            ModLogger.Error($"[FinalDream] spectate: {ex.Message}");
        }
    }

    private static void RestoreLocalIfSpectating()
    {
        try
        {
            if (SpectatorModeController.Instance != null && SpectatorModeController.Instance.IsSpectating)
                SpectatorModeController.Instance.ExitWithoutPositionRestore();
        }
        catch { }
    }

    private static void EndDreamForAll()
    {
        if (_ending) return;
        _ending = true;
        ModLogger.Msg("[FinalDream] All dead — ending dream");

        try
        {
            var player = Player.Instance;
            if (player != null)
            {
                player.invulnerable = false;
                try { if (player.immobilised) player.stopImmobilise(); } catch { }
            }

            var dreams = Singleton<Dreams>.Instance;
            if (dreams != null && dreams.dreaming)
                dreams.endDreaming();
        }
        catch (System.Exception ex)
        {
            ModLogger.Error($"[FinalDream] EndDream: {ex.Message}");
        }

        DreamSession.End("all_dead");
        OnDreamEnded();
    }
}
