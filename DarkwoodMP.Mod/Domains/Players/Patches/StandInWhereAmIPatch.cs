using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A player's stand-in (and a player's corpse) is a clone of the local player and keeps its
    /// <c>WhereAmI</c>, which checks every 1.5 s. Vanilla's check is the local player's: a new
    /// location is discovered (journal popup and sound, journal page, map piece), the hideout
    /// flags, the location and biome music and the biome message are set. Run for a stand-in,
    /// the other player's arrival at a place discovered it on THIS machine first, the page was
    /// sent back, and the player who really walked in found it already known: no popup, no
    /// sound for them, both for whoever was watching from afar. The flags and the music
    /// followed the other player as well.
    /// <para>
    /// A stand-in's check only finds the location it stands in (<c>location</c>, read by the
    /// mod as <c>bigLocation</c> for event-trigger requirements). Everything else is the local
    /// player's.
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(WhereAmI), nameof(WhereAmI.checkWhereAmI))]
    public static class StandInWhereAmIPatch
    {
        private const float LocationRadius = 300f; // vanilla checkWhereAmI
        private const int LocationMask = 73402368; // vanilla checkWhereAmI
        private static readonly Collider[] Around = new Collider[64];

        private static bool Prefix(WhereAmI __instance)
        {
            if (__instance == null || __instance.GetComponent<Player>() != null)
                return true;
            if (!Core.worldGenFinished())
                return false;

            Vector3 pos = __instance.transform.position;
            int n = Physics.OverlapSphereNonAlloc(pos, LocationRadius, Around, LocationMask);
            Location best = null;
            float bestSq = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Collider col = Around[i];
                if (col == null) continue;
                Location loc = col.GetComponent<Location>();
                if (loc == null) continue;
                float dSq = (loc.transform.position - pos).sqrMagnitude;
                if (dSq >= bestSq) continue;
                bestSq = dSq;
                best = loc;
            }
            __instance.location = best;
            __instance.atLocation = best != null;
            return false;
        }
    }
}
