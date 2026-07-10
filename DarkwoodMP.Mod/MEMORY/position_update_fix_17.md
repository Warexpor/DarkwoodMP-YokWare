# Position Update Bug Fix #17

## The Bug
**File:** `Network/NetworkPeer.cs`  
**Line:** 714

**Problem:** The host player's position updates were never being broadcast to clients because of an incorrect exclusion check.

**Root cause:** Condition `msg.PlayerId != 0` in the broadcast logic **EXCLUDED** the host player's position updates. Since the host has PlayerId=0, their position was filtered out and never sent to clients.

```csharp
// BROKEN - filtered out host positions!
if (manager != null && manager.IsHost && msg.PlayerId != 0)
```

## The Fix
Removed the `msg.PlayerId != 0` exclusion so ALL player positions are broadcast to clients.

**Before:**
```csharp
if (manager != null && manager.IsHost && msg.PlayerId != 0)
{
    MelonLogger.Msg($"[HandlePlayerPosition] Broadcasting position from player {msg.PlayerId} to all clients");
    var broadcastMsg = new PlayerPositionMessage { ... };
    SendToAllClients(MessageType.PlayerPosition, broadcastMsg);
}
```

**After:**
```csharp
if (manager != null && manager.IsHost)
{
    MelonLogger.Msg($"[POSITION-BROADCAST] Host broadcasting position from player {msg.PlayerId} to all clients (was PlayerId != 0, now broadcasts all)");
    var broadcastMsg = new PlayerPositionMessage { ... };
    SendToAllClients(MessageType.PlayerPosition, broadcastMsg);
}
```

## Impact
- **Before:** Client could see a static host player visual that never moved
- **After:** Client sees host player moving in real-time

## Testing Verification
Look for these log markers in MelonLoader logs:
- `[POSITION-SEND]` - Position message sent from source
- `[POSITION-REC]` - Position message received by network peer
- `[POSITION-BROADCAST]` - Host broadcasting to all clients
- `[POSITION-APPLY]` - Position being applied to visual

Full trace for client seeing host move:
1. Host: `[POSITION-SEND] Sending PlayerPosition msg for PlayerId=0`
2. Client: `[POSITION-REC-ROUTING] Processing PlayerPosition from player 0`
3. Host: `[POSITION-BROADCAST] Host broadcasting position from player 0 to all clients`
4. Client: `[POSITION-APPLY] Player 0 position changed: (old) -> (new)`

## Additional Logging Added
- `NetworkManager.cs:725-784` - Added `[POSITION-SEND]` markers
- `NetworkPeer.cs:601-612` - Added `[POSITION-REC-ROUTING]` markers  
- `NetworkPeer.cs:673-700` - Added `[POSITION-APPLY]` markers
