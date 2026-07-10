# Changelog

## 0.9.2 — Path B audit fixes + dialogue tree sync — 2026-07-10

Release-quality closure of critical multiplayer audit findings (C1–C4, H1, H3, H5). Product version stays **0.9.x** on purpose.

### Dialogue tree (Yokyy DialogueSync port)

- **Consumed-node sync:** On real `DialogueWindow.close`, peers broadcast `CharacterDialogue` tree state (`alreadyShown` / `disabled` / special options / portrait + NPC wantsToTalk/rep) via `DialogTreeState` (msg **113**). Host fans out; late-join bulk includes progressed trees.
- Codec is Yokyy v2-compatible (`DialogTreeWireCodec`); outcomes path still uses `DialogOutcomeSync` + world-only guard.

### Critical fixes

- **C1 Host-only time authority:** Clients no longer dual-simulate the SP day/night chain. `Controller.FixedUpdate` time ticks and `refreshTime` edge handlers (`startDay` / `startAfterNight` / night `setMe`) are suppressed on connected clients. Host `TimeSync` applies fields + `refreshTimeNoLogic()` only (clock UI / ambient).
- **C2 Dialog without host bag pollution:** Host application of remote dialog outcomes runs under `DialogHostApplyGuard` (world-only): personal `giveItem` / `removeItem` / journal rewards are suppressed on host `Player.Instance`. Flags, world events, NPC dialogue state, and reputation still apply under host authority.
- **C3 Chapter session continuity:** Chapter load still tears the scene network briefly, then **auto rehosts (host) / reconnects (client)** via `ChapterSessionResume` instead of permanent silent solo. Credits still end co-op permanently (documented residual).
- **C4 World share honesty + fail-loud:** Path B identity remains host **WorldSaveShare** (no fake per-chunk worldgen). Share failures surface loud `WORLD SHARE FAILED` status and send `Success=false` end packets so clients do not silently wander into divergent forests. Location/landmark placement residual remains known-limit.

### High fixes

- **H1 Client→host flags:** `FlagSync` is bidirectional — client story flag changes send to host; host applies and fans out to other peers (still under apply-guard so no echo loops).
- **H3 Partial night death:** Beyond skipDay/Save suppress, also blocks `transportToHome`, `respawnAllEnemies`, and death-path `despawnCharacters` while spectating a partial night death.
- **H5 NPC dialogue lock:** One active speaker per NPC (`DialogNpcLock` msg 112 + `initiateDialogue` / `close` patches). Different NPCs may talk in parallel.

### Product

- Version **0.9.2** / Display **0.9.2 Path B (audit fixes)** / Protocol **19** (Horde LAN; optional lock **112**, dialogue tree **113**).
- Pure policy helpers in `CoopPolicy.cs` (unit-tested without Unity).
- Structural + policy tests in `DarkwoodMP.PathB.Tests`.

### Known limits (not shipped as fixed)

- Live 2-instance campaign playtest still open.
- Location/landmark chunk *placement* residual (TODO #5) — heavy worldgen rewrite out of scope.
- Dedicated server Ironbark bridge ≠ Path B LAN peers.
- Host migration after host disconnect unsupported.
- Credits / post-credits co-op not resumed (by design residual).
- SyncCheck, full InteractionLock matrix, ItemState upgrade wire deferred.
- Container simultaneous loot races (H6) not fully locked.

## 0.9.1 — Path B (Horde base) — 2026-07-10

### Breaking / load path

- **Path B rebase:** shippable mod is **Horde remaster** host-authoritative sync, not the Yokyy-core merge.
- Prior Yokyy+partial-port tree moved to `archive/yokyy-merge-0.9/` (do not load).
- Live wire: **Horde protocol 19** (LiteNetLib). Ironbark remains in-repo for tests / future dedicated bridge only.

### Product

- Identity: **YokWare Branch** `0.9.1`, GUID `com.yokware.branch`, GPLv3, Warexpor & Yokyy credit.
- Log tags: `[YokWare/…]`; boot banner documents Path B.
- BepInEx Release build deploys to Steam + SecondDarkwood plugins; removes stale `DWMP_HordeRemaster.dll` if present.
- Feature inventory: `docs/PATH_B_FEATURE_INVENTORY.md`.
- Structural tests: `DarkwoodMP.PathB.Tests`.

### Deferred (documented, not silent)

- Ironbark live client wire, dedicated server↔Horde bridge, MelonLoader dual pack, Yokyy chat, SyncCheck, ItemState, full InteractionLock matrix, IsTimeAuthority elect.

## 0.9.0 — YokWare Branch (Path A merge) — archived

Ironbark v2, dual loader packaging, gap-closure patches on Yokyy structure. Superseded as load path by 0.9.1 Path B after brief testing showed Yokyy residual bugs and sound/sync regressions versus pure Horde.
