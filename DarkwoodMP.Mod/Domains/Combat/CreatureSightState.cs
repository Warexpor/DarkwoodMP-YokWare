using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// A creature's InSightOfPlayer state (a human spider freezes and goes quiet while watched,
    /// and its out-of-sight loop starts when nobody looks). The host decides it for the whole
    /// party (any player's view, HostInSightOfPlayerCheckSightPatch) and sends it in the entity
    /// snapshot; a client's copy, whose own sight check is off, runs the same in-sight /
    /// out-of-sight step when it changes, so its emission and loop match the host's.
    /// </summary>
    internal static class CreatureSightState
    {
        private sealed class Holder { public InSightOfPlayer Value; }

        private static readonly ConditionalWeakTable<Character, Holder> _cache = new ConditionalWeakTable<Character, Holder>(); // process-scoped: per-body component lookup

        private static readonly AccessTools.FieldRef<InSightOfPlayer, float> _timeIn =
            AccessTools.FieldRefAccess<InSightOfPlayer, float>("timeInSightOfPlayer");
        private static readonly AccessTools.FieldRef<InSightOfPlayer, float> _timeOut =
            AccessTools.FieldRefAccess<InSightOfPlayer, float>("timeOutOfSightOfPlayer");
        private static readonly Action<InSightOfPlayer> _doInSight =
            AccessTools.MethodDelegate<Action<InSightOfPlayer>>(AccessTools.Method(typeof(InSightOfPlayer), "doInSight"));
        private static readonly Action<InSightOfPlayer> _doOutOfSight =
            AccessTools.MethodDelegate<Action<InSightOfPlayer>>(AccessTools.Method(typeof(InSightOfPlayer), "doOutOfSight"));

        internal static InSightOfPlayer Of(Character c)
        {
            if (c == null)
                return null;
            if (!_cache.TryGetValue(c, out Holder h))
            {
                h = new Holder { Value = c.GetComponent<InSightOfPlayer>() };
                _cache.Add(c, h);
            }
            return h.Value;
        }

        /// <summary>Host: the in-sight step has run (and not its out-of-sight one since).</summary>
        internal static bool HostSeen(Character c)
        {
            InSightOfPlayer isp = Of(c);
            return isp != null && isp.firedInSight;
        }

        /// <summary>Client: run the copy's step the host's body just ran.</summary>
        internal static void Apply(Character c, bool seen)
        {
            InSightOfPlayer isp = Of(c);
            if (isp == null || isp.firedInSight == seen)
                return;
            // The host already waited out the step's duration; the copy runs it now.
            float past = Time.time - 1000f;
            isp.inSightOfPlayer = seen;
            if (seen)
            {
                _timeIn(isp) = past;
                _doInSight(isp);
            }
            else
            {
                _timeOut(isp) = past;
                _doOutOfSight(isp);
            }
        }
    }
}
