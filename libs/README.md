# Third-party build references (not committed)

## MelonLoader 0.7.0

Run from repo root:

```powershell
pwsh scripts/fetch-melonloader-refs.ps1
```

This fills `libs/MelonLoader/` with `MelonLoader.dll` + `0Harmony.dll` (net35) for compiling the MelonLoader configuration.

**Do not commit those DLLs** (`.gitignore` has `libs/**/*.dll`). Never `git add -f` them.

Game installs with MelonLoader already present can set `MelonLoaderDir` in `GamePath.local.props` instead.
