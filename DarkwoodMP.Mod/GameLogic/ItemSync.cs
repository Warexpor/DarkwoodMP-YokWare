using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;
using DarkwoodMP;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Applies remote item drops and pickups to the local world.
///
/// Identity: dropped world items all share the generic name "DroppedItem", so
/// name matching cannot identify them. Every synced drop therefore gets a
/// session-unique sync id (carried in PickupStatePacket.PickupId) tracked on
/// both machines - removals resolve by exact id first, name+position second.
///
/// Placement: the game's drop routine animates the item away from the local
/// player, overriding any position set right after spawning. Remote-spawned
/// drops are pinned to their target position for a short time instead.
/// </summary>
public class ItemSync
{
    private const float RemoveMatchRadius = 4f;
    private const float PinDuration = 3f;

    private readonly NetworkLayer _network;

    private Type _itemType;
    private Type _playerType;
    private MethodInfo _spawnMethod; // Player.spawnDroppedInvItemm(bool, string, int)
    private MethodInfo _switchTriggerStateMethod; // Item.switchTriggerState()

    // Remote disarm-pickups (beartraps, mushrooms) whose world item was not
    // loaded here yet - retried until the object appears
    private readonly Dictionary<string, float> _pendingDisarms = new();
    // Remote trap firings (beartrap snapped shut) awaiting their local object
    private readonly Dictionary<string, float> _pendingTrapFires = new();
    private const float DisarmRetryInterval = 5f;
    private float _lastDisarmRetry;

    // Sync-id registry (both our own drops and remote-spawned copies)
    private readonly Dictionary<int, string> _syncIdByInstance = new();
    private readonly Dictionary<string, Component> _itemBySyncId = new();
    private int _nextDropId = 1;

    // Remote drops being held at their target position while the game's
    // drop animation would otherwise displace them
    private readonly List<PinnedItem> _pinned = new();

    // Remote throws that arrived before their spawn packet (UDP reordering)
    private readonly Dictionary<string, PendingThrow> _pendingThrows = new();

    // Freshly spawned remote projectiles waiting to be armed. ThrownItem.init
    // runs one frame after Awake and calls onCollide ITSELF when onGround is
    // already true - arming in the same frame made the landing effects run
    // twice (two gas clouds). Arm only after init had its frame.
    private readonly List<PendingArm> _pendingArms = new();
    // Long enough for ThrownItem.init's one-frame deferral, short enough not
    // to visibly lag the flight behind the thrower's (latency already does)
    private const float ArmDelay = 0.1f;

    // Armed remote projectiles in flight - force-landed at their target if
    // the game's own landing hasn't fired well past the fly time
    private readonly List<FlightWatch> _inFlight = new();

    // Throws seen this session (local + remote) - used to replay still-burning
    // flares to late joiners
    private readonly Dictionary<string, ThrowRecord> _throwRecords = new();
    private readonly Dictionary<string, string> _typeBySyncId = new();
    private const float ThrowSnapshotMaxAge = 120f;

    public ItemSync(NetworkLayer network)
    {
        _network = network;
    }

    // ------------------------------------------------------------------
    // Send-side support (called from the patches)
    // ------------------------------------------------------------------

    /// <summary>Register an item the local player just dropped; returns its sync id.</summary>
    public string RegisterLocalDrop(Transform spawned, string itemType = null)
    {
        if (spawned == null) return "";
        // '.' separator, NOT ':' — syncId is embedded in multi-field payloads;
        // a colon inside the id used to shift every later field and break float
        // parse (molotov/gasBomb never replayed on the partner).
        var syncId = $"{Math.Max(_network.LocalClientId, 0)}.{_nextDropId++}";
        // Key on the GameObject id: lookups may come from any component of the
        // item (Transform here, the Item component in the pickup patch) and
        // each component has its own instance id
        _syncIdByInstance[spawned.gameObject.GetInstanceID()] = syncId;
        _itemBySyncId[syncId] = spawned;
        if (!string.IsNullOrEmpty(itemType))
            _typeBySyncId[syncId] = itemType;
        return syncId;
    }

    /// <summary>Remember a throw so late joiners can receive still-burning flares.</summary>
    public void RecordThrow(string syncId, Component item, string itemType, Vector3 target)
    {
        if (string.IsNullOrEmpty(syncId) || item == null) return;
        if (!string.IsNullOrEmpty(itemType))
            _typeBySyncId[syncId] = itemType;
        _throwRecords[syncId] = new ThrowRecord
        {
            Item = item,
            ItemType = itemType ?? "",
            Target = target,
            Time = Time.time
        };
    }

    /// <summary>Sync id of a tracked item, or "" if this item was never synced.</summary>
    public string GetSyncId(Component item)
    {
        return item != null && _syncIdByInstance.TryGetValue(item.gameObject.GetInstanceID(), out var id) ? id : "";
    }

    /// <summary>Called every frame: hold remote drops at their target position.</summary>
    public void OnUpdate()
    {
        if (_pendingArms.Count > 0)
            ArmPendingThrows();

        if (_inFlight.Count > 0)
            WatchFlights();

        if (_pendingThrows.Count > 0)
        {
            List<string> expired = null;
            foreach (var kvp in _pendingThrows)
                if (Time.time > kvp.Value.Expires)
                    (expired ??= new List<string>()).Add(kvp.Key);
            if (expired != null)
                foreach (var id in expired)
                    _pendingThrows.Remove(id);
        }

        if ((_pendingDisarms.Count > 0 || _pendingTrapFires.Count > 0)
            && Time.time - _lastDisarmRetry > DisarmRetryInterval && ResolveApi())
        {
            _lastDisarmRetry = Time.time;
            // ONE scene scan shared by all pending lookups - per-entry
            // FindObjectsOfType scans were a stutter source
            var snapshot = UnityEngine.Object.FindObjectsOfType(_itemType);

            if (_pendingDisarms.Count > 0)
            {
                List<string> applied = null;
                foreach (var kvp in _pendingDisarms)
                {
                    if (TryApplyDisarm(kvp.Key, snapshot))
                        (applied ??= new List<string>()).Add(kvp.Key);
                }
                if (applied != null)
                    foreach (var id in applied)
                        _pendingDisarms.Remove(id);
            }

            if (_pendingTrapFires.Count > 0)
            {
                List<string> applied = null;
                foreach (var kvp in _pendingTrapFires)
                {
                    if (TryApplyTrapFire(kvp.Key, snapshot))
                        (applied ??= new List<string>()).Add(kvp.Key);
                }
                if (applied != null)
                    foreach (var id in applied)
                        _pendingTrapFires.Remove(id);
            }
        }

        if (_pinned.Count == 0) return;
        var now = Time.time;

        for (var i = _pinned.Count - 1; i >= 0; i--)
        {
            var pin = _pinned[i];
            if (pin.Item == null || now > pin.Until)
            {
                _pinned.RemoveAt(i);
                continue;
            }

            pin.Item.position = pin.Position;
            var body = pin.Item.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.position = pin.Position;
            }
        }
    }

    public void Reset()
    {
        _syncIdByInstance.Clear();
        _itemBySyncId.Clear();
        _pinned.Clear();
        _pendingDisarms.Clear();
        _pendingTrapFires.Clear();
        _pendingThrows.Clear();
        _pendingArms.Clear();
        _inFlight.Clear();
        _throwRecords.Clear();
        _typeBySyncId.Clear();
        _nextDropId = 1;
    }

    // ------------------------------------------------------------------
    // Thrown items (flares, molotovs, ...)
    // ------------------------------------------------------------------

    /// <summary>
    /// A remote player threw the drop with this sync id. Placing the item near
    /// its landing point with `thrown=true` makes the game's own ThrownItem
    /// logic land it there next FixedUpdate (verified: checkIfWantToLand fires
    /// onCollide within 10 units of landTarget), including landing effects -
    /// a thrown flare ignites, a lit molotov explodes (Explodes.onActivate).
    /// </summary>
    public void OnRemoteThrow(string syncId, Vector3 landTarget, int senderId)
    {
        if (string.IsNullOrEmpty(syncId)) return;

        if (_itemBySyncId.TryGetValue(syncId, out var item) && item != null)
        {
            ApplyThrow(syncId, item, landTarget, senderId);
            return;
        }

        // Spawn packet not processed yet - buffer briefly
        _pendingThrows[syncId] = new PendingThrow { Target = landTarget, SenderId = senderId, Expires = Time.time + 10f };
    }

    private void ApplyThrow(string syncId, Component item, Vector3 landTarget, int senderId)
    {
        try
        {
            var thrownItem = item.GetComponent<ThrownItem>();
            if (thrownItem == null)
            {
                // Not a throwable prefab - just move it to the landing spot
                item.transform.position = landTarget;
                RepinAt(item, landTarget);
                return;
            }

            // Stop pinning; the game takes over from here
            for (var i = _pinned.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_pinned[i].Item, item.transform))
                    _pinned.RemoveAt(i);

            // Attribute the throw to the thrower's clone (aggro/knockback source)
            var thrower = NetworkManager.Instance?.GetRemotePlayer(senderId);
            if (thrower != null)
                thrownItem.objectThatSpawnedMe = thrower.transform;

            item.transform.position = landTarget;
            thrownItem.landTarget = landTarget;
            thrownItem.onGround = false;
            thrownItem.thrown = true;

            _typeBySyncId.TryGetValue(syncId, out var itemType);
            RecordThrow(syncId, item, itemType, landTarget);
            ModLogger.Msg($"[ItemSync] Remote throw landing at {landTarget:F1}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemSync] Failed to apply remote throw: {ex.Message}");
        }
    }

    /// <summary>
    /// v0.7 self-contained throw replay: spawn the REAL armed projectile prefab
    /// (InvItem.item, carrying ThrownItem + Explodes + prefabToSpawnOnLand) and
    /// let the game's own landing run - a gas bomb bursts into its (ignitable)
    /// cloud, a lit molotov explodes in fire. The old path spawned the DROPPED
    /// pickup prefab, which has none of that: partners just saw a dead
    /// pickupable appear on the floor.
    /// </summary>
    public void OnRemoteThrow2(string syncId, string itemType, bool flaming, Vector3 target, int senderId, Vector3 origin)
    {
        if (string.IsNullOrEmpty(syncId) || string.IsNullOrEmpty(itemType)) return;

        // Duplicate/re-sent event?
        if (_itemBySyncId.TryGetValue(syncId, out var existing) && existing != null) return;

        RemoteApply.Active = true;
        try
        {
            var db = UnityEngine.Object.FindObjectOfType(typeof(ItemsDatabase)) as ItemsDatabase;
            var invItem = db != null ? db.getItem(itemType, false) : null;
            var prefab = invItem != null ? invItem.item : null;

            GameObject go = null;
            if (prefab != null)
                go = Core.AddPrefab(prefab, origin, Quaternion.Euler(90f, 0f, 0f), null);

            if (go == null || go.GetComponent<ThrownItem>() == null)
            {
                // No armed prefab for this type - old behavior (spawn the drop,
                // then the plain landing) is still better than nothing.
                if (go != null) UnityEngine.Object.Destroy(go);
                ModLogger.Msg($"[ItemSync] '{itemType}' has no ThrownItem prefab - drop fallback");
                SpawnDrop(new PickupStatePacket
                {
                    PickupId = syncId,
                    ItemType = itemType,
                    ItemName = itemType,
                    Amount = 1,
                    X = target.x, Y = target.y, Z = target.z,
                    Spawned = true
                });
                OnRemoteThrow(syncId, target, senderId);
                return;
            }

            RemoteApply.MarkRemoteSpawned(go);

            _syncIdByInstance[go.GetInstanceID()] = syncId;
            _itemBySyncId[syncId] = go.transform;
            _typeBySyncId[syncId] = itemType;
            RecordThrow(syncId, go.transform, itemType, target);

            _pendingArms.Add(new PendingArm
            {
                Item = go.transform,
                Target = target,
                Origin = origin,
                Flaming = flaming,
                SenderId = senderId,
                ArmAt = Time.time + ArmDelay
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemSync] OnRemoteThrow2 '{itemType}': {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
        }
    }

    private void ArmPendingThrows()
    {
        for (var i = _pendingArms.Count - 1; i >= 0; i--)
        {
            var pa = _pendingArms[i];
            if (Time.time < pa.ArmAt) continue;
            _pendingArms.RemoveAt(i);
            if (pa.Item == null) continue;

            try
            {
                var thrownItem = pa.Item.GetComponent<ThrownItem>();
                if (thrownItem == null) continue;

                // Attribute to the thrower's clone (aggro/knockback source)
                var thrower = NetworkManager.Instance?.GetRemotePlayer(pa.SenderId);
                if (thrower != null)
                    thrownItem.objectThatSpawnedMe = thrower.transform;

                // FLY it from the origin like Player.throwItem does (velocity
                // = dir * clamped-dist * 2.5, drag 2, flyTime = dist/150, spin)
                // instead of teleport-landing at the target - that exploded the
                // partner's molotov while the thrower's bottle was mid-air.
                var flat = pa.Target - pa.Origin;
                var dist = Mathf.Clamp(flat.magnitude, 10f, 370f);
                var dir = flat.sqrMagnitude > 0.01f ? flat.normalized : Vector3.forward;

                // The origin IS the thrower - their clone's collider is right
                // there, and gas bombs fire onCollide on ANY collision, so a
                // mirror spawned inside it burst/deflected instantly ("shifted
                // position"). Ignore all player colliders + start clear of the
                // clone's capsule.
                var projCol = pa.Item.GetComponent<Collider>();
                if (projCol != null)
                    IgnorePlayerCollisions(projCol);

                pa.Item.position = pa.Origin + dir * Mathf.Min(14f, dist * 0.4f);
                var body = pa.Item.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.drag = 2f;
                    body.velocity = dir * dist * 2.5f;
                    body.angularVelocity = Vector3.zero;
                    if (thrownItem.initialRotationForce != 0f)
                        body.AddTorque(0f, thrownItem.initialRotationForce * 1000f, 0f);
                }

                thrownItem.flaming = pa.Flaming || thrownItem.flaming;
                // The owner's machine is authoritative for direct-hit damage
                // (it syncs through the damage channels) - a mirror that also
                // dealt contact damage would double-hit whoever stands there.
                thrownItem.damage = 0;
                thrownItem.landTarget = pa.Target;
                thrownItem.setFallSpeed(dist); // flyTime timeout, like the game
                thrownItem.onGround = false;
                thrownItem.thrown = true; // game Update lands it near landTarget

                // Failsafe: physics can undershoot on a mirror (collisions the
                // thrower's copy didn't have) - force-land at the target if
                // the game hasn't landed it well past its fly time
                _inFlight.Add(new FlightWatch
                {
                    Item = pa.Item,
                    Target = pa.Target,
                    Deadline = Time.time + dist / 150f + 1.5f
                });

                ModLogger.Msg($"[ItemSync] Remote throw flying {pa.Origin:F0} -> {pa.Target:F0} (flaming={pa.Flaming})");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[ItemSync] Arm throw failed: {ex.Message}");
            }
        }
    }

    /// <summary>A mirrored projectile must never collide with any player body
    /// (local player or remote clones) - contact damage is zeroed anyway and
    /// explosion damage is radius-based, so these collisions only deflect.</summary>
    private static void IgnorePlayerCollisions(Collider projectile)
    {
        try
        {
            var local = Player.Instance;
            if (local != null)
                foreach (var c in local.GetComponentsInChildren<Collider>(true))
                    Physics.IgnoreCollision(projectile, c);

            var manager = NetworkManager.Instance;
            if (manager == null) return;
            foreach (var kvp in manager.RemotePlayers)
            {
                if (kvp.Value == null) continue;
                foreach (var c in kvp.Value.GetComponentsInChildren<Collider>(true))
                    Physics.IgnoreCollision(projectile, c);
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemSync] IgnorePlayerCollisions: {ex.Message}");
        }
    }

    private void WatchFlights()
    {
        for (var i = _inFlight.Count - 1; i >= 0; i--)
        {
            var fw = _inFlight[i];
            if (fw.Item == null) { _inFlight.RemoveAt(i); continue; }

            var thrownItem = fw.Item.GetComponent<ThrownItem>();
            if (thrownItem == null || thrownItem.onGround) { _inFlight.RemoveAt(i); continue; }

            if (Time.time > fw.Deadline)
            {
                // Teleport onto the land target; checkIfWantToLand fires
                // onCollide (with full landing FX) on the next game Update
                fw.Item.position = fw.Target;
                _inFlight.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Join bulk: every still-alive session drop (Horde SyncExistingDroppedItems).
    /// </summary>
    public void CollectDroppedSnapshot(List<Packets.Packet> into)
    {
        if (into == null || _itemBySyncId.Count == 0) return;

        foreach (var kvp in _itemBySyncId)
        {
            var item = kvp.Value;
            if (item == null) continue;
            if (!_typeBySyncId.TryGetValue(kvp.Key, out var itemType) || string.IsNullOrEmpty(itemType))
                continue;

            var pos = item.transform.position;
            var slotItem = ItemState.GetSlotItem(item);
            into.Add(new PickupStatePacket
            {
                PickupId = kvp.Key,
                ItemType = itemType,
                ItemName = item.gameObject.name,
                Amount = 1,
                X = pos.x, Y = pos.y, Z = pos.z,
                Spawned = true,
                Durability = slotItem != null ? slotItem.durability : -1f,
                Ammo = slotItem != null ? slotItem.ammo : 0,
                ModifierQuality = slotItem != null ? (int)slotItem.modifierQuality : 0,
                Modifiers = slotItem != null ? ItemState.EncodeModifiers(slotItem) : ""
            });
        }
    }

    /// <summary>
    /// Host: replay recent throws whose item is still burning (flares) to a
    /// newly joined player - spawn packet followed by the thrown event.
    /// </summary>
    public void CollectThrowSnapshot(List<Packets.Packet> into)
    {
        if (_throwRecords.Count == 0) return;
        var now = Time.time;
        var localId = Math.Max(_network.LocalClientId, 0);

        foreach (var kvp in _throwRecords)
        {
            var record = kvp.Value;
            if (record.Item == null) continue; // consumed (molotov) / despawned
            if (now - record.Time > ThrowSnapshotMaxAge) continue;
            if (string.IsNullOrEmpty(record.ItemType)) continue;

            // Only replay throws that still emit light (burning flare)
            var flare = record.Item.GetComponent<Flare>();
            if (flare == null || flare.dead) continue;

            into.Add(new PickupStatePacket
            {
                PickupId = kvp.Key,
                ItemType = record.ItemType,
                ItemName = record.Item.gameObject.name,
                Amount = 1,
                X = record.Target.x, Y = record.Target.y, Z = record.Target.z,
                Spawned = true
            });
            into.Add(new ThrownItemPacket
            {
                PlayerId = localId,
                SyncId = kvp.Key,
                X = record.Target.x, Y = record.Target.y, Z = record.Target.z
            });
        }
    }

    /// <summary>Find an already-existing world item matching a spawn packet (rejoin dedupe).</summary>
    private Component FindExistingDrop(PickupStatePacket packet)
    {
        var wantedName = CleanName(packet.ItemName);
        if (string.IsNullOrEmpty(wantedName)) return null;
        var pos = new Vector3(packet.X, packet.Y, packet.Z);

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_itemType))
        {
            if (obj is not Component item) continue;
            if (item.transform.root.name.StartsWith("RemotePlayer_")) continue;
            // Skip drops we are ALREADY tracking: this rejoin-dedupe is only for
            // adopting an untracked pre-existing copy. Without this guard a second
            // same-type item dropped on the same spot was collapsed onto the first
            // (adopted, not spawned), so the partner only ever saw one of them.
            if (_syncIdByInstance.ContainsKey(item.gameObject.GetInstanceID())) continue;
            if (CleanName(item.gameObject.name) != wantedName) continue;
            if ((item.transform.position - pos).sqrMagnitude < 0.75f * 0.75f)
                return item;
        }
        return null;
    }

    private void RepinAt(Component item, Vector3 pos)
    {
        foreach (var pin in _pinned)
        {
            if (ReferenceEquals(pin.Item, item.transform))
            {
                pin.Position = pos;
                return;
            }
        }
    }

    // ------------------------------------------------------------------
    // Remote disarm-pickups (beartraps, mushrooms, ...)
    // ------------------------------------------------------------------

    /// <summary>A remote player gathered a world item (Item.disarm succeeded there).</summary>
    public void OnRemoteDisarm(string itemId)
    {
        if (ResolveApi() && TryApplyDisarm(itemId, UnityEngine.Object.FindObjectsOfType(_itemType))) return;
        ModLogger.Msg($"[ItemSync] Disarm target '{itemId}' not loaded - kept pending");
        _pendingDisarms[itemId] = Time.time;
    }

    /// <summary>A trap fired on the other machine (someone stepped on it).</summary>
    public void OnRemoteTrapFire(string itemId)
    {
        if (ResolveApi() && TryApplyTrapFire(itemId, UnityEngine.Object.FindObjectsOfType(_itemType))) return;
        ModLogger.Msg($"[ItemSync] Trap '{itemId}' not loaded - fire kept pending");
        _pendingTrapFires[itemId] = Time.time;
    }

    /// <summary>
    /// Replay Item.switchTriggerState on the matching local item - the same
    /// call the source machine made: destroys the object or switches its
    /// trigger state, but never touches the local player's inventory.
    /// </summary>
    private bool TryApplyDisarm(string itemId, UnityEngine.Object[] snapshot)
    {
        if (_switchTriggerStateMethod == null) return false;

        var item = FindWorldItem(itemId, snapshot);
        if (item == null) return false;

        RemoteApply.Active = true;
        try
        {
            _switchTriggerStateMethod.Invoke(item, null);
            ModLogger.Msg($"[ItemSync] Applied remote pickup of '{itemId}'");
            return true;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemSync] Failed to apply disarm '{itemId}': {ex.Message}");
            return true; // don't retry a throwing target forever
        }
        finally
        {
            RemoteApply.Active = false;
        }
    }

    /// <summary>Replay Trigger.switchToTriggered on the matching local trap.</summary>
    private bool TryApplyTrapFire(string itemId, UnityEngine.Object[] snapshot)
    {
        var item = FindWorldItem(itemId, snapshot);
        if (item == null) return false;

        try
        {
            var trigger = item.GetComponent<Trigger>();
            if (trigger == null || trigger.triggered) return true; // nothing to do

            RemoteApply.Active = true;
            try
            {
                trigger.switchToTriggered();
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[ItemSync] Applied remote trap fire '{itemId}'");
            return true;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemSync] Failed to apply trap fire '{itemId}': {ex.Message}");
            return true;
        }
    }

    private Component FindWorldItem(string itemId, UnityEngine.Object[] snapshot)
    {
        var at = itemId.LastIndexOf('@');
        if (at <= 0) return null;
        var name = itemId.Substring(0, at);
        var coords = itemId.Substring(at + 1).Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (coords.Length != 2
            || !float.TryParse(coords[0], System.Globalization.NumberStyles.Float, inv, out var x)
            || !float.TryParse(coords[1], System.Globalization.NumberStyles.Float, inv, out var z))
            return null;

        Component exact = null;
        Component best = null;
        var bestDist = 10f * 10f;
        foreach (var obj in snapshot)
        {
            if (obj is not Component item) continue;
            if (item.transform.root.name.StartsWith("RemotePlayer_")) continue;
            if (item.gameObject.name != name) continue;

            if (GameIds.ForComponent(item) == itemId) { exact = item; break; }

            var p = item.transform.position;
            var dx = p.x - x;
            var dz = p.z - z;
            var d = dx * dx + dz * dz;
            if (d < bestDist)
            {
                bestDist = d;
                best = item;
            }
        }
        return exact ?? best;
    }

    // ------------------------------------------------------------------
    // Receive side
    // ------------------------------------------------------------------

    public void OnPickupState(PickupStatePacket packet)
    {
        if (packet.Spawned)
            SpawnDrop(packet);
        else
            RemoveItem(packet);
    }

    private void SpawnDrop(PickupStatePacket packet)
    {
        if (!ResolveApi()) return;

        // Already spawned (duplicate/re-sent packet)?
        if (!string.IsNullOrEmpty(packet.PickupId)
            && _itemBySyncId.TryGetValue(packet.PickupId, out var existing) && existing != null)
            return;

        // Rejoin safety: the same drop may already exist in our world from a
        // previous session (snapshot re-send). Adopt it instead of duplicating.
        var adopt = FindExistingDrop(packet);
        if (adopt != null)
        {
            if (!string.IsNullOrEmpty(packet.PickupId))
            {
                _syncIdByInstance[adopt.gameObject.GetInstanceID()] = packet.PickupId;
                _itemBySyncId[packet.PickupId] = adopt;
                _typeBySyncId[packet.PickupId] = packet.ItemType;
                _pendingThrows.Remove(packet.PickupId); // it already landed
            }
            return;
        }

        var playerTransform = DarkwoodMP.DependencyInjection.ServiceLocator
            .Resolve<PlayerSync>()?.LocalPlayerTransform;
        var player = playerTransform != null ? playerTransform.GetComponent(_playerType) : null;
        if (player == null || _spawnMethod == null) return;

        RemoteApply.Active = true;
        try
        {
            // Let the game create the item (near our player) ...
            var spawned = _spawnMethod.Invoke(player,
                new object[] { false, packet.ItemType, Math.Max(packet.Amount, 1) }) as Transform;
            if (spawned == null)
            {
                ModLogger.Warning($"[ItemSync] Game refused to spawn '{packet.ItemType}'");
                return;
            }

            // ... then pin it where the remote player actually dropped it.
            // A single position set is not enough - the drop animation would
            // pull it back towards our own player.
            var pos = new Vector3(packet.X, packet.Y, packet.Z);
            spawned.position = pos;
            _pinned.Add(new PinnedItem { Item = spawned, Position = pos, Until = Time.time + PinDuration });

            // Restore the item's per-instance state onto the freshly created
            // mirror's slot, so picking it up here yields the same wear/ammo/mods
            // the original owner dropped (no-op when the sender carried none).
            ItemState.Apply(ItemState.GetSlotItem(spawned), packet.Durability, packet.Ammo, packet.ModifierQuality, packet.Modifiers);

            if (!string.IsNullOrEmpty(packet.PickupId))
            {
                _syncIdByInstance[spawned.gameObject.GetInstanceID()] = packet.PickupId;
                _itemBySyncId[packet.PickupId] = spawned;
                _typeBySyncId[packet.PickupId] = packet.ItemType;

                // The throw event may have arrived before this spawn packet
                if (_pendingThrows.TryGetValue(packet.PickupId, out var pendingThrow))
                {
                    _pendingThrows.Remove(packet.PickupId);
                    ApplyThrow(packet.PickupId, spawned, pendingThrow.Target, pendingThrow.SenderId);
                }
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemSync] Failed to spawn drop '{packet.ItemType}': {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
        }
    }

    private void RemoveItem(PickupStatePacket packet)
    {
        if (!ResolveApi()) return;

        // Exact resolution via sync id - reliable even though all dropped
        // items share the same GameObject name
        if (!string.IsNullOrEmpty(packet.PickupId)
            && _itemBySyncId.TryGetValue(packet.PickupId, out var known) && known != null)
        {
            DestroyItem(known.gameObject);
            _itemBySyncId.Remove(packet.PickupId);
            return;
        }

        // Fallback for items without an id (world items destroyed by fire etc.)
        if (string.IsNullOrEmpty(packet.ItemName)) return;

        var pos = new Vector3(packet.X, packet.Y, packet.Z);
        var wantedName = CleanName(packet.ItemName);

        Component best = null;
        var bestDist = RemoveMatchRadius * RemoveMatchRadius;
        Component nearest = null;
        var nearestDist = 1.5f * 1.5f;

        var droppedField = _itemType.GetField("isDroppedItem",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_itemType))
        {
            if (obj is not Component item) continue;
            if (item.transform.root.name.StartsWith("RemotePlayer_")) continue;

            var d = (item.transform.position - pos).sqrMagnitude;
            if (CleanName(item.gameObject.name) == wantedName)
            {
                if (d < bestDist)
                {
                    bestDist = d;
                    best = item;
                }
            }
            else if (d < nearestDist)
            {
                // Name mismatch fallback: only ever destroy DROPPED items this
                // way - deleting the nearest arbitrary world object could take
                // out a chest or trap standing next to the pickup
                if (droppedField?.GetValue(item) is bool dropped && dropped)
                {
                    nearestDist = d;
                    nearest = item;
                }
            }
        }

        var target = best ?? nearest;
        if (target == null)
        {
            ModLogger.Warning($"[ItemSync] No match to remove for '{packet.ItemName}' (id '{packet.PickupId}') at {pos:F1}");
            return;
        }
        DestroyItem(target.gameObject);
    }

    private void DestroyItem(GameObject go)
    {
        RemoteApply.Active = true;
        try
        {
            UnityEngine.Object.Destroy(go);
        }
        finally
        {
            RemoteApply.Active = false;
        }
    }

    /// <summary>Strip Unity's "(Clone)" suffixes and whitespace for matching.</summary>
    private static string CleanName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        while (name.EndsWith("(Clone)"))
            name = name.Substring(0, name.Length - "(Clone)".Length);
        return name.Trim();
    }

    private bool ResolveApi()
    {
        if (_itemType != null && _playerType != null) return true;

        _itemType = GameTypes.GetType("Item");
        _playerType = GameTypes.GetType("Player");
        if (_itemType != null && _switchTriggerStateMethod == null)
            _switchTriggerStateMethod = _itemType.GetMethod("switchTriggerState",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (_playerType != null && _spawnMethod == null)
        {
            for (var t = _playerType; t != null; t = t.BaseType)
            {
                var m = t.GetMethod("spawnDroppedInvItemm",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (m != null) { _spawnMethod = m; break; }
            }
        }
        return _itemType != null && _playerType != null;
    }

    private class PinnedItem
    {
        public Transform Item;
        public Vector3 Position;
        public float Until;
    }

    private class PendingThrow
    {
        public Vector3 Target;
        public int SenderId;
        public float Expires;
    }

    private class PendingArm
    {
        public Transform Item;
        public Vector3 Target;
        public Vector3 Origin;
        public bool Flaming;
        public int SenderId;
        public float ArmAt;
    }

    private class FlightWatch
    {
        public Transform Item;
        public Vector3 Target;
        public float Deadline;
    }

    private class ThrowRecord
    {
        public Component Item;
        public string ItemType;
        public Vector3 Target;
        public float Time;
    }
}
