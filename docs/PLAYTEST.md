# YokWare Branch — comfortable playtest checklist

**Product:** 0.9 (release-ready / theoretical 1.0 polish)  
**Wire:** Ironbark v2  
**Build:** `dotnet build DarkwoodMP.sln -c Release`  
**Pack:** `pwsh scripts/pack-release.ps1`

Both machines: same game build, **same loader family**, same mod DLL configuration, **same non-zero WorldSeed**, **new games**.

---

## 0. Boot (pick one loader)

### BepInEx

- [ ] Plugin loads (`====== YokWare Branch` / `Loader: BepInEx`)
- [ ] Log: **Ironbark v2** MessageId u16
- [ ] F1 menu opens

### MelonLoader 0.7.0

- [ ] Mod loads from `Mods/` (`Loader: MelonLoader`)
- [ ] Log: **Ironbark v2**
- [ ] F1 menu opens
- [ ] LiteNetLib present if required (pack folder includes it)

### Config comfort

- [ ] `%LocalAppData%\DarkwoodMP\config.ini` exists or template applied
- [ ] `WorldSeed` same and non-zero on all peers
- [ ] `LootShareMode`, `NamedNpcScaleEnabled` present
- [ ] Boot log shows `[YokWare][INFO][Boot]` banner with seed + loader
- [ ] Mismatched Ironbark version → clear reject (not hang)
- [ ] Optional: `VerboseLogging=true` only when deep-diving (noisy)

---

## 1. Session (15 min)

- [ ] Host + 1 client: join, chat (LeftCtrl+C), disconnect clean
- [ ] Late join gets doors / drops / barricades (join bulk)
- [ ] Optional: dedicated server join; time authority log on lowest id

## 2. World / economy smoke

- [ ] Door open/close both ways
- [ ] Container close reconciles; trade stock stays absolute after buy
- [ ] Drop item → partner sees it
- [ ] Death bag visible to partner

## 3. Combat / night smoke

- [ ] Melee/gun on enemies (authority)
- [ ] Friendly fire hits remote clone
- [ ] Muzzle flash on partner clone (`FiredWeapon`)
- [ ] Night death → spectator (F4 cycle); morning when all dead

## 4. Story smoke (if campaign save)

- [ ] Shared dream or dialog outcome applies on authority
- [ ] Flags/journal move with story actions

## 5. Automated (no game)

```text
dotnet test DarkwoodMP.Protocol.Tests -c Release
dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx
dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader
```

- [ ] All green

---

## Known intentional gaps

See root [README.md](../README.md#known-limits-honest-09) — host migration, PhysicsState free-body, location placement residual, personal skills.
