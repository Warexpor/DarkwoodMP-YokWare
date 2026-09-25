# Dual-box playtest (0.8.6)

Runtime proof for YokWare Branch. Code-only green is not enough — hitch and
sync bugs need host + client logs.

## Before you start

1. Same DLL on both installs (`dotnet build DarkwoodMP.Mod -c Release` deploys
   when `GamePath.local.props` points at Steam + SecondDarkwood).
2. Menu shows **0.8.6** and protocol **25** on both boxes.
3. `BepInEx/config/com.yokware.branch.cfg` → `[Logging]` → `LogPreset=Support`
   (default). Restart after changing presets.
4. Clear or rotate old `LogOutput.log` if you want a clean session slice.

## Launch

| Role | How |
|------|-----|
| Host | Steam Darkwood via `darkwood-host` / `run_bepinex.sh` |
| Client | SecondDarkwood via `seconddarkwood` (LAN join) |

Handshake must complete (`[Perf] probe ON` appears on both once in-world).

## What to do in-session

Minimum soak (FPS / hitch gate):

1. Host + client in the same hideout/yard for ≥2 minutes of free movement.
2. Open/close a door, loot a container, fire a weapon, drag a body once.
3. Optional: one player walks far enough to stress entity + physics sync.
4. Quit cleanly on both (flush logs).

## Diagnosis (logs + this script)

Paths (Omarchy / this machine):

| Role | Log |
|------|-----|
| Host | `~/.local/share/Steam/steamapps/common/Darkwood/BepInEx/LogOutput.log` |
| Client | `~/Work/MyProjects/SecondDarkwood/Darkwood/BepInEx/LogOutput.log` |

```bash
./scripts/check-dualbox-perf.sh
# or: ./scripts/check-dualbox-perf.sh /path/to/host/LogOutput.log /path/to/client/LogOutput.log
```

- **PASS** — both logs have `[Perf]`, zero `[PerfCliff]`.
- **FAIL** — missing probe (not connected / wrong preset) or cliffs present.

Triage field meanings: `DarkwoodMP.Mod/docs/LOGGING.md` (stutter section).

| Tag | Meaning |
|-----|---------|
| `[Perf]` | 2s window summary (fps, poll, upd, entApply, pktRx, …) |
| `[PerfCliff]` | Single frame ≥100ms **or** window fps~ &lt;15 |
| `[PerfSeg]` | Update sub-segment spike ≥25ms (names the hot segment) |

**Host clean / client cliff:** compare `role=Host` vs `role=Client` lines —
client-side apply/interp is the first suspect. Reverse for host cliffs.

## Sync spot-checks (not covered by the perf script)

Reverse initiator and observer (see `.cursor/rules/gamedev-reverse-check.mdc`):

- Door / container / flag / GE: host did X → client saw it; client did X → host saw it.
- Late join while something is mid-state when that domain matters.

Coverage map: `DarkwoodMP.Mod/docs/COOP_COVERAGE.md`.
