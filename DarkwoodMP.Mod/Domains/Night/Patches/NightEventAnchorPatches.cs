using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Where the host's night events happen. Vanilla runs them in the location the local player
    /// stands in (<c>whereAmI.location</c>) and parents location events under it. With the shared
    /// clock the host can be inside an outside location at night (vanilla never ran events there:
    /// its clock was stopped) or out in the forest while a peer is home. Then the events go to
    /// the hideout a living peer stands in, and never into a location pad.
    /// </summary>
    internal static class NightEventAnchor
    {
        /// <summary>Host stands in a world location: vanilla's own path.</summary>
        internal static bool HostUsesVanilla()
        {
            if (!NetGuard.ConnectedHost(out _))
                return true;
            if (LanNetworkManager.IsApplyingRemoteState)
                return true;
            Player host = Player.Instance;
            if (host == null || host.whereAmI == null)
                return true;
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol != null && ol.playerInOutsideLocation)
                return false;
            return host.whereAmI.location != null;
        }

        internal static bool HostInOutsideLocation()
        {
            var ol = Singleton<OutsideLocations>.Instance;
            return ol != null && ol.playerInOutsideLocation;
        }

        /// <summary>Hideout a living peer stands in, or null.</summary>
        internal static Location PeerHideout()
        {
            var net = ModRuntime.Network;
            if (net == null)
                return null;
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive)
                    continue;
                Location loc = Location.getAtPos(proxy.transform.position);
                if (loc != null && loc.playerBase)
                    return loc;
            }
            return null;
        }
    }

    /// <summary>
    /// Host not in a world location: run vanilla <c>NightScenario.checkFrequencies</c> without its
    /// "player is in a location" gate when a peer is home; skip it inside a location pad.
    /// <see cref="HostCheckFrequenciesPostfix"/> still sends whatever fired.
    /// </summary>
    [HarmonyPatch(typeof(NightScenario), "checkFrequencies")]
    public static class HostNightEventsAwayPatch
    {
        private static bool Prefix(NightScenario __instance)
        {
            if (NightEventAnchor.HostUsesVanilla())
                return true;
            if (NightEventAnchor.PeerHideout() == null)
                // In a pad: vanilla would fire events into it. Out in the forest: vanilla returns.
                return !NightEventAnchor.HostInOutsideLocation();
            CheckFrequencies(__instance);
            return false;
        }

        /// <summary>Vanilla NightScenario.checkFrequencies after its location gate.</summary>
        private static void CheckFrequencies(NightScenario s)
        {
            Controller ctrl = Singleton<Controller>.Instance;
            if (Core.isDay())
                return;
            if (s.currentEvent != null && s.currentEvent.shouldEnd())
            {
                s.currentEvent = null;
                s.setLastTimeCheckedForEvents();
                return;
            }
            if (s.currentEvent != null)
                return;
            if (ctrl.CurrentTime != (int)ctrl.nightTime)
            {
                for (int i = 0; i < s.customEventAndInts.Count; i++)
                {
                    var cei = s.customEventAndInts[i];
                    if (ctrl.currentTimeDifference((int)ctrl.nightTime) == cei.timeToStart
                        && cei.customEvent != null && cei.customEvent.theEvent != null)
                    {
                        cei.customEvent.lastTimeCheckedIfWantToFire = ctrl.CurrentTimeAndDay;
                        cei.customEvent.fire(force: true);
                        return;
                    }
                }
            }
            var list = new List<CustomEvent>();
            for (int j = 0; j < s.customEventAndInts.Count; j++)
            {
                var cei = s.customEventAndInts[j];
                if (cei.customEvent != null && cei.timeToStart == 0 && cei.customEvent.theEvent != null
                    && cei.customEvent.frequencyMet())
                {
                    cei.customEvent.lastTimeCheckedIfWantToFire = ctrl.CurrentTimeAndDay;
                    if (cei.customEvent.requirementsMet())
                        list.Add(cei.customEvent);
                }
            }
            if (list.Count > 0)
                list[Random.Range(0, list.Count)].fire();
        }
    }

    /// <summary>
    /// Host location events (<c>RandomEvent.Type.locationEvent</c>) are parented under the host's
    /// location. Host not in a world location: parent them under a peer's hideout, or drop them
    /// (inside a pad, or nobody home: vanilla would hit a pad or a null location).
    /// </summary>
    [HarmonyPatch(typeof(RandomEvent), "fire")]
    public static class HostLocationEventAwayPatch
    {
        private static readonly System.Action<RandomEvent> RemoveMe =
            AccessTools.MethodDelegate<System.Action<RandomEvent>>(AccessTools.Method(typeof(RandomEvent), "removeMe"));

        private static bool Prefix(RandomEvent __instance, bool checkIfRequirementsMet, bool force)
        {
            if (__instance.type != RandomEvent.Type.locationEvent || NightEventAnchor.HostUsesVanilla())
                return true;

            RandomEvent e = __instance;
            // Vanilla fire's entry gate, unchanged.
            if ((e.disabled && !force)
                || !((((checkIfRequirementsMet && e.requirementsMet()) || !checkIfRequirementsMet)
                      && (!e.startedToday || e.multipleTimesPerDay)) || force))
                return false;

            Location hideout = NightEventAnchor.PeerHideout();
            bool fired = false;
            if (hideout != null)
            {
                for (int i = 0; i < e.gameEvents.Count; i++)
                {
                    if (e.gameEvents[i] == null)
                        continue;
                    Core.AddPrefab(e.gameEvents[i].gameObject, Vector3.zero, Quaternion.identity, hideout.gameObject)
                        .GetComponent<GameEvents>().fire();
                    fired = true;
                }
            }
            e.randomizeStartTime();
            if (fired)
            {
                e.startedToday = true;
                if (e.removeOnFire)
                    RemoveMe(e);
            }
            return false;
        }
    }
}
