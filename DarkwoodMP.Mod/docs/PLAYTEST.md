# Playtest checklist

Run it against the version and protocol in the README table.

Dual-box (Steam host + SecondDarkwood client) first, then a 3-player run
(host + two clients). Tick a section only after you saw it in the game; code-only
status lives in `CHANGELOG.md` and `COOP_COVERAGE.md`. Attach **both** loader logs
to any failure (`LOGGING.md`).

Setup for every run: `LogPreset=Support` (or `Trace` for a bug pack) on every box,
same DLL everywhere.

## 1. Deploy and version check

- [ ] `md5sum` of `DarkwoodMP.Mod.dll` (and `LiteNetLib.dll`) matches on every box.
- [ ] F2 window title reads **YokWare Branch &lt;version&gt; / Path B** and the footer shows
      `proto=&lt;protocol&gt;` on every box (both from the README table). Log banner shows the
      same `Protocol=`.
- [ ] Linux dual-box only: `FreeCursorForDualBox = true` in both cfgs (default is
      false); mouse is not trapped and the Wine window does not freeze on blur.
- [ ] A client on a different DLL is refused with a protocol mismatch, not half-joined.

## 2. Join and handshake

- [ ] Host loads a save, enters the chapter; client JOIN LAN shows CONNECTING, then
      WAIT HOST, DOWNLOADING, CHOOSE SLOT, ENTER WORLD.
- [ ] Slot picker: an occupied slot asks to confirm; Cancel clears the confirm; a
      second connect does not show an old confirm.
- [ ] Host start failure (second host on the same port) shows PORT IN USE on the
      button, not only in the log. JOIN with an empty Steam lobby id shows SET LOBBY ID.
- [ ] Third player joins an active session; all three see each other's proxies.
- [ ] Wrong campaign slot shows WRONG SAVE and refuses.
- [ ] F2 fields: typing a port does not save per keystroke; Apply/close saves; a
      Steam lobby id the host just created is not overwritten by stale text.
- [ ] Chat: Ctrl+C opens, typing does not move the character or switch hotbar,
      Enter sends, Esc closes without opening the pause menu; remote lines and
      speech bubbles appear; `ChatEnabled=false` disables it.
- [ ] F3 on a connected **client** refuses ("only the host can save"). On the host a
      save reports success only when `sav.dat` changed. F3 Load asks to confirm and
      refuses during a session.

## 3. Chapter 1 to 2 transition

- [ ] Host walks the chapter-jump trigger; every client gets the world share,
      acknowledges (`ChapterShareAck` 140), then loads on `ChapterLoadGo` 141.
- [ ] Walking the trigger **as a client** also moves everyone (host runs it).
- [ ] Slow or throttled client still lands in chapter 2 (host waits, re-sends).
- [ ] PlayerIds and names are unchanged after the reload; Steam sessions resume on
      the same lobby.
- [ ] First entry of an outside location: all peers' pads sit at the same place
      (`LocationPadSlotRequest` 142 / `LocationPadSlotSync` 143); a late joiner too.

## 4. Night with one dead peer

- [ ] One client dies at night, others survive: dead peer spectates, then at dawn is
      released and sent home (`NightDeathRelease` 144).
- [ ] Each surviving peer in the hideout gets exactly one morning reward
      (`MorningReward` 145): reputation and saturation.
- [ ] Everyone dead: one all-dead morning, no hang. On hard/nightmare the run ends
      only when every death was permadeath-eligible.
- [ ] Host or client disconnects at night: morning still resolves.

## 5. Container deny / refund

- [ ] Two peers take from the same container: the loser is denied and the item is
      refunded (no dupe, no loss), including durability/ammo items and recipes.
- [ ] Take more than a slot holds (modified request) is denied.
- [ ] Known gap: a denied `grabItem` take with the item on the cursor has nothing to
      refund (parked).

## 6. Pickup race

- [ ] Two peers press pickup on the same dropped item in the same moment: exactly one
      gets it, the other sees it vanish, no duplicate on the ground.
- [ ] Items dropped by a peer appear for the others; picking up your own dropped
      item back works.

## 7. Host migration

- [ ] Kill the host process mid-session: survivors elect the lowest player id, the
      new host's world carries on, clients reconnect (LAN and Steam).
- [ ] Graceful DISCONNECT on the host hands off instead of dropping everyone.
- [ ] Known gap: host migration during a dream is unsupported.

## 8. Dreams, including a chain

- [ ] Sleep / dialogue dream: the dreamer enters, peers see the dream state; dream end
      rewards apply once.
- [ ] Chain dream (dream into dream): death roster and the all-dead grace behave; only
      players actually in that dream see its epilogue/credits.
- [ ] Dream failure does not mark it completed.
- [ ] Join during a dream is refused while `AllowJoinDuringDream=false`.

## 9. Spectator (F4)

- [ ] Dead player: F4 cycles living peers; camera, audio listener and position follow.
- [ ] Wrapping the cycle does not release a night-dead player early.
- [ ] Walkie: RMB with the radio only transmits while playing (not in inventory,
      container, dialogue, map, journal, pause, dead, or while chat is open).

## 10. Steam invite join

(Needs two real Steam accounts; SecondDarkwood is GOG and cannot do Steam.)

- [ ] HOST STEAM creates a lobby; the lobby id shows in F2. Friend accepts the invite
      from the overlay: join starts, shows STEAM, the join timeout (35 s) applies,
      and the world-request nudges fire at 10 s and 25 s on the title screen.
- [ ] Launching the game with a pending invite (`+connect_lobby`) joins the same way.
- [ ] Failed join (bad lobby id, Steam not ready) shows a button label, then reverts.
- [ ] Steam voice: proximity voice works; a peer who leaves stops being audible at once.

## Log checks after each run

- No `FATAL`, no unhandled exception stacks, no `Patch target missing`.
- `[Perf]` lines on both roles; no `[PerfCliff]` (`scripts/check-dualbox-perf.sh`).
