# Path B — Feature inventory (Horde base vs Yokyy / prior YokWare merge)

**Product:** YokWare Branch `0.9.1`  
**Date:** 2026-07-10  
**Doctrine:** **Horde remaster is the shippable sync base.** Prior Yokyy-core merge is archived under `archive/yokyy-merge-0.9/` and is **not** the load path.

Status key: **ported** | **present-in-horde** | **fixed** | **deferred**

---

## Must-keep product items (from Path B plan)

| Item | Status | Notes |
|------|--------|--------|
| Host-authoritative combat / entity / AI (Horde quality) | **present-in-horde** | `ClientHitscanDamageRedirectPatch`, `ClientCombatPatches`, `HostCombatPatches`, `EntityStateBroadcastService`, `ClientEntityInterpolationService`, `ClientAIDisablePatches`. No Yokyy `ActionEvent` combat path. |
| Sound / audio co-op path | **present-in-horde** | `AudioSuppressionPatch` never culls menu/BGM; entity/player sound sync patches; distance cull only while connected. |
| ClientStateBackup | **present-in-horde** | `Networking/ClientStateBackup.cs` + save bridge. |
| World save / share | **present-in-horde** | `WorldSaveShareService` (Horde equivalent of Yokyy WorldTransfer for LAN host share). |
| Drag / interaction claims | **present-in-horde** | `DragClaimPatch` (Horde) — covers movable claim role of Yokyy InteractionLock for furniture drag. |
| Spectator full | **present-in-horde** | `SpectatorModeController` + culling patches. |
| Reliability delivery | **present-in-horde** | LiteNetLib `ReliableOrdered` for S/E channels (Horde native; not Yokyy hop envelopes). |
| GPLv3 + Warexpor ownership + Yokyy credit | **ported** | LICENSE, COPYRIGHT, CONTRIBUTORS, PluginInfo.Authors. |
| Public docs / README truth | **ported** | README + CHANGELOG state Path B. |
| Dual BepInEx deploy (Steam + SecondDarkwood) | **ported** | `DarkwoodMP.Mod.csproj` DeployToGameDirs. |
| Dedicated server project | **deferred** | Tree keeps `DarkwoodMP.Server` + Ironbark protocol as **research / future bridge**. Horde LAN wire is protocol **19**, not Ironbark v2 — connecting them without a full re-protocol is unsafe. |
| Ironbark typed wire as live load path | **deferred** | Live clients use **Horde NetMessageType protocol 19**. Ironbark sources remain under `DarkwoodMP.Protocol` for codec tests and future server bridge. |
| Dual MelonLoader packaging | **deferred** | Horde is BepInEx-first; Melon dual-loader from 0.9 merge not carried into Path B load path. |
| Native **MULTIPLAYER** main-menu button (Yokyy) | **ported + fixed** | `MainMenuMultiplayerInject` — HOST/JOIN/SETTINGS/RESTORE/DISCONNECT/BACK; InputScript + menu ticks; config flush; join timeout. See `docs/YOKYY_FEATURE_AUDIT.md`. |
| Chat / HUD overlay (Yokyy) | **ported + hardened** | `ChatHud` + `ChatMessage` (111); Ctrl+C; length/rate clamps; session status strip. |
| SyncCheck (Yokyy digest correctives) | **deferred** | Horde relies on host authority + entity/state streams; full SyncCheck port needs careful design so it does not fight host authority. |
| ItemState (Yokyy product) | **deferred** | Not in Horde; inventory/container absolute sync covers most play cases. |
| IsTimeAuthority / dedicated elect | **deferred** | Horde is **host-authoritative LAN** (`NetworkRole.Host/Client`). Dedicated elect-authority was Yokyy-only. |
| InteractionLock full (all interactive classes) | **deferred** | DragClaim covers drag; full Yokyy InteractionLock matrix not ported — prefer Horde claim model. |

---

## Sync domains (Horde load path)

| Domain | Status |
|--------|--------|
| Session / handshake / multi-peer IDs | **present-in-horde** |
| Time / day / isAfterNight | **present-in-horde** |
| Flags bulk + delta | **present-in-horde** |
| Enemy EntityState + client interp | **present-in-horde** |
| Client damage redirect (melee/hitscan/projectile/AOE) | **present-in-horde** |
| Friendly fire host-authoritative | **present-in-horde** |
| Doors / generators / barricades | **present-in-horde** |
| Containers / death bags / trade | **present-in-horde** |
| Dreams / cutscenes / chapter | **present-in-horde** |
| Weather / map / reputation / hideout | **present-in-horde** |
| Coop balance / named NPC scale | **present-in-horde** |
| Night death / morning rep skip | **present-in-horde** |

Deep audit trail: `DarkwoodMP.Mod/docs/COOP_COVERAGE.md` (Horde).

---

## Bugs fixed this Path B pass

| Bug / defect | Fix |
|--------------|-----|
| Shippable default load path was Yokyy-core merge (his bugs / sound / thin combat) | **Replaced** load path with Horde remaster sources as `DarkwoodMP.Mod`. |
| Product identity still `DWMP HORDE` / old versions | **PluginInfo** + AssemblyInfo → YokWare Branch **0.9.1**, GUID `com.yokware.branch`. |
| Log tags `[DWMP/…]` confusing for YokWare testers | **ModLog** CatTags → `[YokWare/…]`; banner Path B + authors. |
| Deploy could leave both Horde-named and product DLLs | Deploy target removes `DWMP_HordeRemaster.dll` if present; ships `DarkwoodMP.Mod.dll`. |
| Yokyy archive still present as accidental load path risk | Archived under `archive/yokyy-merge-0.9/` with README “do not load”. |

---

## Explicit non-load paths

| Path | Role |
|------|------|
| `DarkwoodMP.Mod/` | **Ship** — Horde base, BepInEx plugin |
| `archive/yokyy-merge-0.9/` | Reference only — prior failed merge |
| `DarkwoodMP.Protocol/` | Codec / Ironbark research + tests |
| `DarkwoodMP.Server/` | Dedicated relay (Ironbark) — **not** Horde LAN peer |

---

## Verification pointers

- Authority grep: no `ActionEventPacket` in shipped mod; client `NetworkRole.Client` + `PlayerAttack` redirect present.
- Build: `dotnet build DarkwoodMP.Mod -c Release`
- Protocol tests: `dotnet test DarkwoodMP.Protocol.Tests -c Release` (Ironbark codec still green; independent of live wire choice).
