# Changelog

All notable changes to **YokWare Branch** (product version **0.9**).

## 0.9 — release-ready (theoretical 1.0 polish)

### Wire
- **Ironbark Protocol v2** — `MessageId` u16 LE, registry, capabilities, ActionEvent removed
- Wave G: entity/player burn, explosion secondary FX, reliable `EntitySpawn`, liquid stop-burn
- Honest `Caps.Local` (no false PhysicsState claim)

### Play / loaders
- Dual build: **BepInEx** (default) and **MelonLoader 0.7.0** (`-p:Loader=MelonLoader`)
- Shared `ModBootstrap`; config default WorldSeed 12345 for first-session comfort
- Dedicated server: reliable relay; enemy truth = client EntityState/EntitySpawn

### Polish
- **Logging overhaul** — `[YokWare][LEVEL][Category]` filterable lines; `VerboseLogging` config; boot banner
- Player anim: unreliable self-heal resend + pause/fps apply hardening
- InteractionLock join bulk; docs/GPLv3/CONTRIBUTORS/PUSH prep
- CI: protocol tests + dedicated server build (mod needs local game assemblies)

### License
- **GNU GPLv3**

## Earlier

Prior work lived as Yokyy vessel + Horde design merge (trade C, spectator, ClientStateBackup, hop reliability, campaign typed domains). See `docs/MERGE_MATRIX.md` and `docs/TODO.md` archive.
