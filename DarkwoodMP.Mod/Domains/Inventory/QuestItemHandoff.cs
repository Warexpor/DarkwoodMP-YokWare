using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using LiteNetLib;
using Newtonsoft.Json;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Story items (vanilla's quest items: Piotrek's car parts, the violin, the brother's hat, the
    /// musician's card) live in one player's bag and are handed in by whoever carries them. A player
    /// who left for good took them along and the quest could not go on without them. When a client
    /// leaves and is not back within <see cref="GraceSec"/>, the host drops its story items on the
    /// ground where it stood (by the host when it stood on a pad or in a dream). The host remembers
    /// each drop for that player, and the record is written with the host's save so it always
    /// matches the saved world. When the player comes back: a drop still lying there is taken off the
    /// ground (the player keeps theirs); one someone took is removed from the player's bag. The
    /// oxygen tanks are every player's (<see cref="OxygenTankParty"/>) and journal items are shared,
    /// so neither is dropped.
    /// </summary>
    internal static class QuestItemHandoff
    {
        private const float GraceSec = 60f;
        private const float DropSpread = 12f;
        private const float FindRadius = 60f;

        internal sealed class Entry
        {
            public string Key;
            public string Type;
            public int Amount;
            public string Guid;
            public float X, Y, Z;
        }

        private sealed class FileData
        {
            public List<Entry> Entries = new List<Entry>();
        }

        internal struct Leaving
        {
            public string Key;
            public Dictionary<string, int> Items;
        }

        private sealed class Leaver
        {
            public string Key;
            public Dictionary<string, int> Items;
            public Vector3 Pos;
            public float DropAt;
        }

        private static readonly List<Leaver> _leavers = new List<Leaver>(); // reset-in: Reset
        private static readonly List<Entry> _entries = new List<Entry>(); // process-scoped: the loaded campaign's records, reloaded when _campaign changes or a save loads
        private static string _campaign; // process-scoped: campaign _entries belong to (null = not loaded)
        private static float _nextTick; // reset-in: Reset

        private static readonly List<KeyValuePair<string, int>> _toRemove = new List<KeyValuePair<string, int>>(); // reset-in: Reset
        private static bool _removeDone; // reset-in: Reset

        public static void Reset()
        {
            _leavers.Clear();
            _nextTick = 0f;
            _toRemove.Clear();
            _removeDone = false;
        }

        /// <summary>A save was loaded: the records go back to what that save was written with.</summary>
        internal static void OnWorldLoaded() => _campaign = null;

        /// <summary>A story item this mod hands off (vanilla quest item, not an oxygen tank).</summary>
        internal static bool IsStoryItem(string type)
        {
            if (string.IsNullOrEmpty(type) || type == OxygenTankParty.Empty || type == OxygenTankParty.Full)
                return false;
            ItemsDatabase db = Singleton<ItemsDatabase>.Instance;
            if (db == null || db.itemsDict == null || !db.itemsDict.TryGetValue(type, out string path) || path == null)
                return false;
            return path.IndexOf("/questItems/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------------------------------------------------------- host

        /// <summary>Host: who is leaving and what story items they carried (before their records are cleared).</summary>
        internal static Leaving Capture(LanNetworkManager net, int playerId)
        {
            var leaving = new Leaving { Key = PlayerKey(net, playerId) };
            Dictionary<string, int> bag = PeerItemPresence.CopyOf(playerId);
            if (bag == null)
                return leaving;
            foreach (var kv in bag)
            {
                if (kv.Value > 0 && IsStoryItem(kv.Key))
                {
                    if (leaving.Items == null)
                        leaving.Items = new Dictionary<string, int>();
                    leaving.Items[kv.Key] = kv.Value;
                }
            }
            return leaving;
        }

        /// <summary>Host: a client left the game; drop its story items unless it is back within the grace.</summary>
        internal static void HostPeerLeft(Leaving leaving, Vector3 lastPos)
        {
            if (string.IsNullOrEmpty(leaving.Key) || leaving.Items == null || leaving.Items.Count == 0)
                return;
            _leavers.RemoveAll(l => l.Key == leaving.Key);
            _leavers.Add(new Leaver
            {
                Key = leaving.Key,
                Items = leaving.Items,
                Pos = lastPos,
                DropAt = Time.unscaledTime + GraceSec
            });
            ModRuntime.LegacyInfo($"[QuestHandoff] {leaving.Key} left with {leaving.Items.Count} story item type(s); dropping in {GraceSec:0}s unless back");
        }

        /// <summary>Host, every frame (also with no peers left: the leaver may have been the only one).</summary>
        internal static void Tick(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Host || _leavers.Count == 0)
                return;
            float now = Time.unscaledTime;
            if (now < _nextTick)
                return;
            _nextTick = now + 1f;
            if (Player.Instance == null || Core.mainMenu || Core.loadingGame || !Core.coreStarted)
                return;
            for (int i = _leavers.Count - 1; i >= 0; i--)
            {
                Leaver l = _leavers[i];
                if (now < l.DropAt)
                    continue;
                if (!TryDropPoint(l.Pos, out Vector3 at))
                {
                    l.DropAt = now + 5f;
                    continue;
                }
                _leavers.RemoveAt(i);
                Drop(net, l, at);
            }
        }

        /// <summary>Where it stood, or by the host when that was a pad or a dream; none while the host is on one too.</summary>
        private static bool TryDropPoint(Vector3 leftAt, out Vector3 at)
        {
            at = leftAt;
            if (leftAt.sqrMagnitude > 1f && !ClientStateBackup.IsDreamPadCoordinate(leftAt))
                return true;
            Vector3 host = Player.Instance.transform.position;
            if (ClientStateBackup.IsDreamPadCoordinate(host) || PersonalPrologue.LocalInPrologue)
                return false;
            at = host;
            return true;
        }

        private static void Drop(LanNetworkManager net, Leaver l, Vector3 at)
        {
            EnsureLoaded();
            int n = 0, total = 0;
            foreach (var kv in l.Items)
                total += kv.Value;
            foreach (var kv in l.Items)
            {
                for (int i = 0; i < kv.Value; i++)
                {
                    float a = total > 1 ? (n * Mathf.PI * 2f / total) : 0f;
                    Vector3 p = at + (total > 1 ? new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * DropSpread : Vector3.zero);
                    p.y = Core.getYPos(p, PosType.items1, randomize: false).y;
                    n++;
                    string guid = SpawnDrop(net, kv.Key, p);
                    if (guid == null)
                        continue;
                    _entries.Add(new Entry { Key = l.Key, Type = kv.Key, Amount = 1, Guid = guid, X = p.x, Y = p.y, Z = p.z });
                    ModRuntime.LegacyInfo($"[QuestHandoff] dropped {kv.Key} of {l.Key} at {p}");
                }
            }
        }

        private static string SpawnDrop(LanNetworkManager net, string type, Vector3 pos)
        {
            InvItem baseItem = Singleton<ItemsDatabase>.Instance.getItem(type, instantiate: false);
            var msg = new DroppedItemSpawnMessage
            {
                Guid = System.Guid.NewGuid().ToString("N"),
                PrefabPath = "Items/DroppedItem",
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotX = 90f,
                ItemType = type,
                Amount = 1,
                Durability = baseItem != null ? baseItem.maxDurability : 0f
            };
            net.PlayerFXHandlers.HandleDroppedItemSpawn(msg);
            if (DroppedItemIdentifier.FindById(msg.Guid) == null)
                return null;
            net.SendDroppedItemSpawn(msg);
            return msg.Guid;
        }

        /// <summary>
        /// Host: a player came back (late-join bulk). Cancel a pending drop, and settle the drops made
        /// for it: back from the ground if still there, else out of its bag.
        /// </summary>
        internal static void HostPlayerReturned(LanNetworkManager net, int playerId)
        {
            string key = PlayerKey(net, playerId);
            if (string.IsNullOrEmpty(key))
                return;
            if (_leavers.RemoveAll(l => l.Key == key) > 0)
                ModRuntime.LegacyInfo($"[QuestHandoff] {key} is back within the grace; nothing dropped");
            EnsureLoaded();
            var take = new Dictionary<string, int>();
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry e = _entries[i];
                if (e.Key != key)
                    continue;
                if (TakeBackFromGround(net, e))
                {
                    _entries.RemoveAt(i);
                    ModRuntime.LegacyInfo($"[QuestHandoff] {e.Type} still on the ground; taken back, {key} keeps theirs");
                    continue;
                }
                take.TryGetValue(e.Type, out int had);
                take[e.Type] = had + e.Amount;
            }
            if (take.Count == 0)
                return;
            var msg = new QuestHandoffMessage { Types = new string[take.Count], Amounts = new int[take.Count] };
            int k = 0;
            foreach (var kv in take)
            {
                msg.Types[k] = kv.Key;
                msg.Amounts[k] = kv.Value;
                k++;
            }
            net.SendToPlayer(playerId, NetMessageType.QuestHandoff, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[QuestHandoff] {key}: {take.Count} story item type(s) were taken by others; removing them from its bag");
        }

        /// <summary>Host: the returning player removed the items; their records are done.</summary>
        internal static void HostAck(LanNetworkManager net, int playerId, QuestHandoffMessage msg)
        {
            string key = PlayerKey(net, playerId);
            if (string.IsNullOrEmpty(key) || msg.Types == null)
                return;
            EnsureLoaded();
            for (int t = 0; t < msg.Types.Length; t++)
                _entries.RemoveAll(e => e.Key == key && e.Type == msg.Types[t]);
            ModRuntime.LegacyInfo($"[QuestHandoff] {key} confirmed the removal");
        }

        private static bool TakeBackFromGround(LanNetworkManager net, Entry e)
        {
            Vector3 at = new Vector3(e.X, e.Y, e.Z);
            DroppedItemIdentifier ident = !string.IsNullOrEmpty(e.Guid) ? DroppedItemIdentifier.FindById(e.Guid) : null;
            if (ident != null && !LanNetworkManager.IsDropGuidConsumed(ident.Id))
            {
                WorldObjectSendNetHandlers.TryConsumeDropGuid(ident.Id);
                UnityEngine.Object.Destroy(ident.gameObject);
                net.SendDroppedItemPickup(new DroppedItemPickupMessage
                {
                    Guid = ident.Id,
                    Mode = DroppedItemPickupMessage.ModeRemove,
                    ItemType = e.Type,
                    Amount = e.Amount
                });
                return true;
            }
            // After a restart the saved drop carries no id: find it by type near where it was left.
            Item found = null;
            float best = FindRadius * FindRadius;
            foreach (Item it in UnityEngine.Object.FindObjectsOfType<Item>())
            {
                if (it == null || !it.isDroppedItem)
                    continue;
                Inventory inv = it.GetComponent<Inventory>();
                InvItemClass held = inv != null && inv.slots != null && inv.slots.Count > 0 ? inv.slots[0].invItem : null;
                if (InvItemClass.isNull(held) || held.type != e.Type)
                    continue;
                DroppedItemIdentifier tag = it.GetComponent<DroppedItemIdentifier>();
                if (tag != null && LanNetworkManager.IsDropGuidConsumed(tag.Id))
                    continue;
                Vector3 d = it.transform.position - at;
                float sq = d.x * d.x + d.z * d.z;
                if (sq < best)
                {
                    best = sq;
                    found = it;
                }
            }
            if (found == null)
                return false;
            Vector3 pos = found.transform.position;
            DroppedItemIdentifier foundTag = found.GetComponent<DroppedItemIdentifier>();
            if (foundTag != null && !string.IsNullOrEmpty(foundTag.Id))
            {
                WorldObjectSendNetHandlers.TryConsumeDropGuid(foundTag.Id);
                net.SendDroppedItemPickup(new DroppedItemPickupMessage
                {
                    Guid = foundTag.Id,
                    Mode = DroppedItemPickupMessage.ModeRemove,
                    ItemType = e.Type,
                    Amount = e.Amount
                });
            }
            else
            {
                WorldPhysicsSyncService.TryConsumeWorldPickup(pos.x, pos.y, pos.z, e.Type);
                net.SendWorldObjectRemoved(new WorldObjectRemovedMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    ObjectName = e.Type,
                    Mode = WorldObjectRemovedMessage.ModeRemove,
                    ItemType = e.Type,
                    Amount = e.Amount
                });
            }
            UnityEngine.Object.Destroy(found.gameObject);
            return true;
        }

        /// <summary>Host: steam id, else the install key a LAN client announced. Null when neither is known.</summary>
        private static string PlayerKey(LanNetworkManager net, int playerId)
        {
            if (net == null || playerId <= 0)
                return null;
            if (net.TryGetSteamIdForPlayer(playerId, out ulong steamId))
                return "s" + steamId;
            if (net.TryGetStableClientKeyForPlayer(playerId, out string key))
                return "k" + key;
            return null;
        }

        // ---------------------------------------------------------------- records on disk

        /// <summary>Host's own world save written: the records now match it.</summary>
        internal static void OnHostSaved()
        {
            if (_campaign == null)
                return;
            string path = FilePath(_campaign);
            if (path == null)
                return;
            try
            {
                if (_entries.Count == 0)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                File.WriteAllText(path, JsonConvert.SerializeObject(new FileData { Entries = _entries }, Formatting.None));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[QuestHandoff] could not write records: " + ex.Message);
            }
        }

        private static void EnsureLoaded()
        {
            string campaign = CoopWorldCopyMeta.TryGetCampaignIdForCurrentProfile() ?? "";
            if (_campaign == campaign)
                return;
            _campaign = campaign;
            _entries.Clear();
            string path = FilePath(campaign);
            if (path == null || !File.Exists(path))
                return;
            try
            {
                FileData data = JsonConvert.DeserializeObject<FileData>(File.ReadAllText(path));
                if (data?.Entries != null)
                    _entries.AddRange(data.Entries);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[QuestHandoff] could not read records: " + ex.Message);
            }
        }

        private static string FilePath(string campaign)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in campaign ?? "")
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }
            return ClientStateBackup.GetProfileBackupDirectory() + "/quest_handoff_" + (sb.Length > 0 ? sb.ToString() : "nocampaign") + ".json";
        }

        // ---------------------------------------------------------------- client

        /// <summary>Client: story items others took while this player was away; out of the bag.</summary>
        internal static void HandleOnClient(LanNetworkManager net, QuestHandoffMessage msg)
        {
            if (net.Role == NetworkRole.Host)
            {
                HostAck(net, net.CurrentReceivePlayerId, msg);
                return;
            }
            if (msg.Types == null || msg.Amounts == null)
                return;
            _toRemove.Clear();
            for (int i = 0; i < msg.Types.Length && i < msg.Amounts.Length; i++)
                _toRemove.Add(new KeyValuePair<string, int>(msg.Types[i], msg.Amounts[i]));
            _removeDone = false;
        }

        /// <summary>Client: a backup restore replaced the bag; take the items out again.</summary>
        internal static void OnBagRestored()
        {
            if (_toRemove.Count > 0)
                _removeDone = false;
        }

        /// <summary>Client, each network tick.</summary>
        internal static void TickClient(LanNetworkManager net)
        {
            if (_removeDone || _toRemove.Count == 0 || net == null || net.Role != NetworkRole.Client)
                return;
            Player p = Player.Instance;
            if (p == null || p.Inventory == null || p.Hotbar == null || !LanNetworkManager.ClientCanApplyWorldBulk()
                || PersonalPrologue.LocalInPrologue || DreamSyncManager.IsDreamActive)
                return;
            _removeDone = true;
            var types = new string[_toRemove.Count];
            var amounts = new int[_toRemove.Count];
            for (int i = 0; i < _toRemove.Count; i++)
            {
                int n = p.Inventory.removeItemAmountFromPlayer(_toRemove[i].Key, _toRemove[i].Value);
                types[i] = _toRemove[i].Key;
                amounts[i] = _toRemove[i].Value;
                ModRuntime.LegacyInfo($"[QuestHandoff] {_toRemove[i].Key} was picked up by another player while away; removed {n}");
            }
            // Written to this player's saved character before the host forgets the records.
            try { net.SendClientStateBackup(); }
            catch { /* non-fatal: the host keeps the records and asks again next time */ }
            var ack = new QuestHandoffMessage { Types = types, Amounts = amounts };
            net.Send(NetMessageType.QuestHandoff, w => ack.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }
}

namespace DWMPHorde.Sync
{
    /// <summary>The host's world save is written: write the story-item records with it.</summary>
    [HarmonyLib.HarmonyPatch(typeof(SaveManager), "Save")]
    public static class QuestItemHandoffSavePatch
    {
        private static void Postfix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
                return;
            if (Patches.NightDeathSavePatch.IsHeld())
                return;
            QuestItemHandoff.OnHostSaved();
        }
    }

    /// <summary>A save is loading: its records are the ones on disk.</summary>
    [HarmonyLib.HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Load))]
    public static class QuestItemHandoffLoadPatch
    {
        private static void Prefix() => QuestItemHandoff.OnWorldLoaded();
    }
}
