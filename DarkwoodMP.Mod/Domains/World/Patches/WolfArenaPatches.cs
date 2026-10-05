using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The Wolfman's arena (med_wolf_01) in co-op. Vanilla keys it all to <c>Player.Instance</c>:
    /// who walked in armed, and whose death resets the fight.
    /// </summary>
    internal static class WolfArena
    {
        internal const string LocationName = "med_wolf_01";
        internal const string DieFighting = "onDieWhenFightingWolfman";
        internal const string DieDefeated = "onDieWhenDefeatedWolfman";

        internal static bool IsDeathEvent(string type)
            => type == DieFighting || type == DieDefeated;

        internal static bool LocalInArena()
        {
            Player p = Player.Instance;
            Location loc = p != null && p.whereAmI != null ? p.whereAmI.bigLocation : null;
            return loc != null && Core.getTrueLocationName(loc.name) == LocationName;
        }

        /// <summary>Host: a peer died in the arena while trapped; reset the fight as vanilla does for the player.</summary>
        internal static void HostFireDeathEvent(LanNetworkManager net, string type)
        {
            Flags flags = Singleton<Flags>.Instance;
            Events events = Singleton<Events>.Instance;
            if (flags == null || events == null || !flags.isFlagTrue("wolf_playerInTrap"))
                return;
            // The flag decides which reset, as on the dying player's side.
            string fire = flags.isFlagTrue("wolf_killed") ? DieDefeated : DieFighting;
            ModRuntime.LegacyInfo($"[WolfArena] p{net.CurrentReceivePlayerId} died in the arena — host fires {fire}");
            // Inside the receive guard the GameEvents fan-out stands down unless the host is
            // applying a peer's action; this is one (actor = the peer that died).
            HostApplyGuard.Begin();
            try
            {
                events.fireWorldEvent(fire);
            }
            finally
            {
                HostApplyGuard.End();
            }
        }
    }

    /// <summary>
    /// Vanilla checks the entering player's bag (<c>getAllItemsInPlayer().Count &gt; 0</c>) to decide
    /// "came armed". The host replayed a peer's entry against its own bag, and every client that
    /// replayed the step checked its own and fired its own branch on top of the host's. The host
    /// now decides, with the bag of the player who walked in; clients take the result it sends.
    /// </summary>
    [HarmonyPatch(typeof(Location), nameof(Location.wolfmanTrapPlayerUnarmedCheck))]
    public static class WolfmanUnarmedCheckPatch
    {
        private static bool Prefix(Location __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return true;
            if (net.Role != NetworkRole.Host)
                return false;
            int actor = GeFireActorContext.PeekOr(0);
            if (actor <= 0 || actor == net.LocalPlayerId)
                return true;

            if (Singleton<Flags>.Instance.isFlagTrue("wolf_releasedDogs"))
                return false;
            Transform events = __instance.transform.Find("Events");
            if (events == null)
                return false;
            if (Singleton<Controller>.Instance.CurrentTime < 900)
            {
                Fire(events, "playerEnteredAndIsDay_med_wolf_01");
                if (PeerItemPresence.PlayerHasAnyItem(actor))
                    Fire(events, "playerEnteredAndIsArmed_med_wolf_01");
                else if (events.Find("checkForObjects_med_wolf_01").GetComponent<CheckForObjects>().areTherePlayerObjectsInsideMe())
                    Fire(events, "playerEnteredAndIsArmed_med_wolf_01");
                else if (Singleton<Flags>.Instance.isFlagTrue("wolf_playerEnteredWhenArmed"))
                    Fire(events, "playerReturnedWhenWasArmed_med_wolf_01");
                else
                    Fire(events, "playerEnteredAndIsUnarmed_med_wolf_01");
            }
            else
            {
                Transform dialogue = events.Find("Dialogue");
                Transform late = dialogue != null ? dialogue.Find("dialogue_tooLateToEnter_med_wolf_01") : null;
                if (late != null)
                    late.GetComponent<GameEvents>().fire();
            }
            return false;
        }

        private static void Fire(Transform events, string name)
        {
            Transform t = events.Find(name);
            GameEvents ge = t != null ? t.GetComponent<GameEvents>() : null;
            if (ge != null)
                ge.fire();
        }
    }

    /// <summary>
    /// Dying while trapped in the arena fires the arena's reset (vanilla Player death). The flag is
    /// shared, so the host dying anywhere else reset a fight a client was still in; and a client's
    /// own fire is a blocked one-shot, so a client dying in the arena left it locked. Only a death
    /// in the arena resets it now, and a client's goes through the host.
    /// </summary>
    [HarmonyPatch(typeof(Events), nameof(Events.fireWorldEvent))]
    public static class WolfArenaDeathEventPatch
    {
        private static bool Prefix(string type)
        {
            if (!WolfArena.IsDeathEvent(type))
                return true;
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return true;
            if (LanNetworkManager.IsApplyingRemoteState || NetworkApplyGuard.IsActive)
                return true;
            if (!WolfArena.LocalInArena())
                return false;
            if (net.Role == NetworkRole.Host)
                return true;
            var msg = new GameEventsFiredMessage { EventName = type };
            net.Send(NetMessageType.GameEventsFired, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[WolfArena] died in the arena — asking the host to fire {type}");
            return false;
        }
    }

    /// <summary>
    /// The Wolfman raids the hideout workbench (vanilla <c>Location.wolfmanStealEvent</c>: ten
    /// random items move to his container, a note is left). Every peer that ran it drew its own
    /// random items from its own copy, and a peer without his container loaded threw after
    /// already taking them. The host draws once and sends both containers.
    /// </summary>
    [HarmonyPatch(typeof(Location), nameof(Location.wolfmanStealEvent))]
    public static class WolfmanStealPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            return net == null || !net.IsConnected || net.Role == NetworkRole.Host;
        }

        private static void Postfix(Location __instance)
        {
            if (!NetGuard.Host(out LanNetworkManager net))
                return;
            Inventory bench = __instance.workbench != null ? __instance.workbench.GetComponent<Inventory>() : null;
            ContainerStateFanout.Broadcast(net, bench, evenIfEmpty: true);
            GameObject stash = Singleton<UniqueObjects>.Instance != null
                ? Singleton<UniqueObjects>.Instance.getObject("wolfsContainerWithStolenItems")
                : null;
            if (stash != null)
                ContainerStateFanout.Broadcast(net, stash.GetComponent<Inventory>(), evenIfEmpty: true);
            ModRuntime.LegacyInfo("[WolfArena] host ran the workbench raid and sent both containers");
        }
    }

    /// <summary>
    /// Leaving any location runs its exit events, and the wolf's despawn clears the shared
    /// "wolf in the player's hideout" flag. A client streaming out of any location cleared it for
    /// the whole party and the host lost the next morning's wolf. The host owns the wolf.
    /// </summary>
    [HarmonyPatch(typeof(Location), nameof(Location.despawnWolf))]
    public static class WolfDespawnClientPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            return net == null || !net.IsConnected || net.Role == NetworkRole.Host;
        }
    }
}
