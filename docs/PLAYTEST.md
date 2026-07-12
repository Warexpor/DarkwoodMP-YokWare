# YokWare Branch — Path B playtest checklist

**Product:** 0.9.2 Path B (Horde LAN, protocol 19)  
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

- [ ] Plugin loads (`YokWare Branch` / protocol 19 / 0.9.2)
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
