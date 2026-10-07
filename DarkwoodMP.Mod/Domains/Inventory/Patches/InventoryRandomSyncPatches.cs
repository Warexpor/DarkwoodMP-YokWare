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
    /// Host-authoritative InventoryRandom (chest / corpse / trader RNG loot).
    /// Vanilla: Awake queues or defers <c>init</c> → <c>randomize</c> (preset
    /// pick + createItem) → <c>spawnItems</c> (chance rolls). Location difficulty
    /// path fills <c>permittedItems</c> then calls <c>spawnItems</c> directly.
    /// NPC traders call <c>randomize(force)</c> on new day after <c>clear</c>.
    /// Independent client rolls diverge container contents (same family as
    /// UniqueItemSpawner / RandomObjectSpawner).
    /// Clients Prefix-skip <c>randomize</c> and <c>spawnItems</c> (set
    /// <c>spawnedItems</c> so init/Location treat them as done). Offline + Host
    /// keep vanilla. No new message id.
    /// Observation:
    /// - Peers already connected after host spawnItems: Broadcast
    ///   <c>ContainerStateSync</c> (76) full snapshot (multi-slot + trader refresh).
    /// - Late open / join-after-roll: client <c>ContainerStateRequest</c> on
    ///   open (<c>ContainerSearchedPatch</c>) → host <c>ContainerStateSync</c>
    ///   (same path as UniqueItemSpawner when spawn was pre-handshake).
    /// Reverse-check: host rolled → client open must match; client must never
    /// roll (skip); late joiner open still requests host snapshot.
    /// </summary>
    [HarmonyPatch(typeof(InventoryRandom), "randomize")]
    public static class InventoryRandomRandomizePatch
    {
        private static bool Prefix(InventoryRandom __instance)
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                __instance.spawnedItems = true;
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.Container,
                        "[InventoryRandom] client skipped randomize (host-authoritative)");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(InventoryRandom), "spawnItems")]
    public static class InventoryRandomSpawnItemsPatch
    {
        private static bool Prefix(InventoryRandom __instance)
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                __instance.spawnedItems = true;
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.Container,
                        "[InventoryRandom] client skipped spawnItems (host-authoritative)");
                return false;
            }
            return true;
        }

        private static void Postfix(InventoryRandom __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            // World generation rolls every container in the world; the world package shared after
            // it carries them. Fanned out, they reached peers with no world yet (hundreds of misses).
            if (!Core.worldGenFinished())
                return;

            Inventory inv = __instance != null
                ? __instance.GetComponent<Inventory>()
                : null;
            // Empty world chests: open-state request is enough. Trader new-day
            // clear+reroll must push even when the roll yields nothing so peers
            // that cleared locally stay aligned (and any stale fill is wiped).
            bool isNpc = __instance != null && __instance.GetComponent<NPC>() != null;
            ContainerStateFanout.Broadcast(net, inv, evenIfEmpty: isNpc);
        }
    }

    /// <summary>Host: push one container's whole contents to every peer (ContainerStateSync).</summary>
    internal static class ContainerStateFanout
    {
        internal static void Broadcast(LanNetworkManager net, Inventory inv, bool evenIfEmpty)
        {
            if (net == null || inv == null || inv.slots == null)
                return;
            // The host's own prologue containers (its private pads) are not the world's.
            if (PersonalPrologue.IsOnProloguePad(inv.transform))
                return;
            var slots = new List<SlotStateEntry>();
            for (int i = 0; i < inv.slots.Count; i++)
            {
                InvSlot s = inv.slots[i];
                if (InvItemClass.isNull(s.invItem))
                    continue;
                InvItemClass it = s.invItem;
                bool isRecipe = it.isRecipe;
                slots.Add(new SlotStateEntry
                {
                    SlotIndex = (byte)i,
                    ItemType = isRecipe ? it.recipeFor : it.type,
                    Amount = it.amount,
                    Durability = it.durability,
                    Ammo = it.ammo,
                    IsRecipe = isRecipe,
                    Upgrades = Sync.InvItemUpgradeWire.CollectNames(it),
                    ShouldBeActive = it.shouldBeActive
                });
            }
            if (slots.Count == 0 && !evenIfEmpty)
                return;

            Vector3 pos = inv.transform.position;
            int entityHash = 0;
            Character ownerChar = inv.GetComponent<Character>();
            if (ownerChar != null)
                entityHash = CharacterTracker.GetStableId(ownerChar);

            if (ModRuntime.VerboseLogging)
                ModLog.Event(LogCat.Container,
                    "[ContainerFanout] host ContainerStateSync slots=" + slots.Count + " at " + pos);

            var sync = new ContainerStateSyncMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                EntityHash = entityHash,
                SlotCount = slots.Count,
                Slots = slots.ToArray()
            };
            // Only to peers playing in the world. A peer on the title waiting for the host's world
            // got every fill of the host's world generation and first location activations (hundreds,
            // none with a container to land in); the world package carries the contents, and
            // opening a container asks the host for its state anyway.
            net.SendToPeersInWorld(NetMessageType.ContainerStateSync,
                w => sync.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }
}
