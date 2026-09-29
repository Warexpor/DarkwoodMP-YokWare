using DWMPHorde;
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
        /// <summary>
        /// Host: push barricade / door / furniture state so late joiners match
        /// host fortifications. Prefer staggered SendBarricadeDoors/Windows/ItemsTo.
        /// </summary>
        internal void SendBarricadeStateTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;
            int sent = 0;
            sent += SendBarricadeDoorsTo(targetPlayerId, maxSend: 512);
            sent += SendBarricadeWindowsTo(targetPlayerId, maxSend: 512 - sent);
            int itemSent = SendBarricadeItemsTo(targetPlayerId, maxSend: 512 - sent, maxItems: 256);
            sent += itemSent;
            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} barricade/door/item states to player {targetPlayerId} (items={itemSent})"
                : $"[BulkSync] Sent {sent} barricade/door/item states to all clients (items={itemSent})");
        }

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
                        MainHealth = 0,
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
                ModRuntime.LegacyInfo("[BulkSync] Barricade doors → p" + targetPlayerId + ": " + sent);
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
                    MainHealth = -1,
                    DamageAmount = -1
                };
                _net.SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }
            if (sent > 0)
                ModRuntime.LegacyInfo("[BulkSync] Barricade windows → p" + targetPlayerId + ": " + sent);
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

                bool needSync = item.destroyed
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
                    Action = item.destroyed ? BarricadeAction.Destroyed : BarricadeAction.Damaged,
                    Health = item.destroyed ? 0 : item.health,
                    PlayerBarricade = false,
                    MainHealth = -1,
                    DamageAmount = -1
                };
                _net.SendBulkOrAll(NetMessageType.BarricadeEvent, w => msg.Serialize(w), targetPlayerId);
                sent++;
                itemSent++;
            }
            if (itemSent > 0)
                ModRuntime.LegacyInfo("[BulkSync] Barricade items → p" + targetPlayerId + ": " + itemSent);
            return itemSent;
        }

        internal void HandleItemDamageEvent(Vector3 pos, BarricadeEventMessage msg)
        {
            // Prefer XZ matching; client wardrobe Y often drifts after body-push or layer offset.
            Item item = WorldQueryHelper.FindDestructibleItemXz(pos, 25f);

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

            if (msg.Action == BarricadeAction.Destroyed || (msg.Action == BarricadeAction.Damaged && msg.Health <= 0))
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[ItemDmgEvent] destroying " + item.name);
                if (!item.destroyed)
                    item.die();
            }
            else
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[ItemDmgEvent] setting " + item.name + " health to " + msg.Health);
                Traverse.Create(item).Field("health").SetValue(msg.Health);
                if (playFx && item.hitParticlePrefabObject != null)
                {
                    Core.AddPrefab(item.hitParticlePrefabObject, item.transform.position,
                        Quaternion.Euler(90f, 0f, 0f), null);
                }
                if (!string.IsNullOrEmpty(item.hitSound))
                    AudioController.Play(item.hitSound, item.transform.position);
            }
        }
    }
}
