using DWMPHorde.Harmony;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla <c>Core.canSee</c> without its allocation: it ran <c>Physics.RaycastAll</c> (a new
    /// hit array) on every sight pass of every creature and player check. Same ray (origin,
    /// direction, <c>trueDistance</c> length, layer mask 16809985, global trigger setting) and the
    /// same answer: clear when nothing is hit, or when the one hit is <c>t2</c> itself. Two slots
    /// are enough to tell those apart (a full buffer means two or more hits: blocked).
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(Core), nameof(Core.canSee), new[] { typeof(Transform), typeof(Transform) })]
    public static class CoreCanSeeNonAllocPatch
    {
        private const int SightMask = 16809985;
        private static readonly RaycastHit[] Hits = new RaycastHit[2]; // process-scoped: main-thread scratch

        private static bool Prefix(Transform t1, Transform t2, ref bool __result)
        {
            float maxDistance = Core.trueDistance(t1, t2);
            Vector3 from = t1.position;
            int n = Physics.RaycastNonAlloc(from, t2.position - from, Hits, maxDistance, SightMask);
            __result = n == 0 || n == 1 && Hits[0].collider.transform == t2;
            return false;
        }
    }
}
