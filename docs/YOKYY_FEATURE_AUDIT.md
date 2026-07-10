# Yokyy feature audit on Path B (Horde base)

**Date:** 2026-07-10  
**Load path:** `DarkwoodMP.Mod` = Horde remaster + deliberate Yokyy product ports  
**Reference:** `archive/yokyy-merge-0.9/`

Status: **in-base (Horde)** | **ported** | **fixed** | **equivalent** | **deferred**

---

## Product / UX (what players notice)

| Feature | Yokyy | Path B | Notes |
|---------|-------|--------|-------|
| Native **MULTIPLAYER** title button | yes | **ported + fixed** | `MainMenuMultiplayerInject` — HOST/JOIN/SETTINGS/RESTORE/DISCONNECT/BACK |
| IMGUI settings (IP/port/password) | yes (F1/HUD) | **in-base** + wired to SETTINGS | F2; fields push to BepInEx config before host/join |
| Chat | yes | **ported + hardened** | `ChatHud` + `NetMessageType.ChatMessage` (107); Ctrl+C; 160-char clamp; 0.25s send rate; name clamp |
| Status HUD strip | yes | **ported** (slim) | Session line when online (role/id/peers) |
| DarkwoodTheme skin | yes | **deferred** | Horde IMGUI is plain; not required for co-op |
| Spectator | yes (thin) | **in-base** (full) | Horde SpectatorModeController + F4 |
| Manual save UI | partial | **in-base** | F3 ManualSaveGUI |
| Client self-backup restore | yes | **in-base** + RESTORE SELF button | `ClientStateBackup` |
| Pause suppression | yes | **in-base** | `NoWorldPausePatch` / PauseSuppression |
| Entity spawner | debug flag | **separate plugin** | `YokWare.EntitySpawner` F5 |

### MULTIPLAYER button — bugs found and fixed

| Bug | Severity | Fix |
|-----|----------|-----|
| Inject only polled from MultiplayerMenu.Update (could miss title) | high | Also tick from `InputScriptUpdatePatch` |
| Join poll shared 15-frame interval → sluggish CONNECTING | med | Join poll every frame while pending |
| Host/Join did not always flush IMGUI → config | high | `PushFieldsToConfig` before host/join; port clamp 1–65535 |
| No DISCONNECT on native panel | med | DISCONNECT button when session active |
| Leaving menu wiped join pending mid-handshake | med | Keep join pending across menu teardown |
| Destroyed button refs not re-injected | high | `Alive()` checks + Menu0 rebuild re-inject; avoid double inject via Find |
| Clone EXIT texture still “EXIT” | known Yokyy | Destroy LocalizedText + tk2dTextMesh label (Yokyy fix kept) |
| Panel sibling of Menu0 | ok | Same as Yokyy |
| Mouse-only (no controller) | known | Documented; same as Yokyy v1 |

---

## Sync / systems (Yokyy “unique” vs Horde)

| Feature | Yokyy | Path B | Verdict |
|---------|-------|--------|---------|
| Host-authoritative combat | partial/thin | **in-base** full | Keep Horde; do not re-port Yokyy combat |
| Entity AI stream | EntityState-ish | **in-base** | Horde EntityStateBroadcast + client interp |
| Reliability hop 0xE0/E1 | custom | **equivalent** | LiteNetLib `ReliableOrdered` |
| World transfer / save share | WorldTransfer | **equivalent** | `WorldSaveShareService` |
| InteractionLock full | yes | **equivalent** for drag | `DragClaimPatch` |
| SyncCheck digests | yes | **deferred** | Host authority reduces need; careful port later |
| ItemState | yes | **deferred** | Container absolute covers most |
| IsTimeAuthority / dedicated elect | yes | **deferred** | Horde LAN host role; server bridge later |
| Ironbark wire | yes | **deferred** as live path | Protocol 19 live; Ironbark tree for research |
| MelonLoader dual | yes | **deferred** | BepInEx first |

---

## Cross-cutting bug fixed while auditing

| Bug | Where | Fix |
|-----|-------|-----|
| Host **Forwardable Direct** fan-out used length-prefixed `Put(byte[])` so 3rd peer got corrupt payloads | `LanNetworkManager` rebroadcast | `NetWriter.PutRaw` + forward uses `PutRaw(payload)` |

This is a Horde wire bug that also would have broken chat and any Direct-forwarded message in 3+ sessions.

---

## Explicitly NOT ported (and why)

1. **Yokyy dual-sim / ActionEvent combat** — root of his desync bugs; Horde client redirect is correct.
2. **SyncCheck** — can fight host authority if naïvely ported.
3. **ItemState / full InteractionLock matrix** — Horde container/drag covers play; full port is large and regression-prone.
4. **Dedicated server as live peer** — different wire (Ironbark vs 19).

---

## How to verify in-game

1. Title: **MULTIPLAYER** visible under other buttons; log `[YokWare/UI] Injected MULTIPLAYER…`
2. SETTINGS → set IP/port/password/name → HOST GAME → profile menu
3. Client: JOIN GAME → CONNECTING… → CONNECTED (or timeout message)
4. In session: Ctrl+C chat; F2 still works; F4 spectator; F5 spawner plugin
