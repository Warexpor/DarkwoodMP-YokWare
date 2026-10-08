# YokWare Branch

Co-op multiplayer for **Darkwood**. One player hosts their campaign and friends
join it: the same forest, the same nights, the same story, played together.

| | |
|--|--|
| Product | YokWare Branch **0.8.167** |
| Wire | Horde protocol **44** |
| Players | Up to 8 by default (`MaxPlayers`, host included) |
| Transport | LAN (LiteNetLib) or Steam lobby (SteamNetworkingSockets) |
| Loaders | BepInEx 5.x or MelonLoader 0.7.x |
| License | GPLv3, see [LICENSE](LICENSE) |
| Authors | Warexpor and Yokyy |

Every player in a session must run the same version: the protocol number is
checked when a player joins.

---

## How it works

Darkwood is built for one player. In this mod the **host's game is the world**:
it runs the creatures, the story, the clock and the save. The other players send
what they do to the host, the host checks it against its world and tells everyone
the result. A client sees the host's world and should barely notice that it is not
the host.

- **The world is shared.** Items are unique: what one player takes is gone for
  everyone, what one player opens is open for everyone. Story choices, the journal,
  NPCs and the workbench are shared, and so is the look of the world: random tints,
  flips and animations come out the same on every machine.
- **Each character is personal.** Every player keeps their own bag, health, skills,
  level, recipes, home oven and trader standing.
- **Single-player rules are adapted, not removed.** Menus no longer pause the world
  for one player (players in a dialogue, the level-up menu or the pause menu are
  protected instead); the world pauses when everyone is in the pause menu. A night death
  makes you spectate until morning instead of skipping the night for everyone. Each
  player plays their own prologue. Dreams take the whole party.

The full rulebook (design philosophy, who owns what, how every gameplay area
behaves, what players can and cannot do, and what is still open) is
**[How co-op works](DarkwoodMP.Mod/docs/HOW_COOP_WORKS.md)**.

---

## Status

Every gameplay area has a multiplayer path in code, and the unit tests run on
every push. Runtime verification is a dual-box (two game installs on one machine)
or three-player playtest. Recent releases are built and unit-tested but not yet
playtested; the [CHANGELOG](CHANGELOG.md) says which is which for each release.
Expect bugs, and send logs (see [Reporting a bug](#reporting-a-bug)).

---

## Install

Use one loader per game installation. Never put both loader builds in the same
game folder.

Prebuilt zips are attached to a [GitHub release](https://github.com/Warexpor/DarkwoodMP-YokWare/releases)
when one is published (each zip has an `INSTALL.txt`). Otherwise build the DLLs
yourself (see [Building from source](#building-from-source)).

### BepInEx (default)

1. Install BepInEx 5.x for your Darkwood build (Windows, or Linux native).
2. Copy `DarkwoodMP.Mod.dll` and `LiteNetLib.dll` into `Darkwood/BepInEx/plugins/`.
3. Launch Darkwood. On Linux with Steam, launch through BepInEx's
   `run_bepinex.sh` (Steam launch options: `./run_bepinex.sh %command%`); a plain
   Steam launch skips the loader.

### MelonLoader

1. Install MelonLoader 0.7.x for Darkwood. BepInEx is not needed.
2. Copy `DarkwoodMP.Mod.dll` into `Darkwood/Mods/` and `LiteNetLib.dll` into
   `Darkwood/UserLibs/`.
3. Launch Darkwood.

To check the install, press **F2** in game: the window title shows
`YokWare Branch <version> / Path B` and the footer shows the protocol, both as in
the table above.

---

## Playing

### Host and join

The title screen has a **MULTIPLAYER** button with **HOST**, **JOIN** and
**SETTINGS** (LAN or Steam for each). The **F2** window in game has the same
options.

- **Host:** choose HOST LAN or HOST STEAM, then load or start a campaign and enter
  a chapter. Players can join once the host is in the world; until then they see
  "WAIT HOST".
- **Join over LAN:** set the host's IP address (default port 7788) in SETTINGS or
  F2, then JOIN LAN.
- **Join over Steam:** accept the host's Steam invite, or set the lobby id in
  SETTINGS and press JOIN STEAM. The host chooses the lobby type (friends, public or
  private).
- **Password:** optional. If the host sets `HostPassword`, every player needs the
  same one.

When you join, you download the host's world, **CHOOSE SLOT** (one of your 5 save
slots for your copy of it) and **ENTER WORLD**. Empty slots are safe; an occupied slot asks before it is
overwritten, and a slot from a different campaign is marked
`[DIFFERENT CAMPAIGN]`. Your other saves are not touched. Your character (bag,
skills, level) is saved separately and comes back when you rejoin.

Only the host saves the world. When the host saves, every player's copy and
character are saved with it.

A GOG copy of Darkwood has no Steam, so a GOG player joins over LAN.

### Controls

| Key | Action |
|-----|--------|
| **F2** | Multiplayer window (host, join, settings, session status) |
| **F3** | Save (host only) |
| **F4** | Spectate other players; also used while dead at night |
| **Ctrl+C** | Text chat (Enter sends, Esc closes) |
| **V** | Push-to-talk voice chat (needs a logged-in Steam client) |
| **Right mouse** with a walkie-talkie in hand | Talk on the radio |

### Settings

Settings live in one INI file, created on first launch:

- BepInEx: `Darkwood/BepInEx/config/com.yokware.branch.cfg`
- MelonLoader: `UserData/YokWare/com.yokware.branch.cfg`

Edit it with the game closed. The gameplay settings (friendly fire, loot sharing,
party scaling) are the host's and apply to everyone. Every key and its default is
in [CONFIG.md](DarkwoodMP.Mod/docs/CONFIG.md).

### Reporting a bug

Send the `LogOutput.log` (BepInEx) or Melon log from **every** player, taken right
after the problem and before the game is started again (it is overwritten on each
launch). Say who was the host and what each player did. Log settings and what they
capture: [LOGGING.md](DarkwoodMP.Mod/docs/LOGGING.md).

---

## Known limitations

- Runtime verification of a full campaign with two and three players is still in
  progress. The per-area status is in
  [COOP_COVERAGE.md](DarkwoodMP.Mod/docs/COOP_COVERAGE.md).
- The wrong-save warnings (slot picker, overwrite confirm, `WRONG SAVE` on join)
  are in code; the multi-slot experience still needs a playtest.
- There is no exclusive lock on containers or the workbench. Two players can have
  the same one open; the host settles any race and refunds the loser exactly.
- Host migration during a dream is in code but not yet playtested.
- Some dream, spectator and dialogue presentation details can differ between
  players. They do not change the world.

The full list, including what was left as is on purpose, is in
[How co-op works, section 20](DarkwoodMP.Mod/docs/HOW_COOP_WORKS.md#20-known-gaps-and-parked-items).

---

## Building from source

### Requirements

- .NET SDK 8 (the mod targets `net471`; the tests target `net8.0`).
- A local Darkwood install. The game and loader assemblies are referenced from
  it and are not in this repository. `LiteNetLib` 1.3.5 comes from NuGet.

### Game path

Create `DarkwoodMP.Mod/GamePath.local.props` (gitignored). It is required on
Linux, where the built-in default (a Windows path) does not exist. `SecondPlugins`
is optional; it is the plugins folder of a second install for dual-box testing.

```xml
<Project>
  <PropertyGroup>
    <GameDir>/home/you/.local/share/Steam/steamapps/common/Darkwood</GameDir>
    <SecondPlugins>/home/you/Games/SecondDarkwood/Darkwood/BepInEx/plugins</SecondPlugins>
  </PropertyGroup>
</Project>
```

On Windows, use paths like
`C:\Program Files (x86)\Steam\steamapps\common\Darkwood`.

### Build

Both loaders (fetches the MelonLoader references on first run):

```bash
./scripts/build-loaders.sh
```

One loader:

```bash
dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx
```

```bash
dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader
```

The output is in `DarkwoodMP.Mod/bin/Release/<Loader>/`. A BepInEx build also
copies the DLLs into `GameDir` and `SecondPlugins` as a development convenience;
add `-p:SkipDeploy=true` to build without copying. Release zips for both loaders:

```bash
./scripts/pack-release.sh
```

### Test

No game install is needed:

```bash
dotnet test DarkwoodMP.PathB.Tests -c Release
```

The tests cover the wire format of every message, handler coverage, the Harmony
and session-reset rules, the policy helpers, and that the version, protocol, docs
and config table agree with the code. CI runs them on every push and pull request
to `dev` and `main`.

### Dual-box testing

Two Darkwood installs on one machine, one hosting and one joining over LAN
(typically a Steam host and a GOG client, which cannot use Steam). The second
install keeps its saves in its own `Darkwood_Second` folder automatically; set
`SaveRootOverride` if both still share one. On Linux with Wayland and a
Wine/Proton client, set `FreeCursorForDualBox = true` in both installs' config
(it is off by default).

### Entity spawner (optional)

`DarkwoodMP.EntitySpawner` is a separate debug plugin (**F5** opens it) for
spawning creatures while testing. It is not part of the co-op mod. Build it with
`dotnet build DarkwoodMP.EntitySpawner -c Release` and copy its DLL next to the
mod.

### Network notes

LAN and Steam carry the same messages. The host owns the world simulation and
validates everything clients send; it relays a client's message to the other
clients only after applying it, and drops message types only the host may send.
The highest assigned message ID is 167 (`MapPinEvent`). Voice uses message 129.

---

## Documentation

| Document | For |
|---|---|
| [How co-op works](DarkwoodMP.Mod/docs/HOW_COOP_WORKS.md) | The design and rules of the co-op game |
| [CONFIG.md](DarkwoodMP.Mod/docs/CONFIG.md) | Every setting and its default |
| [LOGGING.md](DarkwoodMP.Mod/docs/LOGGING.md) | Log presets, what to send with a bug report |
| [PLAYTEST.md](DarkwoodMP.Mod/docs/PLAYTEST.md) | Manual playtest checklist |
| [ARCHITECTURE.md](DarkwoodMP.Mod/docs/ARCHITECTURE.md) | Code layout and the rules for a change |
| [COOP_COVERAGE.md](DarkwoodMP.Mod/docs/COOP_COVERAGE.md) | Code and runtime coverage per area |
| [CHANGELOG.md](CHANGELOG.md) | What changed in each release ([older entries](CHANGELOG-ARCHIVE.md)) |
| [CONTRIBUTORS.md](CONTRIBUTORS.md) | Authors, lineage, how to contribute |

---

## Credits and license

Warexpor and Yokyy co-author YokWare Branch; see [CONTRIBUTORS.md](CONTRIBUTORS.md).
Built on BepInEx, MelonLoader, Harmony and LiteNetLib. Darkwood is a game by Acid
Wizard Studio; this is an unofficial fan mod and needs a legal copy of the game.

Licensed under GPLv3: see [LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).
