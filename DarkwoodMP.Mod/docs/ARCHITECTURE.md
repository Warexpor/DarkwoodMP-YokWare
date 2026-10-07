# Architecture

How the Path B mod is put together, and the rules a change has to follow. For
what each domain covers at runtime, see [COOP_COVERAGE.md](COOP_COVERAGE.md). For
the co-op design rules themselves (who owns what, and how each gameplay area
behaves), see [HOW_COOP_WORKS.md](HOW_COOP_WORKS.md).

---

## Source layout (`DarkwoodMP.Mod/`)

| Folder | What lives there |
|---|---|
| `Bootstrap/` | Loader entry points (BepInEx `DWMPEntry`, MelonLoader `MelonModMain`), `ModRuntime` (start/stop, patch application, reset registrations), `PluginInfo` (the single source of version and protocol). |
| `Harmony/` | Patch application per class (`PatchApplier`), the `[OptionalPatch]` marker, coroutine helpers. |
| `Networking/` | `LanNetworkManager` (the network component), transports, dispatch, messages, session services. |
| `Networking/Transport/` | `IPeerTable` with `LanPeerTable` (LiteNetLib) and `SteamPeerTable` (SNS + host-password gate). |
| `Networking/Steam/` | Steam lobby and SteamNetworkingSockets backend. |
| `Networking/Dispatch/` | The inbound handler table and the host relay. |
| `Networking/Messages/` | Wire message structs and `NetMessageType`. |
| `Networking/Session/` | Handshake, late join, save sync, world identity, `SessionState` / `LinkState`. |
| `Networking/Services/` | World save share, client backups, host migration, entity broadcast/interpolation, reset registry. |
| `Domains/<Area>/` | Net handlers and sync services per gameplay area (Combat, Dialogue, Doors, Dream, Inventory, Map, Night, Players, World). |
| `Domains/<Area>/Patches/` | Harmony patches for that area. |
| `Core/` | Pure policy helpers (unit-tested), death state, guards, gameplay constants. |
| `Audio/`, `UI/`, `Items/`, `Patches/`, `Config/`, `Logging/` | As named; `Patches/` holds cross-cutting patches (save path, world gen share, pause). |

---

## Wire

- Every packet is one type byte (`NetMessageType`) plus the message body. Each
  message struct has `Serialize(NetWriter)` and a static `Deserialize(NetReader)`
  that reads exactly what `Serialize` wrote. There are no optional trailers:
  peers on another protocol are refused at the handshake, so `HandshakeMessage`
  is the only tolerant reader.
- Any wire change (a field added, removed or reordered, or a new message id)
  bumps `PluginInfo.ProtocolVersion`. New ids go after the current highest.
- `NetMessageType` attributes:
  - `[Forwardable]`: a client may send it; after the host applied it, the host
    relays the body to the other clients.
  - `[ForwardablePlayer]`: same, wrapped in `RemotePlayerForward` so receivers
    know which player it came from.
  - `[HostOnly]`: only the host sends it; the host drops it unread from clients.
    A type is never both host-only and forwardable (a test checks this).

## Inbound dispatch (`Networking/Dispatch/`)

Each message type registers exactly one handler, once, in `Awake`:

```csharp
On(NetMessageType.DoorOpen, DoorOpenMessage.Deserialize, m => DoorHandlers.HandleDoorOpen(m));
OnRaw(NetMessageType.MapMarker, payload => { /* stamp sender, then RelayStamped(...) */ });
```

`ProcessInboundMessage` then, for every packet:

1. On the host, drops it if the sender is unknown or refused, has not completed
   its handshake (anything but `Handshake`), or sent a `[HostOnly]` type.
2. Runs the handler inside `NetworkApplyGuard` (`IsApplyingRemoteState` is true
   for *every* inbound message, so it never means "came from the host").
3. Relays a forwardable client message unless the handler threw, could not
   parse it, or called `SuppressRelay()`. `RelayStamped(...)` replaces the raw
   body with a host-corrected copy. The relay keeps the inbound delivery method.
4. Clears all per-message state. `CurrentReceivePlayerId` is the sender only
   while a handler runs; it is -1 everywhere else, so deferred work must carry
   the sender itself.

Host handlers use `CurrentReceivePlayerId > 0` to mean "a client sent this",
validate against the host's own world, and on rejection call `SuppressRelay()`
(and send the client a correction or refund where the client acted
optimistically).

## Sending

`LanNetworkManager` exposes `SendToPlayer`, `SendToAll`, `SendToAllExcept`,
`Broadcast` (host: everyone, client: the host) and the hot-path variants
(`BroadcastHot`, `SendRawToReadyPeers`). All of them go through one fan-out
loop over the active `IPeerTable`, which skips refused peers and (Steam) lobby
members that have not proved the host password. Unreliable streams are chunked
below the MTU by their senders; LAN promotes an oversized unreliable packet to
reliable as a backstop, and the Steam reliable backlog is capped per connection.

## Session lifetime

- `ModRuntime.Network` is the one way to reach the network manager (null when
  the mod is not running). `NetGuard.Connected/Host/ConnectedHost(out net)` is
  the standard check at the top of a sync patch.
- Per-peer bookkeeping lives in `LinkState` (replaced whenever the transport
  stops: network stop, host migration, soft reconnect) inside `SessionState`
  (replaced on `StopNetwork`). Add new per-peer or per-session collections there
  instead of to `LanNetworkManager`.
- Static state: register a reset with `NetworkResetRegistry` in
  `ModRuntime.RegisterNetworkResets`, or mark the declaration `// process-scoped` (or
  `// reset-in: Method` when another partial resets it). `StaticStateResetTests`
  fails on any static in the runtime folders that does neither.

## Harmony patches

- Patches are applied class by class. A class that fails is logged by name; if
  it is not `[OptionalPatch]`, Host and Join are refused for that run.
- Every patch no-ops outside a session unless it is pure vanilla parity. The one
  exception is the cosmetic seeding (`Sync.CosmeticRolls`): it also runs offline in a
  co-op world (a slot with `savcos.dat`), so a co-op world keeps its looks between
  sessions. A single-player world runs as vanilla.
- A flag set in a Prefix and cleared in a Postfix also needs a Finalizer
  (Harmony skips the Postfix when the original throws). A Prefix that skips an
  `IEnumerator` method assigns `__result`. A Finalizer that returns null must
  test for a specific exception type. `HarmonyPatchRulesTests` checks all three.
- Some hot targets have one patch class that runs every feature in a fixed order
  (`Core.AddPrefab` per overload, `DialogueWindow.displayNextBoard`); add to
  those classes instead of adding a second patch on the same method.

## Tests (`DarkwoodMP.PathB.Tests/`)

The test project compiles the pure parts of the mod (policy helpers, every
message file, the wire codec) against small stubs for the few game types they
touch, plus whole-tree rule tests that read the sources:

- `WireSymmetryTests`: every message re-serializes to the same bytes with nothing
  left unread; `CarriedStateRoundTripTests` / `MessageRoundTripTests`: values
  survive the wire.
- `DispatchCoverageTests`: every message type has exactly one handler.
- `HarmonyPatchRulesTests`, `StaticStateResetTests`, `ProductInvariantTests`:
  the rules above, plus a few tree-wide bans.
- `ReleaseConsistencyTests`, `ConfigDocTests`: version, README, CHANGELOG and
  CONFIG.md agree with the code.

CI runs them on every push and pull request to `dev` and `main`. The mod DLL
itself needs a local game install to build (see the README).
