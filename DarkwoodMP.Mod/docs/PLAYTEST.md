# Playtest checklist

Run it against the version and protocol in the README table.

Dual-box (Steam host + SecondDarkwood client) first, then a 3-player run
(host + two clients). Tick a section only after you saw it in the game; code-only
status lives in `CHANGELOG.md` and `COOP_COVERAGE.md`. Attach **both** loader logs
to any failure (`LOGGING.md`).

Setup for every run: `LogPreset=Support` with `PerfProbe = true` (or `Trace` for a
bug pack) on every box, same DLL everywhere.

## 0. First checks for the current release

The latest `CHANGELOG.md` entries add listening in on another player's dialogue and settle
story hand-ins and dialogue trips; earlier ones moved creature sounds to the host's actual
calls. Listen and watch on the client:

- [ ] Listening in: the host talks to the Wolf, the client talks to him too and gets
      the host's window: same portrait, same lines typing out, same highlighted option. The
      host picks a decision: the client's view follows. Client presses Esc: its view closes,
      the host's talk goes on. Swap roles (client talks, host listens). Join midway through a
      board, and while the talking player is trading (portrait only).
- [ ] Sister's key at the same time: one player shows it to the Wolf, the other to the
      Musician: only one hand-in takes; the other gets "Someone already handed that over."
- [ ] Client accepts the Wolf's lift to the Doctor's house: the client travels, the host stays.
- [ ] Client hits and kills a dog / villager with an axe: one hit sound per blow, the
      pain sound and one death scream; a third player (or the host) hears them too.
- [ ] Client shoots a creature: its hit sound plays on the client (no local prediction
      for guns).
- [ ] Creature idle sounds (barks, grunts) and their loops (breathing, buzzing, the
      wolfman growl bed) are heard on the client near the creature, also after a late
      join or walking up to one the host woke; the loop stops when it dies.
- [ ] Enemy footsteps near the client move with the enemy and are muffled behind a
      wall, as on the host.
- [ ] Walking up to an old corpse: no death scream.
- [ ] Banshee sees the client: the client hears the scream on itself, its camera shakes
      and the red overlay fades in; the host gets none of it. It stops when the banshee
      loses sight. The banshee's light shows on every peer.
- [ ] Client inside a bunker / cellar with enemies, host outside: the enemies chase and
      patrol normally (no reset to their route every second); the pad's enter events
      (music stingers, scripted spawns) run once.
- [ ] Host and client in the same pad: the other player's body moves smoothly (no
      hitch to an idle pose once a second).
- [ ] Client leaves a bunker the host is not in: host log has `last remote left` for it.
- [ ] Client enters a cellar / bunker the host never visited: enemies inside walk and
      chase (they have paths), the pad's doors and props are all there, and the host's
      forest keeps streaming around the host.
- [ ] Client uses a location entrance with a cursor action (`*_enter*`): it is moved in
      with the black screen.
- [ ] Host lost mid-dream (section 7): the dream continues under the new host.
- [ ] Dreams, all on the client: a second dream in one run has its own music, items and
      outcome (not the first dream's); pad doors and the dialogue door work; die in a
      shared dream, the teammate finishes it: you wake up out of spectate with no story
      reward; all die: you wake up and can move; a dialogue-started dream shows a black
      fade, not the skill video; a chained dream moves both players to the next pocket.

The previous entry (sound pass) gives every world sound one owner. Listen
on the peer that did NOT cause the sound:

- [ ] Host opens and closes a wooden door near the client: one open and one close
      sound on the client, not two. Same with the client opening, heard on the host.
- [ ] Zombies hit a barricaded door at night: one wood hit per blow, one break sound
      when it goes, no hit sound on top of the break. A metal door hit clangs.
- [ ] Toggle a lamp / switch and start the generator: one click and one start sound;
      after turning the generator off its hum stops on every peer.
- [ ] Set something on fire, let it burn out: the fire loop stops on every peer.
- [ ] Start a fire or the generator, walk away past hearing, come back: it is audible
      again on the way in.
- [ ] A gunshot or explosion well beyond 700 from you (host and client): heard
      faintly, as far as single player carries it.
- [ ] Late join near lit lamps: no burst of switch clicks.
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

## Automated runs (test pilot)

Unattended dual-box runs on this machine, no hands needed. Back up both save trees first
(host `~/.config/unity3d/Acid Wizard Studio/Darkwood`, client compatdata
`…/LocalLow/Acid Wizard Studio`) and restore them after: the pilot plays on real profiles.

- `PILOT_MODE=host:1 scripts/pilot/pilot-run.sh` — host loads profile 1 and hosts LAN, the
  client joins (world copy in its slot 1). `newgame:4` / `newgameskip:4` start a new game in
  an EMPTY slot instead (run `BACKUP_DIR=… scripts/pilot/prologue-reset.sh` first). Both
  windows are parked on Hyprland workspace 7.
- `scripts/pilot/pcmd.sh h|c <command>` — run a pilot command on the host / client and print
  its reply (`status`, `tp x z`, `door open`, `chars 600`, `time 1090`, `endnight`, `die`,
  `prologue`, `skipmovie`, `dreamend <outcome>`, `shot name`, `desync`, … — see
  `TestPilot.cs`). Screenshots land in `<game>/pilot/`.
- `scripts/pilot/pilot-quit.sh <tag>` — quit both and copy both logs to `/tmp/darkwood-pilot`.
- Read `[Pilot]`, `[Desync]` and `unity Error/Exception` lines (each distinct Unity error is
  recorded once with its stack).

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
- [ ] Kill the host mid-dream (3 players: two survivors): both stay in the dream; the
      new host's dream enemies move and attack; the other survivor rejoins (log
      `Still in dream session … after host migration`); finishing the dream ends it for
      both with the reward. Kill it during the entry video instead: both are back in
      their world, no reward, and migration still completes.

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
- Client `[EntTimeline]` (same switch): `interp%` near 100 while creatures move, `coast%` /
  `hold%` low; host `hostEntTick ms avg` near 50.
- Host: no `Dropping <type> from pN (host-only type)` in a normal run (a client sent
  something only the host may send: bug). `(no handshake yet)` / `(refused peer)` are
  expected only around a join or a kick; attach the log if they show up mid-session.
