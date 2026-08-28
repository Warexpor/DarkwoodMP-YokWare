# Changelog

## Versioning

The current product line is `0.7.x`. The plugin and display version are
**0.7.81**. The current Horde wire protocol is **25**.

This file is a public ship log. Code-only status and runtime status are called
out separately. A runtime item is not considered verified until it has been
tested in the game.

---

## 0.7.81: Ordered snapshots and authority hardening

This release closes several state-ordering, authority, and lookup gaps found
during the stabilization pass.

### Changed

- `PlayerState`, `EntityState`, and `PhysicsState` now carry per-sender
  sequence numbers. Duplicate and older unreliable snapshots are ignored.
  Reliable physics events use a separate sequence stream.
- Host combat derives the attacker from the receiving peer and uses the host
  proxy for attack position and range checks. Invalid positions, damage,
  target types, player IDs, and unknown victims are rejected.
- Client AI suppression applies to `Sniffer` and `AIPath` components without
  requiring a `Character` lookup.
- Active dream objects are resolved under `Dreams.dreamLocation`. Cleanup no
  longer uses global name lookup that could select an overworld twin.
- Primitive and byte-array packet reads now reject truncated or unreasonable
  payloads instead of accepting unsafe lengths.

### Verification and deployment

- The Path B tests cover sequence ordering and wraparound, combat validation,
  component-level AI guards, dream lookup policy, and malformed packets.
- Protocol **25** is required on both peers because the three snapshot payloads
  changed.
- This documentation pass does not rebuild or deploy the game mod. Existing
  build output from the stabilization pass is not treated as a fresh build.
- Dual-box combat, dream, late-join, and packet-reordering runtime checks are
  still pending. Static and unit tests do not verify Unity lifecycle behavior.

### Deferred

- Host migration during a dream.
- Speculative gameplay rewrites.
- Broad linker and output-size optimization.

## 0.7.80: Split-map host simulation

Host and clients can occupy different locations without the host dropping the
other player's world grid, entities, or location.

- Remote player bubbles remain entered after vanilla location transport.
- Proxy culling checks every relevant grid, not only the host's current grid.
- Object registration uses the grid containing the position.
- A location remains active while a remote player is inside it.
- Return-to-world no longer snaps a proxy that remains in a location.
- Night-death counting includes handshaked peers whose proxies have not spawned.
- LAN and Steam use the same dream-join rejection rule.

Protocol 24 remained unchanged. Runtime coverage for dense three-player
split-map sessions and late-join story state remains pending.

## 0.7.79: Dialogue world authority

Dialogue choices now have one world-state writer.

- Clients defer flags, world events, transport, shared reputation, and map
  markers while applying a dialogue board. The host applies those outcomes.
- Linear and source boards are replayed on the host. Destination boards remain
  available for keyhole and portrait chains.
- Journal entries and journal item identity are shared. Physical item rewards
  remain personal to the speaking player.
- Shared NPC reputation is applied once on the host. NightTrader and TheThree
  reputation remains per-player.
- Dialogue tree state is flushed by the host after the authoritative apply.
- Remote location requirements can use a proxy, and bag item requirements use
  `PeerHasItem` message 133.

Deferred: spectator dialogue UI, welcome and gossip randomness, late-join
GameEvent bulk, proxy field-of-view parity, and GameEvent code paths that
bypass `NPC.set_reputation`. Runtime dialogue verification is pending.

## 0.7.78: Entity host-player identity

Host AI, sight, culling, and special attacks now account for remote players.
Forest spirit, banshee, shooter, hit-and-run, and nearest-player paths no
longer assume that `Player.Instance` is the only player. Client AI remains
disabled; clients present host snapshots.

Protocol 23 remained unchanged. Runtime coverage is pending.

## 0.7.77: Repository cleanup

Removed the unused Path A archive, research-only trees, scratch files, and
superseded audit documents from the ship repository. The Path B mod, tests,
entity spawner, scripts, tools, and current docs remain.

No mod behavior or protocol changed.

## 0.7.76: Dialogue overlay and dream-door audio

- World-only dialogue now hides the full dialogue background and clears stale
  presentation state.
- Leave-door GameEvents own the dream door sound. A second `DoorOpen` path no
  longer plays the sound twice.

## 0.7.75: Dream dialogue presentation and forest spirit

- Delayed portrait changes no longer restore another player's dialogue
  presentation after a world-only dialogue drain.
- Dream leave-door handling records the GameEvent as the owner of the open
  operation and suppresses duplicate force-open audio.
- Forest spirit attacks retain their assigned player instead of retargeting
  to a different peer.

## 0.7.74: Beartrap removal hitch

Trap removal is debounced before scene searches, captures names before
destruction, and avoids unnecessary item scans. Generator turn-on performance
was not changed.

## 0.7.73: Join work and share-pack scheduling

- Heavy late-join searches run one type per frame.
- World-share file reads and compression run away from the main thread.
- Player animation libraries are sent only when the library changes.

The first join can still cost time for world loading and vanilla save work.
No claim is made that all join hitches are fixed.

## 0.7.72: Host push audio and trap destruction

- Physics snapshots no longer suppress host-observed scrape when a client is
  pushing an object.
- Beartrap disarm and destruction now reach the other peers through the
  destruction path.

## 0.7.71: Scrape and trap path diagnostics

Scrape ownership now follows drag and contact state rather than proximity.
Network stop only stops the mod-owned moving-object sound. Trap disarm sends
its removal state from the actual disarm and progress completion paths.

## 0.7.70: Menu hint, scrape, and beartrap disarm

Removed the title-screen hosting hint. Client-owned scrape no longer competes
with the host echo. Silent beartrap disarm applies the triggered and inactive
state without relying on a name filter.

## 0.7.69: Dream party-once rules

Completed named dreams are rejected for the current party, skill-dream flags
are enforced before the vanilla check, and completed random presets are kept
out of the refill pool.

## 0.7.68: Dream re-entry and abort cleanup

Dream re-entry loads completed named dreams through the normal remote path.
Failed pad loads, disconnects, rejected requests, and join attempts during
entry now clear the session and freeze state. Dream door fan-out is held until
the active pad exists.

Deferred: a fallback for a lost dream-chain packet, portrait edge cases on
remote load, and fresh dual-box re-testing.

## 0.7.67: Steam host grant and migration

Steam peer rosters now carry Steam IDs. Graceful and crashed host departure
use the same lowest-player-ID election model as LAN, with SNS reconnect to the
elected host.

## 0.7.66: Restore-self safety

Restore Self is available only in a chapter, only for a client, and only when a
usable campaign-scoped backup exists. It now reports the backup result in the
settings UI.

## 0.7.51 through 0.7.65: Multiplayer menu presentation

The title multiplayer entry and its panel were aligned with the native menu:
labels, hover states, hitboxes, button sizing, fallback text, and embedded
wordmark assets were corrected. The panel now keeps connection progress on the
active join row and places recovery actions under settings.

These releases changed presentation and menu flow only. Protocol 23 remained
unchanged.

## 0.7.50: Wardrobe loot and client scrape

Empty furniture inventories are no longer mistaken for dropped pickups.
Drag-owned objects are excluded from the physics snapshot path so native
scrape audio does not compete with DragSync.

## 0.7.49: Fleeing entities and roaming presentation

Host `Character.removeMe` now sends entity despawn state for fleeing wildlife.
Client presentation restarts stopped locomotion clips and does not revive
corpses while waking entities.

## 0.7.48: Refactor and trust hardening

- Research-only projects were moved out of the ship solution.
- Remote forwards are checked against the actual sender.
- Container placement, trade, damage, friendly-fire, and save-share paths
  reject invalid or unauthorized requests.
- Repeated patch and tracker code was consolidated, and dead code was removed.
- LiteNetLib is supplied through NuGet version 1.3.5.
- CI paths and public build documentation were corrected.

The product line was normalized to 0.7.x. Runtime soak remained required.

## 0.7.47: Steam SNS join reliability

Steam joins received a longer timeout, early invite callback registration,
more tolerant lobby-member timing, and a relay warm-up retry.

## 0.7.46: Death recovery and object lookup

Day-death recovery now leaves the stale location grid and refreshes the World
grid. Object destruction and light application gained inactive-object and
position-aware lookup paths. Dead combat targets are resolved for cleanup and
then rejected for damage.

## 0.7.45: Location transport and trap state

Client location entry uses a host transport request instead of moving the host.
Audio interest matches the entity interest range. Barricade and furniture
matching uses XZ distance. Trap disarm clears the local trap state safely.

## 0.7.40 through 0.7.44: Entity and audio stabilization

This group aligned entity interest, proxy culling, flee behavior, corpse
handling, client claims, and multi-listener audio. It also added pending
matching and grace periods for save-point entities and reduced duplicate
scrape paths. Remaining runtime checks were documented rather than treated as
closed by code inspection.

## 0.7.28 through 0.7.39: Steam, voice, dreams, and diagnostics

The project added SteamNetworkingSockets transport, optional Steam voice and a
craftable walkie item, then hardened dream entry and exit, campaign-scoped
client backups, dialogue authority, host clock authority, and world-share
failure handling. Performance probes were added for network polling, entity
application, pending queues, and scene searches.

## Earlier 0.7.x history

Earlier 0.7.x releases established the Path B host-authoritative foundation:
player and entity snapshots, world physics, locations, weather, map markers,
containers, dropped items, death bags, trade, reputation, construction,
combat, night death, dreams, chapter transitions, audio, spectator mode, and
world save sharing. The protocol increased as wire contracts were added; the
current value is 25.

### Ongoing verification

- Full dual-box and three-player campaign coverage is still a playtest task.
- Late-join one-shot and scenario bulk remain deferred.
- Wrong-save warning UI, full interaction-lock coverage, and some dream and
  spectator presentation edge cases remain deferred.
- Runtime behavior must be checked in both directions: host action observed by
  client, client action observed by host, late join, and players in different
  locations.
