using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Late-join / first-pad-enter barricade and destructible-item snapshots.</summary>
    internal sealed partial class BarricadeNetHandlers
    {
        /// <summary>Host join bulk: Door barricade and health state.</summary>
        internal int SendBarricadeDoorsTo(int targetPlayerId, int maxSend = 512)
        {
            if (_net.Role != NetworkRole.Host) return 0;
            int sent = 0;
            Door[] doors = WorldQueryHelper.GetCachedSceneComponents<Door>();
            for (int i = 0; i < doors.Length && sent < maxSend; i++)
            {
                Door door = doors[i];
                if (door == null) continue;
                // Personal pads (the host's prologue, a solo dream) are not the world.
                if (PersonalPads.Contains(door.transform)) continue;
                // The live twin at this position owns the state.
                if (WorldQueryHelper.IsInactiveTwin(door, doors)) continue;

                Vector3 p = door.transform.position;
                Vector3 key = new Vector3(
                    (float)System.Math.Round(p.x, 1),
                    (float)System.Math.Round(p.y, 1),
                    (float)System.Math.Round(p.z, 1));

                if (door.destroyed)
                {
                    var msg = new BarricadeEventMessage
                    {
                        PosX = key.x, PosY = key.y, PosZ = key.z,
                        IsWindow = 0,
                        Action = BarricadeAction.Destroyed,
                        Health = 0,
                        PlayerBarricade = false,
                        // Real health (<= 0): the joiner's door matches the host's, digest included.
                        MainHealth = BarricadeSyncHelpers.DoorMainHealthForWire(door),
                        DamageAmount = -1
                    };
                    _net.SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                    sent++;
                }
                else if (door.barricaded)
                {
                    var msg = new BarricadeEventMessage
                    {
                        PosX = key.x, PosY = key.y, PosZ = key.z,
                        IsWindow = 0,
                        Action = BarricadeAction.Built,
                        Health = door.barricadeHealth,
                        PlayerBarricade = door.playerBarricade,
                        MainHealth = door.health,
                        DamageAmount = -1
                    };
                    _net.SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                    sent++;
                }
                else if (door.baseHealth > 0 && door.health < door.baseHealth)
                {
                    var msg = new BarricadeEventMessage
                    {
                        PosX = key.x, PosY = key.y, PosZ = key.z,
                        IsWindow = 0,
                        Action = BarricadeAction.Damaged,
                        Health = 0,
                        PlayerBarricade = false,
                        MainHealth = door.health,
                        DamageAmount = -1
                    };
                    _net.SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                    sent++;
                }
            }
            if (sent > 0)
                ModRuntime.LegacyInfo($"[BulkSync] Barricade doors → p{targetPlayerId}: {sent}");
            // Mid-session board removals (door.destroyed stays false) — not in scan above.
            sent += BarricadeSyncHelpers.SendRemovedBoardsTo(_net, targetPlayerId, isWindow: 0, maxSend: maxSend - sent);
            return sent;
        }

        /// <summary>Host join bulk: Window barricade state.</summary>
        internal int SendBarricadeWindowsTo(int targetPlayerId, int maxSend = 512)
        {
            if (_net.Role != NetworkRole.Host) return 0;
            int sent = 0;
            Window[] windows = WorldQueryHelper.GetCachedSceneComponents<Window>();
            for (int i = 0; i < windows.Length && sent < maxSend; i++)
            {
                Window window = windows[i];
                if (window == null || !window.barricaded) continue;
                if (PersonalPads.Contains(window.transform)) continue;
                // The live twin at this position owns the state (an inactive twin keeps its
                // authored boards and would re-board a window torn open on the host).
                if (WorldQueryHelper.IsInactiveTwin(window, windows)) continue;

                Vector3 p = window.transform.position;
                Vector3 key = new Vector3(
                    (float)System.Math.Round(p.x, 1),
                    (float)System.Math.Round(p.y, 1),
                    (float)System.Math.Round(p.z, 1));

                var msg = new BarricadeEventMessage
                {
                    PosX = key.x, PosY = key.y, PosZ = key.z,
                    IsWindow = 1,
                    Action = BarricadeAction.Built,
                    Health = window.barricadeHealth,
                    PlayerBarricade = window.playerBarricade,
                    MainHealth = BarricadeEventMessage.NoMainHealth,
                    DamageAmount = -1
                };
                _net.SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }
            if (sent > 0)
                ModRuntime.LegacyInfo($"[BulkSync] Barricade windows → p{targetPlayerId}: {sent}");
            // Soft-reconnect / late-join: torn window boards (scan only sends Built).
            sent += BarricadeSyncHelpers.SendRemovedBoardsTo(_net, targetPlayerId, isWindow: 1, maxSend: maxSend - sent);
            return sent;
        }

        /// <summary>Host join bulk: destructible Item state.</summary>
        internal int SendBarricadeItemsTo(int targetPlayerId, int maxSend = 512, int maxItems = 256)
        {
            if (_net.Role != NetworkRole.Host) return 0;
            int sent = 0;
            int itemSent = 0;
            Item[] items = WorldQueryHelper.GetCachedSceneComponents<Item>();
            for (int i = 0; i < items.Length && itemSent < maxItems && sent < maxSend; i++)
            {
                Item item = items[i];
                if (item == null || item.gameObject == null || !item.gameObject.scene.IsValid())
                    continue;
                if (!item.destructible)
                    continue;
                if (PersonalPads.Contains(item.transform))
                    continue;

                bool repaired = !item.destroyed && _repairedItems.Contains(item);
                bool needSync = repaired || item.destroyed
                    || (item.maxHealth > 0 && item.health < item.maxHealth);
                if (!needSync)
                    continue;

                Vector3 p = item.transform.position;
                Vector3 key = new Vector3(
                    (float)System.Math.Round(p.x, 1),
                    (float)System.Math.Round(p.y, 1),
                    (float)System.Math.Round(p.z, 1));

                var msg = new BarricadeEventMessage
                {
                    PosX = key.x, PosY = key.y, PosZ = key.z,
                    IsWindow = 2,
                    Action = item.destroyed ? BarricadeAction.Destroyed
                        : repaired ? BarricadeAction.Built : BarricadeAction.Damaged,
                    Health = item.destroyed ? 0 : item.health,
                    PlayerBarricade = false,
                    MainHealth = BarricadeEventMessage.NoMainHealth,
                    DamageAmount = -1,
                    ItemName = item.name
                };
                _net.SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                sent++;
                itemSent++;
            }
            if (itemSent > 0)
                ModRuntime.LegacyInfo($"[BulkSync] Barricade items → p{targetPlayerId}: {itemSent}");
            if (itemSent >= maxItems || sent >= maxSend)
                ModLog.Warn(LogCat.Session, $"[BulkSync] Barricade items → p{targetPlayerId}: cap {maxItems} reached, later damaged items not sent");
            return itemSent;
        }

        /// <summary>
        /// The item a damage event is about. At the sender's position first (XZ: this copy's Y
        /// often drifts after a body-push or layer offset). A hit also pushes furniture, and the
        /// sender's copy stops being reported once it breaks, so this copy can be left short of
        /// that position: then the nearest item of that name, within the range a pushed body's
        /// states are resolved by name.
        /// </summary>
        internal static Item ResolveEventItem(Vector3 pos, BarricadeEventMessage msg)
        {
            Item item = WorldQueryHelper.FindDestructibleItemXz(pos, ItemMatchRadius);
            if (item != null && (string.IsNullOrEmpty(msg.ItemName) || item.name == msg.ItemName))
                return item;
            if (string.IsNullOrEmpty(msg.ItemName))
                return item;
            Item named = WorldQueryHelper.FindDestructibleItemByName(
                msg.ItemName, pos, Sync.WorldPhysicsSyncService.ResolvedNameMaxDist);
            return named != null ? named : item;
        }

        internal void HandleItemDamageEvent(Vector3 pos, BarricadeEventMessage msg)
        {
            Item item = ResolveEventItem(pos, msg);

            if (item == null)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[ItemDmgEvent] no item found at {pos}");
                return;
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[ItemDmgEvent] found {item.name} health={item.health} destructible={item.destructible} destroyed={item.destroyed}");
            if (!item.destructible) return;

            bool playFx = msg.DamageAmount >= 0
                && ClientWorldMeleeRedirectHelper.ShouldSuppressApplyFx(2, pos) == false;

            if (msg.Action == BarricadeAction.Built)
            {
                // Join bulk: the host's copy was repaired since the save this world came from.
                if (item.destroyed)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[ItemDmgEvent] repairing {item.name}");
                    Constructible site = item.GetComponent<Constructible>();
                    if (site != null && site.options != null && site.options.Count > 0)
                        site.construct(false, Mathf.Clamp(site.chosenOption, 0, site.options.Count - 1));
                    if (item.destroyed)
                        item.setNormalState();
                }
                if (msg.Health > 0)
                    Traverse.Create(item).Field("health").SetValue(msg.Health);
            }
            else if (msg.Action == BarricadeAction.Destroyed || (msg.Action == BarricadeAction.Damaged && msg.Health <= 0))
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[ItemDmgEvent] destroying {item.name}");
                if (!item.destroyed)
                {
                    // Break where the sender's copy broke, so what it leaves lies in the same place.
                    Vector3 own = item.transform.position;
                    float dx = own.x - pos.x, dz = own.z - pos.z;
                    if (dx * dx + dz * dz > 0.01f)
                    {
                        Sync.WorldPhysicsSyncService.RemoveObjectFromInterpolation(item.gameObject);
                        Vector3 at = new Vector3(pos.x, own.y, pos.z);
                        item.transform.position = at;
                        Rigidbody rb = item.GetComponent<Rigidbody>();
                        if (rb != null) rb.position = at;
                    }
                    // Vanilla's getHit takes the last hit off before die(); die() itself leaves
                    // health alone, so the wreck kept its last hit's health in the health bar.
                    item.health = 0;
                    item.die();
                }
            }
            else
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[ItemDmgEvent] setting {item.name} health to {msg.Health}");
                Traverse.Create(item).Field("health").SetValue(msg.Health);
                if (playFx && item.hitParticlePrefabObject != null)
                {
                    Core.AddPrefab(item.hitParticlePrefabObject, item.transform.position,
                        Quaternion.Euler(90f, 0f, 0f), null);
                }
                if (!string.IsNullOrEmpty(item.hitSound))
                    AudioController.Play(item.hitSound, item.transform.position);
            }
            HealthBarRefresh.IfShowing(item.gameObject);
        }
    }
}
