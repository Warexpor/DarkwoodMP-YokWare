using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Syncs lootable container contents (chests, wardrobes, corpses).
///
/// Instead of tracking every possible take/put path through the inventory UI,
/// the full remaining content is broadcast when a container is CLOSED. The
/// remote side reconciles its matching container: counts per item type are
/// compared and adjusted via the game's own removeItemAmount/addItemType.
/// Containers are matched by name + position like doors.
/// </summary>
public class ContainerSync
{
    private readonly NetworkLayer _network;

    private Type _inventoryType;
    private Type _invItemType;
    private FieldInfo _slotsField;        // Inventory.slots : List<InvSlot>
    private FieldInfo _openField;         // Inventory.open : bool
    private FieldInfo _slotItemField;     // InvSlot.invItem : InvItemClass
    private FieldInfo _itemTypeField;     // InvItemClass.type
    private FieldInfo _itemAmountField;   // InvItemClass.amount
    private MethodInfo _isNullMethod;     // InvItemClass.isNull()
    private MethodInfo _removeMethod;     // Inventory.removeItemAmount(string, int)
    private MethodInfo _addMethod;        // Inventory.addItemType(string, int)

    // Container inventories the local player currently has open.
    // Usually one, but the workbench exposes two at once (storage + upgrades).
    private readonly List<Component> _openInventories = new();

    // Remote container contents that could not be applied yet (container not
    // loaded / not found). Kept and re-applied when the container shows up -
    // dropping these was the main source of container desync and item dupes.
    private readonly Dictionary<string, string> _pendingRemote = new();
    private const float PendingRetryInterval = 5f;
    private float _lastPendingRetry;

    // Death bags this session (join bulk + looted-id set — Horde DeathBag* design)
    private readonly Dictionary<string, DeathDropRecord> _sessionDeathDrops = new();
    private readonly HashSet<string> _lootedDeathDropIds = new();

    private class DeathDropRecord
    {
        public string Prefab;
        public string Uid;
        public Vector3 Pos;
        public string Payload;
    }

    public ContainerSync(NetworkLayer network)
    {
        _network = network;
    }

    // ------------------------------------------------------------------
    // Send side (driven by Container_Patch)
    // ------------------------------------------------------------------

    /// <summary>A container inventory was opened (or cleared) by the local player.</summary>
    public void OnContainerOpened(Component inventory)
    {
        // The game also calls setOpenedItemInventory(null) to clear the
        // reference - that IS the close signal, snapshot before losing it
        if (inventory == null)
        {
            OnContainerClosing();
            return;
        }

        if (_openInventories.Contains(inventory)) return;

        // Apply any remote content we received while this container was not
        // reachable BEFORE the player starts looting it
        TryApplyPending(inventory);

        _openInventories.Add(inventory);
        var containerId = GameIds.ForComponent(inventory);
        ModLogger.Msg($"[ContainerSync] Opened: {containerId}");

        // Horde-style open snapshot: ask time-authority for absolute contents
        // so mid-session / pending-miss cases converge before loot starts.
        RequestContainerSnapshot(containerId);
    }

    /// <summary>Client → authority: please send absolute inventory for this container.</summary>
    public void RequestContainerSnapshot(string containerId)
    {
        if (!_network.IsConnected || string.IsNullOrEmpty(containerId)) return;
        _network.SendReliable(new ContainerRequestPacket
        {
            PlayerId = Math.Max(_network.LocalClientId, 0),
            ContainerId = containerId
        });
    }

    /// <summary>
    /// Authority: answer a containerreq with absolute contents to the requester only.
    /// </summary>
    public void RespondContainerSnapshot(int requesterId, string containerId)
    {
        if (!_network.IsConnected || string.IsNullOrEmpty(containerId)) return;
        if (!ResolveApi()) return;

        var inventory = FindContainer(containerId);
        if (inventory == null)
        {
            // Fall back to last pending absolute we know (session close history)
            if (_pendingRemote.TryGetValue(containerId, out var known))
            {
                _network.SendToPlayer(requesterId, new ContainerStatePacket
                {
                    PlayerId = Math.Max(_network.LocalClientId, 0),
                    ContainerId = containerId,
                    PayloadCsv = known
                }, reliable: true);
            }
            return;
        }

        try
        {
            var counts = CountContent(inventory);
            var payload = new StringBuilder();
            foreach (var kvp in counts)
            {
                if (payload.Length > 0) payload.Append(';');
                payload.Append(kvp.Key).Append(',').Append(kvp.Value);
            }
            _network.SendToPlayer(requesterId, new ContainerStatePacket
            {
                PlayerId = Math.Max(_network.LocalClientId, 0),
                ContainerId = containerId,
                PayloadCsv = payload.ToString()
            }, reliable: true);
            ModLogger.Msg($"[ContainerSync] Sent open-snapshot '{containerId}' -> player {requesterId}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ContainerSync] RespondContainerSnapshot: {ex.Message}");
        }
    }

    /// <summary>
    /// Called every frame: the game closes containers through several paths
    /// (ESC, walking away, TAB) - watching the inventory's `open` field
    /// catches all of them.
    /// </summary>
    public void OnUpdate()
    {
        // Retry pending remote contents - containers load/unload as players move
        if (_pendingRemote.Count > 0 && Time.time - _lastPendingRetry > PendingRetryInterval)
        {
            _lastPendingRetry = Time.time;
            RetryPending();
        }

        if (_openInventories.Count == 0) return;
        if (!ResolveApi() || _openField == null) return;

        for (var i = _openInventories.Count - 1; i >= 0; i--)
        {
            var inventory = _openInventories[i];
            try
            {
                if (inventory == null)
                {
                    _openInventories.RemoveAt(i);
                    continue;
                }
                if (_openField.GetValue(inventory) is bool open && !open)
                {
                    _openInventories.RemoveAt(i);
                    Flush(inventory);
                }
            }
            catch
            {
                _openInventories.RemoveAt(i); // container despawned
            }
        }
    }

    /// <summary>All open containers are closing - broadcast their remaining content.</summary>
    public void OnContainerClosing()
    {
        for (var i = _openInventories.Count - 1; i >= 0; i--)
        {
            var inventory = _openInventories[i];
            _openInventories.RemoveAt(i);
            if (inventory != null)
                Flush(inventory);
        }
    }

    /// <summary>
    /// Broadcast an inventory that is NOT driven by the open/close watcher
    /// (trader stock after a trade, saw output after converting). Same wire
    /// format and reconciliation as regular containers (v0.4).
    /// </summary>
    public void FlushExternal(Component inventory)
    {
        // Trade absolute model C owns trader inventories — do not dual-flush.
        if (TradeInventorySync.IsTraderInventory(inventory))
            return;
        if (inventory != null)
            Flush(inventory);
    }

    private void Flush(Component inventory)
    {
        if (!_network.IsConnected) return;
        if (!ResolveApi()) return;

        try
        {
            var counts = CountContent(inventory);
            var payload = new StringBuilder();
            foreach (var kvp in counts)
            {
                if (payload.Length > 0) payload.Append(';');
                payload.Append(kvp.Key).Append(',').Append(kvp.Value);
            }

            var containerId = GameIds.ForComponent(inventory);
            var payloadStr = payload.ToString();
            ModLogger.Msg($"[ContainerSync] Closing '{containerId}' -> [{payloadStr}]");
            // Keep last absolute for open-snapshot / join
            _pendingRemote[containerId] = payloadStr;
            _network.SendReliable(new ContainerStatePacket
            {
                PlayerId = Math.Max(_network.LocalClientId, 0),
                ContainerId = containerId,
                PayloadCsv = payloadStr
            });

            // Death bag emptied → mark looted so join bulk does not resurrect it
            if (inventory.gameObject.name.IndexOf("deathDrop", StringComparison.OrdinalIgnoreCase) >= 0
                && counts.Count == 0)
            {
                var uid = ExtractDeathDropUid(inventory.gameObject.name);
                if (!string.IsNullOrEmpty(uid))
                    MarkDeathDropLooted(uid);
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ContainerSync] Failed to snapshot container: {ex.Message}");
        }
    }

    private static string ExtractDeathDropUid(string goName)
    {
        var hash = goName.LastIndexOf('#');
        if (hash < 0 || hash >= goName.Length - 1) return null;
        return goName.Substring(hash + 1);
    }

    // ------------------------------------------------------------------
    // Receive side
    // ------------------------------------------------------------------

    public void OnRemoteContainer(string containerId, string payload)
    {
        ModLogger.Msg($"[ContainerSync] Remote content for '{containerId}': [{payload}]");

        // Always remember the latest remote state first - if anything below
        // fails the data survives and is retried later
        _pendingRemote[containerId] = payload;

        if (!ResolveApi()) return;

        var inventory = FindContainer(containerId);
        if (inventory == null)
        {
            ModLogger.Msg($"[ContainerSync] Container '{containerId}' not loaded - kept pending");
            return;
        }

        if (ApplyPayload(inventory, containerId, payload))
            _pendingRemote.Remove(containerId);
    }

    /// <summary>Reconcile a local container inventory to the remote content list.</summary>
    private bool ApplyPayload(Component inventory, string containerId, string payload)
    {
        // Parse "type,amount;type,amount;..."
        var remote = new Dictionary<string, int>();
        if (!string.IsNullOrEmpty(payload))
        {
            foreach (var entry in payload.Split(';'))
            {
                var comma = entry.LastIndexOf(',');
                if (comma <= 0) continue;
                var type = entry.Substring(0, comma);
                if (!int.TryParse(entry.Substring(comma + 1), out var amount)) continue;
                remote.TryGetValue(type, out var existing);
                remote[type] = existing + amount;
            }
        }

        try
        {
            var local = CountContent(inventory);

            RemoteApply.Active = true;
            try
            {
                // Remove what the other player took
                foreach (var kvp in local)
                {
                    remote.TryGetValue(kvp.Key, out var remoteCount);
                    if (kvp.Value > remoteCount)
                        _removeMethod.Invoke(inventory, new object[] { kvp.Key, kvp.Value - remoteCount });
                }
                // Add what the other player put in
                foreach (var kvp in remote)
                {
                    local.TryGetValue(kvp.Key, out var localCount);
                    if (kvp.Value > localCount)
                        _addMethod.Invoke(inventory, new object[] { kvp.Key, kvp.Value - localCount });
                }
            }
            finally
            {
                RemoteApply.Active = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ContainerSync] Failed to reconcile '{containerId}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Apply pending remote content that matches a just-opened container.</summary>
    private void TryApplyPending(Component inventory)
    {
        if (_pendingRemote.Count == 0 || !ResolveApi()) return;

        var id = GameIds.ForComponent(inventory);
        if (_pendingRemote.TryGetValue(id, out var exact))
        {
            if (ApplyPayload(inventory, id, exact))
            {
                _pendingRemote.Remove(id);
                ModLogger.Msg($"[ContainerSync] Applied pending content to '{id}' on open");
            }
            return;
        }

        // Fuzzy match: same object name, encoded position within 10m (worlds
        // can differ slightly even with a shared seed)
        var invName = inventory.gameObject.name;
        var pos = inventory.transform.position;
        foreach (var key in _pendingRemote.Keys)
        {
            if (!TryParseId(key, out var name, out var x, out var z)) continue;
            if (name != invName) continue;
            var dx = pos.x - x;
            var dz = pos.z - z;
            if (dx * dx + dz * dz > 10f * 10f) continue;

            if (ApplyPayload(inventory, key, _pendingRemote[key]))
            {
                _pendingRemote.Remove(key);
                ModLogger.Msg($"[ContainerSync] Applied pending content to '{key}' on open (fuzzy match)");
            }
            return;
        }
    }

    /// <summary>Periodic retry for containers that were unloaded when their update arrived.</summary>
    private void RetryPending()
    {
        if (!ResolveApi()) return;

        List<string> applied = null;
        foreach (var kvp in _pendingRemote)
        {
            var inventory = FindContainer(kvp.Key);
            if (inventory == null) continue;
            // Never fight the player: skip containers currently open locally
            if (_openInventories.Contains(inventory)) continue;
            if (ApplyPayload(inventory, kvp.Key, kvp.Value))
                (applied ??= new List<string>()).Add(kvp.Key);
        }
        if (applied != null)
        {
            foreach (var id in applied)
                _pendingRemote.Remove(id);
            ModLogger.Msg($"[ContainerSync] Applied {applied.Count} pending container update(s)");
        }
    }

    private static bool TryParseId(string id, out string name, out float x, out float z)
    {
        name = null;
        x = z = 0f;
        var at = id.LastIndexOf('@');
        if (at <= 0) return false;
        name = id.Substring(0, at);
        var coords = id.Substring(at + 1).Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return coords.Length == 2
            && float.TryParse(coords[0], System.Globalization.NumberStyles.Float, inv, out x)
            && float.TryParse(coords[1], System.Globalization.NumberStyles.Float, inv, out z);
    }

    public void Reset()
    {
        _openInventories.Clear();
        _pendingRemote.Clear();
        _sessionDeathDrops.Clear();
        _lootedDeathDropIds.Clear();
    }

    /// <summary>Join bulk: last-known absolute contents for containers closed this session.</summary>
    public void CollectContainerSnapshot(List<Packets.Packet> into)
    {
        if (into == null || _pendingRemote.Count == 0) return;
        var localId = Math.Max(_network.LocalClientId, 0);
        foreach (var kvp in _pendingRemote)
        {
            // Skip death-drop ids handled by CollectDeathDropSnapshot
            if (kvp.Key.IndexOf("deathDrop", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            into.Add(new ContainerStatePacket
            {
                PlayerId = localId,
                ContainerId = kvp.Key,
                PayloadCsv = kvp.Value
            });
        }
    }

    public void RecordDeathDrop(string prefab, string uid, Vector3 pos, string payload)
    {
        if (string.IsNullOrEmpty(uid) || _lootedDeathDropIds.Contains(uid)) return;
        _sessionDeathDrops[uid] = new DeathDropRecord
        {
            Prefab = prefab ?? "deathDrop",
            Uid = uid,
            Pos = pos,
            Payload = payload ?? ""
        };
    }

    public void MarkDeathDropLooted(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return;
        _lootedDeathDropIds.Add(uid);
        _sessionDeathDrops.Remove(uid);
    }

    public bool IsDeathDropLooted(string uid) =>
        !string.IsNullOrEmpty(uid) && _lootedDeathDropIds.Contains(uid);

    /// <summary>Join bulk: still-lootable death bags (Horde SyncExistingDeathBags).</summary>
    public void CollectDeathDropSnapshot(List<Packets.Packet> into)
    {
        if (into == null || _sessionDeathDrops.Count == 0) return;
        var localId = Math.Max(_network.LocalClientId, 0);
        foreach (var kvp in _sessionDeathDrops)
        {
            if (_lootedDeathDropIds.Contains(kvp.Key)) continue;
            var r = kvp.Value;
            into.Add(new DeathDropSpawnPacket
            {
                PlayerId = localId,
                Prefab = r.Prefab,
                Uid = r.Uid,
                X = r.Pos.x, Y = r.Pos.y, Z = r.Pos.z,
                PayloadCsv = r.Payload
            });
        }
    }

    // ------------------------------------------------------------------
    // Death loot drop (v0.6 audit find)
    // ------------------------------------------------------------------
    // On death the game (Player.dropBody, in the onDeath coroutine) spawns a
    // "deathDrop" bag, moves a random subset of the dead player's items into
    // it and leaves it in the world to be recovered. That bag is a plain
    // Inventory container spawned via Core.AddPrefab - so the DYING machine
    // has it, but the partner never saw it. DeathDrop_Patch broadcasts the
    // spawn + the ACTUAL resulting contents (no re-roll); this recreates an
    // identical bag on the partner. From then on it is an ordinary synced
    // container: the name+position matching above reconciles looting, so
    // whoever grabs the items empties it on both machines - no duplication.

    /// <summary>Read a container's current contents (for the death-drop broadcast).</summary>
    public Dictionary<string, int> ReadContents(Component inventory)
    {
        if (inventory == null || !ResolveApi()) return new Dictionary<string, int>();
        return CountContent(inventory);
    }

    /// <summary>Partner replays a death-drop bag: spawn the prefab + fill it once.</summary>
    public void OnRemoteDeathDrop(string prefabName, string uid, Vector3 pos, string payload)
    {
        try
        {
            if (!ResolveApi()) return;
            if (IsDeathDropLooted(uid))
            {
                ModLogger.Msg($"[ContainerSync] Death drop #{uid} already looted - skipped");
                return;
            }
            RecordDeathDrop(prefabName, uid, pos, payload);

            // Exact idempotency: the token is globally unique, so a bag whose
            // name already carries it is this same drop (re-sent / re-scanned).
            // This must NOT be spatial - all 3 death bags share one position.
            var suffix = "#" + uid;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(_inventoryType))
            {
                if (obj is Component existing && existing.gameObject.name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    ModLogger.Msg($"[ContainerSync] Death drop #{uid} already present - skipped");
                    return;
                }
            }

            RemoteApply.Active = true;
            try
            {
                var go = Core.AddPrefab("Objects/_Unique/" + prefabName, pos, Quaternion.identity, null, false);
                if (go == null)
                {
                    ModLogger.Warning($"[ContainerSync] Could not spawn death drop prefab '{prefabName}'");
                    return;
                }
                // Match the dying machine's rename so both sides derive the same
                // unique container id (name@x,z) for this bag.
                go.name += suffix;
                Core.addToSaveable(go, true, true);
                if (WorldGrid.Instance != null)
                    WorldGrid.Instance.registerToNode(go, (WorldGrid.Cullable.Type)0);

                var inventory = go.GetComponent(_inventoryType) as Component;
                if (inventory != null)
                {
                    // initSlots(true) so addItemType has slots to place into - the
                    // game's own dropBody calls initSlots(true) here (verified IL);
                    // passing no arg threw TargetParameterCountException and left
                    // the bag empty.
                    _inventoryType.GetMethod("initSlots",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.Invoke(inventory, new object[] { true });
                    FillContainer(inventory, payload);
                }
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[ContainerSync] Spawned partner death drop '{prefabName}' #{uid} at {pos:F1} [{payload}]");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ContainerSync] Failed to spawn death drop: {ex.Message}");
        }
    }

    private void FillContainer(Component inventory, string payload)
    {
        if (string.IsNullOrEmpty(payload) || _addMethod == null) return;
        foreach (var entry in payload.Split(';'))
        {
            var comma = entry.LastIndexOf(',');
            if (comma <= 0) continue;
            var type = entry.Substring(0, comma);
            if (!int.TryParse(entry.Substring(comma + 1), out var amount) || amount <= 0) continue;
            try { _addMethod.Invoke(inventory, new object[] { type, amount }); }
            catch (Exception ex) { ModLogger.Error($"[ContainerSync] death-drop fill '{type}': {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------------

    private Dictionary<string, int> CountContent(Component inventory)
    {
        var counts = new Dictionary<string, int>();
        if (_slotsField?.GetValue(inventory) is not System.Collections.IEnumerable slots)
            return counts;

        foreach (var slot in slots)
        {
            if (slot == null) continue;
            var invItem = _slotItemField?.GetValue(slot);
            if (invItem == null) continue;
            if (_isNullMethod?.Invoke(invItem, null) is bool isNull && isNull) continue;

            var type = _itemTypeField?.GetValue(invItem) as string;
            if (string.IsNullOrEmpty(type)) continue;
            var amount = _itemAmountField?.GetValue(invItem) is int a ? Math.Max(a, 1) : 1;

            counts.TryGetValue(type, out var existing);
            counts[type] = existing + amount;
        }
        return counts;
    }

    private Component FindContainer(string containerId)
    {
        Component exact = null;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_inventoryType))
        {
            if (obj is not Component inv) continue;
            if (GameIds.ForComponent(inv) == containerId) { exact = inv; break; }
        }
        if (exact != null) return exact;

        // Name + nearest fallback (worlds can differ slightly)
        if (!TryParseId(containerId, out var name, out var x, out var z))
            return null;

        Component best = null;
        var bestDist = 10f * 10f;
        Component npcMatch = null;
        var npcMatches = 0;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_inventoryType))
        {
            if (obj is not Component inv) continue;
            if (inv.gameObject.name != name) continue;
            var p = inv.transform.position;
            var dx = p.x - x;
            var dz = p.z - z;
            var d = dx * dx + dz * dz;
            if (d < bestDist)
            {
                bestDist = d;
                best = inv;
            }
            // NPC inventories (traders) wander beyond the 10m fuzzy radius -
            // remember them for the unambiguous-name fallback (v0.5)
            if (inv.GetComponent<NPC>() != null)
            {
                npcMatch = inv;
                npcMatches++;
            }
        }
        if (best == null && npcMatches == 1)
            return npcMatch;
        return best;
    }

    private bool ResolveApi()
    {
        if (_inventoryType != null)
            return _slotsField != null && _slotItemField != null && _removeMethod != null && _addMethod != null;

        _inventoryType = GameTypes.GetType("Inventory");
        _invItemType = GameTypes.GetType("InvItemClass");
        var invSlotType = GameTypes.GetType("InvSlot");
        if (_inventoryType == null || _invItemType == null || invSlotType == null) return false;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _slotsField = _inventoryType.GetField("slots", flags);
        _openField = _inventoryType.GetField("open", flags);
        _slotItemField = invSlotType.GetField("invItem", flags);
        _itemTypeField = _invItemType.GetField("type", flags);
        _itemAmountField = _invItemType.GetField("amount", flags);
        _isNullMethod = _invItemType.GetMethod("isNull", flags, null, Type.EmptyTypes, null);

        foreach (var m in _inventoryType.GetMethods(flags))
        {
            var ps = m.GetParameters();
            if (m.Name == "removeItemAmount" && ps.Length == 2) _removeMethod = m;
            if (m.Name == "addItemType" && ps.Length == 2) _addMethod = m;
        }

        return _slotsField != null && _slotItemField != null && _removeMethod != null && _addMethod != null;
    }
}
