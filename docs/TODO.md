# Residuals (Path B)

**Product:** YokWare Branch **0.7.46** · Horde protocol **22**  
(Older **0.9.x** notes below were too ambitious — live line is **0.7.x**. Ship log: **CHANGELOG.md**.)

Living detail: **CHANGELOG.md**, **DarkwoodMP.Mod/docs/COOP_COVERAGE.md**, playtest: **docs/PLAYTEST.md**.

**Default branch:** `main` on [github.com/Warexpor/DarkwoodMP-YokWare](https://github.com/Warexpor/DarkwoodMP-YokWare) — all further work lands here.

## Still open (parked)

- [ ] **Handlers.cs monolith** — split deferred; no behavior change this pass
- [ ] **Ironbark ↔ Horde bridge** — research only; not LAN load path (delete dead weight later)
- [ ] **Mid-dream host migration rehydrate** — refused/disconnect in historical 0.9.3; full rehydrate still unsupported
- [ ] **FlagSync allowlist** — intentional story path; grief vector accepted on trusted LAN
- [ ] **Embedded `msg.PlayerId` spoof** (other domains) — LocationEnter/Exit + chat + PlayerState fixed; audit remaining Forwardable payloads
- [ ] **Landmark placement full determinism** if share fails before gen (`WorldGenSharePatch` mitigates connected dual-gen; hard-block on terminal share failure)
- [ ] **Live dual/triple campaign soak** (human playtest) — **docs/PLAYTEST.md** §5 + §6 + §7
- [ ] **Credits continuous co-op** (by design: credits stop network)
- [ ] **SyncCheck / full InteractionLock / ItemState** (still deferred product)
- [ ] **Workbench exclusive-open lock** — intentionally parked/no-op since **0.7.40** (handlers ignore wire)

## Recent closed (do not re-park)

See **CHANGELOG** **0.7.43–0.7.46**: entity present/death SFX/corpse, peer hear hysteresis, save-POI XZ interest, bunker `LocationTransport`, trap disarm release, wardrobe XZ destroy, dog aggro audio range, death→World grid hygiene, ObjectDestroy/LightApply match, corpse `PlayerAttack` null spam.

## Dream sync (historical 0.9.5 labels)

Historical soak notes used **0.9.x** labels — live line is **0.7.x**. Human sign-off still: **docs/PLAYTEST.md** §6c–§6e; bunker enter / day-death grid: §7.

## Deep-review playtest (not signed off)

Run dual-box (host + SecondDarkwood):

- **§5** — SaveSync, night disconnect, trade, SceneLoad, workbench, shotgun FF
- **§6** — Dream sync harden
- **§7** — Location enter, death grid, traps/destructibles (0.7.45+)

## Removed / do not use

Historical matrices and obsolete plans were deleted as redundant with COOP_COVERAGE + CHANGELOG (SYNC_MATRIX, MERGE_MATRIX, old TODO archive, YOKYY_FEATURE_AUDIT, PLAN_INWORLD_AUDIO_FX, DEFERRED_FEATURES_PLAN, docs/LOGGING.md). Use `DarkwoodMP.Mod/docs/LOGGING.md` for logging.
