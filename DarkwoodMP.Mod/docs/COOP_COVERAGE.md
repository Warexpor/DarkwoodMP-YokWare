# Co-op coverage checklist

This is a compact code-coverage and runtime-verification checklist for the
Path B Horde mod.

**Current baseline:** product `0.7.81`, protocol **25**, message IDs through
`PeerHasItem` (133), host-authoritative N-player LAN. Steam uses the same
message contracts through its separate networking transport.

Code coverage and runtime coverage are different:

- **Code covered** means the current source contains the intended authority and
  apply path.
- **Runtime pending** means a Unity dual-box or three-player check is still
  required.
- **Deferred** means the behavior is intentionally not implemented or is
  outside the current stabilization scope.

---

## Verification rules

For each domain, check:

1. host action observed by every client;
2. client action observed by the host and other clients;
3. a third player joining while the domain is active;
4. disconnect and reconnect cleanup;
5. players in different locations when location state matters;
6. duplicate, delayed, malformed, or out-of-order packets.

State must be keyed by `PlayerId`. Host fan-out must reach every ready peer,
not only the first connection. A late join must receive the relevant bulk
state without changing existing players' state.

---

## Coverage summary

| Domain | Main code paths | Current status |
|---|---|---|
| Session and handshake | `LanNetworkManager`, `ConnectionBackend`, `HostMigration` | Code covered; runtime pending |
| World share and saves | `WorldSaveShareService`, `ClientStateBackup`, `SaveSyncPatches` | Code covered; runtime pending |
| Clock and pause | `ClientTimeAuthorityPatches`, `SleepSyncPatches`, `TimeSync` | Code covered; runtime pending |
| Flags and reset | `FlagSyncPatches`, `NetworkApplyGuard`, `NetworkResetRegistry` | Code covered; runtime pending |
| Player state | `PlayerStateMessage`, player proxy and animation paths | Code covered; runtime pending |
| Entity AI and snapshots | `EntityStateBroadcastService`, `ClientEntityInterpolationService`, `ClientAIDisablePatches` | Code covered; runtime pending |
| Physics and world objects | `WorldPhysicsSyncService`, door, generator, trap, and drag handlers | Code covered; runtime pending |
| Locations and grids | `LocationEnter` / `LocationExit`, location visibility patches | Code covered; split-map runtime pending |
| Inventory and containers | container, dropped-item, death-bag, journal, and trade handlers | Code covered; runtime pending |
| Combat and threats | combat handlers, proxy damage, projectiles, shadows, night death | Code covered; runtime pending |
| Story and dialogue | `DialogOutcome`, `DialogTreeState`, `GameEventsFired` | Code covered; runtime pending |
| Dreams and epilogue | `DreamSession`, `DreamSyncManager`, dream door and scene paths | Code covered; runtime pending |
| Audio and spectator mode | player/entity audio, culling, spectator listener and grid | Code covered; runtime pending |
| Balance features | loot sharing and allowlisted dream NPC presence | Code covered; runtime pending |

---

## Current stabilization coverage

### Ordered snapshots

`PlayerState`, `EntityState`, and `PhysicsState` carry sequence metadata.
Unreliable duplicate or older snapshots are rejected per sender and session.
Reliable physics events use a separate ordering stream.

Runtime checks still needed: reorder snapshots on both host and client paths,
cross a session reset, and verify a late unreliable update cannot overwrite a
reliable door, trap, or generator event.

### Combat authority

The host derives the attacker from the receiving peer. It uses the host proxy
for attack origin and range checks, validates finite positions and damage,
accepts only known target types, and rejects unknown victims.

Runtime checks still needed: host attacks client, client attacks host, client A
attacks client B, melee and projectile paths, friendly fire on and off, and
dead or missing targets.

### Client AI suppression

Client suppression applies directly to `Sniffer` and `AIPath` components. It
does not require a `Character` component to be present.

Runtime checks still needed: inactive and component-only objects, remote
player proxies, and scene reloads.

### Dream and world scoping

When a dream pad is active, dream objects are resolved under
`Dreams.dreamLocation`. Cleanup does not use a global name lookup because the
overworld and dream copies can have the same names.

Runtime checks still needed: dream entry, leave-door dialogue, cleanup,
re-entry, late scene loading, and a peer remaining in the overworld.

### Packet validation

`NetReader` bounds primitive and byte-array reads. Snapshot decoders reject
invalid collection sizes or incomplete payloads where the message contract
requires the data.

Runtime checks still needed: truncated and oversized payloads through both
transports. Unit tests cover the shared reader and policy helpers.

---

## Authority decisions

- The host owns world simulation, combat damage, entity AI, story world
  mutations, and the day/night clock.
- Clients own their personal inventory, skills, and morning-trader reputation.
- Shared journal identity and shared story-NPC reputation are host-authoritative.
- Physical dialogue item rewards remain personal to the speaking player.
- Client AI and autonomous client time progression are suppressed while
  connected.
- Dream objects and dream cleanup are scoped to the active dream location.
- Host migration elects the lowest positive surviving `PlayerId`; dream
  migration remains deferred.

---

## Deferred or incomplete areas

- Full dual-box and three-player campaign soak.
- Late-join bulk for already-fired GameEvents and night scenarios.
- Wrong-save warning UI. World share failure is blocked, but the warning UI is
  not a complete save-identity solution.
- Complete interaction-lock coverage, including simultaneous container and
  crafting races.
- Exact proxy field-of-view parity for event-trigger sight checks.
- Some dream, spectator, and dialogue presentation edge cases.
- Host migration during an active dream.

Do not mark these items as runtime-verified from static or unit tests alone.
