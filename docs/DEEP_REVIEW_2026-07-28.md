# DarkwoodMP-YokWare — Deep Review (2026-07-28)

> **Historical snapshot.** Live ship is **0.7.46** / protocol **22** — see root `CHANGELOG.md` + `docs/PLAYTEST.md`.

Static code audit of Path B Horde (protocol **19** at review time). **No** BepInEx logs or game-install directory reads.

Interactive summary: open the canvas beside chat  
`C:\Users\amicu\.cursor\projects\c-MyProjects-DarkwoodMP-YokWare\canvases\darkwoodmp-deep-review.canvas.tsx`

## Verdict

Mature host-authoritative LAN/Steam co-op. Core domains (time, combat PvE, containers, dialog defer, dream entry guards, 3-phase join) are in good shape. Remaining risk is **authority holes**, **dream teardown**, **night disconnect morning**, and **unautomated join/save** — not missing architecture.

| Metric | Value |
|--------|-------|
| Mod `.cs` | ~188 |
| Networking LOC | ~19k (`Handlers.cs` ~8.2k) |
| Wire IDs | 115 (`NetMessageType`) |
| COOP_COVERAGE | ~63 OK / 3 Partial / 0 Broken |
| Parallel unused | Ironbark `Protocol` + `Server` (no Horde bridge) |

## High (verified in source)

| # | Finding | Status | Close note (2026-07-28) |
|---|---------|--------|-------------------------|
| 1 | **`HandleSceneLoad`** — no role check; `SceneLoad` Forwardable → client force credits | **CLOSED** | Host ignores inbound; clients apply host-only; peer packets not forwarded (`EpilogueSyncPatches.cs`). |
| 2 | **`HandleNightDeathState`** — no role guard → client `AllDeadTrigger` grief | **CLOSED** | Host ignores peer `AllDeadTrigger`; clients apply host-only (`LanNetworkManager.Combat.cs`). |
| 3 | **Night disconnect morning** — alive leaver + night-dead survivor → `skipDay` | **CLOSED** | `NightDeathPolicy.ShouldResolveMorningOnDisconnect`; no remotes==0 false all-dead (`DeathStateTracker`, `CoopPolicy.cs`). |
| 4 | **Trade stock** — any role broadcasts NPC inventory after trade | **CLOSED** | Client → host only; host sole broadcaster; dialog lock + suppress Forwardable echo (`TradeSyncPatches`, handlers). |
| 5 | **DamageRedirect fail-open** — catch returns `true` → local `getHit` | **CLOSED** | Catch returns `false` (fail-closed); shared `ProxyCombatRelay` debounce (`ClientHitscanDamageRedirectPatch`, `ProxyCombatRelay`). |
| 6 | **Dream `UnfreezeWorld`** stomps clock after `timeCopy` | **CLOSED** | `UnfreezeWorld(restoreTime: false)` after time restore (`DreamSyncManager.cs`). |
| 7 | **Spectator** sticks after `ApplyRemoteDreamCleanup` | **CLOSED** | Cleanup exits spectate without position restore (`DreamSyncManager.cs`). |
| 8 | **Client `DreamEnded`** drives host `initiateEndDreaming` | **CLOSED** | Host validates living handshaked dream participants + session/preset (`DreamHandlers`). |

**All High findings closed in code** (protocol 19 unchanged). Human playtest for regressions: `docs/PLAYTEST.md` §5.

## Medium (selected)

- Embedded `msg.PlayerId` preferred over `_currentReceivePlayerId` (location/state spoof).
- Host blind `FlagSync` from clients (intentional story path; grief vector).
- Any-peer `SaveSync` full-save fan-out (hitch storms).
- Triple FF relay paths (ProxyDamage / collision / Hitscan).
- Worldgen landmark residual if share fails before gen (`docs/TODO.md`).
- `Handlers.cs` monolith; Ironbark dead weight for LAN.

## SESSION dream checklist

| Issue | Status |
|-------|--------|
| False-local-start (Prefix false + Postfix) | Mitigated (`__state`) |
| Death spam | Mitigated |
| Proxy `*_done` vs live pad | Mitigated (residual `GameObject.Find`) |
| Peer drop mid-dream | Mitigated (host-migration mid-dream still unsupported) |

## Fix order

1. ~~Role-guard SceneLoad + NightDeathState~~ **done**  
2. ~~Night disconnect: only resolve morning for dead leavers / remaining-all-dead~~ **done**  
3. ~~Host-only trade inventory broadcast~~ **done**  
4. ~~DamageRedirect fail-closed~~ **done**  
5. ~~Dream UnfreezeWorld(restoreTime:false) + exit spectate on cleanup~~ **done**  
6. ~~Validate DreamEnded / DreamStartRequest on host~~ **done**  
7. ~~Bind PlayerId to wire sender on host~~ **done** (LocationEnter/Exit, chat, PlayerState)  
8. ~~Debounce SaveSync~~ **done** (host 3s debounce; clients request-only)  
9. ~~Worldgen share-fail hard-block~~ **done** (`WorldSharePolicy`, join/slot picker)  
10. Dual/triple campaign soak — **open** (human; see `docs/TODO.md`)  

## Tests

PathB.Tests: policy + structure greps + dialog codec. Protocol.Tests: Ironbark only.  
**Missing:** join state machine, WorldSaveShare, SaveSync, chapter resume, host migration, Harmony/e2e.
