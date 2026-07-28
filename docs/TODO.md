# Residuals (Path B)

**Product:** YokWare Branch **0.7.7** · Horde protocol **19**  
(Older **0.9.x** notes below were too ambitious — live line is **0.7.x**.)

Living detail: **CHANGELOG.md**, **DarkwoodMP.Mod/docs/COOP_COVERAGE.md**, playtest: **docs/PLAYTEST.md**.

**Default branch:** `main` on [github.com/Warexpor/DarkwoodMP-YokWare](https://github.com/Warexpor/DarkwoodMP-YokWare) — all further work lands here.

## Still open (parked)

- [ ] **Handlers.cs monolith** — split deferred; no behavior change this pass
- [ ] **Ironbark ↔ Horde bridge** — research only; not LAN load path (delete dead weight later)
- [ ] **Mid-dream host migration rehydrate** — refused/disconnect in 0.9.3; full rehydrate still unsupported
- [ ] **FlagSync allowlist** — intentional story path; grief vector accepted on trusted LAN
- [ ] **Embedded `msg.PlayerId` spoof** (other domains) — LocationEnter/Exit + chat + PlayerState fixed; audit remaining Forwardable payloads
- [ ] **Landmark placement full determinism** if share fails before gen (`WorldGenSharePatch` mitigates connected dual-gen; hard-block on terminal share failure)
- [ ] **Live dual/triple campaign soak** (human playtest) — **docs/PLAYTEST.md** §5 + §6
- [ ] **Credits continuous co-op** (by design: credits stop network)
- [ ] **SyncCheck / full InteractionLock / ItemState** (still deferred product)

## Dream sync (0.9.5)

0.9.4 soak left wrong overworld bunk GE, host black screen, client entry stingers. **0.9.5** root-cause fixes — see **CHANGELOG** `0.9.5`. Human sign-off: **docs/PLAYTEST.md** §6c.

## Deep-review playtest (not signed off)

Run dual-box (host + SecondDarkwood) after **0.9.2+** / **0.9.3** waves:

- **§5** — SaveSync, night disconnect, trade, SceneLoad, workbench, shotgun FF
- **§6** — Dream sync full harden (GE dual-fire, chain inventory, story-end nack, epilog 1a, peer drop)

## Removed / do not use

Historical matrices and obsolete plans were deleted as redundant with COOP_COVERAGE + CHANGELOG (SYNC_MATRIX, MERGE_MATRIX, old TODO archive, YOKYY_FEATURE_AUDIT, PLAN_INWORLD_AUDIO_FX, DEFERRED_FEATURES_PLAN, docs/LOGGING.md). Use `DarkwoodMP.Mod/docs/LOGGING.md` for logging.
