using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// 4.3: host EventTriggerRequirement.haveItem / locationState must see any living peer.
    /// Journal/haveKey use shared journal (host copy). Skills/health stay per-body.
    /// </summary>
    [HarmonyPatch(typeof(EventTriggerRequirement), "requirementsMet")]
    public static class EventTriggerRequirementAnyPeerPatch
    {
        /// <summary>
        /// Host out in the forest: vanilla's location check has no location of its own to read
        /// ("No location for player found") and answers false; the postfix below answers for the
        /// player the trigger belongs to, or any player's location.
        /// </summary>
        private static bool Prefix(EventTriggerRequirement __instance, ref bool __result)
        {
            if (__instance == null || __instance.type != EventTriggerRequirement.Type.locationState)
                return true;
            if (!EventTriggersAuth.IsMultiplayerConnected())
                return true;
            Player p = Player.Instance;
            if (p == null || p.whereAmI == null || p.whereAmI.bigLocation != null)
                return true;
            __result = false;
            return false;
        }

        private static void Postfix(EventTriggerRequirement __instance, ref bool __result)
        {
            if (__instance == null) return;
            if (!EventTriggersAuth.IsMultiplayerConnected()) return;

            if (__instance.type == EventTriggerRequirement.Type.locationState)
            {
                // The location of the player the trigger belongs to (vanilla: "the player's"). Only
                // a world event with no player behind it asks whether any player's location
                // matches: OR-ing for a player's own trigger let "fewer than N lights on" in an
                // empty house the host stood in pass for a client in the hideout.
                int actor = GeFireActorContext.PeekOr(0);
                var net = ModRuntime.Network;
                if (actor > 0 && net != null && actor == net.LocalPlayerId)
                    return; // vanilla read the local player's location
                if (actor > 0 && net != null && net.Role == NetworkRole.Host)
                {
                    RemotePlayerProxy proxy = net.GetProxy(actor);
                    Location loc = proxy != null ? LocationForProxy(proxy) : null;
                    if (loc != null)
                        __result = __instance.locationState.getBool(loc);
                    return;
                }
                if (__result) return;
                if (AnyPeerLocationMatches(__instance.locationState))
                    __result = true;
                return;
            }

            if (__instance.type == EventTriggerRequirement.Type.haveKey)
            {
                // Shared journal; the host dictionary is enough.
                return;
            }

            if (__instance.type == EventTriggerRequirement.Type.playerState
                && TryActorBodyState(__instance, ref __result))
                return;

            if (__instance.type == EventTriggerRequirement.Type.playerState
                && __instance.playerState == Player.State.haveItem)
            {
                // Vanilla: has ? activeModifier : !activeModifier.
                // The old postfix only ORed peers when the host check already failed,
                // so "must not have X" stayed true while a peer held X.
                string key = ItemTypeKey(__instance);
                if (string.IsNullOrEmpty(key)) return;
                int need = __instance.amount > 0 ? __instance.amount : 1;
                // A dialogue choice is offered to the speaker, who pays it from their own bag:
                // offered on a teammate's bag, "give X" was picked by a speaker without X and the
                // outcome found nothing to take. Choices are built on the speaker's own machine.
                if (DialogueRequirementScope.Active)
                    return;
                bool has = PeerItemPresence.AnyPeerHas(key, need) || JournalHas(key);
                __result = PartyRequirementPolicy.HaveItem(has, __instance.activeModifier);
            }
        }

        /// <summary>
        /// Vanilla reads health, darkness, attackers and skills off <c>Player.Instance</c>. On the
        /// host that is the host's body even when a peer set the trigger off (its proxy walked in,
        /// it used something): check the body of the player the action belongs to.
        /// Attackers: the host's <c>charactersAttackingMe</c> holds everything attacking any
        /// player, so count only the ones after that body.
        /// </summary>
        private static bool TryActorBodyState(EventTriggerRequirement req, ref bool result)
        {
            Player.State st = req.playerState;
            if (st != Player.State.health && st != Player.State.darknessState
                && st != Player.State.enemiesAttacking && st != Player.State.haveSkill)
                return false;
            if (!NetGuard.Host(out LanNetworkManager net))
                return false;
            Player host = Player.Instance;
            if (host == null)
                return false;
            int actor = GeFireActorContext.PeekOr(0);
            RemotePlayerProxy proxy = actor > 0 && actor != net.LocalPlayerId ? net.GetProxy(actor) : null;
            if (proxy == null)
            {
                if (st != Player.State.enemiesAttacking || !HostPlayerIdentity.HostWithRemotes())
                    return false;
                result = PartyRequirementPolicy.Below(CountAttackersOf(host, null), req.amount, req.activeModifier);
                return true;
            }
            switch (st)
            {
                case Player.State.health:
                    result = PartyRequirementPolicy.AtLeast(proxy.RemoteHealthPct / 100f, req.area, req.activeModifier);
                    return true;
                case Player.State.darknessState:
                    result = PartyRequirementPolicy.AtLeast(proxy.RemoteDarknessPct / 100f, req.area, req.activeModifier);
                    return true;
                case Player.State.enemiesAttacking:
                    result = PartyRequirementPolicy.Below(CountAttackersOf(host, proxy.transform), req.amount, req.activeModifier);
                    return true;
                default:
                    if (req.itemType == null)
                    {
                        result = false;
                        return true;
                    }
                    result = proxy.RemoteSkills.Contains(req.itemType.name) ? req.activeModifier : !req.activeModifier;
                    return true;
            }
        }

        /// <summary>Attackers whose target is <paramref name="body"/> (null = the host's own).</summary>
        private static int CountAttackersOf(Player host, Transform body)
        {
            if (host.charactersAttackingMe == null)
                return 0;
            int n = 0;
            for (int i = 0; i < host.charactersAttackingMe.Count; i++)
            {
                Character c = host.charactersAttackingMe[i];
                if (c != null && HostBodySwap.PeerBodyOf(c, nearestWhenNoTarget: false) == body)
                    n++;
            }
            return n;
        }

        private static string ItemTypeKey(EventTriggerRequirement req)
        {
            try
            {
                if (req.itemType == null) return null;
                GameObject go = req.itemType as GameObject;
                if (go == null) return null;
                InvItem inv = go.GetComponent<InvItem>();
                return inv != null ? inv.type : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool JournalHas(string key)
        {
            Journal journal = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.journal : null;
            if (journal == null) return false;
            if (journal.notesDict != null && journal.notesDict.ContainsKey(key)) return true;
            if (journal.keysDict != null && journal.keysDict.ContainsKey(key)) return true;
            if (journal.itemsDict != null && journal.itemsDict.ContainsKey(key)) return true;
            return false;
        }

        private static bool AnyPeerLocationMatches(Location.GetState locState)
        {
            if (locState == null) return false;
            if (Player.Instance != null && Player.Instance.whereAmI != null
                && Player.Instance.whereAmI.bigLocation != null)
            {
                if (locState.getBool(Player.Instance.whereAmI.bigLocation))
                    return true;
            }

            var net = ModRuntime.Network;
            if (net == null) return false;
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                Location loc = LocationForProxy(proxy);
                if (loc != null && locState.getBool(loc))
                    return true;
            }
            return false;
        }

        private static Location LocationForProxy(RemotePlayerProxy proxy)
        {
            WhereAmI where = proxy.GetComponent<WhereAmI>();
            if (where == null)
                where = proxy.GetComponentInChildren<WhereAmI>();
            if (where == null) return null;
            try { where.checkWhereAmI(); }
            catch { /* clone may lack colliders */ }
            return where.bigLocation;
        }
    }

    /// <summary>A dialogue choice's requirements are being checked (they belong to the speaker).</summary>
    internal static class DialogueRequirementScope
    {
        internal static int Depth; // process-scoped: call-scoped, balanced by the patch Finalizers

        internal static bool Active => Depth > 0;
    }

    [HarmonyPatch(typeof(CharacterDialogue.Dialogue.Board.Decision), nameof(CharacterDialogue.Dialogue.Board.Decision.requirementsMet))]
    public static class DialogueDecisionRequirementScopePatch
    {
        private static void Prefix() => DialogueRequirementScope.Depth++;

        private static void Finalizer() => DialogueRequirementScope.Depth--;
    }
}
