# YokWare Branch

Co-op multiplayer for **Darkwood**. One player hosts their campaign and friends
join it: the same forest, the same nights, the same story, played together.

| | |
|--|--|
| Product | YokWare Branch **0.8.213** |
| Wire | Horde protocol **54** |
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
  protected instead); the world pauses when everyone is in the pause menu. A night
  death makes you spectate until morning, your body lying where you fell, instead
  of skipping the night for everyone. Each player plays their own prologue. Dreams
  take the whole party.

The full rulebook (design philosophy, who owns what, how every gameplay area
behaves, what players can and cannot do, and what is still open) is
**[How co-op works](DarkwoodMP.Mod/docs/HOW_COOP_WORKS.md)**.

---

## Status

Every gameplay area has a multiplayer path in code, and the unit tests run on
every push. Runtime verification is a dual-box (two game installs on one machine)
or three-player playtest. Recent releases are unit-tested and checked in automated
three-player runs but not yet playtested by hand; the [CHANGELOG](CHANGELOG.md) says
which is which for each release.
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

To check the install, open **MULTIPLAYER** on the title screen: the bottom of the
screen shows `YokWare Branch <version>`, as in the table above.

---

## Playing

### Host and join

**MULTIPLAYER** sits under PLAY on the title screen and under OPTIONS in the pause
menu. Its screens look and work like the game's own menus (Esc goes back):

- **Host:** Host > Local network or Steam, then choose a profile and play. Players
  can join once the host is in the world. In the pause menu, **Host this game**
  opens the game you are playing to other players.
- **Host over LAN:** the Host screen shows the address the others type in. Type your own
  under **Address** when they reach you another way (a VPN adapter, a forwarded port).
- **Join over LAN:** Join, type the host's address (default port 7788) and, if the
  host set one, the password, then **Connect**.
- **Join over Steam:** **Join > Steam friends** lists the friends who are hosting right
  now, one click each; **Open Steam friends list** there brings up Steam's own list
  (Join Game on a friend); or accept the host's Steam invite; or type the lobby id and
  press **Join the lobby**. Hosting over Steam opens Steam's invite window by itself. The host chooses the lobby type in Host
  settings (friends only, public or invite only).
- **Settings:** your name, when other players' names show (when pointed at,
  always, off), text chat, other players' footstep volume, and **Voice**: off / push
  to talk / always on, the push-to-talk, radio talk and radio on/off keys, which
  microphone and how loud (with a live level meter), voice volume, and each other
  player's volume.
- **Host settings:** friendly fire, extra loot, night monsters (x1 to x10), whether
  creatures hear voices, and, before hosting, player count, Steam lobby type, port
  and password. The rules can be changed mid-game too and apply to everyone at once.
- **Apply and Revert to default** sit on the Return row of every settings screen, as
  in the game's Options. A change shows at once. Leaving with a change that matters
  to others not applied (your name, the voice mode, a rule while hosting) asks first;
  small adjustments (volumes, keys, the microphone) are just kept.
- **In the pause menu:** Invite friends (Steam host), Send the world again (host),
  Restore my character (client, when the automatic restore missed), Disconnect.

When you join, you download the host's world and choose which of your 5 profiles
keeps your copy of it, then **Enter world**. Empty profiles are safe; an occupied
one asks before it is overwritten, and one from a different campaign is marked
"another campaign". Your other saves are not touched. Your character (bag, skills,
level) is saved separately and comes back when you rejoin.

Only the host saves the world. When the host saves, every player's copy and
character are saved with it.

A GOG copy of Darkwood has no Steam, so a GOG player joins over LAN.

### Controls

| Key | Action |
|-----|--------|
| **Esc** | Pause menu, with **MULTIPLAYER** (session, settings) |
| **F3** | Save slots, with the optional manual-saves add-on |
| **F4** | Spectate other players; also used while dead at night |
| **Ctrl+C** | Text chat (Enter sends, Esc closes, Ctrl+V pastes) |
| **V** | Push-to-talk voice chat (any install, LAN or Steam; no Steam client needed) |
| **Right mouse** with a walkie-talkie in hand | Talk on the radio (rebindable, side mouse buttons too) |
| **B** with a walkie-talkie in hand | Switch the radio on or off |
| **R** with a walkie-talkie in hand | Put in a fresh 9V battery |

The walkie-talkie is crafted at the workbench (level 1) from 2 junk and 1 nail. Switched
on and charged, it receives the radio anywhere it is carried; the full rules are in
[How co-op works, section 17](DarkwoodMP.Mod/docs/HOW_COOP_WORKS.md#17-seeing-and-hearing-other-players).

### Settings

Settings live in one INI file, created on first launch:

- BepInEx: `Darkwood/BepInEx/config/com.yokware.branch.cfg`
- MelonLoader: `UserData/YokWare/com.yokware.branch.cfg`

The common ones are also in **Multiplayer > Settings**; edit the file itself with
the game closed. The gameplay settings (friendly fire, extra loot,
night monsters, creatures hearing voices) are the host's and apply to everyone. Every key and its default is
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
- The wrong-save warnings (profile picker, overwrite confirm, `WRONG SAVE` on join)
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

### Manual saves (optional add-on)

`DarkwoodMP.ManualSaves` builds `YokWare.ManualSaves.dll`, an optional add-on: ten
extra save slots per profile, opened with **F3** in the world (a Saves screen in the
pause menu). It needs the co-op mod next to it. In co-op only the host saves (every
player's copy is saved with it), and loading a slot needs the session left first.
Build it with `dotnet build DarkwoodMP.ManualSaves -c Release` (it deploys like the
mod); the release ships it as its own zip.

### Entity spawner (optional)

`DarkwoodMP.EntitySpawner` is a separate debug plugin (**F5** opens it) for
spawning creatures while testing. It is not part of the co-op mod. Build it with
`dotnet build DarkwoodMP.EntitySpawner -c Release` and copy its DLL next to the
mod.

### Network notes

LAN and Steam carry the same messages. The host owns the world simulation and
validates everything clients send; it relays a client's message to the other
clients only after applying it, and drops message types only the host may send.
The highest assigned message ID is 168 (`PlayerName`). Voice uses message 129.

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
Built on BepInEx, MelonLoader, Harmony and LiteNetLib. Recorded radio sounds are CC0
recordings from Freesound, listed in
[SOURCES.md](DarkwoodMP.Mod/Resources/Radio/SOURCES.md). Darkwood is a game by Acid
Wizard Studio; this is an unofficial fan mod and needs a legal copy of the game.

Licensed under GPLv3: see [LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).
