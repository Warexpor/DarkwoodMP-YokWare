# YokWare Branch

**Release-ready Darkwood co-op** (product **0.9** — theoretical 1.0 polish).  
**Yokyy × Warexpor** — Yokyy’s multiplayer house, Warexpor / Horde sync design, united as **Ironbark Protocol v2**.

| | |
|--|--|
| **Product** | YokWare Branch **0.9** |
| **Wire** | Ironbark **v2** (`MessageId` u16, registry, no ActionEvent) |
| **Loaders** | **BepInEx** (default) **or** **MelonLoader 0.7.0** |
| **License** | [**GNU GPLv3**](LICENSE) |
| **Maintainers** | [Warexpor](https://github.com/Warexpor) (repo), **Yokyy** (co-author) |

This is the branch you run for real multiplayer testing: host/join UI, dedicated relay server, campaign-scale sync, and a documented playtest path.

---

## Credits

- **Yokyy** — original co-op architecture (Mod / shared Protocol / Server, hop reliability, join bulk, SyncCheck, chat).
- **Warexpor** — Horde remaster sync patterns, campaign feature union, Ironbark wire, dual-loader packaging, maintainer of this repo.
- Darkwood © Acid Wizard Studio — this mod is fan-made and unaffiliated.

See [CONTRIBUTORS.md](CONTRIBUTORS.md).

---

## Requirements

- **Darkwood** (Steam/GOG), PC
- **One** mod loader (do not install both on the same game folder):
  - [BepInEx](https://docs.bepinex.dev/) 5.x (Unity Mono), **or**
  - [MelonLoader **0.7.0**](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.0)
- For building: [.NET SDK](https://dotnet.microsoft.com/) 8+ (mod targets **net472**; server **net8**)

---

## Quick start (playtest)

### 1. Config (all peers)

On first launch the mod writes:

`%LocalAppData%\DarkwoodMP\config.ini`

Or copy [DarkwoodMP.Mod/config_template.ini](DarkwoodMP.Mod/config_template.ini).

**Critical for a comfortable first session:**

1. Set the **same non-zero `WorldSeed`** on every machine (default template: `12345`).
2. Each player starts a **new game** (not mismatched old saves).
3. Same mod **DLL build** and product **0.9** / Ironbark **v2** on everyone.

### 2a. Install — BepInEx

1. Install BepInEx into the Darkwood folder.
2. Build or grab `DarkwoodMP.Mod.dll` (BepInEx configuration).
3. Copy into `Darkwood/BepInEx/plugins/`.
4. Launch game → log should show `[YokWare][INFO][Boot]` with product **0.9**, loader **BepInEx**, and **Ironbark v2**.

### 2b. Install — MelonLoader 0.7.0

1. Install MelonLoader **0.7.0** into the Darkwood folder.
2. Build with MelonLoader configuration (below).
3. Copy `DarkwoodMP.Mod.dll` (and `LiteNetLib.dll` if packed next to it) into `Darkwood/Mods/`.
4. Launch game → Melon console shows the same `[YokWare][INFO][Boot]` banner with `Loader=MelonLoader`.

### 3. Session

| Key | Action |
|-----|--------|
| **F1** (or `MenuKey`) | Multiplayer menu — host / join / settings |
| **Left Ctrl + C** | Chat (when connected) |
| **F4** | Spectate target cycle (night death spectator) |

**In-game host:** one player hosts from the menu; others join host IP:port.  
**Dedicated server:** run `DarkwoodMP.Server` (relay); clients join the server; **time/sim authority** = lowest client id (nobody is `IsHost`).

---

## Build

Set game path (gitignored):

```xml
<!-- DarkwoodMP.Mod/GamePath.local.props -->
<Project>
  <PropertyGroup>
    <GameDir>C:\Path\To\Darkwood</GameDir>
    <!-- optional if not using libs/MelonLoader -->
    <!-- <MelonLoaderDir>…\MelonLoader\net35</MelonLoaderDir> -->
  </PropertyGroup>
</Project>
```

```text
# BepInEx (default) + server + tests
dotnet build DarkwoodMP.sln -c Release
dotnet test DarkwoodMP.Protocol.Tests -c Release

# MelonLoader 0.7 build (needs MelonLoader.dll refs)
pwsh scripts/fetch-melonloader-refs.ps1   # once — fills libs/MelonLoader (gitignored)
dotnet build DarkwoodMP.Mod/DarkwoodMP.Mod.csproj -c Release -p:Loader=MelonLoader

# Optional pack drop folders (player zips)
pwsh scripts/pack-release.ps1
# → artifacts/bepinex-plugins/
# → artifacts/melonloader-Mods/
# → artifacts/dedicated-server/
```

See [CHANGELOG.md](CHANGELOG.md) for release notes.

Outputs:

| Loader | Output |
|--------|--------|
| BepInEx | `DarkwoodMP.Mod/bin/Release/BepInEx/net472/DarkwoodMP.Mod.dll` |
| MelonLoader | `DarkwoodMP.Mod/bin/Release/MelonLoader/net472/DarkwoodMP.Mod.dll` |

---

## Dedicated server

See [DarkwoodMP.Server/README.md](DarkwoodMP.Server/README.md).

- Default mode: **reliable relay** (`AuthoritativeWorld=false`).
- Does **not** simulate enemies; game clients own `EntityState` / `EntitySpawn`.
- Ironbark v2 MessageId framing + hop reliability.

---

## Protocol & design docs

| Doc | Purpose |
|-----|---------|
| [docs/IRONBARK_PROTOCOL.md](docs/IRONBARK_PROTOCOL.md) | Wire doctrine (v2) |
| [docs/IRONBARK_MESSAGES.md](docs/IRONBARK_MESSAGES.md) | Message table |
| [docs/MERGE_MATRIX.md](docs/MERGE_MATRIX.md) | Feature coverage matrix |
| [docs/PLAYTEST.md](docs/PLAYTEST.md) | Comfortable smoke / playtest checklist |
| [docs/LOGGING.md](docs/LOGGING.md) | How to read/filter tester logs |
| [docs/SYNC_MATRIX.md](docs/SYNC_MATRIX.md) | Historical interaction matrix (v0.6 era; labels may lag Ironbark tags) |
| [docs/GAME_API.md](docs/GAME_API.md) | Patch targets vs game API |
| [docs/decompile docs/](docs/decompile%20docs/) | **Developer reference only** — not required to play |

---

## Logs (for testers)

Every line is filterable:

```text
[YokWare][INFO][Session] Connected — local player id=1
[YokWare][WARN][World] WORLD MISMATCH: …
[YokWare][WARN][SyncCheck] DESYNC …
[YokWare][ERROR][Session] Connection rejected: …
```

| Tag | Meaning |
|-----|---------|
| `Boot` | Version, loader, seed, keys |
| `Session` | Host / join / disconnect / time authority |
| `Join` | Join bulk snapshot |
| `World` | Seed mismatch, clock |
| `Reliable` | Ack/resend stats |
| `SyncCheck` | Digest desync |
| `VERBOSE` | Only if `VerboseLogging=true` in config |

Config: `VerboseLogging = true` under `[Debug]` for high-frequency net/entity lines.

## Known limits (honest 0.9)

These are intentional or deferred — not silent failures:

- **No host migration** — if the in-game host leaves, the session ends.
- **Physics free-body** — doors/movables/interactives covered; full rigidbody `PhysicsState` lane is reserved, not product-emitted (`Caps` do not claim it).
- **World location placement** — pro-chunk trees/objects are seeded; *which* chunk receives a named location/landmark can still diverge (order-dependent vanilla worldgen). Full fix is a large worldgen rewrite.
- **Player walk anim** — code self-heals unreliable anim state; confirm in live 2-box if a peer still freezes mid-walk.
- **Personal progression** — skills, hunger, private inventory detail stay per-player by design.
- **CI** builds protocol tests + dedicated server only (mod DLL needs local Darkwood assemblies).

---

## License

**GNU General Public License v3.0** — see [LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).

You may run, study, share, and modify this software under GPLv3.  
Darkwood game assets and the game itself are **not** covered by this license.

---

## Push / publish

Repo prep notes for the owner: [PUSH.md](PUSH.md).
