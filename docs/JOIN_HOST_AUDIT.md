# Host / Join system audit (Path B Horde)

## Intended happy path

1. **Host:** MULTIPLAYER → HOST GAME → pick profile / new game → **enter chapter** (Player exists).
2. **Client:** MULTIPLAYER → JOIN (IP/port/password match) → stay on title.
3. Host detects handshake + in-world → **auto world share** (save files) → client.
4. Client writes files to **profile slot 5** → `LoadScene(chapterN)` → co-op.

## Bugs found (root causes)

| ID | Severity | Bug | Fix |
|----|----------|-----|-----|
| J1 | P0 | JOIN only connected; never pushed save when host already in-world | Auto share on handshake + delayed 0.75s |
| J2 | P0 | Host starts LAN on title, client joins early → no world, client stuck | `HostEnterWorldSharePatch` on `Player.Start` resends to peers |
| J3 | P0 | Dual Steam/SecondDarkwood share **same AppData**; client apply to host profN **overwrites live host save** | Client always applies join package to **slot 5** |
| J4 | P1 | Journal bulk NRE on title menu aborted join pipeline | Null-safe queue |
| J5 | P1 | World share read files 1 frame after Save → empty/missing | Wait up to 5s for sav.dat/savs.dat |
| J6 | P1 | GetHostProfileId only `currentProfile` → 0 fail | Fallbacks: Active profiles, newest disk prof |
| J7 | P1 | Manual Resend to clients **already in-game** would LoadScene wipe | HandleBegin ignores if `Player` and not mainMenu |
| J8 | P2 | Profile path used string concat | `Path.Combine(persistentDataPath, 1_4Save, profN)` |
| J9 | P2 | savch.dat often missing (OK) but silent | Log skip missing file |
| J10 | P2 | UX still said “same slot” | Menu + session notes updated |
| J11 | P0 | Client `saveGameProfiles()` with partial `Core.profiles` **nuked `profs.dat`** — only one slot left; PLAY + HOST showed almost no worlds | Merge on-disk profile index first, re-register orphan `profN` with `sav.dat`, then save; one-time restore of user's index from disk + May backup |
| J12 | P0 | Host sent **journal/flags/bags bulk on connect** while client still on title → `Journal.addJournalEntry` NRE, inventory “No item type …”, broken load | Defer gameplay bulk until after world share; client queues until `Player` exists (`ClientCanApplyWorldBulk`) |
| J13 | P0 | Client LoadScene + host force-Save + late-join `FindObjectsOfType` + 500× proxy spawn spam → dual freeze, “event” hitch, stuck loading | Skip force Save if sav fresh; bulk only on first client PlayerState; gate proxy spawn during load; no afterNight clear from half-loaded packets |
| J14 | P0 | Path B **auto share on join** force-`Save()` (`Save static`) + `removeAfterNightEffect` + scenario bulk + immediate heavy bulk ≠ Horde base (no auto-share). Host “unique event” + freeze | Late-join share = **disk files only, no Save**; no afterNight clear; no scenario bulk on join; 8s settle before light bulk; client mute net send until `coreStarted` |

## Still fragile (not fully fixed)

- **Same-PC dual box** always shares one LocalLow tree — slot 5 mitigates overwrite; long-term need separate Windows users or save-root override.
- **Password/IP mismatch** still fails at LiteNetLib AcceptIfKey — check SETTINGS both sides.
- **Firewall** blocking UDP 7788.
- **Protocol version** must match both DLLs.

## Verify in logs (Support preset)

Host:
- `Handshake OK from Player 2`
- `scheduling auto world share` / `Auto world share → player 2 starting now`
- `Sharing world → player 2` / `World save share complete`

Client:
- `Handshake OK — assigned PlayerId=2`
- `Receiving host world for profile slot 5`
- `Loading chapterN with host world on profile slot 5`
