using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host is sole authority for morning freeze end (leave hideout / endAfterNight).
    /// Client must not destroy traders or bump time; request host, then TimeSync clears freeze.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "endAfterNight")]
    public static class EndAfterNightCoopPatch
    {
        private static bool Prefix(Controller __instance, bool byKillingTrader)
        {
            if (!NetGuard.Connected(out var net))
                return true;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return true;

            // Client: never run SP world end (trader destroy + CurrentTime++ + refreshTime).
            // Host will end once and TimeSync IsAfterNight=false for everyone.
            if (net.Role == NetworkRole.Client)
            {
                if (__instance != null && __instance.isAfterNight)
                {
                    // This player's morning is over (vanilla fades the end-of-night effect on
                    // leaving); the shared one ends on the host when the hideout is empty.
                    __instance.removeAfterNightEffect();
                    net.Send(NetMessageType.AfterNightEndRequest,
                        w => new AfterNightEndRequestMessage().Serialize(w),
                        DeliveryMethod.ReliableOrdered);
                    ModRuntime.LegacyInfo("[DayNight] client endAfterNight → AfterNightEndRequest (host-auth)");
                }
                return false;
            }

            return true;
        }

        private static void Postfix(Controller __instance, bool byKillingTrader)
        {
            if (!NetGuard.ConnectedHost(out var net))
                return;
            // Also when a peer's leave request ended it (that runs under the apply guard).
            HostAwayMorning.OnEnded(byKillingTrader);
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            MorningHideoutHold.Reset();
            // Immediate — 2s periodic TimeSync left peers frozen after host left hideout.
            net.SendTimeSyncTo(-1);
            ModRuntime.LegacyInfo("[DayNight] host endAfterNight → TimeSync");
        }
    }

    /// <summary>Host day-chain edges: flush TimeSync so peers do not lag up to 2s.</summary>
    [HarmonyPatch(typeof(Controller), "startDay")]
    public static class StartDayTimeSyncPatch
    {
        private static void Postfix()
        {
            FlushHostTime("startDay");
        }

        internal static void FlushHostTime(string reason)
        {
            if (!NetGuard.ConnectedHost(out var net))
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;
            net.SendTimeSyncTo(-1);
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[DayNight] host {reason} → TimeSync");
        }
    }

    [HarmonyPatch(typeof(Controller), "startAfterNight")]
    public static class StartAfterNightTimeSyncPatch
    {
        private static void Postfix()
        {
            StartDayTimeSyncPatch.FlushHostTime("startAfterNight");
        }
    }

    /// <summary>
    /// Host dawn minute: the clients play their own white fade when their clock crosses it
    /// (WorldWeatherTimeNetHandlers.ClientNightCycle); a 2 s periodic sync left it late.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "startBeforeDay")]
    public static class StartBeforeDayTimeSyncPatch
    {
        private static void Postfix() => StartDayTimeSyncPatch.FlushHostTime("startBeforeDay");
    }

    /// <summary>
    /// Host nightfall: tonight's scenario was just set (ScenarioSync went out) and reset. The
    /// clock goes out now, ahead of any event of the night on the same ordered channel, so a
    /// client resets its copy before the first ScenarioEventFired arrives, never after.
    /// </summary>
    [HarmonyPatch(typeof(NightScenario), nameof(NightScenario.setMe))]
    public static class NightStartTimeSyncPatch
    {
        private static void Postfix() => StartDayTimeSyncPatch.FlushHostTime("night start");
    }

    [HarmonyPatch(typeof(Controller), "skipDay")]
    public static class SkipDayTimeSyncPatch
    {
        private static void Postfix()
        {
            StartDayTimeSyncPatch.FlushHostTime("skipDay");
        }
    }

    /// <summary>
    /// Host startBeforeDay is a full-screen fade + invuln. Clients only need a soft
    /// invuln window so they are not shredded while host runs the 5s blackout sequence
    /// before TimeSync morning arrives (no dual fade / dual karma).
    /// </summary>
    [HarmonyPatch(typeof(Controller), "startBeforeDay")]
    public static class StartBeforeDayClientSoftPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return true;

            // Should not run on client (clock suppressed) — belt if something calls it.
            if (Player.Instance != null)
                Player.Instance.invulnerable = true;
            ModRuntime.LegacyInfo("[DayNight] client startBeforeDay suppressed (soft invuln only)");
            return false;
        }
    }

    /// <summary>
    /// <c>player_survivedNight</c> is the host's own (the trader greets by it). Vanilla sets it in
    /// <c>startBeforeDay</c>, which a player who died that night never reaches: its
    /// <c>skipDay</c> jumps past the minute. The shared clock reaches it while the host is down
    /// until morning, so a host that died keeps the flag as it was. Clients set their own at
    /// their dawn (<c>WorldWeatherTimeNetHandlers</c>).
    /// </summary>
    [HarmonyPatch(typeof(Controller), "startBeforeDay")]
    public static class HostSurvivedNightPatch
    {
        private const string Flag = "player_survivedNight";

        private static void Prefix(out bool __state)
        {
            Flags flags = Singleton<Flags>.Instance;
            __state = flags != null && flags.isFlagTrue(Flag);
        }

        private static void Postfix(Controller __instance, bool __state)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host || __instance == null)
                return;
            if (PerPlayerFlagPolicy.SurvivedNight(__instance.day, DeathStateTracker.LocalNightDeathDay))
                return;
            Flags flags = Singleton<Flags>.Instance;
            if (flags == null || flags.isFlagTrue(Flag) == __state)
                return;
            flags.setFlag(Flag, __state);
            ModRuntime.LegacyInfo("[DayNight] host died this night — " + Flag + " left " + __state);
        }
    }

    /// <summary>
    /// Morning trader stays until the hideout is empty. One person walking out
    /// must not despawn the trader for people still inside.
    /// </summary>
    internal static class MorningHideoutHold
    {
        private static readonly HashSet<int> Left = new HashSet<int>();
        private static readonly HashSet<int> SeenOutside = new HashSet<int>();
        private static readonly HashSet<int> Pending = new HashSet<int>();

        public static void Reset()
        {
            Left.Clear();
            SeenOutside.Clear();
            Pending.Clear();
        }

        public static bool HasPending => Pending.Count > 0;

        public static void Forget(int playerId)
        {
            Left.Remove(playerId);
            SeenOutside.Remove(playerId);
            Pending.Remove(playerId);
        }

        public static void NoteLeft(int playerId, bool confirmedOutside)
        {
            if (playerId <= 0)
                return;
            if (!confirmedOutside)
            {
                Pending.Add(playerId);
                return;
            }
            Pending.Remove(playerId);
            Left.Add(playerId);
            SeenOutside.Add(playerId);
        }

        /// <summary>Host position tick: a pending leaver whose body is now outside the hideout.</summary>
        public static void Observe(int playerId, Vector3 pos)
        {
            if (playerId <= 0 || !Pending.Contains(playerId))
                return;
            if (PositionInPlayerBase(pos))
                return;
            NoteLeft(playerId, confirmedOutside: true);
            TryEndIfHideoutEmpty();
        }

        public static bool PositionIsPlayerBase(Vector3 pos) => PositionInPlayerBase(pos);

        public static bool SomeoneStillInside()
        {
            if (HostStillInside())
                return true;

            var net = ModRuntime.Network;
            if (net == null)
                return false;

            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                bool alive = cb == null || cb.alive;
                if (CountsAsInside(proxy.PlayerId, proxy.transform.position, alive))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Last person inside died or disconnected. End the morning if the hideout is empty.
        /// </summary>
        public static void TryEndIfHideoutEmpty()
        {
            // Role only, not IsConnected: the disconnect cleanup calls this after the leaver is
            // already out of the roster, and when it was the last peer the host is no longer
            // "connected" — the morning would then never end for a host already outside.
            if (!NetGuard.Host(out var net))
                return;
            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl == null || !ctrl.isAfterNight)
                return;
            if (SomeoneStillInside())
                return;
            Reset();
            try
            {
                ctrl.endAfterNight();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DayNight] end morning after hideout emptied: " + ex.Message);
            }
        }

        private static bool CountsAsInside(int playerId, Vector3 pos, bool alive)
        {
            if (!alive)
                return false;
            bool inside = PositionInPlayerBase(pos);
            if (!Left.Contains(playerId))
                return inside;
            if (!inside)
            {
                SeenOutside.Add(playerId);
                return false;
            }
            if (SeenOutside.Contains(playerId))
            {
                Left.Remove(playerId);
                SeenOutside.Remove(playerId);
                return true;
            }
            return false;
        }

        public static bool LocalPositionInside()
        {
            Player host = Player.Instance;
            if (host == null)
                return false;
            Vector3 pos = host._transform != null ? host._transform.position : host.transform.position;
            return PositionInPlayerBase(pos);
        }

        private static bool HostStillInside()
        {
            Player host = Player.Instance;
            if (host == null)
                return false;
            var net = ModRuntime.Network;
            int hostId = net != null ? net.LocalPlayerId : 0;
            Vector3 pos = host._transform != null ? host._transform.position : host.transform.position;
            return CountsAsInside(hostId, pos, host.alive);
        }

        private static bool PositionInPlayerBase(Vector3 pos)
        {
            try
            {
                Location loc = Location.getAtPos(pos);
                return loc != null && loc.playerBase;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Host walking out of the hideout only ends the morning when nobody else is still inside.
    /// </summary>
    [HarmonyPatch(typeof(Location), "OnTriggerExit", new[] { typeof(Collider) })]
    public static class MorningHideoutExitPatch
    {
        private static bool Prefix(Location __instance, Collider _collider)
        {
            if (!NetGuard.ConnectedHost(out var net))
                return true;
            if (__instance == null || __instance.isSubLocation || !__instance.playerBase)
                return true;
            if (_collider == null || _collider.GetComponent<Player>() == null)
                return true;
            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl == null || !ctrl.isAfterNight)
                return true;
            if (Singleton<Dreams>.Instance != null && Singleton<Dreams>.Instance.dreaming)
                return true;
            if (!MorningHideoutHold.SomeoneStillInside())
                return true;

            if (!MorningHideoutHold.LocalPositionInside())
                MorningHideoutHold.NoteLeft(net.LocalPlayerId, confirmedOutside: true);
            ModRuntime.LegacyInfo("[DayNight] hideout still occupied — morning stays");
            return false;
        }
    }
}
