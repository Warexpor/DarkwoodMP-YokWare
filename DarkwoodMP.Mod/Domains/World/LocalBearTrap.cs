using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The trap that caught the local player. Vanilla <c>Trigger.checkCollision</c> sets
    /// <c>inBearTrap</c> on the player right before <c>OnAfterTrigger</c>, so the trap is known
    /// exactly: no position guessing. Used for the player's <c>TrapNetId</c> and for the co-op
    /// rescue (a teammate removing that trap frees the player through vanilla's own exit).
    /// </summary>
    internal static class LocalBearTrap
    {
        private static GameObject _trap;
        private static Vector3 _pos;
        private static bool _noted;

        internal static void Reset()
        {
            _trap = null;
            _pos = Vector3.zero;
            _noted = false;
        }

        private static bool Trapped(Player p)
            => p != null && (p.inBearTrap || p.startingInBearTrap || p.endingInBearTrap);

        internal static void Note(Trigger trap)
        {
            _trap = trap.gameObject;
            _pos = trap.transform.position;
            _noted = true;
        }

        /// <summary>The occupied trap, or null once the player is out.</summary>
        internal static GameObject Current
        {
            get
            {
                if (_noted && !Trapped(Player.Instance))
                    Reset();
                return _trap;
            }
        }

        internal static int CurrentId(bool hostMint)
        {
            GameObject go = Current;
            if (go == null)
                return 0;
            return hostMint ? TrapNetworkId.GetOrMintHost(go) : TrapNetworkId.GetId(go);
        }

        /// <summary>
        /// A trap object was removed here (teammate looted, disarmed or destroyed it). If it is the
        /// one holding the local player, end the hold the vanilla way: removing the immobilise
        /// effect runs <c>CharBase.stopImmobilise</c>, which plays <c>BeartrapStop</c> and restores
        /// actions, animations and the held item (clearing the flags alone left the player frozen).
        /// </summary>
        internal static void ReleaseIfRemoved(GameObject removed, Vector3 removedPos)
        {
            Player p = Player.Instance;
            if (!Trapped(p) || !_noted)
                return;
            GameObject held = _trap;
            bool same = removed != null && held != null && removed == held;
            if (!same)
            {
                // Destroyed objects compare by where the trap was: the very same spot.
                float dx = removedPos.x - _pos.x, dz = removedPos.z - _pos.z;
                same = dx * dx + dz * dz < 1f;
            }
            if (!same)
                return;
            _trap = null;
            _noted = false;
            if (p.effects != null && p.effects.hasEffectType(CharacterEffectType.immobilise))
                p.effects.deleteThisTypeOfEffect(CharacterEffectType.immobilise);
            else if (p.inBearTrap && !p.endingInBearTrap)
                p.endingInBearTrap = true;
            Logging.ModLog.Event(Logging.LogCat.World, $"[Trap] freed from removed trap at {removedPos}");
        }
    }

    /// <summary>Records the trap that just caught the local player (see <see cref="LocalBearTrap"/>).</summary>
    [HarmonyPatch(typeof(Trigger), "OnAfterTrigger", typeof(Collider), typeof(bool))]
    public static class LocalBearTrapNotePatch
    {
        private static void Prefix(Trigger __instance, Collider other)
        {
            if (__instance == null || other == null || !__instance.isBearTrap)
                return;
            Player p = Player.Instance;
            if (p != null && p.startingInBearTrap && other.GetComponent<Player>() == p)
                LocalBearTrap.Note(__instance);
        }
    }

    /// <summary>
    /// Traps on peers: a client never springs a trap on its copies of host characters (the host's
    /// trap does, and the result arrives by trap and entity state; a local spring would spend the
    /// host's trap early with no catch there). With friendly fire off, a trap placed by another
    /// player does not catch the local player (vanilla: your own trap still does).
    /// </summary>
    [HarmonyPatch(typeof(Trigger), nameof(Trigger.checkCollision))]
    public static class TrapCollisionCoopPatch
    {
        private static bool Prefix(Trigger __instance, Collider other)
        {
            if (__instance == null || other == null)
                return true;
            if (!NetGuard.Connected(out LanNetworkManager net))
                return true;
            Player local = other.GetComponent<Player>();
            if (local == null)
                return net.Role == NetworkRole.Host || other.GetComponent<Character>() == null;
            if (SessionSettings.FriendlyFireEnabled)
                return true;
            TrapPlacer placer = __instance.GetComponent<TrapPlacer>();
            return placer == null || placer.PlayerId <= 0 || placer.PlayerId == net.LocalPlayerId;
        }
    }

    /// <summary>Who placed a player-set trap on this peer (from <c>ItemSpawn.PlacerId</c>).</summary>
    public sealed class TrapPlacer : MonoBehaviour
    {
        public int PlayerId;
    }
}
