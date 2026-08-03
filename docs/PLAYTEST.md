# YokWare Branch — Path B playtest checklist

**Product:** 0.7.7 Path B (Horde LAN, protocol 19) — older **0.9.x** checklist titles below are historical mislabels  

**Ship loader:** BepInEx 5.x  
**Optional:** MelonLoader dual-build  

**Machine paths (agents):** see repo root [`AGENTS.md`](../AGENTS.md) — Steam host, SecondDarkwood client, decompile, both `LogOutput.log` paths.

Both machines: same game build, **same loader family**, **same mod DLL**, host **in-chapter** before client JOIN.

---

## 0. Boot

### BepInEx (default)

```bash
dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx
# auto-deploys Steam + SecondDarkwood plugins if present
```

- [ ] Plugin loads (`YokWare Branch` / protocol 19 / 0.7.7)
- [ ] SecondDarkwood log: `Save root override` → `Darkwood_Second` (dual-box)
- [ ] Title **MULTIPLAYER** injects; F2 settings; Ctrl+C chat

### MelonLoader (optional)

```bash
pwsh scripts/fetch-melonloader-refs.ps1
dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader
# → bin/Release/MelonLoader/DarkwoodMP.Mod.dll → Mods/
```

- [ ] Mod loads; log `Loader: MelonLoader`
- [ ] Do **not** mix BepInEx plugin + Melon Mods DLL on the same process

### Automated (no game)

```bash
dotnet test DarkwoodMP.PathB.Tests -c Release
dotnet test DarkwoodMP.Protocol.Tests -c Release
dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx
dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader
```

- [ ] All green

---

## 1b. Steam SNS smoke (0.7.28+)

Steam path uses **SteamNetworkingSockets** (not classic P2P). Both peers need Steam logged on + same protocol 19 DLL.

1. Host: MULTIPLAYER → **HOST STEAM** → wait for lobby id in SETTINGS
2. Host: invite overlay (or paste lobby id to friend)
3. Client: **JOIN STEAM** with lobby id (or accept invite / `+connect_lobby`)
4. Expect: `Steam SNS peer connected` / `Steam SNS connected to host` then Handshake + proxies
5. Host leave / disconnect: client tears cleanly (`Steam host left` / connection lost)

- [ ] Host create lobby + SNS listen
- [ ] Client join (password + proto match)
- [ ] In-world proxies + entity sync
- [ ] Host leave → client disconnect (no LAN migration on Steam)

Voice (Steam Voice API; works on LAN too if Steam is up): default PTT **V**, open-mic via config `VoiceMode=open`. Walkie: craft `walkie_talkie` (2 scrap + 1 nail, WB lvl 1), hold + RMB for radio TX.

---

## 1. Join pipeline (critical)

1. Host: MULTIPLAYER → HOST → load/continue → **in world**
2. Client: MULTIPLAYER → JOIN (IP/port/password match) → stay on title
3. Expect host: phase 1 share → client disconnect (expected) → phase 3 reconnect `AlreadyInWorld`
4. Expect client: receive slot 5 → offline `initLoadGame` → wait playable → reconnect

- [ ] Share completes (savs + sav)
- [ ] Host does **not** freeze during client load
- [ ] After reconnect: both see partner proxy; bulk after ~1.5s settle

Logs: both `BepInEx/LogOutput.log` (Support preset).

---

## 2. Session smoke (15 min)

- [ ] Chat: Ctrl+C → type → Enter / SEND / Esc
- [ ] Drag furniture: scrape starts when **moved**, not on grab
- [ ] Push furniture: scrape like drag (motion-gated)
- [ ] Container dual-loot same slot: loser sees “Already taken…”, no dupe
- [ ] Death bag: die → bag both sides → empty → gone both

## 3. Combat / night

- [ ] Melee/gun on enemies (host AI)
- [ ] Friendly fire on remote proxy
- [ ] Night death → spectator (F4); morning when all dead

## 4. Known intentional gaps

See root [README.md](../README.md) residuals table — host migration, credits end co-op, landmark seed lock L, SyncCheck deferred.

---

## 5. Deep-review regression (2026-07-28)

Dual-box after **0.9.2+ deep-review** fixes. Same DLL both machines; host in-chapter.

| # | Scenario | Pass if |
|---|----------|---------|
| 5.1 | **Night disconnect alive-leaver** — P1 night-dead spectating; P2 alive; P2 quits | P1 stays night-dead / spectating; **no** premature morning / skipDay |
| 5.2 | **Dream exit time + spectate** — shared dream; story end or cleanup | World clock matches host (no pre-dream freeze snap); survivors exit spectate |
| 5.3 | **Trade stock 2p** — client buys last item or sells into shop | Host + peer NPC stock identical; no client-only restock |
| 5.4 | **SceneLoad cannot client-force** — (debug) client sends credits SceneLoad if possible | Session continues; only host can end to credits |
| 5.5 | **SaveSync storm** — rapid ManualSave / autosave from both peers within ~5s | No multi-second hitch loop; client logs show host fan-out only |
| 5.6 | **Host workbench upgrade sync** — host upgrades workbench in hideout | Client sees same workbench level + recipes without local upgrade |
| 5.7 | **Shotgun FF multi-pellet** — FF on; client A shotguns client B once | B takes damage once (or debounced), not per-pellet multi-tick |

- [ ] 5.1 Night alive-leaver
- [ ] 5.2 Dream exit time + spectate
- [ ] 5.3 Trade stock 2p
- [ ] 5.4 SceneLoad client cannot force
- [ ] 5.5 SaveSync storm
- [ ] 5.6 Host workbench upgrade
- [ ] 5.7 Shotgun FF multi-pellet

---

## 6e. Dream door fire + scrape/footsteps/transition (0.7.7)

- [ ] Client log: `applied 'onEnterLocation_dream_underground' ... firedNow=True` (not silent no-op)
- [ ] Client door opens with dream dialogue (`welcome_opening_dream` — not normal bunk lock text)
- [ ] After entry video, no overworld flash/gap before dream fade-in
- [ ] Client push/drag scrape is single (not doubled)
- [ ] In dream, peer footsteps are positioned/volumed; fade with distance (no hard cut)

## 6d. Dream enter timing / black hold (0.9.6)

- [ ] Client log: `onEnterLocation_dream_underground` applied **after** `Player positioned` / `startDreaming` (not before `Dream scene loaded`)
- [ ] Client door opens with dream dialogue (`welcome_opening_dream` / lookKeyhole_dream path — not normal bunk “Locked. It's my only way out”)
- [ ] After entry video ends, client stays black until dream loads (no overworld flash)

## 6c. Dream pad GE / black / entry audio (0.9.5)

Dual-box bunker dream (client-initiated preferred):

- [ ] Client log: `onEnterLocation_dream_underground` applied at dream pad coords (~-75000), **not** (-6342,…)
- [ ] Client door dialogue matches host (dream act1 / open path — not overworld bunk talk)
- [ ] Host screen recovers after entry video (no permanent black) when client starts the dream
- [ ] Client entry stinger stops; dream music not stacked under leftover transition audio
- [ ] Walking apart: client not blasted by host footprint ambients

## 6b. Dream sync playtest harden (0.9.4)

Dual-box — after bunker + random meadow soak:

- [ ] Host+client SessionId match in logs (`Starting session N` same N both sides)
- [ ] Random dream: host `Ending session … preset=` matches actual location (not previous bunker)
- [ ] Client exit: clock leaves dream time (not stuck at 900 until TimeSync)
- [ ] LocationEnter: at most a handful of dream LocationEnter lines (not ~180)
- [ ] Bunker dialogue door: same door open on both; no wrong wooden door
- [ ] Entry video/stinger: single play, not doubled/prolonged on client
- [ ] No `fov_trigger_*dream_*` GameEventsSync after dream end
- [ ] No DreamAudio `aimReturn` unresolved warnings during dream

## 6. Dream sync full harden (0.9.3) — prior

Dual-box; same DLL; protocol 19. Prefer Support logging on both.

| # | Scenario | Pass if |
|---|----------|---------|
| 6.1 | **GE startDream** — host fires dream GameEvent (e.g. home/church) | Both enter once; client log shows skip GE startDream under apply guard; no double pad |
| 6.2 | **Solo dream death** — host alone in dream (peer not joined / disconnected) dies | Dream ends via vanilla transition; not stuck spectating |
| 6.3 | **Doctor / transfer chain** — enter dream that transfers to next pocket | Inventory/time copies preserved across pocket; single DreamChainStart; no wipe |
| 6.4 | **Client story end** — client triggers endDream outcome | Host runs initiateEndDreaming; both exit; if host rejects, client gets `rejected:*` or timeout cleanup |
| 6.5 | **All-dead** — both die in dream | Host ends via initiateEndDreaming (transition), not hard-cut; both wake |
| 6.6 | **Epilog 1a** — enter `epilog_part1a_dream` as client | Road `outside_roadToHome_01` gone; inEpilogue UI; crawl death not dream-spectate |
| 6.7 | **Peer drop mid-dream** — client quits while both in dream | Host session ends cleanly; no zombie freeze |

- [ ] 6.1 GE startDream no dual-fire
- [ ] 6.2 Solo death teardown
- [ ] 6.3 Chain inventory intact
- [ ] 6.4 Client story end / nack recovery
- [ ] 6.5 All-dead transition
- [ ] 6.6 Epilog 1a remote parity
- [ ] 6.7 Peer drop mid-dream
