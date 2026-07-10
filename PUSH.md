# Before you push to GitHub

This tree is prepared for a **public repo under Warexpor**, co-authored with **Yokyy**, license **GPLv3**.

## You should confirm / provide

| # | Item | Default if you say nothing |
|---|------|----------------------------|
| 1 | GitHub owner username | `Warexpor` |
| 2 | Repo name | `DarkwoodMP-YokWare` |
| 3 | Yokyy display name / `@handle` | `Yokyy` (no link) |
| 4 | License | **GPLv3** (already applied) |
| 5 | Public vs private | Public assumed |
| 6 | MelonLoader install on test machine | Optional; build uses `libs/MelonLoader` via `scripts/fetch-melonloader-refs.ps1` |
| 7 | Optional: one-line Yokyy approval for public release | Omitted until you add it |
| 8 | Optional: Discord / issues contact | GitHub Issues |

## Do not commit

- `DarkwoodMP.Mod/GamePath.local.props`
- **`libs/MelonLoader/*.dll`** — gitignored; **never** `git add -f` those DLLs (fetch via `scripts/fetch-melonloader-refs.ps1`)
- `bin/`, `obj/`, `artifacts/`
- Game `Assembly-CSharp.dll` or other proprietary game files
- Real session passwords

Before commit: `git status` must not list `*.dll` under `libs/`.

## Suggested first remote

```bash
# On GitHub: create empty repo DarkwoodMP-YokWare under Warexpor (no README)
cd /path/to/DarkwoodMP-YokWare
git add .
git status   # review: no GamePath.local.props, no libs DLLs
git commit -m "YokWare Branch 0.9 — release-ready co-op (Ironbark v2, dual loader, GPLv3)"
git branch -M main
git remote add origin https://github.com/Warexpor/DarkwoodMP-YokWare.git
git push -u origin main
```

Add Yokyy as collaborator in GitHub settings if he wants write access.

## License note for collaborators

GPLv3 requires derivative works that you distribute to stay under GPLv3. Third-party loaders (BepInEx, MelonLoader) are not part of this repo’s grant.
