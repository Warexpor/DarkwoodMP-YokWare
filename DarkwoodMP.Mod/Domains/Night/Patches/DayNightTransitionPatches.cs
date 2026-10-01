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
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return true;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return true;

            // Client: never run SP world end (trader destroy + CurrentTime++ + refreshTime).
            // Host will end once and TimeSync IsAfterNight=false for everyone.
            if (net.Role == NetworkRole.Client)
            {
                if (__instance != null && __instance.isAfterNight)
                {
                    net.Send(NetMessageType.AfterNightEndRequest,
                        w => new AfterNightEndRequestMessage().Serialize(w),
                        DeliveryMethod.ReliableOrdered);
                    ModRuntime.LegacyInfo("[DayNight] client endAfterNight → AfterNightEndRequest (host-auth)");
                }
                return false;
            }

            return true;
        }

        private static void Postfix(Controller __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
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
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;
            net.SendTimeSyncTo(-1);
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[DayNight] host " + reason + " → TimeSync");
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
            var net = ModRuntime.Network as LanNetworkManager;
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

            var net = LanNetworkManager.Instance;
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
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host)
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

        public static bool HostPositionInside()
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
            var net = LanNetworkManager.Instance;
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
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
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

            if (!MorningHideoutHold.HostPositionInside())
                MorningHideoutHold.NoteLeft(net.LocalPlayerId, confirmedOutside: true);
            ModRuntime.LegacyInfo("[DayNight] hideout still occupied — morning stays");
            return false;
        }
    }
}
