using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;
// BindingFlags

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Syncs world state (day number, day/night) between host and clients.
/// The host is authoritative: its Controller_Time_Patch broadcasts transitions,
/// clients apply them to their local Controller singleton.
/// </summary>
public class WorldSync
{
    private readonly NetworkLayer _network;
    private int _prevDayNumber = -1;
    private bool _prevIsNight;

    public WorldSync(NetworkLayer network)
    {
        _network = network;
    }

    public void OnGameStateSync(GameStateSyncPacket packet)
    {
        ApplyRemote(packet.DayNumber, packet.IsNight);
    }

    public void OnDayNightUpdate(DayNightUpdatePacket packet)
    {
        ApplyRemote(packet.DayNumber, packet.IsNight);
    }

    /// <summary>Host side: broadcast a local day/night transition.</summary>
    public void SyncDayNightChange(int dayNumber, bool isNight)
    {
        _prevDayNumber = dayNumber;
        _prevIsNight = isNight;
        _network.SendReliable(new DayNightUpdatePacket
        {
            IsNight = isNight,
            DayNumber = dayNumber,
            TimeRemaining = 0f
        });
    }

    // ------------------------------------------------------------------
    // Continuous clock sync
    // ------------------------------------------------------------------
    // Only DISCRETE events were synced (day/night transitions, sleep, death),
    // so each machine advanced its OWN Controller.gameTime between them and they
    // drifted apart (~15 in-game minutes reported). The time authority now
    // broadcasts its clock a few times a minute and non-authorities slave to it,
    // so the in-game time stays aligned within one interval.

    private float _nextClockBroadcast;
    private const float ClockBroadcastInterval = 5f;
    private const int ClockDriftThreshold = 2; // correct once >= 2 CurrentTime units off

    /// <summary>Authority: periodically publish the current clock (unreliable - it repeats).</summary>
    public void OnUpdate()
    {
        if (!_network.IsConnected) return;
        var manager = NetworkManager.Instance;
        if (manager == null || !manager.IsTimeAuthority) return;
        if (Time.unscaledTime < _nextClockBroadcast) return;
        _nextClockBroadcast = Time.unscaledTime + ClockBroadcastInterval;

        var controller = UnityEngine.Object.FindObjectOfType<Controller>();
        if (controller == null) return;

        var afterNight = false;
        try
        {
            var f = typeof(Controller).GetField("isAfterNight",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && f.GetValue(controller) is bool b) afterNight = b;
            else
            {
                var p = typeof(Controller).GetProperty("isAfterNight",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetValue(controller, null) is bool pb) afterNight = pb;
            }
        }
        catch { }

        _network.Send(new ClockSyncPacket
        {
            PlayerId = Math.Max(_network.LocalClientId, 0),
            Day = controller.day,
            GameTime = controller.gameTime,
            CurrentTime = controller.CurrentTime,
            AfterNight = afterNight,
            HasAfterNight = true
        });
    }

    /// <summary>Non-authority: adopt the authority's clock when drift is noticeable.</summary>
    public void OnRemoteClock(int day, float gameTime, int currentTime, bool? isAfterNight = null)
    {
        try
        {
            var manager = NetworkManager.Instance;
            if (manager == null || manager.IsTimeAuthority) return; // authority is the source
            var controller = UnityEngine.Object.FindObjectOfType<Controller>();
            if (controller == null) return;

            // Different day -> a day/night transition is in flight; that path
            // (DayNightUpdate) owns the correction, not this periodic nudge.
            if (day != controller.day) return;
            if (Math.Abs(currentTime - controller.CurrentTime) < ClockDriftThreshold
                && isAfterNight == null) return;

            RemoteApply.Active = true;
            try
            {
                controller.gameTime = gameTime;
                controller.CurrentTime = currentTime;
                if (isAfterNight.HasValue)
                    ApplyAfterNight(controller, isAfterNight.Value);
            }
            finally
            {
                RemoteApply.Active = false;
            }
            if (NetworkLayer.VerboseLogging)
                ModLogger.Msg($"[WorldSync] Clock slaved to authority (day {day}, time {currentTime}, afterNight={isAfterNight})");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WorldSync] Failed to apply clock: {ex.Message}");
        }
    }

    /// <summary>Mirror isAfterNight VFX/freeze (Horde TimeSync full after-night).</summary>
    public static void ApplyAfterNight(Controller controller, bool afterNight)
    {
        if (controller == null) return;
        try
        {
            if (afterNight)
                typeof(Controller).GetMethod("addAfterNightEffect",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(controller, null);
            else
                typeof(Controller).GetMethod("removeAfterNightEffect",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(controller, null);

            var f = typeof(Controller).GetField("isAfterNight",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            f?.SetValue(controller, afterNight);
        }
        catch (Exception ex) { ModLogger.Error($"[WorldSync] AfterNight: {ex.Message}"); }
    }

    /// <summary>
    /// Another player slept: adopt their clock (v0.4). Forward jumps only -
    /// a stale or reordered packet must never rewind local time.
    /// </summary>
    public void OnRemoteGameTime(float gameTime)
    {
        try
        {
            var controller = UnityEngine.Object.FindObjectOfType<Controller>();
            if (controller == null) return;
            if (gameTime <= controller.gameTime) return;

            RemoteApply.Active = true;
            try
            {
                controller.gameTime = gameTime;
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[WorldSync] Adopted remote game time {gameTime:F1} (partner slept)");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WorldSync] Failed to apply game time: {ex.Message}");
        }
    }

    /// <summary>
    /// A partner died and respawned - their clock jumped to the morning
    /// (v0.5, death is a shared consequence). Adopt the clock forward-only;
    /// the day-start chain (refreshTime -> startBeforeDay/startDay) runs from
    /// the new time on the NEXT frame, outside the RemoteApply guard, so on
    /// the host the resulting transitions broadcast through the normal
    /// channels and re-align everyone.
    /// </summary>
    public void OnRemoteDeathTime(int day, float gameTime, int currentTime)
    {
        try
        {
            var controller = UnityEngine.Object.FindObjectOfType<Controller>();
            if (controller == null) return;

            var ahead = day > controller.day
                || (day == controller.day && gameTime > controller.gameTime);
            if (!ahead) return;

            RemoteApply.Active = true;
            try
            {
                controller.gameTime = gameTime;
                controller.CurrentTime = currentTime;
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[WorldSync] Adopted death clock (day {day}, time {currentTime}) - partner died");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WorldSync] Failed to apply death clock: {ex.Message}");
        }
    }

    /// <summary>The other player crafted a workbench upgrade (v0.4). Never downgrades.</summary>
    public void OnRemoteWorkbenchLevel(int level)
    {
        try
        {
            var controller = UnityEngine.Object.FindObjectOfType<Controller>();
            if (controller == null || controller.workbenchLevel >= level) return;
            controller.workbenchLevel = level;
            ModLogger.Msg($"[WorldSync] Workbench level -> {level} (partner upgraded)");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WorldSync] Failed to apply workbench level: {ex.Message}");
        }
    }

    /// <summary>Client side: apply a remote day/night state to the local game.</summary>
    private void ApplyRemote(int dayNumber, bool isNight)
    {
        var manager = NetworkManager.Instance;
        if (manager == null) return;

        manager.DayNumber = dayNumber;
        manager.IsNight = isNight;

        // The time authority drives its own clock - it must not apply its own
        // broadcast back onto itself (host, or elected client on a dedicated
        // server). Non-authorities adopt the remote day/night below.
        if (manager.IsTimeAuthority) return;

        var changedNight = isNight != _prevIsNight;
        var changedDay = dayNumber > 0 && dayNumber != _prevDayNumber;
        _prevIsNight = isNight;
        _prevDayNumber = dayNumber;
        if (!changedNight && !changedDay) return;

        var controllerType = GameTypes.GetType("Controller");
        if (controllerType == null) return;

        var controller = UnityEngine.Object.FindObjectOfType(controllerType);
        if (controller == null) return;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        RemoteApply.Active = true;
        try
        {
            if (changedDay)
            {
                var setDay = controllerType.GetMethod("setDay", flags);
                setDay?.Invoke(controller, new object[] { dayNumber });
                ModLogger.Msg($"[WorldSync] Applied remote day {dayNumber}");
            }

            if (changedNight)
            {
                var method = controllerType.GetMethod(isNight ? "startNightMode" : "endNightMode", flags);
                method?.Invoke(controller, null);
                ModLogger.Msg($"[WorldSync] Applied remote night={isNight}");
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WorldSync] Failed to apply day/night: {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
        }
    }

    /// <summary>Join bulk: absolute workbench level (max-wins on apply).</summary>
    public void CollectSnapshot(List<Packet> into)
    {
        if (into == null) return;
        try
        {
            var controller = UnityEngine.Object.FindObjectOfType<Controller>();
            if (controller == null) return;
            var level = controller.workbenchLevel;
            if (level <= 0) return;
            into.Add(new WorkbenchLevelPacket
            {
                PlayerId = Math.Max(_network.LocalClientId, 0),
                Level = level
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WorldSync] CollectSnapshot: {ex.Message}");
        }
    }
}
