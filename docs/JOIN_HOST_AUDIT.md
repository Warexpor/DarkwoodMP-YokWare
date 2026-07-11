# Host / Join system audit (Path B Horde)

## Intended happy path (strict order)

1. **Host:** MULTIPLAYER → HOST GAME → enter chapter (Player exists). Stays in-world.
2. **Phase 1 — share:** Client JOIN → transfer handshake → host **WorldSaveShare** → client writes **slot 5**.
3. **Phase 2 — enter world:** Client shows **ENTER WORLD**, then **disconnects transfer link**, loads host world offline (`initLoadGame` / `LoadScene`). Host stays playable.
4. **Phase 3 — co-op connection:** `ChapterSessionResume` soft reconnects; handshake `AlreadyInWorld=true` → host **skips re-share**, queues late-join bulk after first PlayerState (light dump + staggered heavy sticky world).

## Bugs found (root causes)

| ID | Severity | Bug | Fix |
|----|----------|-----|-----|
| J1 | P0 | JOIN only connected; never pushed save when host already in-world | Auto share on handshake + delayed 0.75s |
| J2 | P0 | Host starts LAN on title, client joins early → no world, client stuck | `HostEnterWorldSharePatch` on `Player.Start` resends to peers |
| J3 | P0 | Dual Steam/SecondDarkwood share **same AppData**; client apply to host profN **overwrites live host save** | Client always applies join package to **slot 5** |
| J4 | P1 | Journal bulk NRE on title menu aborted join pipeline | Null-safe queue |
| J5 | P1 | World share read files 1 frame after Save → empty/missing | Wait up to 5s for sav.dat/savs.dat |
| J6 | P1 | GetHostProfileId only `currentProfile` → 0 fail | Fallbacks: Active profiles, newest disk prof |
| J7 | P1 | Manual Resend to clients **already in-game** would LoadScene wipe | HandleBegin ignores if `Player` and not mainMenu |
| J8 | P2 | Profile path used string concat | `Path.Combine(persistentDataPath, 1_4Save, profN)` |
| J9 | P2 | savch.dat often missing (OK) but silent | Log skip missing file |
| J10 | P2 | UX still said “same slot” | Menu + session notes updated |
| J11 | P0 | Client `saveGameProfiles()` with partial `Core.profiles` **nuked `profs.dat`** — only one slot left; PLAY + HOST showed almost no worlds | Merge on-disk profile index first, re-register orphan `profN` with `sav.dat`, then save; one-time restore of user's index from disk + May backup |
| J12 | P0 | Host sent **journal/flags/bags bulk on connect** while client still on title → `Journal.addJournalEntry` NRE, inventory “No item type …”, broken load | Defer gameplay bulk until after world share; client queues until `Player` exists (`ClientCanApplyWorldBulk`) |
| J13 | P0 | Client LoadScene + host force-Save + late-join `FindObjectsOfType` + 500× proxy spawn spam → dual freeze, “event” hitch, stuck loading | Skip force Save if sav fresh; bulk only on first client PlayerState; gate proxy spawn during load; no afterNight clear from half-loaded packets |
| J14 | P0 | Path B **auto share on join** force-`Save()` (`Save static`) + `removeAfterNightEffect` + scenario bulk + immediate heavy bulk ≠ Horde base (no auto-share). Host “unique event” + freeze | Late-join share = **disk files only, no Save** (skewed sav/savs still force-save once); no afterNight clear; no scenario bulk on join; 8s settle before bulk; client mute net send until `coreStarted` |
| J19 | P1 | sav/savs on-disk skew after long host session → client SaveManager NRE / stuck `loadingGame` | Late-join pack force-saves once when pair incomplete or mtime skew &gt; 30s |
| J20 | P1 | Light late-join dump skipped sticky world (bags/drops/barricades/gas/locks/construct/trade/weather) | Light dump + registry; heavy FindObjects **staggered one phase/frame** (`TickHeavyLateJoinBulk`); scenario still skipped |
| J21 | P1 | Phase-3 full StopNetwork re-purged entities / FPS crater | Soft `ConnectToHost` when already in-chapter; client entity interest cull |

## Residual status (2026-07-11)

| Issue | Status |
|-------|--------|
| Dual-box AppData | **Fixed** — SecondDarkwood auto → `Darkwood_Second`; optional `Saves.SaveRootOverride` |
| Container H6 dual-loot | **Fixed** — host validate + `ContainerTakeDenied` refund (msg 115) |
| Landmark dual-gen | **Mitigated** — client cannot finish new worldgen while connected; full placement seed lock still L |
| Credits ends co-op | **By design** — epilogue; no CaptureForResume |
| Host grant on host crash (LAN) | **Fixed (v1)** — PeerRoster 123 / HostHandoff 124; elect lowest id (`HostMigrationEnabled`) |
| SyncCheck (Yokyy digest) | **Deferred** (feature, not a one-patch residual) |
| Password/IP/firewall | Config/ops |

## Client pull (Yokyy RequestWorld equivalent) — shipped

| ID | Status | Behavior |
|----|--------|----------|
| J15 | **Fixed** | Client on title after handshake with no download: auto `WorldRequest` (msg **114**) at **10s** and **25s**; JOIN while CONNECTED also sends request (12s rate limit). Host → `ScheduleHostShareToPlayer` if in-world; else log (no force Save). |
| J16 | **Fixed** | Client apply wrote prof5 then `LoadScene` **without** `SaveManager.updateFilePaths()` → load used wrong paths → `getGOsFromID` NRE in `SaveManager.Load`; client wedged mid-load while still connected. Host kept blasting PlayerState/physics/entity to a peer that stopped PollEvents → dual-box host freeze until client killed. Fix: `updateFilePaths` + prefer `UI.initLoadGame`; host mutes gameplay flood to `_peersLoadingWorld` until first PlayerState. |
| J17 | **Fixed** | Strict order: **share → offline enter world → co-op reconnect**. After share, client `CaptureForResume` + `StopNetwork` then load; reconnect handshake `AlreadyInWorld` → host skips re-share, only late-join bulk. |
| J18 | **Fixed** | Phase-3 joined **before** save finished: `sceneLoaded` + 1.25s raced `SaveManager.Load` (reconnect then "Load game ver"). Proxy spawned at **local feet** (body stack). 8s bulk settle on already-playable reconnect. Fix: wait until playable Player; park proxy far below until first PlayerState; 1.5s settle for phase-3; skip mid-night death on expected transfer detach. |

## Verify in logs (Support preset)

Host:
- `Join pipeline phase 1: client 2 handshaked on title — scheduling world share`
- `World save share complete`
- `Player 2 disconnected` (expected — client offline load)
- later: `Join pipeline phase 3: peer N already in world — skip world share`
- `gameplay-ready (first PlayerState)` / late-join bulk

Client:
- `Join pipeline phase 1: transfer link up`
- `Receiving host world for profile slot 5` / wrote files
- `Join pipeline phase 2: … disconnect transfer link` / `initLoadGame` or LoadScene offline
- `[ChapterResume] waiting for offline load…` then `client playable after Ns — phase 3`
- `co-op reconnect (AlreadyInWorld)` / Handshake OK phase 3
- Host: bulk settle **1.5s** (`phase3 reconnect`), not 8s; then light bulk + staggered heavy (`Late-join heavy bulk complete`)
