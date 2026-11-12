using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla <c>Location.Awake</c> picks one of the location's layout sets at random and
    /// destroys the rest — per machine, so the same pad grew different interiors on host
    /// and clients (the village well: a passage for one player, a wall for the other).
    /// The first spawner wins: its pick is shared through the pad-slot assignment and every
    /// later spawn of the same location forces it instead of rolling again.
    /// </summary>
    [HarmonyPatch(typeof(Location), "Awake")]
    public static class LocationSetPickAwakePatch
    {
        private static void Prefix(Location __instance, out List<GameObject> __state)
        {
            __state = null;
            if (__instance == null || __instance.sets == null || __instance.sets.Count == 0)
                return;
            // Neutralize vanilla's random pick; the Postfix applies the forced or a fresh pick.
            __state = new List<GameObject>(__instance.sets);
            __instance.sets.Clear();
        }

        private static void Postfix(Location __instance, List<GameObject> __state)
        {
            if (__instance == null || __state == null || __state.Count == 0)
                return;
            string name = __instance.name;
            string forced = null;
            if (ModRuntime.Network != null && ModRuntime.Network.IsConnected
                && OutsidePadSlots.TryGetForcedPick(__instance.name, out forced)
                && !string.IsNullOrEmpty(forced))
            {
                ApplyForcedPick(__instance, __state, name, forced);
                return;
            }
            ApplyVanillaPick(__instance, __state, name);
        }

        private static void ApplyForcedPick(Location loc, List<GameObject> stashed, string name, string forced)
        {
            var names = new List<string>(stashed.Count);
            for (int i = 0; i < stashed.Count; i++)
                names.Add(stashed[i] != null ? stashed[i].name : null);
            int at = OutsidePadSlotLogic.MatchSetIndex(names, forced);
            if (at >= 0)
            {
                FinishPick(loc, stashed, stashed[at], name, reportIfClient: false);
                return;
            }
            ModLog.Warn(LogCat.World,
                "[PadSlot] forced set pick '" + forced + "' for '" + name + "' not among its sets — vanilla pick instead");
            FinishPick(loc, stashed, stashed[UnityEngine.Random.Range(0, stashed.Count)], name, reportIfClient: true);
        }

        private static void ApplyVanillaPick(Location loc, List<GameObject> stashed, string name)
        {
            GameObject pick = stashed[UnityEngine.Random.Range(0, stashed.Count)];
            FinishPick(loc, stashed, pick, name, reportIfClient: true);
        }

        /// <summary>Vanilla end state: pick removed from sets, the rest destroyed, pick active.</summary>
        private static void FinishPick(Location loc, List<GameObject> stashed, GameObject pick, string name, bool reportIfClient)
        {
            stashed.Remove(pick);
            for (int i = 0; i < stashed.Count; i++)
            {
                GameObject go = stashed[i];
                if (go != null)
                    UnityEngine.Object.Destroy(go);
            }
            loc.sets.AddRange(stashed);
            if (pick != null)
            {
                pick.SetActive(true);
                loc.addToObjects(pick);
                OutsidePadSetPick.RecordPick(name, pick.name);
                if (reportIfClient)
                    OutsidePadSetPick.ReportOwnPick(name, pick.name);
            }
        }
    }

    /// <summary>
    /// The shared pick table both roles read in <c>Location.Awake</c>: the host's recorded
    /// picks and the client's host-assigned (or own reported) picks, plus the client→host
    /// report for a location the client spawned first.
    /// </summary>
    internal static class OutsidePadSetPick
    {
        internal static void RecordPick(string locationName, string pick)
        {
            OutsidePadSlots.NoteOwnPick(locationName, pick);
        }

        internal static void ReportOwnPick(string locationName, string pick)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;
            if (string.IsNullOrEmpty(locationName) || string.IsNullOrEmpty(pick))
                return;
            net.Send(NetMessageType.LocationPadSlotPickReport,
                w => new LocationPadSlotPickReportMessage { LocationName = locationName, Pick = pick }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }
    }
}
