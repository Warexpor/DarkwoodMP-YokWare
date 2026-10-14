using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A player who dies at night lies where they fell until morning, and that body is what the
    /// others loot (<see cref="Players.PlayerCorpses"/> hides the bag under it). Vanilla's bag is
    /// a day-death rule: it drops only when the backpack holds more than one item, takes a random
    /// half of the backpack and never the hotbar, because the player gets up at home with the
    /// rest. A night-dead player gets up nowhere, so a body often had no bag at all (few items,
    /// or all of them on the hotbar) and could not be looted. For such a death the bag always
    /// drops and holds everything the player carried, backpack and hotbar.
    /// </summary>
    internal static class NightBodyBag
    {
        internal static bool Dropped; // process-scoped: one death sequence, cleared at its end (NightBodyAlwaysDropsPatch)

        /// <summary>This machine's player is down for the night with a body on the ground.</summary>
        internal static bool Applies()
            => NetGuard.Connected(out _)
               && DeathStateTracker.LocalNightDeath && DeathStateTracker.LocalDiedInNightWindow
               && !FinalDreamsceneManager.IsActive;

        internal static int Carried(Player p)
            => (p.Inventory != null ? p.Inventory.getAllItems().Count : 0)
               + (p.Hotbar != null ? p.Hotbar.getAllItems().Count : 0);

        /// <summary>Everything vanilla left on the player goes into the bag it just dropped.</summary>
        internal static void MoveRest(Player p, DeathDrop drop)
        {
            Inventory bag = drop != null ? drop.GetComponent<Inventory>() : null;
            if (bag == null || !p.dropInventoryOnDeath)
                return;
            int moved = Move(p.Inventory, bag) + Move(p.Hotbar, bag);
            if (moved == 0)
                return;
            // The hand is empty now, as vanilla's own death sequence leaves it a moment later.
            p.Hotbar.selectSlot(0, noiseless: true, force: true);
            bag.checkForActiveSwitches(force: true);
            ModRuntime.LegacyInfo($"[Death] night body: {moved} more item(s) into the bag (the whole kit)");
        }

        private static int Move(Inventory from, Inventory bag)
        {
            if (from == null)
                return 0;
            from.deactivateSwitches("", all: true);
            List<InvItemClass> items = from.getAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                InvItemClass it = items[i];
                bag.addSlot();
                bag.getNextFreeSlot().createItem(it, it.amount);
                it.slot.removeItem();
            }
            return items.Count;
        }
    }

    /// <summary>Runs before <see cref="DeathBagDropSyncPatch"/> sends the bag, so the others get the full one.</summary>
    [HarmonyPatch(typeof(Player), "dropBody", new[] { typeof(Vector3) })]
    public static class NightBodyDropAllPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Postfix(Player __instance, Vector3 destPos)
        {
            if (__instance == null || !NightBodyBag.Applies())
                return;
            NightBodyBag.Dropped = true;
            DeathDrop drop = DeathBagDropSyncPatch.FindDeathDropAt(destPos);
            // Hidden under the body before its first frame is drawn.
            Players.PlayerCorpses.ClaimNewBag(drop);
            NightBodyBag.MoveRest(__instance, drop);
        }
    }

    /// <summary>
    /// The step of vanilla's death sequence right after its bag drop (its one caller is
    /// <c>Player.onDeath</c>): when vanilla dropped nothing (one backpack item or none), the
    /// night body still gets its bag.
    /// </summary>
    [HarmonyPatch(typeof(SkillsMenu), "onPlayerDeath")]
    public static class NightBodyAlwaysDropsPatch
    {
        private static void Prefix()
        {
            bool dropped = NightBodyBag.Dropped;
            NightBodyBag.Dropped = false;
            Player p = Player.Instance;
            if (dropped || p == null || !NightBodyBag.Applies() || NightBodyBag.Carried(p) == 0)
                return;
            p.dropBody(DeathStateTracker.LocalDeathPosition);
            NightBodyBag.Dropped = false;
        }

        /// <summary>
        /// The bag is down and the life spent; the rest of this step of vanilla's sequence (halved
        /// saturation) is done by the next frame. That character is what a rejoin before morning gets.
        /// </summary>
        private static void Postfix()
        {
            if (!(ModRuntime.Network is LanNetworkManager net) || net.Role != NetworkRole.Client || !net.IsConnected
                || !DeathStateTracker.LocalNightDeath || FinalDreamsceneManager.IsActive)
                return;
            net.StartCoroutine(SendNextFrame(net));
        }

        private static System.Collections.IEnumerator SendNextFrame(LanNetworkManager net)
        {
            yield return null;
            if (DeathStateTracker.LocalNightDeath)
                net.SaveHandlers.SendNightDeathCharacter();
        }
    }
}
