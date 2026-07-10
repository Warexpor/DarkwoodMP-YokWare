using System;
using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Exclusive interaction lock (v0.7): only one player at a time may have a given
/// world object open - containers/lootables/corpses, NPC dialogue (incl. traders)
/// and workbenches. Two players rummaging the same chest or trading with the same
/// NPC at once is a prime dupe/desync source (both pull from the same inventory
/// before the container/trade sync reconciles), so the second interactor is
/// cleanly blocked with an "in use" notice.
///
/// Model: a player can only interact with ONE object at a time (the game gates
/// this with performingAction), so we track a single locally-held lock plus a map
/// of remote-held locks. Locks are optimistic (claimed on open, broadcast, no
/// round-trip) which keeps the common uncontended open instant. Objects are keyed
/// by GameIds.ForComponent (name@x,z) - identical on every machine for the same
/// world object.
///
/// Conflict (both open within one RTT): resolved deterministically by lowest
/// player id. When a remote claim arrives for the object we hold, the higher-id
/// player yields - force-closing its just-opened UI through the game's own close
/// path (no soft-lock).
///
/// Safety: a held lock is re-broadcast periodically and expires after a TTL, and
/// all of a player's locks are dropped when they disconnect - so a missed unlock
/// or a crash never strands an object as permanently occupied.
/// </summary>
public class InteractionLock
{
    public enum Kind { Container, Npc, Workbench, Drag }

    private const double TtlSeconds = 20.0;      // remote lock is stale after this with no refresh
    private const double RefreshSeconds = 6.0;   // holder re-broadcasts this often
    private const double MessageCooldown = 2.0;  // don't spam the "in use" notice

    private readonly NetworkLayer _network;

    private struct RemoteLock { public int owner; public Kind kind; public DateTime seen; }
    private readonly Dictionary<string, RemoteLock> _remote = new();

    private string _heldId;
    private Kind _heldKind;
    private Component _heldObj;
    private DateTime _lastRefresh;

    private string _lastMsgId;
    private DateTime _lastMsgTime;

    public InteractionLock(NetworkLayer network)
    {
        _network = network;
    }

    private int MyId => _network != null ? Math.Max(_network.LocalClientId, 0) : 0;

    // ------------------------------------------------------------------
    // Local intent (called from the open-patch prefixes)
    // ------------------------------------------------------------------

    /// <summary>
    /// Try to begin interacting with an object. Returns false if another player
    /// currently holds it (the caller should abort the open). On success the lock
    /// is claimed locally and broadcast. Drag locks pass the MovableSync registry
    /// id via <paramref name="idOverride"/> (stable while the item moves).
    /// </summary>
    public bool TryBegin(Component obj, Kind kind, string idOverride = null)
    {
        try
        {
            if (obj == null) return true;
            // Not networked -> no contention, never block.
            if (_network == null || !_network.IsConnected) return true;

            var id = idOverride ?? GameIds.ForComponent(obj);

            var blockedBy = OwnerOf(id);
            if (blockedBy >= 0 && blockedBy != MyId)
            {
                NotifyInUse(id, blockedBy, kind);
                return false;
            }

            // Claim (releasing any previous hold first - shouldn't happen, but keeps
            // the single-held invariant true).
            if (_heldId != null && _heldId != id) Release();

            _heldId = id;
            _heldKind = kind;
            _heldObj = obj;
            _lastRefresh = DateTime.UtcNow;
            _remote.Remove(id);
            BroadcastLock(id, kind);
            return true;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock] TryBegin failed: {ex.Message}");
            return true; // fail open - never soft-lock a player out of the world
        }
    }

    /// <summary>
    /// Is this object currently held by another player? (Shows the "in use"
    /// notice as a side effect.) Used where the claim must wait for a postfix
    /// confirmation but the block decision has to happen up front.
    /// </summary>
    public bool IsBlocked(Component obj, Kind kind, string idOverride = null)
    {
        try
        {
            if (obj == null) return false;
            if (_network == null || !_network.IsConnected) return false;

            var id = idOverride ?? GameIds.ForComponent(obj);
            if (_heldId == id) return false;

            var owner = OwnerOf(id);
            if (owner >= 0 && owner != MyId)
            {
                NotifyInUse(id, owner, kind);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock] IsBlocked failed: {ex.Message}");
            return false; // fail open
        }
    }

    /// <summary>Release whatever the local player currently holds (idempotent).</summary>
    public void Release()
    {
        try
        {
            if (_heldId == null) return;
            var id = _heldId;
            _heldId = null;
            _heldObj = null;
            BroadcastUnlock(id);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock] Release failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Release only when the current hold is of the given kind. The close hooks
    /// overlap (a trade's shop inventory hides while the NPC dialogue is still
    /// open), so each hook must only ever drop its OWN kind of lock.
    /// </summary>
    public void ReleaseIfKind(Kind kind)
    {
        if (_heldId != null && _heldKind == kind) Release();
    }

    /// <summary>Release only if the current hold is a drag lock (stopDragging hook).</summary>
    public void ReleaseDrag() => ReleaseIfKind(Kind.Drag);

    // ------------------------------------------------------------------
    // Remote events (from NetworkManager)
    // ------------------------------------------------------------------

    public void OnRemoteLock(int ownerId, Kind kind, string id)
    {
        if (string.IsNullOrEmpty(id) || ownerId == MyId) return;

        if (_heldId == id)
        {
            // Contested: both opened it. Lowest id wins.
            if (ownerId < MyId)
            {
                ModLogger.Msg($"[InteractionLock] Yielding '{id}' to player {ownerId}");
                var kindToClose = _heldKind;
                var objToClose = _heldObj;
                _heldId = null;
                _heldObj = null;
                _remote[id] = new RemoteLock { owner = ownerId, kind = kind, seen = DateTime.UtcNow };
                ForceCloseLocal(kindToClose, objToClose);
            }
            else
            {
                // We keep it - remind them so they yield.
                BroadcastLock(id, _heldKind);
            }
            return;
        }

        _remote[id] = new RemoteLock { owner = ownerId, kind = kind, seen = DateTime.UtcNow };
    }

    public void OnRemoteUnlock(int ownerId, string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (_remote.TryGetValue(id, out var rl) && rl.owner == ownerId)
            _remote.Remove(id);
    }

    /// <summary>Drop every lock a departed player held.</summary>
    public void OnPlayerLeft(int playerId)
    {
        var stale = new List<string>();
        foreach (var kvp in _remote)
            if (kvp.Value.owner == playerId) stale.Add(kvp.Key);
        foreach (var id in stale) _remote.Remove(id);
    }

    // ------------------------------------------------------------------
    // Per-frame upkeep
    // ------------------------------------------------------------------

    public void OnUpdate()
    {
        var now = DateTime.UtcNow;

        // Re-broadcast our held lock so long interactions never expire remotely.
        // If the interaction silently ended without hitting a release hook (open
        // refused after the claim, object destroyed, drag interrupted), drop the
        // lock instead - a stranded hold would otherwise refresh forever and
        // permanently occupy the object for the partner.
        if (_heldId != null && (now - _lastRefresh).TotalSeconds >= RefreshSeconds)
        {
            _lastRefresh = now;
            if (HeldStillValid())
                BroadcastLock(_heldId, _heldKind);
            else
                Release();
        }

        // Expire stale remote locks (missed unlock / crashed holder).
        if (_remote.Count > 0)
        {
            List<string> expired = null;
            foreach (var kvp in _remote)
            {
                if ((now - kvp.Value.seen).TotalSeconds > TtlSeconds)
                    (expired ??= new List<string>()).Add(kvp.Key);
            }
            if (expired != null)
                foreach (var id in expired) _remote.Remove(id);
        }
    }

    public void Reset()
    {
        _remote.Clear();
        _heldId = null;
        _heldObj = null;
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private int OwnerOf(string id)
    {
        if (!_remote.TryGetValue(id, out var rl)) return -1;
        if ((DateTime.UtcNow - rl.seen).TotalSeconds > TtlSeconds)
        {
            _remote.Remove(id);
            return -1;
        }
        return rl.owner;
    }

    /// <summary>Does the held lock still correspond to a live interaction?</summary>
    private bool HeldStillValid()
    {
        try
        {
            switch (_heldKind)
            {
                case Kind.Npc:
                    var ui = Singleton<global::UI>.Instance;
                    return ui != null && ui.dialogueWindow != null && ui.dialogueWindow.npc != null;
                case Kind.Drag:
                    return _heldObj is Item item && item != null && item.beingDragged;
                default: // Container / Workbench
                    var player = Player.Instance;
                    return player != null && player.openedItemInventory != null;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void ForceCloseLocal(Kind kind, Component obj)
    {
        // Use the game's own close path so player state (halt/performingAction)
        // unwinds exactly as a normal close. Guard with RemoteApply so the close
        // hooks don't rebroadcast (we already yielded the lock).
        RemoteApply.Active = true;
        try
        {
            if (kind == Kind.Npc)
            {
                var ui = Singleton<global::UI>.Instance;
                if (ui != null && ui.dialogueWindow != null) ui.dialogueWindow.close();
            }
            else if (kind == Kind.Drag)
            {
                if (obj is Item item && item != null && item.beingDragged)
                    item.stopDragging(true);
            }
            else
            {
                var player = Player.Instance;
                if (player != null && player.openedItemInventory != null)
                    player.closeOpenedItemInventory();
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock] ForceClose failed: {ex.Message}");
        }
        finally
        {
            RemoteApply.Active = false;
        }
    }

    private void NotifyInUse(string id, int ownerId, Kind kind)
    {
        var now = DateTime.UtcNow;
        if (_lastMsgId == id && (now - _lastMsgTime).TotalSeconds < MessageCooldown) return;
        _lastMsgId = id;
        _lastMsgTime = now;

        var who = NetworkManager.Instance?.GetPlayerName(ownerId) ?? "Another player";
        var doing = ActionPhrase(kind);

        // Game-native bottom-screen message (same style as "Too far away") for
        // immersion; chat only as fallback when no player exists yet.
        try
        {
            var player = Player.Instance;
            if (player != null)
            {
                player.displayMessage($"{who} is {doing} right now.");
                return;
            }
        }
        catch { /* fall through to chat */ }

        DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ChatManager>()
            ?.AddSystemMessage($"{who} is {doing} right now.");
    }

    /// <summary>What the blocking player is doing, phrased for the object kind.</summary>
    private static string ActionPhrase(Kind kind) => kind switch
    {
        Kind.Container => "looting this",
        Kind.Drag => "dragging this",
        Kind.Workbench => "crafting here",
        Kind.Npc => "talking to them",
        _ => "using this"
    };

    private void BroadcastLock(string id, Kind kind)
    {
        if (_network == null || !_network.IsConnected) return;
        _network.SendReliable(new InteractionLockSyncPacket
        {
            PlayerId = MyId,
            Locked = true,
            KindChar = (byte)KindChar(kind),
            ObjectId = id
        });
    }

    private void BroadcastUnlock(string id)
    {
        if (_network == null || !_network.IsConnected) return;
        _network.SendReliable(new InteractionLockSyncPacket
        {
            PlayerId = MyId,
            Locked = false,
            KindChar = (byte)'C',
            ObjectId = id
        });
    }

    public static char KindChar(Kind kind) => kind switch
    {
        Kind.Npc => 'N',
        Kind.Workbench => 'W',
        Kind.Drag => 'D',
        _ => 'C',
    };

    public static Kind KindFromChar(char c) => c switch
    {
        'N' => Kind.Npc,
        'W' => Kind.Workbench,
        'D' => Kind.Drag,
        _ => Kind.Container,
    };

    /// <summary>Join bulk: re-apply active locks held by this machine (local + known remotes).</summary>
    public void CollectSnapshot(List<Packet> into)
    {
        if (into == null || _network == null || !_network.IsConnected) return;
        try
        {
            if (_heldId != null)
            {
                into.Add(new InteractionLockSyncPacket
                {
                    PlayerId = MyId,
                    Locked = true,
                    KindChar = (byte)KindChar(_heldKind),
                    ObjectId = _heldId
                });
            }
            foreach (var kvp in _remote)
            {
                into.Add(new InteractionLockSyncPacket
                {
                    PlayerId = kvp.Value.owner,
                    Locked = true,
                    KindChar = (byte)KindChar(kvp.Value.kind),
                    ObjectId = kvp.Key
                });
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock] CollectSnapshot: {ex.Message}");
        }
    }
}
