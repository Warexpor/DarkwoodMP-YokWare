using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Syncs draggable world objects (wardrobes, furniture - Darkwood's `Item` class
/// with `draggable=true`). While the local player drags an item, its position is
/// broadcast at 10Hz; remote clients apply it to their matching item.
///
/// Items are matched cross-client by name + initial position, captured in a
/// registry the first time sync is needed after connecting. Both players must
/// therefore share the same world state (same save) - true for a co-op session.
/// </summary>
public class MovableSync
{
    private readonly NetworkLayer _network;

    private const float SendInterval = 0.1f;   // 10Hz while dragging
    private const float MoveEpsilon = 0.01f;
    private const float RescanCooldown = 5f;

    private const float ClaimRadius = 50f;     // max distance for name-based matching

    private Type _itemType;
    private FieldInfo _beingDraggedField;
    private FieldInfo _draggableField;

    // Cross-client registry: stable id <-> live component
    private Dictionary<string, Component> _itemById;
    private readonly Dictionary<int, string> _idByInstance = new();
    // Local items already claimed by a remote id (prevents double-mapping)
    private readonly HashSet<int> _remoteMapped = new();

    // Items the local player is currently dragging
    private readonly List<Component> _draggedLocally = new();
    private readonly Dictionary<int, Vector3> _lastSentPos = new();
    private float _lastSend;
    private float _lastRescan = float.MinValue;

    // Drag-scrape sound for remotely moved items. The game's ItemSounds.Update
    // plays movingSound off RIGIDBODY VELOCITY - but remote moves are applied
    // by teleporting the transform with velocity zeroed (to stop physics from
    // fighting the sync), so the vanilla path can never fire on the mirror.
    // We drive the same sound manually while move packets keep arriving.
    private sealed class MoveSound { public AudioObject ao; public float lastMove; }
    private readonly Dictionary<int, MoveSound> _remoteMoveSounds = new();
    private const float MoveSoundTimeout = 0.35f;     // packets are 10Hz
    private const float MoveSoundMinSqrStep = 1f;      // ~ vanilla 11 u/s velocity gate

    public MovableSync(NetworkLayer network)
    {
        _network = network;
    }

    // ------------------------------------------------------------------
    // Send side (called from ItemDrag_Patch)
    // ------------------------------------------------------------------

    /// <summary>
    /// Session-stable cross-machine id for a world item (used by the drag
    /// interaction lock). Unlike a live-position id this doesn't drift while
    /// the item is dragged, and once a remote ObjectMove claims the item the
    /// registry maps BOTH machines to the same shared id.
    /// </summary>
    public string SharedIdFor(Component item)
    {
        if (item == null) return null;
        EnsureRegistry();
        if (_itemById == null) return null;
        RegisterItem(item);
        return _idByInstance.TryGetValue(item.GetInstanceID(), out var id) ? id : null;
    }

    public void OnLocalDragStart(Component item)
    {
        if (item == null) return;
        EnsureRegistry();
        RegisterItem(item);
        if (!_draggedLocally.Contains(item))
            _draggedLocally.Add(item);
    }

    public void OnLocalDragStop(Component item)
    {
        if (item == null) return;
        SendItem(item, force: true);
        _draggedLocally.Remove(item);
    }

    /// <summary>Called every frame; throttles itself to 10Hz.</summary>
    public void OnUpdate()
    {
        FadeOutStaleMoveSounds();

        if (_draggedLocally.Count == 0) return;
        if (Time.time - _lastSend < SendInterval) return;
        _lastSend = Time.time;

        // Backwards: dragged items can be destroyed (picked up) mid-drag
        for (var i = _draggedLocally.Count - 1; i >= 0; i--)
        {
            var item = _draggedLocally[i];
            if (item == null)
            {
                _draggedLocally.RemoveAt(i);
                continue;
            }
            SendItem(item, force: false);
        }
    }

    private void SendItem(Component item, bool force)
    {
        if (item == null) return;
        if (!_idByInstance.TryGetValue(item.GetInstanceID(), out var id)) return;

        var pos = item.transform.position;
        if (!force
            && _lastSentPos.TryGetValue(item.GetInstanceID(), out var last)
            && (pos - last).sqrMagnitude < MoveEpsilon * MoveEpsilon)
            return;
        _lastSentPos[item.GetInstanceID()] = pos;

        var rot = item.transform.rotation;
        _network.Send(new ObjectMovePacket
        {
            ObjectId = id,
            PlayerId = Math.Max(_network.LocalClientId, 0),
            X = pos.x, Y = pos.y, Z = pos.z,
            Rx = rot.x, Ry = rot.y, Rz = rot.z, Rw = rot.w
        });
    }

    // ------------------------------------------------------------------
    // Receive side
    // ------------------------------------------------------------------

    public void OnObjectMove(ObjectMovePacket packet)
    {
        var manager = NetworkManager.Instance;
        if (manager != null && packet.PlayerId == manager.LocalPlayerId) return;

        EnsureRegistry();
        if (_itemById == null) return;

        if (!_itemById.TryGetValue(packet.ObjectId, out var item) || item == null)
        {
            // The two worlds don't place objects at identical positions (separate
            // saves), so exact ids often won't match. Fall back to claiming the
            // nearest same-named item; the mapping then sticks for the session.
            item = ClaimByName(packet);
            if (item == null && Time.time - _lastRescan > RescanCooldown)
            {
                _lastRescan = Time.time;
                BuildRegistry();
                if (!_itemById.TryGetValue(packet.ObjectId, out item))
                    item = ClaimByName(packet);
            }
            if (item == null) return;
        }

        // Don't fight over an item both players grabbed at once
        if (_beingDraggedField?.GetValue(item) is bool dragged && dragged) return;

        var pos = new Vector3(packet.X, packet.Y, packet.Z);
        var rot = new Quaternion(packet.Rx, packet.Ry, packet.Rz, packet.Rw);

        var movedSqr = (item.transform.position - pos).sqrMagnitude;

        item.transform.position = pos;
        item.transform.rotation = rot;

        if (movedSqr > MoveSoundMinSqrStep)
            PlayRemoteMoveSound(item);

        // Stop physics from immediately undoing the move
        var body = item.GetComponent<Rigidbody>();
        if (body != null && !body.isKinematic)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = pos;
            body.rotation = rot;
        }

        // Remember the applied position so our own send-side diffing
        // doesn't re-broadcast a position we just received
        _lastSentPos[item.GetInstanceID()] = pos;
    }

    public void Reset()
    {
        _itemById = null;
        _idByInstance.Clear();
        _remoteMapped.Clear();
        _draggedLocally.Clear();
        _lastSentPos.Clear();
        foreach (var ms in _remoteMoveSounds.Values)
        {
            try { ms.ao?.Stop(); } catch { }
        }
        _remoteMoveSounds.Clear();
    }

    // ------------------------------------------------------------------
    // Remote drag sound (see field comment)
    // ------------------------------------------------------------------

    private void PlayRemoteMoveSound(Component item)
    {
        try
        {
            var sounds = item.GetComponent<ItemSounds>();
            if (sounds == null || string.IsNullOrEmpty(sounds.movingSound)) return;

            // Same surface selection as ItemSounds.Update
            var audioId = Ground.getGround(item.transform.position) == null
                ? sounds.movingSound_grass
                : sounds.movingSound;

            var key = item.GetInstanceID();
            if (!_remoteMoveSounds.TryGetValue(key, out var ms))
            {
                ms = new MoveSound();
                _remoteMoveSounds[key] = ms;
            }
            ms.lastMove = Time.time;

            if (ms.ao != null && ms.ao.audioID != audioId)
            {
                ms.ao.Stop(0.5f);
                ms.ao = null;
            }
            if (ms.ao == null)
                ms.ao = AudioController.Play(audioId, item.transform, sounds.volumeModifier);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[MovableSync] move sound: {ex.Message}");
        }
    }

    private void FadeOutStaleMoveSounds()
    {
        if (_remoteMoveSounds.Count == 0) return;

        List<int> done = null;
        foreach (var kvp in _remoteMoveSounds)
        {
            if (Time.time - kvp.Value.lastMove < MoveSoundTimeout) continue;
            try { kvp.Value.ao?.Stop(0.5f); } catch { }
            (done ??= new List<int>()).Add(kvp.Key);
        }
        if (done != null)
            foreach (var key in done) _remoteMoveSounds.Remove(key);
    }

    /// <summary>
    /// Host: full state of all draggable items, sent to newly joined players so
    /// their world converges to ours (starting positions differ between saves).
    /// </summary>
    public void CollectSnapshot(List<Packets.Packet> into)
    {
        EnsureRegistry();
        if (_itemById == null) return;

        var playerId = Math.Max(_network.LocalClientId, 0);
        foreach (var kvp in _itemById)
        {
            var item = kvp.Value;
            if (item == null) continue;
            if (_draggableField?.GetValue(item) is not bool draggable || !draggable) continue;

            var pos = item.transform.position;
            var rot = item.transform.rotation;
            into.Add(new ObjectMovePacket
            {
                ObjectId = kvp.Key,
                PlayerId = playerId,
                X = pos.x, Y = pos.y, Z = pos.z,
                Rx = rot.x, Ry = rot.y, Rz = rot.z, Rw = rot.w
            });
        }
    }

    /// <summary>
    /// Match an unknown remote id to the nearest local item with the same name.
    /// The mapping is remembered so subsequent packets (and our own sends for
    /// that item) use the remote id.
    /// </summary>
    private Component ClaimByName(ObjectMovePacket packet)
    {
        var at = packet.ObjectId.LastIndexOf('@');
        if (at <= 0) return null;
        var name = packet.ObjectId.Substring(0, at);
        var pos = new Vector3(packet.X, packet.Y, packet.Z);

        Component best = null;
        var bestDist = ClaimRadius * ClaimRadius;
        foreach (var kvp in _idByInstance)
        {
            if (_remoteMapped.Contains(kvp.Key)) continue;
            if (!_itemById.TryGetValue(kvp.Value, out var candidate) || candidate == null) continue;
            if (candidate.gameObject.name != name) continue;

            var d = (candidate.transform.position - pos).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                best = candidate;
            }
        }

        if (best == null) return null;

        var instanceId = best.GetInstanceID();
        _remoteMapped.Add(instanceId);
        _itemById[packet.ObjectId] = best;
        // Our own drags of this item now report the shared (remote) id
        _idByInstance[instanceId] = packet.ObjectId;
        return best;
    }

    // ------------------------------------------------------------------
    // Registry
    // ------------------------------------------------------------------

    private void EnsureRegistry()
    {
        if (_itemById != null) return;
        if (_itemType == null)
        {
            _itemType = GameTypes.GetType("Item");
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _beingDraggedField = _itemType?.GetField("beingDragged", flags);
            _draggableField = _itemType?.GetField("draggable", flags);
        }
        if (_itemType == null) return;
        _itemById = new Dictionary<string, Component>();
        BuildRegistry();
        ModLogger.Msg($"[MovableSync] Registered {_itemById.Count} world items");
    }

    private void BuildRegistry()
    {
        // Only adds unknown items - existing entries (incl. remote-id mappings)
        // must survive a rescan
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_itemType))
        {
            if (obj is not Component item) continue;
            RegisterItem(item);
        }
    }

    private void RegisterItem(Component item)
    {
        // Items inside remote-player clones are copies, not world objects
        if (item.transform.root.name.StartsWith("RemotePlayer_")) return;

        var instanceId = item.GetInstanceID();
        if (_idByInstance.ContainsKey(instanceId)) return;

        var id = BuildId(item);
        // Same name at the same rounded position: disambiguate deterministically
        while (_itemById.ContainsKey(id))
            id += "'";

        _itemById[id] = item;
        _idByInstance[instanceId] = id;
    }

    private static string BuildId(Component c)
    {
        var p = c.transform.position;
        // Invariant culture: ids must be byte-identical across machines with
        // different locales (German Windows would use ',' as decimal separator)
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"{c.gameObject.name}@{p.x.ToString("F1", inv)},{p.z.ToString("F1", inv)}";
    }
}
