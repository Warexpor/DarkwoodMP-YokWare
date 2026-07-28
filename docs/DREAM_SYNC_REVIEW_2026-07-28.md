# Dream sync review vs vanilla (2026-07-28)

Code + decompile only. No log files. Interactive summary: Cursor canvas `dream-sync-review`.

## Status (0.9.3)

**All critical and high findings below are Fixed** in the 0.9.3 dream sync full harden. Protocol 19 unchanged.

## Verdict

Host `DreamSession` + remote `LoadDreamScene` is the right shape for skill/dialogue entry. The deepest bugs came from **three vanilla paths that fought that shape** (now addressed):

1. Client still executed `GameEvent` `startDream` / `endDream` when applying host `GameEventsFired` → **Fixed (C1)** via `GameEventDreamAuthorityPatch`
2. Dream death Prefix blocked `initiateEndDreaming` even when spectate/solo fallthrough did nothing → **Fixed (C2)**
3. Remote chain load did not set `switchingDream`, so `startDreaming` overwrote the real inventory snapshot → **Fixed (C3)**

## Vanilla single-player chain

| Stage | Call |
|-------|------|
| Triggers | `SkillsMenu.confirmSkills`, `DialogueWindow` startDream, `GameEvent` startDream, `DreamTransition.onFinishedVideo`, `WorldGenerator` tutorial, save resume |
| Enter | `prepareDream` → `saveCurrentPlayerState` → `OutsideLocations.prepareLocation` → `onLocationSpawned` → `startDreaming` |
| Exit | `initiateEndDreaming` → outcome `DreamTransition` → `endDreaming` |
| Chain | `wantToSwitchDream` (skips `endDreaming`) **or** `endDreaming` effect `transferToDream` with `switchingDream=true` |

Key invariant: while `switchingDream`, `startDreaming` **must not** call `saveCurrentPlayerState` again (`Dreams.cs` ~353–358).

## Mod MP chain (intended)

| Stage | Behavior |
|-------|----------|
| Host begin | `TryBegin` → `prepareDream` → `startDreaming` → `DreamStarted` |
| Client entry | `DreamEntryClientPatch` / `DreamStartRequest`, or wait `DreamStarted` → `ProcessRemoteDreamCoroutine` → `LoadDreamScene` → `startDreaming` under `IsApplyingRemoteState` |
| Client story end | Defer via `DreamEnded` → host `initiateEndDreaming` → broadcast `DreamEnded` → `ApplyRemoteDreamCleanup` (nack/timeout → forced cleanup) |
| Death | Spectate via `FinalDreamsceneManager` until all dead / story end; solo allows vanilla end |
| Chain | Host `DreamPrepareChainPatch` → `DreamChainStart` (SessionId) → client `switchingDream` + load |

## Critical — Fixed

| ID | Issue | Fix |
|----|-------|-----|
| C1 | GE startDream/endDream dual-fire on client | `GameEventDreamAuthorityPatch` skips those types under client NetworkApplyGuard |
| C2 | playerDeath + empty peer set stuck | Solo → allow vanilla `initiateEndDreaming`; never block+no-op |
| C3 | Chain wipe of client inventory snapshot | `ProcessChainCoroutine` sets `switchingDream=true` before remote `startDreaming` |
| C4 | Client story-end defer, no reject recovery | Host `rejected:*` DreamEnded nack + 15s client watchdog |

## High — Fixed

| ID | Issue | Fix |
|----|-------|-----|
| H1 | `wantToSwitchDream` never hits `DreamEndPatch` | `DreamWantToSwitchPatch` + ChainStart via `prepareDream` only |
| H2 | Dual ChainStart; client ignores SessionId | Single broadcast site; SessionId validate on client |
| H3 | All-dead hard-calls `endDreaming` | `initiateEndDreaming` + `AllowDeathEndPass` |
| H4 | Completions forever-ban re-entry | MirrorPoolRemove only; named re-entry allowed |
| H5 | epilog 1a road + forceSaveStatic | Remote load destroys road + forceSaveStatic |
| H6 | Client `getPreset("")` → null NRE | Abort client `prepareDream("")` without host pick |

## Also shipped (0.9.3)

- Last peer drop ends shared dream (living or dead local)
- Mid-dream HOST GRANT refused
- Stale SessionId drop on DreamStarted / DreamEnded / DreamChainStart

## Asset chain verify

Exact `transferToDream` dest prefabs live in `Resources/DreamPresets/*` (globalgamemanagers), not C#. Code path confirmed for any `transferToDream` / `wantToSwitchDream`:

1. Host sets next pocket (`SetChainedPreset`) via prepare / wantToSwitch
2. Single `DreamChainStart` (SessionId)
3. Client `OnDreamChain` → `switchingDream` → `LoadDreamScene` → `startDreaming` (no inventory re-save)
4. `MirrorPoolRemove` for pool parity

Known scenes (BuildSettings / ResourceManager): `dream_doctor_01` / `_02`, `dream_doctorTrap`, `dream_oneChance_01` / `_01_2`, `dream_morf_01`, epilog trio.

## Already correct (do not regress)

- Host random resolve + early `DreamSessionBulk`
- `DreamEntryClientPatch` → `DreamStartRequest`
- Dialog world-auth startDream + silent host prepare
- `ApplyRemoteDreamCleanup` inventory/time/uniqueObject/tutorial wake
- `CanonicalDreamLocationName` / live pad vs `*_done`
- `UnfreezeWorld(restoreTime: false)` after `timeCopy`
- Join reject while `DreamSession.IsActive`
