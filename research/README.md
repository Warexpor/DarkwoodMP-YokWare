# DarkwoodMP — Research & legacy trees

**Not shipped, not loaded into the game.** Kept alive for reference and
standalone builds only.

| Tree | What it is | Status |
|------|-----------|--------|
| `DarkwoodMP.Protocol/` | Ironbark v2 typed-packet wire (codec + packets) | Research / future bridge |
| `DarkwoodMP.Server/` | Dedicated Ironbark relay (net8) | Research — not a Horde LAN peer |
| `DarkwoodMP.Protocol.Tests/` | Ironbark codec round-trip tests | Research tests |

The shipped game wire is **Horde protocol 22** in `DarkwoodMP.Mod/` (the main
solution). These trees are excluded from `DarkwoodMP.sln` and from
`scripts/pack-release.ps1`.

## Build standalone

```powershell
dotnet build research\DarkwoodMP.Research.sln
dotnet test  research\DarkwoodMP.Protocol.Tests -c Release
```

The `DarkwoodMP.Protocol/` sources are compiled into both `DarkwoodMP.Server`
and `DarkwoodMP.Protocol.Tests` via `<Compile Include="..\DarkwoodMP.Protocol\**\*.cs">`
— the three folders must stay siblings under `research/`.

## Legacy fork

`archive/yokyy-merge-0.9/` is the frozen pre-Path-B merge. Also not loaded into
the game; preserved for reference.