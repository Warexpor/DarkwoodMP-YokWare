# Audit: logging coverage + DLL bloat / redundancy

Date: 2026-07-15 · Target: `DarkwoodMP.Mod` Path B (YokWare)  
DLL: ~**790 KB** (`DarkwoodMP.Mod.dll`) · Source: ~**46k LOC** / **186** `.cs` files

---

## 1. Logging audit

### 1.1 Architecture (what exists)

| Layer | Role |
|-------|------|
| `ModLog` | Categories (`LogCat`), levels, presets, redact, rate-limit |
| `ClientPerfProbe` | Client-only 2s `[Perf]` Event while co-op connected |
| `ModRuntime.LegacyInfo` | **Dev preset only** (rate-limited) — not Trace, not Support |
| Raw `Log.LogInfo` / mixed | Still present (~140 sites) |

**Presets (config default = `Support`):**

| Preset | Event cats | Trace | LegacyInfo |
|--------|------------|-------|------------|
| Public | Core/Net/Session/Dream/Death/Save | no | no |
| **Support (default bind)** | + Combat/Entity/World/Container | no | **no** |
| Dev | all Event | no | **yes** |
| Trace | all + Trace | yes | **no** (LegacyInfo gated on Dev only) |

Docs (`LOGGING.md`) still say default Trace — **config code says Support**. Dual-box banner often shows Dev because local cfg was set for playtest.

### 1.2 Call-site inventory (approx)

| API | Count | Notes |
|-----|------:|-------|
| `LegacyInfo(` | **460** | Dominates; **silent unless LogPreset=Dev** |
| `ModLog.Event` | 188 | Structured, survives Support |
| `ModLog.Warn/Error` | ~100 | OK |
| `ModLog.Trace*` | **5** | Almost unused |
| `VerboseLogging` gates | 121 | Trace preset only |

**Top LegacyInfo files:** Handlers (158), WorldPhysics (38), DreamSyncManager (23), ClientEntityInterp (19).

### 1.3 What we *do* log well (when Dev / Trace + Event)

| Interest | Coverage |
|----------|----------|
| Session / join / handshake / phase 1–3 | Good (`ModLog.Event` Session/Net) |
| World share / bulk late-join | Good (Legacy + some Event) |
| Client FPS breakdown | Good — `[Perf] poll/upd/entApply/findOfType/pktRx` |
| Host session lifecycle | Good Events |
| Steam lobby connect | Good Events on Steam path |
| Dreams / death / save | Categories exist + Events |

### 1.4 Gaps — **not** logged (or wrong layer) for stutter / co-op debug

These are the things that burned time in recent dual-box work:

| Need | Status | Why it matters |
|------|--------|----------------|
| **Host `[Perf]`** | **Missing** | Host was “clean” by feel only — no poll/entity-build numbers |
| **Per-msg-type RX counts** | **Missing** | `pktRx=90` but not Entity vs PlayerState vs Lure vs Phys |
| **Handler ms in poll** | **Missing** | `poll=110` not split (FOOT vs entity apply vs corpse) |
| **FOOT / scan cost** | Partial | `findOfType` count only; no ms, no type name |
| **Pending queue depths** | **Missing** | locks/lures/lights pending size over time |
| **Entity interest skip ratio** | Partial | in Perf when probe on; not host send size |
| **GC / frame hitch outside mod** | **Missing** | can’t tell Unity GC vs mod |
| **LegacyInfo under Support** | Silent | Default Support → most bulk/sync lines **gone** |
| **Trace under Dev** | Weak | Dev gets Legacy dumps; only 5 real Trace calls |
| **Lure apply** | Trace only now | Correct for spam; invisible on Dev unless MinLevel=Trace + cat |

### 1.5 Verdict: “do we log everything interesting?”

**No.** We log **a lot** under Dev (mostly unstructured LegacyInfo), and **just enough** under Support for session bugs — but **not** the structured signals needed to prove hitch root causes without re-adding code:

1. Host needs the same Perf probe (or lighter host variant).  
2. Poll needs **bucket times** (or top-N message type cost).  
3. FOOT needs **type + ms**, not only a counter.  
4. Default Support hides 460 LegacyInfo sites — playtest for stutters **must** be Dev or Trace + Perf.

### 1.6 Minimal logging upgrades (recommended, not done yet)

Priority order:

1. **HostPerfProbe** (or `ClientPerfProbe` → `CoopPerfProbe` with role tag) — entity tick, phys build, peer send count.  
2. **`pktByType` top 5** in client Perf line (rolling counters in `ProcessInboundMessage`).  
3. **`NoteFindObjectsOfType(Type, ms)`** — one field `footMs` + `footType`.  
4. **Pending depths** every 2s if any queue non-zero: `pendLure= N pendLock=…`.  
5. Optionally: promote 10–20 critical LegacyInfo join/bulk lines to `ModLog.Event` so Support packs still work.

Do **not** flood Support with full LegacyInfo — that recreates the FPS/log I/O crater.

---

## 2. Redundancy / bloat audit

### 2.1 Size reality check

| Artifact | Size | Note |
|----------|-----:|------|
| `DarkwoodMP.Mod.dll` | **~790 KB** | Large for a “small mod,” normal for full co-op |
| `LiteNetLib.dll` (deployed) | ~108 KB | Required for LAN |
| netfx facades in `plugins/` | **~ hundreds KB** | `System.Net.Http`, `System.IO.Compression`, etc. — often **unnecessary** deploy noise |
| Source Handlers.cs alone | **~385 KB** | God partial — maintenance bloat more than IL |
| Source WorldPhysicsSyncService | **~174 KB** | Second monolith |
| Patches folder | **~512 KB** / ~95 files | Harmony surface area |

**790 KB IL is not “wrong”** for 100+ message types and full game hooks. “Bloated” is more:

1. **God files** (Handlers / WorldPhysics / WorldSaveShare).  
2. **Duplicate work paths** (pending FOOT + bulk FOOT + live FOOT).  
3. **Deploy folder** shipping .NET Framework compatibility DLLs next to the mod.  
4. **Dual backend always compiled** (LAN + Steam) — correct product-wise, not dead weight.

### 2.2 What is *not* inside the Mod DLL (good)

| Project | In Mod? |
|---------|---------|
| `DarkwoodMP.Server` | No |
| `DarkwoodMP.Protocol` / Ironbark packets | No (Mod uses Horde `NetMessageType`) |
| `archive/` | No |

### 2.3 Real redundancy / cut candidates

| Item | Risk | Save |
|------|------|------|
| **God-class split only** (Handlers → files by domain) | Low if pure move | Maintainability, not KB |
| **Pending FOOT every-frame paths** (partially fixed) | Low | CPU, not DLL size |
| **Dual Melon + BepInEx entry** | Medium | Small; keep if dual-loader required |
| **Steam always linked** (`com.rlabrecque.steamworks.net` Private=false) | None to IL size | Game already has Steamworks |
| **System.* facades copied to plugins** | Low | Clean deploy; check csproj CopyLocal |
| **Dead NetMessage gaps / unused handlers** | Needs pass | Small IL; only if truly dead |
| **EntitySpawner separate plugin** | Already split | OK |
| **LegacyInfo spam paths** | Low | Runtime cost under Dev, not size |
| **WorldSaveShare ~1.5k lines** | High to rewrite | Only if feature can slim |
| **ClientStateBackup / migration / dream epilogue** | Product | Don’t cut without product decision |

### 2.4 Structural hotspots (code smell → future hits)

```
LanNetworkManager.Handlers.cs   ~385 KB  — all RX apply
WorldPhysicsSyncService.cs      ~174 KB  — build + apply + sound
WorldSaveShareService.cs         ~63 KB  — join file share
ClientEntityInterpolationService ~45 KB
~100 Harmony patch files
~100+ NetMessageType values
```

Redundancy pattern: **same world object found N ways** (OverlapSphere, FOOT, pending flush, bulk join scan). That’s CPU bloat more than DLL bloat — exactly what client hitch logs showed.

### 2.5 Verdict: “DLL too bloated?”

| Claim | Assessment |
|-------|------------|
| DLL should be ~200 KB | Unrealistic for this feature set |
| Something is wrong at 790 KB | **Not by itself** |
| Bloat that *matters* | God files, FOOT/pending duplication, **plugins folder facades**, Dev log spam |
| Safe size wins | Stop copying unused System.* to plugins; keep LiteNetLib; don’t IL-link Steamworks (already not) |

---

## 3. Recommended next steps (ordered)

### Logging (prove interest)

1. Extend probe → **host + client**, tag `role=Host|Client`.  
2. Add `pkt top` + `footMs` to Perf line.  
3. Pending queue Event when non-zero (rate 2s).  
4. Align docs/config: either Support default + “stutter triage = Dev”, or document clearly.  
5. Convert **join pipeline** critical LegacyInfo → `ModLog.Event` (10–15 lines max).

### Redundancy (shrink / simplify)

1. **Deploy hygiene:** ensure only `DarkwoodMP.Mod.dll` + `LiteNetLib.dll` (+ pdb optional) land in plugins.  
2. Continue **FOOT elimination** (cache, interest, OverlapSphere) — already started.  
3. Split Handlers by domain **only** when next feature touches that domain (no big-bang).  
4. Optional: dead-message / dead-patch pass with grep of handlers vs enum.  
5. Do **not** delete Steam or dream/save stacks for size.

---

## 4. Stutter-related log checklist (dual-box)

Use **both** logs, **same build**, quit clean.

| Preset | Use for |
|--------|---------|
| Support | Join/handshake bugs only |
| **Dev** | Current Legacy dumps + Perf (what you use now) |
| Trace | Only if hunting VerboseLogging-gated dumps |

Must see on client: `[Perf] … poll= … findOfType= … pktRx=`  
Still missing for proof: host Perf, per-type pkt, foot ms.

---

---

## 5. Implemented (2026-07-15)

Full-scope plan shipped:

- `CoopPerfProbe` + Host/Client wiring, pkt top, foot ms/type, pending depths, hostEntSend
- BulkSync join lines → Session Events
- LOGGING.md + ModConfig truth
- Deploy strips System.* facades from plugins

See CHANGELOG **0.9.2+ — Full logging audit + deploy hygiene**.
