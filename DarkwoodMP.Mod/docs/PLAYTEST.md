# Playtest checklist

Run it against the version and protocol in the README table.

Dual-box (Steam host + SecondDarkwood client) first, then a 3-player run
(host + two clients). Tick a section only after you saw it in the game; code-only
status lives in `CHANGELOG.md` and `COOP_COVERAGE.md`. Attach **both** loader logs
to any failure (`LOGGING.md`).

Setup for every run: `LogPreset=Support` with `PerfProbe = true` (or `Trace` for a
bug pack) on every box, same DLL everywhere.

## 0. First checks for the current release

The latest `CHANGELOG.md` entry (sound pass) gives every world sound one owner. Listen
on the peer that did NOT cause the sound:

- [ ] Host opens and closes a wooden door near the client: one open and one close
      sound on the client, not two. Same with the client opening, heard on the host.
- [ ] Zombies hit a barricaded door at night: one wood hit per blow, one break sound
      when it goes, no hit sound on top of the break. A metal door hit clangs.
- [ ] Toggle a lamp / switch and start the generator: one click and one start sound;
      after turning the generator off its hum stops on every peer.
- [ ] Set something on fire, let it burn out: the fire loop stops on every peer.
- [ ] Host drags a crate across grass, then floor, near the client: the client hears
      the grass scrape then the floor scrape, and it stops with the crate. While that
      runs, another crate dragged elsewhere keeps its own scrape when the first is let go.
- [ ] The Sound volume slider at 0 on the client also silences peers' scrapes and
      host dream sounds.
- [ ] Voice (two Steam installs only): a teammate a room away is heard; fades out
      around the same distance as their footsteps.

The previous entry (entity sync remaster) moved enemy-hit judging to the player being hit and
changed how clients interpolate enemies. Run these first. `LogPreset=Support` shows
the `[EnemyAttack] local hit` / `confirmed hit` lines; `Trace` adds every attack sent
and re-created.

- [ ] Client fights a dog / villager: a step back out of the swing on the client
      screen is a miss; standing in it is a hit. Client log has
      `[EnemyAttack] local hit by melee` per hit, and no `[DamagePlayer] local took`
      from the same enemy (no double damage).
- [ ] Same enemy attacks twice in a row: the client sees both swings.
- [ ] Host watches the client get hit: hit sound and blood on the client's body; a
      third player sees the blood too.
- [ ] Host gets hit while a client stands next to it: the host takes vanilla damage;
      the client is not hit by the host's swing unless the swing reaches it on the
      client's own screen.
- [ ] A ranged / throwing enemy (bullets, rocks) shoots at the client: the shot is
      visible on the client and only hits when it reaches the client there.
- [ ] Enemies near the client move smoothly at chase speed, stop cleanly (no slide
      past the stop point and snap back), and appear with the right body after a
      late join (at most a 1 s wait).
- [ ] Night shadows on a client: the hit is flat (`shadow` in the `[DamagePlayer]`
      line), no armor reduction, no interrupt.
- [ ] Banshee scream near a client: one scream, the client's camera shakes, the host
      does not get a second scream.

Still open from the previous entry (host validation, relay and session reset):

- [ ] Startup log has `Harmony: N patch classes applied, 0 critical / ...`. With a
      critical failure, Host and Join show the "Multiplayer disabled" text and refuse.
- [ ] Two players loot the same container slot at once: one gets it, the other is
      refunded exactly (section 5 in detail).
- [ ] Saw: one player converts while the other takes wood; both end on the host's
      stock, no wood created or lost.
- [ ] A client drags a body / talks on the walkie while doors and pickups happen:
      the drag and voice stay smooth and the events are not delayed (relays keep the
      unreliable method).
- [ ] Host migration, then the old host reconnects: it gets its old player id, no
      stuck "loading" peer, no doubled inventory (section 7).
- [ ] Load a manual save from the title screen while hosting: inventory and stats
      are the slot's, not the pre-load character's.
- [ ] Steam client drops (kill the process) and rejoins the same lobby: one player
      entity, not two; with a host password set, the rejoin still has to pass it.
- [ ] Stop and host again in the same game process: no leftover remote players,
      thrown flares, kinematic bodies or pending container takes from the last session.

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
- `[Perf]` lines on both roles (needs `PerfProbe = true` under Support); no
  `[PerfCliff]` (`scripts/check-dualbox-perf.sh`).
- Host: no `Dropping <type> from pN (host-only type)` in a normal run (a client sent
  something only the host may send: bug). `(no handshake yet)` / `(refused peer)` are
  expected only around a join or a kick; attach the log if they show up mid-session.
