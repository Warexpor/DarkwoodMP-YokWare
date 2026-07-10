# YokWare logging guide (testers)

Product **0.9**. Format:

```text
[YokWare][LEVEL][Category] message
```

Levels: `INFO` · `WARN` · `ERROR` · `VERBOSE` (only if enabled).

## Enable verbose

In `%LocalAppData%\DarkwoodMP\config.ini`:

```ini
[Debug]
VerboseLogging = true
```

Or let the mod regenerate the file after updating.

## Categories to filter

| Category | When it matters |
|----------|-----------------|
| **Boot** | Wrong version / seed / loader |
| **Session** | Cannot host, reject, dedicated authority |
| **Join** | Late-join missing world state |
| **World** | Seed mismatch, clock |
| **Reliable** | Packet loss (resent↑ dropped↑) |
| **SyncCheck** | Stable shared-state desync |
| **Anim** | VERBOSE only — walk/weapon stream |
| **Config** | Config path / load failures |

## Healthy session sample

```text
[YokWare][INFO][Boot] YokWare Branch v0.9 | …
[YokWare][INFO][Boot] WorldSeed=12345 …
[YokWare][INFO][Session] Hosting on port 7777…
[YokWare][INFO][Session] Connected — local player id=1
[YokWare][INFO][Join] Queued N snapshot packets for player 1
[YokWare][INFO][World] Seed check OK (12345)
```

## Red flags

| Log | Action |
|-----|--------|
| `WORLD MISMATCH` | Same WorldSeed + NEW game both sides |
| `Connection rejected` + Ironbark | Both peers same wire version (v2) |
| `SyncCheck DESYNC` | Note field name; re-open door/flag or rejoin |
| `Reliable … dropped=` rising | Network loss; check firewall/UDP |
