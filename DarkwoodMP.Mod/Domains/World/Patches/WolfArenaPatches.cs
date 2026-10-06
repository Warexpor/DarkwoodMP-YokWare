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

        /// <summary>The arena's step taking the table leg it handed out (vanilla Player function).</summary>
        internal static bool IsTableLegDrain(GameEvent ge)
            => ge != null && ge.type == GameEvent.Type.runFunction
               && ge.Value == "special_drainAllTableLegDurability";

        /// <summary>
        /// Where the local player died, taken when the death starts: vanilla fires the arena reset
        /// a second later, after it has already carried the body home.
        /// </summary>
        internal static bool DeathInArena; // process-scoped: set at each local death, consumed by the reset event

        internal static bool LocalInArena()
        {
            Player p = Player.Instance;
            Location loc = p != null && p.whereAmI != null ? p.whereAmI.bigLocation : null;
            return loc != null && Core.getTrueLocationName(loc.name) == LocationName;
        }

        /// <summary>
        /// Another living player (not <paramref name="exceptPlayerId"/>) is still in the arena: the
        /// fight goes on for them, so one player's death does not reset it.
        /// </summary>
        internal static bool SomeoneElseFighting(LanNetworkManager net, int exceptPlayerId)
        {
            if (net == null)
                return false;
            Player host = Player.Instance;
            if (exceptPlayerId != net.LocalPlayerId && host != null && host.alive && LocalInArena())
                return true;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null || proxy.PlayerId == exceptPlayerId)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive)
                    continue;
                Location at = Location.getAtPos(proxy.transform.position);
                Location big = at != null && at.bigLocation != null ? at.bigLocation : at;
                if (big != null && Core.getTrueLocationName(big.name) == LocationName)
                    return true;
            }
            return false;
        }

        /// <summary>Host: a peer died in the arena while trapped; reset the fight as vanilla does for the player.</summary>
        internal static void HostFireDeathEvent(LanNetworkManager net, string type)
        {
            Flags flags = Singleton<Flags>.Instance;
            Events events = Singleton<Events>.Instance;
            if (flags == null || events == null || !flags.isFlagTrue("wolf_playerInTrap"))
                return;
            if (SomeoneElseFighting(net, net.CurrentReceivePlayerId))
            {
                ModRuntime.LegacyInfo($"[WolfArena] p{net.CurrentReceivePlayerId} died in the arena — others still fighting, no reset");
                return;
            }
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
            bool inArena = WolfArena.DeathInArena;
            WolfArena.DeathInArena = false;
            if (!inArena)
                return false;
            if (net.Role == NetworkRole.Host)
                return !WolfArena.SomeoneElseFighting(net as LanNetworkManager, net.LocalPlayerId);
            var msg = new GameEventsFiredMessage { EventName = type };
            net.Send(NetMessageType.GameEventsFired, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[WolfArena] died in the arena — asking the host to fire {type}");
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), "onDeath")]
    public static class WolfArenaDeathPlacePatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix() => WolfArena.DeathInArena = WolfArena.LocalInArena();
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
        private static bool Prefix() => HostOwnedDespawn.Allowed();
    }

    internal static class HostOwnedDespawn
    {
        /// <summary>Outside a session, or on the host.</summary>
        internal static bool Allowed()
        {
            var net = ModRuntime.Network;
            return net == null || !net.IsConnected || net.Role == NetworkRole.Host;
        }
    }

    /// <summary>
    /// The same exit runs vanilla's trader and porter despawn (<c>Location.checkExitEvents</c>). Both
    /// are host bodies (the host spawns the morning trader and the porter, peers get them through
    /// entity sync): a client walking out of a hideout destroyed its copy while the host's stayed, and
    /// the hideout 5 trader-burn step (<c>despawnTrader</c>) did the same on replay. The host's despawn
    /// reaches clients as an entity despawn.
    /// </summary>
    [HarmonyPatch(typeof(Location), nameof(Location.despawnTrader))]
    public static class TraderDespawnClientPatch
    {
        private static bool Prefix() => HostOwnedDespawn.Allowed();
    }

    [HarmonyPatch(typeof(Location), nameof(Location.despawnPorter))]
    public static class PorterDespawnClientPatch
    {
        private static bool Prefix() => HostOwnedDespawn.Allowed();
    }
}
