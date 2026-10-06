# Playtest checklist

The manual checks that turn "code covered" into "works". Run them against the
version and protocol in the README table. The sections follow
[How co-op works](HOW_COOP_WORKS.md), so each check tests a rule written there.

## How to run

- **Order:** a dual-box run first (Steam host and the SecondDarkwood client over
  LAN), then a three-player run (host and two clients). Section 1 lists what the
  current release changed; run it first.
- **Both arrows:** a check written as "host does X, client sees Y" also runs the other
  way round (client does X, host sees Y), and once with a third player watching or
  joining late. Tick a box only when every direction worked in the game.
- **Settings on every box:** the same DLL, `LogPreset=Support` and
  `PerfProbe = true` (`LogPreset=Trace` for a bug pack). On the Linux dual-box,
  `FreeCursorForDualBox = true` in both config files.
- **Logs:** BepInEx overwrites `LogOutput.log` on every launch. Save both players'
  logs right after a failure, before starting the game again, and attach both
  ([LOGGING.md](LOGGING.md)). Code-only status lives in the
  [CHANGELOG](../../CHANGELOG.md) and [COOP_COVERAGE.md](COOP_COVERAGE.md).

---

## 1. Current release first

The releases since the last playtest that need a first look, newest first. Each item
points to the section with the full check.

- [ ] **Animations in step:** stand host and client side by side at a campfire, swaying
      trees and water, and the mimic bodies under the church / the Musician's house
      zombies: the same frame at the same moment. Turn away and back: still in step. Pause
      everyone, resume: no jump. A late joiner is in step within a few seconds (client log:
      `[AnimClock] following the host's clock`; section 17).
- [ ] **Same look everywhere:** grass, trees, debris, creature tints, flickering and
      twitching animations and vines look the same for host and client, in a fresh
      world, after a reload, for a late joiner and after a chapter change; examine
      lines are one deck (sections 13 and 17).
- [ ] **Pause menu:** one player in Esc does not stop the others; everyone in Esc
      pauses the world; a paused client still saves and backs up (section 6).
- [ ] **Story hand-ins:** the egg and the sister's key handed to two NPCs at once:
      one outcome and one reward (section 13).
- [ ] **Listening in on a dialogue** and dialogue trips (section 13).
- [ ] **Story items stay in the world** when a client leaves (section 4).
- [ ] **Oxygen tank for every player** (section 12).
- [ ] **Scene data pass:** party dream openings and endings, the wolf arena table leg,
      the doctor's house clock (sections 14 and 15).
- [ ] **Per-player prologue**, shared permadeath, the desync checker (sections 7, 9
      and 20).
- [ ] **Shared clock** stops only when everyone is inside (section 6).
- [ ] **Sounds:** one owner per sound, creature sounds (section 18).
- [ ] **Enemies:** the player being hit decides the hit, smooth interpolation, one
      target per creature (section 10).

---

## 2. Deploy and version

- [ ] `md5sum` of `DarkwoodMP.Mod.dll` (and `LiteNetLib.dll`) matches on every box.
- [ ] The F2 title reads **YokWare Branch &lt;version&gt; / Path B** and the footer shows
      `proto=&lt;protocol&gt;` on every box, as in the README table. The log banner shows
      the same `Protocol=`.
- [ ] The startup log has `Harmony: N patch classes applied, 0 critical / ...`. With a
      critical failure, HOST and JOIN show "Multiplayer disabled" and refuse.
- [ ] A client on a different DLL is refused with a protocol mismatch, not half-joined.

## 3. Hosting and joining

- [ ] The host loads a save and enters the chapter. The client's JOIN LAN goes through
      CONNECTING, WAIT HOST (until the host is in the world), DOWNLOADING, CHOOSE SLOT
      and ENTER WORLD.
- [ ] Slot picker: an empty slot is used without asking; an occupied slot asks to
      confirm, and Cancel clears the confirm; a slot from another campaign shows
      `[DIFFERENT CAMPAIGN]`; a second connect does not show an old confirm.
- [ ] A client claiming a world from another campaign is refused with WRONG SAVE.
- [ ] A third player joins an active session; all three see each other.
- [ ] A third player joins at night, during a fight and while another player is in a
      location: the night state, the creatures and that player's location are right.
- [ ] Host start failure (a second host on the same port) shows PORT IN USE on the
      button. JOIN STEAM with no lobby id shows SET LOBBY ID.
- [ ] F2 fields: typing a port does not save per keystroke; closing the window saves;
      a Steam lobby id the host just created is not overwritten by stale text.
- [ ] With `HostPassword` set on the host, a client with the wrong password is
      refused (LAN and Steam).
- [ ] `MaxPlayers`: a player beyond the limit is refused.

## 4. Saving, leaving and rejoining

- [ ] F3 on the host saves the world, and every client's copy and character are saved
      at the same moment (`SaveSync` lines on both logs). F3 on a client refuses
      ("only the host can save"). F3 Load refuses during a session.
- [ ] A client quits and rejoins: it gets its own bag, skills, level and position back,
      not the host's character.
- [ ] A client drops briefly (pull the cable or kill the network for a few seconds):
      it reconnects by itself with the same player id, nothing doubled.
- [ ] A client carrying a quest item (a car part, the violin) leaves for good: after
      60 seconds the item lies where the client stood. Someone picks it up; the client
      returns: the item is gone from the returner's bag. Again without picking it up:
      the returner gets it back off the ground.
- [ ] A client leaving does not drop its oxygen tank or journal items.
- [ ] Load a manual save from the title screen, then host: the inventory and stats are
      the slot's, not the character from before the load.
- [ ] Stop and host again in the same game process: no leftover remote players,
      flares, dragged bodies or pending container takes from the last session.

## 5. Host migration

- [ ] Kill the host process mid-session: the survivors elect the lowest player id, the
      new host's world carries on, the others reconnect (LAN and Steam).
- [ ] A graceful DISCONNECT on the host hands off instead of dropping everyone.
- [ ] The old host reconnects: it gets its old player id, no stuck "loading" peer, no
      doubled inventory.
- [ ] A client sitting in the pause menu when the host dies takes part in the
      migration (it does not just leave).
- [ ] Kill the host mid-dream (three players, two survivors): both stay in the dream,
      the dream's enemies move and attack, finishing it ends it for both with the
      reward. Kill it during the entry video instead: both are back in their world
      with no reward, and migration still completes.

## 6. Time, menus and the pause menu

- [ ] The host goes into a location while the client stays in the forest: the clock
      keeps running for both. Both inside a location: the clock stops.
- [ ] Map, journal, padlock, dialogue and the level-up menu do not pause the world for
      the others. A player in a dialogue or the level-up menu is ignored by creatures
      and cannot be hurt.
- [ ] **Pause menu, one player:** the host opens Esc next to a dog. The client's world
      keeps going (clock, creatures); the dog ignores the host and cannot hurt it. The
      other way round: the client's body stays put and is left alone, and the client's
      game is not frozen behind the menu.
- [ ] **Pause menu, everyone:** all players open Esc: the world pauses on every machine
      (creatures, clock, flares stop; the menu music plays). Any one closes it: the
      world runs again for everyone. A player joining while the others are paused
      unpauses the world.
- [ ] **Pause menu, session paths:** the host presses F3 while the client sits in Esc
      (the client's save still follows); the client quits to the title from Esc (its
      character is backed up, `[Backup]` lines on both logs); the client keeps
      sending its position while in Esc (its body does not freeze for the host).
- [ ] Chat (Ctrl+C) does not move the character or switch the hotbar; Esc closes chat
      without opening the pause menu.

## 7. Prologue

- [ ] A new world: the host plays its prologue, stays connected, and the day 1 clock
      holds at 05:00.
- [ ] A player new to the world joins: after the download it plays its own opening
      movie and prologue offline, then reconnects. Day 1 waits for it (at most 45
      minutes). Journal pages it wrote reach the shared journal.
- [ ] A player who joins after the host skipped the prologue starts in the hideout
      with the chapter's starting pack.
- [ ] A returning player does not replay the prologue.
- [ ] Three players, two of them in their prologue at the same time.

## 8. Night, morning and hideouts

- [ ] Night with players in different hideouts: monsters come to every living player at
      home; a player out in the forest gets the worm. A ward protects the player the
      monster is after.
- [ ] The host away from home at night: night events run in a client's hideout and
      never inside a location.
- [ ] Dawn: each surviving player standing in a hideout gets their own morning reward
      (`MorningReward` 145), once. A player not at home gets none.
- [ ] Dawn with the host away: the morning runs in the client's hideout (trader,
      cleared creatures).
- [ ] One player walks out of the hideout in the morning: the trader stays for the
      others until the hideout is empty.
- [ ] Generator: a client refuels and switches it; the fuel matches on every machine.
- [ ] Village at night: friendly villagers go home between the night warning and
      morning, only while nobody sees them (`[NightVillage]` lines count the right
      villagers).

## 9. Death, spectating and difficulty

- [ ] A client dies by day: it drops its bag (looted by anyone, marker inside a
      location), respawns at its own home; the others' fights are not reset.
- [ ] A client dies at night, others survive: it spectates (F4 cycles living players,
      camera and sound follow), cannot release itself early, and at dawn is released
      and sent home (`NightDeathRelease` 144).
- [ ] Everyone dies at night: one shared morning, no hang. The last living player
      disconnecting at night also resolves the morning.
- [ ] Hard, last life (or Nightmare): a death keeps that player down until the next
      morning, also by day. Everyone down with every death permadeath-eligible: the
      permadeath ending for all, and "start over" resets every character. Anyone with
      a life left: a normal all-dead morning.
- [ ] A living player presses F4: spectates the others; wrapping returns to the body.
      F4 does nothing in menus, chat, dialogue, cutscenes and dream entries.

## 10. Enemies

- [ ] Defender decides: the client fights a dog or villager. A step back out of the
      swing on the client's screen is a miss; standing in it is a hit
      (`[EnemyAttack] local hit by melee`, no `[DamagePlayer] local took` from the same
      enemy). The same enemy swinging twice: the client sees both.
- [ ] The host watches the client get hit: hit sound and blood on the client's body; a
      third player sees the blood too.
- [ ] A ranged or throwing enemy shoots at the client: the shot is visible and hits only
      when it reaches the client there.
- [ ] Enemies near the client move smoothly at chase speed, stop cleanly (no slide and
      snap back), and appear with the right body after a late join.
- [ ] Two players near one dog: it picks one and keeps it (no flipping back and forth;
      `[TargetSwitch]` lines are rare); it switches when its target is lost or the other
      player is much closer.
- [ ] Night shadows on a client: the hit is flat (`shadow` in the `[DamagePlayer]`
      line), no armor reduction.
- [ ] Banshee sees the client: the scream, camera shake and red overlay are the
      client's only; the banshee's light shows on every peer.
- [ ] The daytime redneck ambush comes for a client out on the road while the host is
      at home.
- [ ] Walking up to an old corpse: no death scream; a body the host destroys
      disappears for everyone.

## 11. Player combat and friendly fire

- [ ] Friendly fire on: players can hurt each other with melee, guns, explosions,
      fire and traps.
- [ ] Friendly fire off: another player's weapons, explosions, fires and traps spare
      you; your own fire and traps still hurt you; enemies are caught by everyone's
      traps.
- [ ] A client's molotov or gas bomb: the blast and fire hurt creatures once (host
      damage), and the look matches on every machine.
- [ ] A client pours gasoline and lights it: the trail burns on every machine.
- [ ] A shotgun blast from a client lands every pellet on a creature.

## 12. Items, containers, trade and crafting

- [ ] Two players take from the same container slot at once: one gets it, the other
      is refunded exactly (no dupe, no loss), including items with durability or
      ammo, recipes, and an item already on the cursor.
- [ ] Two players press pickup on the same item on the ground at once: one gets it, no
      duplicate stays on the ground. Items a player drops land in the same spot for
      everyone.
- [ ] Loot sharing (`ScaleWithPlayers`): taking an odd mushroom stack gives the taker
      the stack times the party size; the container keeps its stack for the next
      player. Wood and nails are not scaled.
- [ ] Oxygen tank: one player gets the empty tank; every player gets one. It is filled
      at the compressor: every player's tank is full. A tank stashed later is not
      handed out again.
- [ ] Trade: two players buy the same item from the trader at once: one gets it, the
      other's trade is reversed. Standing with the night trader is per player.
- [ ] Workbench: both players use the same bench; upgrading the bench counts for
      both; materials taken from the bench's pile are gone for everyone; the crafted
      item goes to the crafter.
- [ ] Saw: one player converts while the other takes wood; both end on the host's
      stock.

## 13. Story and dialogue

- [ ] One speaker per NPC: the host talks to the Wolf, the client talks to him too and
      gets a read-only view of the host's talk: same portrait, lines typing out,
      highlighted option. The host picks a decision: the view follows. Esc closes the
      view; the host's talk goes on. The other way round, and joining midway through a
      board.
- [ ] The egg at the same time: the client hands it to the Wolf while the host (or a
      second client) hands it to Piotrek: the client's talk waits a moment before the
      hand-in, only one of them gets the outcome and the pistol, the other hears
      "Someone already handed that over." A client that backs out of the Wolf's talk
      before the hand-in frees the egg for the others at once.
- [ ] The sister's key at the same time (Wolf and Musician): only one hand-in takes.
- [ ] The client accepts the Wolf's lift to the Doctor's house: the client travels,
      the host stays.
- [ ] Items a dialogue gives go to the speaker only; flags and world changes reach
      everyone.
- [ ] A client examines an object with a story trigger: the trigger fires once and the
      object is examined for everyone.
- [ ] Examine lines from a random pool (corpses, junk): host and client examine
      different objects of the same kind in turn; no line comes up twice until every
      line was read. A late joiner continues the same deck.
- [ ] A one-shot hint or recipe lesson in the hideout is given to each player once.

## 14. Dreams

- [ ] A level-up dream: everyone alive enters, the dead sit it out. Each dreamer gets
      the outcome items; world events reach everyone. The dream is not played again.
- [ ] A late player reaching that level later gets a dream the party has not played
      (or just levels up).
- [ ] Die in a shared dream while the teammate finishes it: you wake up out of
      spectate with no reward. Everyone dies: you all wake up and can move.
- [ ] A chained dream moves every dreamer to the next part; the dead stay down.
- [ ] A second dream in one run has its own music, items and outcome.
- [ ] The bunker dream's leave door opens for every dreamer; the overworld bunker door
      is untouched.
- [ ] A failed dream is not marked done. A join during a dream is refused while
      `AllowJoinDuringDream=false`.
- [ ] Party dream openings and endings: every dreamer gets the clothes and positions
      (dream_home, the epilogue room).
- [ ] Black chompers in a dream: one extra per extra player, fighting with the
      original.

## 15. Locations and chapter change

- [ ] A client enters a cellar or bunker the host never visited: the enemies inside walk
      and chase, the doors and props are all there; the host's forest keeps running.
- [ ] A client inside a bunker with enemies, the host outside: the enemies chase and
      patrol normally (no reset to their route), and the location's entry events (music
      stingers, scripted spawns) run once.
- [ ] Host and client in the same location: the other player's body moves smoothly.
      A client leaving a location the host is not in: `last remote left` in the host log.
- [ ] The first entry to a location: every peer's pad sits at the same place, a late
      joiner too.
- [ ] A location entrance with a cursor action (`*_enter*`) moves the client in with
      the black screen.
- [ ] The wolf arena: every player still in it loses the table leg; the arena resets
      only when nobody living is left in it.
- [ ] The doctor's house entry: the clock moves once, on the host.
- [ ] Chapter 1 to 2: the host walks the chapter-jump trigger; every client gets the
      world, confirms it (`ChapterShareAck` 140) and loads on `ChapterLoadGo` 141.
      Walking it as a client moves everyone too. A slow client still arrives. Player
      ids and names are unchanged; Steam sessions resume on the same lobby.

## 16. Ending

- [ ] Every player in the ending reads their pages; the credits start when all are
      done (or after 2 minutes), and only for players in the ending.

## 17. World objects

- [ ] Doors: one player opens, another closes, a third watches: the same state and
      one sound each time. A barricaded door cannot be opened; door health matches.
- [ ] Lights: lamps, the generator, flares (they go out together), and the other
      player's lantern and flashlight look the same everywhere. A late joiner near lit
      lamps hears no burst of switch clicks.
- [ ] Dragging a body or crate: smooth for everyone, and the scrape stops with it.
- [ ] Traps placed by one player catch enemies for everyone; a sprung trap disappears
      for everyone.
- [ ] Same look: stand host and client side by side in the open world, in a location
      the client entered first, in a dream, and next to a dog or spider pack the host
      spawned. Ground decals, bushes, tree tints and flips, dead-body twitches and
      flickering fire animations match. Then the host reloads (F3, quit, load) and a
      third player joins late: still the same. Each save writes `savcos.dat` next to
      `sav.dat` (host log: `[Cosmetic] roll seeds saved: N`; a load logs
      `[Cosmetic] roll seeds from save: N`).

## 18. Sound, voice and chat

Listen on the player who did not cause the sound.

- [ ] One open and one close sound per door; one hit per blow on a barricade, one break
      sound when it goes.
- [ ] Lamp, switch and generator: one click and one start sound; the generator hum
      stops on every peer when it is turned off. A fire loop stops when it burns out.
- [ ] Walking away from a fire or generator past hearing and back: it is audible again.
- [ ] A gunshot or explosion far away is heard faintly, as in single player.
- [ ] A client kills a dog with an axe: one hit sound per blow, the pain sound and one
      death scream, heard by the others too.
- [ ] Creature barks, breathing and growls near the client, also after a late join; the
      loop stops when the creature dies. Enemy footsteps move with the enemy and are
      muffled behind a wall.
- [ ] The Sound volume slider at 0 also silences other players' sounds.
- [ ] Voice (needs two logged-in Steam clients): a teammate a room away is heard and
      fades out around the distance of their footsteps; muffled through a wall. A
      walkie-talkie carries from anywhere. A player who leaves stops being heard.
- [ ] The walkie transmits only while playing (right mouse with the radio in hand), not
      in the inventory, a container, dialogue, the map, the journal, the pause menu,
      while dead or with chat open.
- [ ] A client dragging a body or talking on the walkie while doors and pickups happen:
      the drag and the voice stay smooth and the events are not delayed.
- [ ] Chat: lines and speech bubbles appear for everyone; `ChatEnabled=false` turns it
      off.

## 19. Steam

(Needs two real Steam accounts; the GOG install cannot use Steam.)

- [ ] HOST STEAM creates a lobby and shows its id in F2. A friend joins from the invite
      overlay; the 35 s join timeout applies.
- [ ] Launching the game from a pending invite (`+connect_lobby`) joins the same way.
- [ ] A failed join (bad lobby id, Steam not ready) shows a button label, then reverts.
- [ ] A Steam client is killed and rejoins the same lobby: one player, not two; with a
      host password set, the rejoin still has to pass it.

## 20. Log checks after each run

- No `FATAL`, no unhandled exception stacks, no `Patch target missing`.
- No `[Desync] DESYNC` line that is not followed by `resolved` (the checker compares
  the host's world with each client's every 15 seconds).
- `[Perf]` lines on both roles; no `[PerfCliff]` (`scripts/check-dualbox-perf.sh`).
- Client `[EntTimeline]`: `interp%` near 100 while creatures move, `coast%` and
  `hold%` low; host `hostEntTick ms avg` near 50.
- Host: no `Dropping <type> from pN (host-only type)` in a normal run (a client sent
  something only the host may send: a bug). `(no handshake yet)` and
  `(refused peer)` are expected only around a join or a kick.

## Automated runs (local tooling)

The maintainers' machine has an unattended dual-box test pilot (the `TestPilot`
commands in the mod, driven by scripts under `scripts/pilot/` that are not part of
this repository). It plays on real profiles: back up both save trees first and
restore them after. Read its `[Pilot]`, `[Desync]` and Unity error lines in both logs.
