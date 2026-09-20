using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Death-bag session maps + spawn/loot/late-join sync (host-authoritative).
    /// </summary>
    internal sealed class CombatDeathBagNetHandlers
    {
        private readonly LanNetworkManager _net;

        /// <summary>Maps BagId to the local and remote DeathDrop mirrors.</summary>
        private readonly Dictionary<string, DeathDrop> _spawnedDeathBags =
            new Dictionary<string, DeathDrop>(System.StringComparer.Ordinal);

        /// <summary>BagIds already fully looted; block late spawn retransmits and join races.</summary>
        private readonly HashSet<string> _lootedDeathBagIds =
            new HashSet<string>(System.StringComparer.Ordinal);

        internal CombatDeathBagNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        /// <summary>Clear death-bag session maps on disconnect.</summary>
        internal void Reset()
        {
            _spawnedDeathBags.Clear();
            _lootedDeathBagIds.Clear();
        }

        /// <summary>Register a bag under its stable BagId (local drop or remote spawn).</summary>
        internal void RegisterDeathBag(string bagId, DeathDrop drop)
        {
            if (string.IsNullOrEmpty(bagId) || drop == null) return;
            if (_lootedDeathBagIds.Contains(bagId)) return;
            _spawnedDeathBags[bagId] = drop;
        }

        internal void RegisterDeathBagLooted(string bagId)
        {
            if (!string.IsNullOrEmpty(bagId))
                _lootedDeathBagIds.Add(bagId);
        }

        internal bool IsDeathBagLooted(string bagId)
        {
            return !string.IsNullOrEmpty(bagId) && _lootedDeathBagIds.Contains(bagId);
        }

        /// <summary>
        /// Sends all existing DeathDrop bags to a newly connected player so they
        /// see bags that were dropped before they joined.
        /// </summary>
        internal void SyncExistingDeathBags(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;
            DeathDrop[] allBags = UnityEngine.Object.FindObjectsOfType<DeathDrop>(true);
            int sent = 0;
            foreach (DeathDrop bag in allBags)
            {
                if (bag == null) continue;
                Inventory inv = bag.GetComponent<Inventory>();
                if (inv == null) continue;

                Vector3 pos = bag.transform.position;
                var types = new List<string>();
                var amounts = new List<int>();
                var durabilities = new List<float>();
                var ammos = new List<int>();

                if (inv.slots != null)
                {
                    foreach (InvSlot slot in inv.slots)
                    {
                        if (!InvItemClass.isNull(slot.invItem))
                        {
                            types.Add(slot.invItem.type);
                            amounts.Add(slot.invItem.amount);
                            durabilities.Add(slot.invItem.durability);
                            ammos.Add(slot.invItem.ammo);
                        }
                    }
                }

                // Water prefab: component flag, else name heuristic (deathDrop_water).
                var netId = bag.GetComponent<Sync.DeathBagNetworkId>();
                bool inWater = netId != null && netId.InWater;
                if (!inWater)
                {
                    string n = bag.gameObject.name ?? "";
                    inWater = n.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0;
                }

                string bagId = Sync.DeathBagNetworkId.GetOrAssignBagId(bag.gameObject, inWater);
                if (IsDeathBagLooted(bagId))
                    continue;
                // Skip empty bags (already looted, awaiting destroy)
                if (types.Count == 0)
                    continue;

                RegisterDeathBag(bagId, bag);

                var msg = new DeathBagSpawnMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    InWater = inWater,
                    ExpAmount = bag.expAmount,
                    ItemCount = types.Count,
                    ItemTypes = types.ToArray(),
                    ItemAmounts = amounts.ToArray(),
                    ItemDurabilities = durabilities.ToArray(),
                    ItemAmmos = ammos.ToArray(),
                    BagId = bagId
                };

                _net.SendToPlayer(targetPlayerId, NetMessageType.DeathBagSpawn,
                    w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
                sent++;
            }

            if (sent > 0)
                ModRuntime.LegacyInfo($"[Death] Synced {sent} existing death bag(s) to player {targetPlayerId}");
            else
                ModRuntime.LegacyInfo($"[Death] No existing death bags to sync");
        }

        internal void UnregisterDeathBag(string bagId)
        {
            if (!string.IsNullOrEmpty(bagId))
                _spawnedDeathBags.Remove(bagId);
        }

        /// <summary>Lookup by BagId; purges destroyed entries.</summary>
        internal DeathDrop FindDeathBagById(string bagId)
        {
            if (string.IsNullOrEmpty(bagId)) return null;
            if (!_spawnedDeathBags.TryGetValue(bagId, out DeathDrop drop))
            {
                // Component scan (local bags registered late, or dict cleared mid-session).
                foreach (DeathDrop dd in UnityEngine.Object.FindObjectsOfType<DeathDrop>(true))
                {
                    if (dd == null) continue;
                    if (string.Equals(Sync.DeathBagNetworkId.GetBagId(dd.gameObject), bagId, System.StringComparison.Ordinal))
                    {
                        _spawnedDeathBags[bagId] = dd;
                        return dd;
                    }
                }
                return null;
            }
            if (drop == null)
            {
                _spawnedDeathBags.Remove(bagId);
                return null;
            }
            return drop;
        }

        internal void HandleDeathBagSpawn(DeathBagSpawnMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            string bagId = msg.BagId;

            // Dedup: already have this bag (retransmit / late-join race).
            if (!string.IsNullOrEmpty(bagId))
            {
                if (_lootedDeathBagIds.Contains(bagId))
                {
                    ModRuntime.LegacyInfo($"[Death] Skip spawn — bag id={bagId} already looted");
                    return;
                }
                DeathDrop existing = FindDeathBagById(bagId);
                if (existing != null)
                {
                    ModRuntime.LegacyInfo($"[Death] Skip spawn — bag id={bagId} already present");
                    return;
                }
            }
            else
            {
                bagId = System.Guid.NewGuid().ToString("N");
            }

            ModRuntime.LegacyInfo($"[Death] Spawning death bag id={bagId} at {pos} water={msg.InWater}");

            string bagPrefab = msg.InWater ? "Objects/_Unique/deathDrop_water" : "Objects/_Unique/deathDrop";
            GameObject bagGO = Core.AddPrefab(bagPrefab,
                Core.getYPos(pos, PosType.items2),
                Quaternion.Euler(90f, 0f, 0f),
                Core.ItemContainer);
            if (bagGO == null)
            {
                ModRuntime.Log?.LogWarning("[Death] Failed to spawn death bag prefab");
                return;
            }

            DeathDrop deathDrop = bagGO.GetComponent<DeathDrop>();
            if (deathDrop != null)
                deathDrop.expAmount = msg.ExpAmount;

            Sync.DeathBagNetworkId.Ensure(bagGO, bagId, msg.InWater);
            if (deathDrop != null)
                RegisterDeathBag(bagId, deathDrop);

            Inventory bagInv = bagGO.GetComponent<Inventory>();
            if (bagInv != null)
            {
                bagInv.removeWhenEmpty = true;

                if (msg.ItemCount > 0 && msg.ItemTypes != null)
                {
                    bagInv.initSlots();
                    for (int i = 0; i < msg.ItemCount && i < msg.ItemTypes.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(msg.ItemTypes[i]))
                        {
                            bagInv.addSlot();
                            InvSlot slot = bagInv.getNextFreeSlot();
                            if (slot != null)
                            {
                                int amount = msg.ItemAmounts != null && i < msg.ItemAmounts.Length ? msg.ItemAmounts[i] : 1;
                                InvItemClass item = slot.createItem(msg.ItemTypes[i], amount);
                                if (item != null)
                                {
                                    if (msg.ItemDurabilities != null && i < msg.ItemDurabilities.Length)
                                        item.durability = msg.ItemDurabilities[i];
                                    if (msg.ItemAmmos != null && i < msg.ItemAmmos.Length && msg.ItemAmmos[i] > 0)
                                        item.ammo = msg.ItemAmmos[i];
                                }
                            }
                        }
                    }
                    bagInv.checkForActiveSwitches(force: true);
                }
            }
        }

        internal void HandleDeathBagLooted(DeathBagLootedMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            DeathDrop found = null;

            // Primary: stable BagId
            if (!string.IsNullOrEmpty(msg.BagId))
            {
                _lootedDeathBagIds.Add(msg.BagId);
                found = FindDeathBagById(msg.BagId);
                if (found != null)
                    _spawnedDeathBags.Remove(msg.BagId);
            }

            // Fallback: XZ near position (legacy / missing id)
            if (found == null)
            {
                GameObject container = Core.ItemContainer;
                if (container != null)
                {
                    Vector2 posXZ = new Vector2(pos.x, pos.z);
                    foreach (Transform child in container.transform)
                    {
                        if (child == null) continue;
                        DeathDrop dd = child.GetComponent<DeathDrop>();
                        if (dd == null) continue;
                        Vector2 childXZ = new Vector2(child.position.x, child.position.z);
                        if (Vector2.Distance(childXZ, posXZ) < 2f)
                        {
                            found = dd;
                            break;
                        }
                    }
                }
            }

            if (found == null)
            {
                Vector2 posXZ = new Vector2(pos.x, pos.z);
                DeathDrop best = null;
                float bestDist = 2f;
                foreach (DeathDrop dd in UnityEngine.Object.FindObjectsOfType<DeathDrop>(true))
                {
                    if (dd == null) continue;
                    Vector2 ddXZ = new Vector2(dd.transform.position.x, dd.transform.position.z);
                    float d = Vector2.Distance(ddXZ, posXZ);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = dd;
                    }
                }
                found = best;
            }

            if (found != null)
            {
                string id = Sync.DeathBagNetworkId.GetBagId(found.gameObject);
                if (!string.IsNullOrEmpty(id))
                {
                    _lootedDeathBagIds.Add(id);
                    _spawnedDeathBags.Remove(id);
                }
                // Destroy under apply guard so Inventory.hide loot patch does not echo.
                using (new NetworkApplyGuard())
                {
                    UnityEngine.Object.Destroy(found.gameObject);
                }
                ModRuntime.LegacyInfo($"[Death] Destroyed death bag id={id ?? "?"} at {found.transform.position}");
            }
        }
    }
}
