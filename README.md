# YokWare Branch

Darkwood co-op multiplayer, Path B: a host-authoritative Horde sync mod.

| | |
|--|--|
| Product | YokWare Branch **0.8.133** |
| Wire | Horde protocol **33** |
| Transport | LiteNetLib LAN, with optional SteamNetworkingSockets |
| Loaders | BepInEx 5.x and MelonLoader 0.7.x (peer builds) |
| License | GPLv3, see [LICENSE](LICENSE) |
| Authors | Warexpor and Yokyy |

Path B is the supported load path. Earlier Path A and Ironbark material is not
part of the shipped mod. See [CHANGELOG.md](CHANGELOG.md) for history and
known gaps.

---

## Wire

All peers in a session must use the same mod version and protocol.

- LiteNetLib UDP provides LAN sessions.
- SteamNetworkingSockets provides the optional Steam lobby transport.
- Both transports use the same Horde message framing.
- The host owns world simulation, combat authority, entity AI, and the
  day/night clock where the mod has a multiplayer path.
- Clients present host state and suppress the local systems that would create
  duplicate world simulation.
- The host validates client requests against its own world, drops message types
  only the host may send, and relays a client message to the other clients only
  after applying it.

The highest assigned message ID is 152 (`TradeCommit`). Voice data uses message
129 when Steam voice is enabled. How the code is organised, and the rules for
adding messages, handlers, patches and session state, are in
[DarkwoodMP.Mod/docs/ARCHITECTURE.md](DarkwoodMP.Mod/docs/ARCHITECTURE.md).

---

## Install

Use one loader per game process. Do not place both loader variants in the same
game installation.

### BepInEx

1. Install BepInEx 5.x for the installed Darkwood architecture.
2. Build both loaders (recommended), or BepInEx only:

   `./scripts/build-loaders.sh`

   `dotnet build DarkwoodMP.Mod -c Release -p:Loader=BepInEx`

3. Copy `DarkwoodMP.Mod.dll` and `LiteNetLib.dll` from
   `DarkwoodMP.Mod/bin/Release/BepInEx/` to
   `Darkwood/BepInEx/plugins/`.
4. Launch Darkwood. The F2 window title should read YokWare Branch &lt;version&gt; / Path B
   and its footer the protocol, both as in the table above.

The project can also copy these files to the configured local Steam and
SecondDarkwood plugin directories after a BepInEx build. Treat that as a local
development convenience, not as a release packaging step.

### MelonLoader

Melon and BepInEx share the same Path B runtime. The Melon DLL does **not**
need BepInEx installed.

1. Install MelonLoader 0.7.x for Darkwood (separate game dir from BepInEx —
   one Doorstop per install).
2. Fetch Melon reference assemblies (once per clone):

   `./scripts/fetch-melonloader-refs.sh`

3. Build:

   `dotnet build DarkwoodMP.Mod -c Release -p:Loader=MelonLoader`

   Or both: `./scripts/build-loaders.sh`

4. Copy `DarkwoodMP.Mod.dll` and `LiteNetLib.dll` from
   `DarkwoodMP.Mod/bin/Release/MelonLoader/` to `Darkwood/Mods/`.

Configuration uses the same INI key shape on both loaders:

- BepInEx: `Darkwood/BepInEx/config/com.yokware.branch.cfg`
- Melon: Melon `UserData/YokWare/com.yokware.branch.cfg`

Every key, its default and what it does is listed in
[DarkwoodMP.Mod/docs/CONFIG.md](DarkwoodMP.Mod/docs/CONFIG.md).

### Entity spawner (optional)

`DarkwoodMP.EntitySpawner` is a separate debug plugin (**F5** opens its
window). It is not part of the co-op mod and is not needed to play; build it
with `dotnet build DarkwoodMP.EntitySpawner -c Release` and copy its DLL next to
the mod only when you want to spawn entities while testing.

### Dual-box testing

SecondDarkwood is the second (GOG) installation used as the client box. Use LAN
for Steam to GOG testing. Steam lobby sessions require two Steam clients. The
second installation uses an isolated `Darkwood_Second` save root when the normal
local setup is used.

| | Linux (Steam host + Wine/Proton client) | Windows |
|--|--|--|
| Host game | `~/.local/share/Steam/steamapps/common/Darkwood` | `C:\Program Files (x86)\Steam\steamapps\common\Darkwood` |
| Client game | `~/Work/MyProjects/SecondDarkwood/Darkwood` | `C:\MyProjects\SecondDarkwood\Darkwood` |

On Linux dual-box (Wayland + Wine), set `FreeCursorForDualBox = true` in both
installs' `com.yokware.branch.cfg`; it defaults to off.

---

## Controls

- **F2**: multiplayer settings
- **F3**: manual save
- **F4**: spectator mode
- **Ctrl+C**: chat (`ChatEnabled`, on by default). Enter sends, Esc closes;
  gameplay input is held while the input box is open.

The title menu provides HOST, JOIN, SETTINGS, and recovery actions. The host
must enter a chapter before a client can join its world.

---

## Build and test

Create `DarkwoodMP.Mod/GamePath.local.props` locally (gitignored). On Linux it
is required, because the built-in default is a Windows path. `SecondPlugins` is
optional and only used by the post-build deploy to the second install.

Linux:

```xml
<Project>
  <PropertyGroup>
    <GameDir>/home/you/.local/share/Steam/steamapps/common/Darkwood</GameDir>
    <SecondPlugins>/home/you/Work/MyProjects/SecondDarkwood/Darkwood/BepInEx/plugins</SecondPlugins>
  </PropertyGroup>
</Project>
```

Windows:

```xml
<Project>
  <PropertyGroup>
    <GameDir>C:\Program Files (x86)\Steam\steamapps\common\Darkwood</GameDir>
    <SecondPlugins>C:\MyProjects\SecondDarkwood\Darkwood\BepInEx\plugins</SecondPlugins>
  </PropertyGroup>
</Project>
```

`-p:SkipDeploy=true` builds without copying into any game directory.

Build the solution:

`dotnet build DarkwoodMP.sln -c Release`

Run the tests (no game install needed; .NET 8 SDK):

`dotnet test DarkwoodMP.PathB.Tests -c Release`

They cover wire round-trips for every message, dispatch coverage, the Harmony
and session-reset rules, policy helpers, and release consistency (version,
protocol, docs). CI runs them on every push and pull request to `dev` and `main`.

`LiteNetLib` version 1.3.5 comes from NuGet. Game and loader assemblies are
resolved from the local installation.

---

## Coverage and limitations

The mod covers the main Path B multiplayer paths for player state, entities,
physics, locations, inventory, combat, story events, dreams, audio, spectator
mode, and world save sharing. Coverage is not a claim of complete runtime
parity.

Known deferred or runtime-dependent areas include:

- full dual-box and three-player campaign soak: every domain is code-covered,
  runtime verification is still pending (start with
  [PLAYTEST.md](DarkwoodMP.Mod/docs/PLAYTEST.md))
- wrong-save warning UI: **code shipped** — slot picker `[DIFFERENT CAMPAIGN]`
  + overwrite confirm; backup refuse → in-world / join `WRONG SAVE` via
  `WrongSaveWarning`. Dual-box / multi-slot UX still soak-pending.
- complete interaction-lock coverage (no workbench exclusive lock by design:
  both players may share a bench; msg **119** stays reserved)
- some dream, spectator, and dialogue presentation edge cases (parked as
  presentation-only in `COOP_COVERAGE.md` — not world-authority gaps)
- host migration during a dream

Late-join `GameEventsBulk` / `ScenarioStateBulk`, EventTriggers FOV parity,
worldgen RNG host-auth, and Examinable `onExamine` host triggers are no longer
deferred — see
[CHANGELOG.md](CHANGELOG.md) and
[DarkwoodMP.Mod/docs/COOP_COVERAGE.md](DarkwoodMP.Mod/docs/COOP_COVERAGE.md).
For support logs, use [DarkwoodMP.Mod/docs/LOGGING.md](DarkwoodMP.Mod/docs/LOGGING.md).
The manual playtest checklist is
[DarkwoodMP.Mod/docs/PLAYTEST.md](DarkwoodMP.Mod/docs/PLAYTEST.md).

---

## Credits and license

Warexpor and Yokyy co-author the YokWare Branch. See
[CONTRIBUTORS.md](CONTRIBUTORS.md).

GPLv3: see [LICENSE](LICENSE), [COPYRIGHT](COPYRIGHT), and
[CONTRIBUTORS.md](CONTRIBUTORS.md).

Current ship: **0.8.133**, protocol **33**. See
[CHANGELOG.md](CHANGELOG.md).
