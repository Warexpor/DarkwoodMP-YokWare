# How YokWare co-op works

Darkwood is a single-player game. Almost every system in it assumes one player:
`Player.Instance`, one clock, one save, one bag, one camera, one set of story
choices. YokWare Branch turns it into a co-op game for two or more players (up to
8 by default). This document explains how: the design philosophy, the rules every
system follows, what each player can and cannot do, what is shared, what is
personal, what is duplicated or scaled for the party, and what is still open.

It describes the current build. The version, the wire protocol and the install
steps are in the [README](../../README.md). What changed and when is in the
[CHANGELOG](../../CHANGELOG.md). Related documents:

| Document | What it covers |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Source layout, wire format, dispatch, patch and test rules (for contributors) |
| [COOP_COVERAGE.md](COOP_COVERAGE.md) | Per-domain code and runtime coverage, decompile notes on parked mechanics |
| [CONFIG.md](CONFIG.md) | Every config key and its default |
| [PLAYTEST.md](PLAYTEST.md) | Manual playtest checklist |
| [LOGGING.md](LOGGING.md) | Log presets and what to send for support |

**Status note.** "Code covered" and "works" are not the same thing here. A rule in
this document is how the code is meant to behave. It counts as verified only after
a dual-box (or three-player) playtest. The CHANGELOG marks each release as
playtested or not.

---

## Contents

1. [Philosophy](#1-philosophy)
2. [The single-player problem and the toolbox](#2-the-single-player-problem-and-the-toolbox)
3. [Who owns what](#3-who-owns-what)
4. [Sessions: hosting, joining, saving, leaving](#4-sessions-hosting-joining-saving-leaving)
5. [Time, pause and the clock](#5-time-pause-and-the-clock)
6. [Night, morning and hideouts](#6-night-morning-and-hideouts)
7. [Death, spectating and difficulty](#7-death-spectating-and-difficulty)
8. [Enemies and AI](#8-enemies-and-ai)
9. [Player combat and friendly fire](#9-player-combat-and-friendly-fire)
10. [Items, containers, trade and crafting](#10-items-containers-trade-and-crafting)
11. [Story: flags, events, examinables, chapters](#11-story-flags-events-examinables-chapters)
12. [Dialogue](#12-dialogue)
13. [Dreams](#13-dreams)
14. [Prologue, tutorial and ending](#14-prologue-tutorial-and-ending)
15. [Locations and travel](#15-locations-and-travel)
16. [World objects: doors, lights, fire, traps](#16-world-objects-doors-lights-fire-traps)
17. [Seeing and hearing other players](#17-seeing-and-hearing-other-players)
18. [Balancing for the party](#18-balancing-for-the-party)
19. [What players cannot do](#19-what-players-cannot-do)
20. [Known gaps and parked items](#20-known-gaps-and-parked-items)
21. [Rules for contributors](#21-rules-for-contributors)

---

## 1. Philosophy

These are the principles every decision in the mod is measured against. When two
of them pull in different directions, the order below is roughly the priority.

### One world, one host

The host's game **is** the world. The host runs the simulation: enemy AI,
spawners, combat damage, story events, the clock, weather, night scenarios and the
world save. Clients do not run a second copy of any of that. They send requests
("I opened this door", "I hit this dog", "I took this item"), the host checks them
against its own world, applies them, and tells everyone the result.

This is the only model that stays consistent with 3 or more players. Anything a
client decided on its own in earlier builds drifted or duplicated: two copies of a
spawned enemy, two copies of a looted item, two different night events.

### The client must feel like the host

A client should barely notice it is not the host. Enemies, deaths, corpses, doors,
lights and sounds should look and behave the way they do for the host. The client
presents the host's result smoothly; it does not run a thinner, laggier version of
the game. When latency makes that impossible (dodging an enemy swing, for example),
the mod moves the decision to the player who would feel it (see "defender decides"
in [section 8](#8-enemies-and-ai)) instead of accepting a worse experience for
clients.

### The world is shared, the hero is personal

- **Shared:** everything in the world. Items are unique: there is one sister's key,
  one shotgun in that crate, one door. What one player opens is open for everyone;
  what one player takes is gone for everyone. Story flags, NPC states, the journal,
  the workbench level and the clock are shared.
- **Personal:** the player character. Each player has their own bag and hotbar,
  health, skills, level, recipes, home oven and respawn point, map pins and trader
  standing. A player's progress is not overwritten by the host's.

When a rule is unclear, the first question is: "is this a fact about the world, or a
fact about one player?"

### Three or more players must really work

Nothing is designed for "host plus one client". State is keyed by player id, host
fan-out reaches every peer, and every rule is checked with a third player joining
late, standing somewhere else, or racing the other two for the same thing.

### Vanilla parity first

The decompiled game is the reference. Before adding co-op handling for a mechanic,
we read how vanilla does it and keep that behaviour wherever one player is
involved. The mod only changes a mechanic where vanilla's single-player assumption
breaks with several players, and then changes as little as possible.

Corollary: the decompile contains dead mechanics the shipped game never uses (there
is no hunger, and there is no sleeping or time skip). Before building around a
mechanic, confirm the game actually calls it and that its text exists in the
language files. Dead mechanics are left alone.

### Never crutch

No magic tighter ranges, forced 2D audio, swallowed exceptions, "retry and hope",
or patches that hide a symptom. Every fix finds the real cause (a wrong setting, a
double code path, a lifecycle kill, a gate that disagrees with the game's own
range) and fixes that. A symptom patch ships as a bug.

### Trust-limited co-op, not anti-cheat

The host validates what clients send: positions must be finite, attackers must be in
range and alive, damage is clamped, item takes are checked against the host's own
container, trades against the host's own stock. That stops bugs and casual griefing
from breaking the shared world. It is not a defence against a determined cheater;
the mod is meant for friends.

### Cosmetic divergence is a last resort

"The client must feel like the host" wins over this rule. By default a client sees
exactly what the host sees, cosmetics included: sprite tints, idle animation
phases, the order of examine descriptions and any other visual rolled from a local
random number generator. Usually that only takes a shared seed or the rolled value
on the wire.

A peer may differ only when there is really no way to make it match, for example
when the value is rolled inside engine code the mod cannot reach, or when matching
it would break something a player can act on. Being cheaper to skip is not a
reason. Each accepted divergence is written down with why it cannot be matched, and
it must never touch gameplay, collisions or the story.

---

## 2. The single-player problem and the toolbox

Most co-op work in this mod is one of a handful of techniques. Knowing them makes
the rest of this document easier to read.

| Technique | When it is used | Examples |
|---|---|---|
| **Host-only, client suppressed** | The thing is world state and must happen exactly once. Clients skip the vanilla method (Harmony prefix) and receive the result. | Spawners, enemy AI, random events, world generation, loot rolls, the clock, one-shot story events |
| **Request, validate, broadcast** | A client acts on the world. | Doors, container takes, pickups, attacks, trades, generator switches, gasoline |
| **Host replays the client's action** | The action runs complex vanilla code that only the host may run for the world. The client does it locally for itself, the host re-runs it silently for the world. | Dialogue outcomes, examining objects |
| **Per-player instance** | Vanilla does it for "the player", and every player should get their own. | Prologue, morning reward, home oven, level-up dreams, hints and one-shot "lessons" |
| **One for every player** | A unique item would block everyone except its holder. | Chapter 2 oxygen tank |
| **Party check** | Vanilla asks "does the player have X?" for a unique item. With one shared world, any player holding it counts. | Event "has item" requirements |
| **Arbitration (first come, first served)** | Two players race for one thing. The host picks the winner and the loser is refused cleanly (and refunded). | Container slots, ground pickups, hand-ins of shared story items, one speaker per NPC |
| **Defender decides** | Latency makes the host's judgement wrong for the player being affected. | Enemy attacks on clients |
| **Stand-ins fed into vanilla checks** | Vanilla only knows `Player.Instance`. Remote players exist on the host as stand-in bodies (`RemotePlayerProxy`) and are run through the same vanilla checks. | Enemy sight and targeting, "in sight of player" triggers, birds, the porter, the banshee |
| **Scoping** | The same object name exists in two worlds (overworld and a dream, two hideouts). Lookups are scoped to the right pad or location instead of by name. | Dream pads, location pads |
| **Rewrite** | A vanilla outcome makes no sense with a party. | A permadeath death becomes the shared death model; the enemy reset on death runs only when nobody is alive |
| **Shared seed** | Cosmetic randomness every machine rolls itself: the roll runs on a seed from the object's identity, stored with the save. | Sprite tints and flips, animation picks, vine rotations, parallax drift |
| **Leave it alone** | Dead code, or cosmetic randomness that really cannot be matched (see "Cosmetic divergence is a last resort"). | Hunger, time skip |

---

## 3. Who owns what

### Shared (one copy, the host is authoritative)

- The world itself: terrain, locations, objects, everything generated by the host.
- Items in the world: containers, ground items, death bags, traps, placed objects.
- Story flags and GameEvents (except the per-player flags listed below).
- NPC state: alive or dead, attacked, portraits, story reputation.
- The journal: notes, keys and journal items (sister's key, church box, eggs).
- The workbench level and the materials pile on it.
- Trader stock.
- Doors, barricades, windows, generators and their fuel, lamps, fires.
- The clock and weather.
- The map's discovered locations (map pins a player places are personal).
- Game settings that affect balance: difficulty, friendly fire, loot sharing.

### Personal (each player has their own)

- Bag, hotbar and everything in them.
- Health, darkness, status effects, burning.
- Skills, level, level-up dream slots, recipes, craft-limit counters.
- Home oven and respawn point.
- Night-trader and morning-trader standing.
- Personal map pins.
- Per-player flags: anything named `player_in*`, `player_at*` or
  `player_entering*`, help popups, the first oven talk, "survived the night".
- Hints, thought lines and one-shot "lessons" (recipes or journal pages taught by
  a hideout event): each player gets them once.
- The prologue.
- Camera, field of view, audio listener.

Each client's personal state is saved as a character snapshot on its own disk and
on the host, so a returning player gets their own character back, not the host's.

### Duplicated or scaled for the party

- **Oxygen tank:** every player gets one (see [section 10](#10-items-containers-trade-and-crafting)).
- **Hideout fuels:** taking one gives the taker a stack multiplied by party size
  ([section 18](#18-balancing-for-the-party)).
- **Black chompers in dreams:** one extra per extra player
  ([section 18](#18-balancing-for-the-party)).
- **Morning:** every hideout a living player greets the dawn in gets its own
  morning ([section 6](#6-night-morning-and-hideouts)).

Nothing else is duplicated. Story rewards, unique items and journal items are one
shared copy.

---

## 4. Sessions: hosting, joining, saving, leaving

### Transport

- **LAN:** LiteNetLib over UDP, default port 7788.
- **Steam:** a Steam lobby over SteamNetworkingSockets (friends, public or private;
  join from the invite overlay or by lobby id).
- Both carry the same messages. All peers must run the same mod build: the
  handshake carries the protocol number and refuses a mismatch.
- A GOG copy has no Steam, so a Steam host and a GOG client must use LAN.
- An optional `HostPassword` gates both transports. On Steam it is checked in the
  handshake (lobby data is public to anyone who can see the lobby), and a lobby
  member that never proves it is dropped after 20 seconds.
- Up to `MaxPlayers` players including the host (default 8).

### Hosting

The host starts or loads a campaign and enters a chapter. A client that connects
before the host is in the world waits ("WAIT HOST") and is served once the host is
ready.

### Joining

1. **Download:** the host zips its world save and sends it in chunks.
2. **Slot pick:** the client chooses one of its 5 profile slots for its copy of the
   host's world. Empty slots are safe; an occupied slot asks for an overwrite
   confirm. A slot that holds a different campaign is marked
   `[DIFFERENT CAMPAIGN]`; one identical to the host's is marked `[SAME AS HOST]`
   and the copy is skipped. The commit is staged and rolled back on failure.
3. **Enter world:** the client loads its copy offline, then reconnects as "already
   in world". The host checks that claim (campaign id and chapter id): a different
   campaign is refused as **WRONG SAVE**, the same campaign in another chapter gets a
   fresh share.
4. **Late-join catch-up:** the host sends everything a save file cannot carry: the
   night-death state, the journal, flags, reputation, the hideout, the workbench,
   map discoveries, the clock, traps, lights and generators, location pads,
   dropped items, the night scenario, weather, trader stock, barricades and doors,
   padlocks, death bags, fired story events, the dream session, other players'
   states, and the joiner's own stored character.
5. A player new to this world then plays their own prologue
   ([section 14](#14-prologue-tutorial-and-ending)).

Joining is refused while a dream is running (and from the start of a dream's entry
movie), unless the host sets `AllowJoinDuringDream`.

### Saving

- **Only the host saves the world.** F3 on the host, or any host save, makes every
  client save its copy at the same moment and send its character snapshot to the
  host. F3 on a client refuses ("only the host can save").
- A connected client never writes the world to disk on its own, because a
  client-side save could overwrite shared state with a partial picture.
- Loading a save is refused while in a session.
- No saves happen during a prologue, a dream, a held night death or a chapter
  change.
- A host that was promoted by host migration never auto-saves (its world is a
  client copy); it gets an F3 reminder instead.
- Each save of a co-op world also writes `savcos.dat` next to it: the seed of every cosmetic roll on
  a saved object ([section 16](#16-world-objects-doors-lights-fire-traps)). It is part
  of the world download.
- A client's own single-player saves are untouched except the slot it chose for the
  co-op copy. The second install on a dual-box setup uses its own save root.
- Each campaign carries an id. Character snapshots are tied to it, so a snapshot
  from another campaign is never applied (WRONG SAVE).

### Leaving and rejoining

- When a client leaves, the host removes its stand-in, releases anything it held
  (a dragged body, an NPC it was talking to, its night-death mark) and tells the
  others.
- A brief drop retries in the background (soft reconnect) and keeps the same
  player id.
- **Story items do not leave with a player.** If a client is not back within 60
  seconds, the host drops that player's quest items (car parts, violin, hat,
  musician's card) where they stood, or next to the host if they were inside a
  location or dream. When the player returns, a drop that is still lying there is
  taken back, and a drop someone else picked up is removed from the returner's bag.
  Oxygen tanks and journal items are not dropped (everyone has them already).
- A returning player gets their own character back from the snapshot the host
  stored (or their local copy, if that one is newer).

### Chapter change

The host runs the chapter transition. Clients go black, locked and unhurt, receive
the new chapter's world, confirm it, and load together. A client that never gets the
world shows an error and leaves cleanly (it rejoins by hand; see
[section 20](#20-known-gaps-and-parked-items)).

### Host migration

If the host crashes or times out (`HostMigrationEnabled`, on by default), the
survivors elect the lowest remaining player id as the new host and reconnect to it.
Every survivor computes the same answer from the player roster the host shared
while it was alive. A migration during a dream carries the dream over (done in
code, not yet playtested).

---

## 5. Time, pause and the clock

- **The host is the only clock.** Clients never advance time or fire day and night
  edges; they show the host's time.
- **The clock runs while anyone is in the open world.** Vanilla stops the clock
  while the player is inside a location (a village, a bunker). With a party, the
  clock stops only when nobody is in the open world. A player still loading or in
  their prologue does not count. Vanilla's own freezes (morning, death, scripted
  events) still stop it.
- **Day 1 waits for prologues.** On a fresh world the clock holds at 05:00 while
  anyone is still in their prologue, for at most 45 minutes.
- **Menus do not pause the world.** In single player the map, journal, padlocks,
  dialogue, skill menus, the level-up menu and interactive items pause the game. In
  co-op the world keeps running for everyone. To make up for it, a player in the
  level-up menu, a dialogue (or trading) or the pause menu is invulnerable and
  ignored by creatures until they close it.
- **The pause menu (Esc) pauses only when everyone is in it.** Opening it does not
  stop the world for the others; the player in it is protected the same way as in a
  dialogue. When every player in the session has it open, the whole world pauses on
  every machine. The first player to close it resumes. A player still joining or
  loading keeps the world running.
- **There is no sleeping or time skip.** The game has no such mechanic (the code for
  it is dead), so there is nothing to vote on.

---

## 6. Night, morning and hideouts

- **Homes are personal, buildings are shared.** Each player has their own home oven
  and respawn point. The hideout's walls, doors, barricades, generator and
  furniture are one shared world. An oven goes out only when nobody calls it home.
- **Whoever pushes or drags a thing moves it.** The pusher's own game moves it and
  everyone else sees the result. Other players' bodies pass through pushable things
  on your screen, so nothing gets pushed twice.
- **Night comes to every player.** Night monsters spawn around every living player
  who is at home, not only around the host. Players out in the forest get the worm,
  as in vanilla. Wards count for the player a monster is after. Night events play
  in every hideout a living player stands in, and a shadow wave is the curse of the
  one player it targets.
- **The host does not have to be home.** If the host is away, the night type is
  picked at a peer's hideout, and night events never land inside location pads.
- **Morning is per hideout and per player.** Every hideout a living player greets
  the dawn in gets its own morning: the screen effect, the trader, cleared night
  creatures. Each surviving player standing in a hideout at dawn gets their own
  reward (trader standing). A player who is not home at dawn, or who died that
  night, gets none (as in vanilla).
- **The morning holds until the hideout is empty.** One player walking out does not
  despawn the trader for the others. The trader restocks only when nobody is
  trading with it.
- **Generators:** fuel is host-owned. A client's switch or refuel is a request. The
  Electrician skill applies at the best rate any player has.
- **Village nights (co-op only).** Vanilla never needed this: friendly villagers go
  home between the "night is coming" warning and morning, but only while nobody
  sees them. Village houses give the shadow ward to a player standing inside.

---

## 7. Death, spectating and difficulty

### Dying by day

The player drops their bag where they died (a shared death bag anyone can loot, with
a map marker if it is inside a location), loses a life on Hard, and respawns at
their own home oven after vanilla's death sequence. The host clears enemies, traps
and infection from that home first. Vanilla's "respawn every enemy" step runs only
when nobody is left alive, so one player's death does not reset the others' fights.

### Dying at night

Vanilla answers a night death by skipping to the next day. That would end the
night for players who are still alive, so in co-op:

- The dead player becomes a spectator until morning: invulnerable, invisible,
  passing through walls, following a living player's camera and hearing what they
  hear. F4 cycles between living players. A dead player cannot release themselves
  early.
- Enemies, worms and shadows ignore night-dead players, and attacks from a dead
  player are rejected.
- The first death does not skip the day or save the game.
- **At dawn** every night-dead player is released and sent home. A death that
  arrives after the host's dawn counts as a day death.
- **If everyone dies,** the host resolves one shared morning (day skip and save),
  with no morning reward. If the last living player disconnects, the same happens
  instead of everyone spectating forever.

### Difficulty and permadeath

- The host's difficulty travels with the world. Clients play on it too.
- Nobody sees a solo game over in co-op. A death that vanilla would turn into
  permadeath (Nightmare, or the last life on Hard) becomes the shared death model
  instead: that player stays down, spectating, until the next morning, whatever the
  hour.
- **The run ends only on a party wipe:** everyone down at the same time and every one
  of those deaths permadeath-eligible. Then everyone sees the permadeath ending, and
  "start over" resets every player's character on every machine. If anyone who died
  still had a life left, it is a normal all-dead morning.

### Spectator mode (F4)

Any living player can press F4 to spectate the others and cycle between them;
wrapping around the list returns to their own body. F4 is blocked in menus, chat,
dialogue, dream entries, cutscenes and the title screen.

### Dying in a dream

No bag is dropped; the player keeps their kit and spectates until the dream ends.
See [section 13](#13-dreams).

---

## 8. Enemies and AI

### The host runs every creature

Clients never run enemy AI, spawners, teleports, despawn timers or summons. They
receive snapshots of every creature near them and draw it smoothly:

- Creatures within 800 units of a remote player are sent 20 times a second; others
  10 times. A client sees creatures within about 1400 units (with a little hysteresis
  so edge creatures do not flicker).
- The client draws each creature slightly behind the host's clock (by that
  creature's measured send interval plus measured network lateness) and
  interpolates between snapshots. Animations ride the same timeline, so a swing
  plays when the drawn body reaches it.
- Corpses: a creature first seen dead lies on its last frame; only a creature seen
  alive plays its death. Loot comes from the host when opened. A body the host
  destroys disappears for everyone.

### Who enemies chase

One arbiter picks the target of every creature, and every player body counts the
same; the host gets no preference.

- A creature takes the nearest player it senses (by vanilla's sight and range,
  applied to remote stand-ins too; smell does not count as sight for stand-ins).
- It keeps that target while the target is alive, visible and sensed within the last
  2 seconds. It switches only when the target is lost, or when another player is
  much closer (under 75% of the distance), and at most once every 2.5 seconds.
- Dead, invisible, ignored and menu-shielded players are not targets.
- A client hitting a creature draws it to that client.

### Defender decides

Enemy attacks on clients are judged on the client being attacked. The host sends
each attack (melee, ranged, thrown) to clients, and each client replays it on its
own copy of the enemy, where only that client's player can be hit. The client then
reports the hit so blood and sound play for everyone.

Why: when the host judged swings against a stand-in that trails the real client by a
round trip, dodges did not work for clients. Now a dodge on screen is a dodge.

Still decided by the host: area damage around a creature, diving fliers, turrets,
explosions, night shadows, traps and fire.

### Spawners

Every spawner runs on the host only: character spawn points, random objects, world
generation, bird areas, the porter, night monsters, trader stock rolls. A
replayed story event on a client skips its spawn steps, so a client never creates a
second copy of anything.

### Random encounters for every player

Vanilla events aimed at "the player" can come for any player who meets the
condition: the daytime redneck ambush picks a random living player out on the road,
the banshee screams at the nearest player who sees it, birds dive at whoever walks
in, the porter appears when any player is out of sight of his spot.

---

## 9. Player combat and friendly fire

### Client attacks

A client predicts its own hits (sound and blood play at once) and sends each hit to
the host, which checks it before applying damage:

- positions are finite and close to where the host sees the attacker;
- the target is within reach (200 units for melee, 1400 for ranged);
- the attacker is alive, not night-dead and not in a dream the host is not in;
- the target type is known;
- damage per hit is clamped to `MaxPeerDamage` (default 200);
- each player has a hit budget (20 hits per second with a burst of 40, 1000
  damage per second with a burst of 3000), so a shotgun's pellets all land but
  sustained spam does not.

A rejected hit is not passed on to anyone. Weapon status effects (bleeding,
poison, stun) travel with the hit and are applied the vanilla way.

### Friendly fire

`FriendlyFireEnabled` (host setting, on by default).

- **On:** players can hurt each other with any weapon, explosion, fire or trap.
- **Off:** other players' bullets, melee, explosions, fires and traps spare you.
  Your own fire and traps still hurt you, as in vanilla. Fires and traps remember
  who placed them; traps loaded from an old save have no owner and catch everyone.
  Enemies are caught by everyone's traps either way.

### Projectiles, explosions and fire

- Every machine flies its own copy of a thrown item. Only the host's copy deals
  damage and leaves gasoline. A thrown axe or spear keeps its wear and upgrades.
- A client's explosion is look and sound only; the host's deals damage to everyone,
  and burning or stun reach other players.
- Fire damage on doors, crates and creatures ticks on the host. Pouring gasoline and
  setting it alight are requests the host checks.

---

## 10. Items, containers, trade and crafting

### First come, first served

Items are unique. When two players race for the same thing, the host decides:

- **Containers:** both players can have the same container open. Each take is
  checked against the host's copy of the slot. The loser's take is refused and the
  item is returned exactly (durability, ammo, recipe and all). Containers are filled
  by the host and sent to a client when it opens them.
- **Ground items:** a pickup is a claim the host grants or refuses. Dropped items
  carry their throw velocity so they land in the same place everywhere.
- **Death bags** are ordinary shared loot and vanish for everyone once emptied.

### Journal and story items

- **Journal items are shared** (keys, notes, the church box, Piotrek's eggs): they go
  into everyone's journal and "show item" list. A peer that already has one does
  not pick up a second copy.
- **"Has item" requirements are a party check.** Vanilla asks whether "the player"
  holds an item. In co-op any player holding it counts, so a unique key cannot lock
  a three-player party out.
- **A shared story item can be handed over only once.** Chapter 1's big choices
  are who gets an item: the sister's key to the Wolf or the Musician, the egg to the
  Wolf or Piotrek. A client's talk asks the host before it goes on to the hand-in,
  and the host holds the item for the first player who asks. A second player at the
  other NPC hears "Someone already handed that over." and is sent back to the main
  options, so only one player gets the story outcome and the personal reward (the
  Wolf's pistol for the egg).
- **Quest items in a bag** (car parts, violin, hat, musician's card) belong to
  whoever carries them and are handed in by them. They stay in the world if their
  carrier leaves ([section 4](#4-sessions-hosting-joining-saving-leaving)).

### Oxygen tank

Chapter 2's water passages need the oxygen tank. With one tank, everyone else was
stuck at the water while its holder was away. Now every player gets one: the host
remembers the best tank anyone has held (empty or full) and each machine tops its
own bag up to it once. A tank someone drops or stashes later is not handed out
again.

### Trading

Trader stock is shared and owned by the host. A client's trade is sent as "what I
bought and what I sold"; if the host's stock no longer has the goods, the trade is
refused and reversed on the client. A trader restocks only when nobody is trading
with it. Standing with the night trader is personal.

### Crafting, upgrades and the workbench

- The **workbench level** is shared and only goes up. Two players upgrading at once
  both count.
- The **materials pile** on the workbench is shared. Crafting, repairing,
  upgrading, building and barricading take materials from it through the host, and a
  craft that finishes after a teammate took the ingredients is undone.
- The **product** of a craft or upgrade is personal: it goes to the crafter.
- Craft limits per item (`timesCraftedLimit`) are personal, so one player's crafts
  never lock another out.
- There is **no workbench lock**: several players can use the same bench at once.
  This is a product decision.

### Recipes and skills

Recipes, skills and levels are personal. A hideout "lesson" (a recipe or journal page
a scripted event teaches) is given to each player once.

---

## 11. Story: flags, events, examinables, chapters

### Flags

Story flags are shared and set by the host. Per-player flags (listed in
[section 3](#3-who-owns-what)) stay on each machine: syncing them made popups,
location-dependent dialogue options and entry spawns flip back and forth between
players.

### GameEvents

Darkwood's scripted world is built from GameEvents: chains of steps that open
doors, spawn creatures, move the camera, give items, teleport the player.

- **One-shot events run on the host only.** A client's local copy is blocked, and
  the host sends "this event fired" to everyone. A player joining later receives
  every event that already fired.
- **Steps aimed at "the player" apply to the player who set the event off,** not to
  the host and not to everyone: a teleport, a fall, a scripted hit, a thought line,
  a hint. Only a decompile-checked list of world steps is shared.
- **Steps that would double up are done once by the host:** counters, reputation,
  spawning creatures or items, moving the clock, scripted damage. Clients replaying
  the event skip them and receive the result.
- **Requirements check the triggering player** (their health, darkness, skill,
  attackers), except "has item", which is a party check.
- **Camera, black screens, input locks and slow motion** play for the player who
  triggered them and for players in the same location. An open-world scene plays
  for everyone.

### Examinables

A client examining something sees its text at once. The host re-runs the examine
silently to fire its story triggers and marks it examined for everyone. A pool of
random examine lines is one deck for the party: the line one player read is gone for
everyone until the pool refills, and a late joiner gets the decks as they stand. Two
players drawing from one pool in the same instant can both read the same line (see
[section 20](#20-known-gaps-and-parked-items)).

### Chapters

The chapter transition is a world event that only the host runs. See
[section 4](#4-sessions-hosting-joining-saving-leaving).

---

## 12. Dialogue

- **One speaker per NPC.** Only one player at a time can talk to an NPC (keyed by
  the NPC's place and world, since names repeat). The lock is released if the
  speaker disconnects. Dialogue options check the speaker's own bag.
- **Listening in.** Talking to an NPC someone else is already talking to opens a
  read-only copy of their dialogue window: the same portrait, the same lines typing
  out at the same speed, the same options and the option they are pointing at. Only
  the speaker chooses; Esc leaves. The trading screen, the cooking menu after a talk
  and journal pages opened by a talk are not shown to listeners.
- **Who gets what.** Items a dialogue gives are personal to the speaker. Story
  flags, world events and NPC reputation the dialogue changes are world outcomes:
  the host replays the client's finished conversation in order and applies them
  once for everyone. Items a dialogue takes (keys, quest items) are removed for
  everyone when they are shared journal items.
- **Two NPCs, one item:** see "handed over only once" in
  [section 10](#10-items-containers-trade-and-crafting).
- **Dialogue trips** (the Wolf's lift to the Doctor's house, and back) move the
  player who said yes, not the host.

---

## 13. Dreams

Dreams are separate scenes the game loads far outside the map. In co-op they are
party events.

- **Everyone alive goes.** A dream started for one player brings every connected,
  living player along. Dead players sit it out.
- **The host's world waits.** From the start of the entry movie until the dream is
  up, the host freezes its world (creatures included). New joins are refused for the
  duration unless the host allows them.
- **Level-up dreams.** The first player to reach level 2, 3, 5, 6 or 7 brings that
  level's dream to everyone present, and everyone who was in it has that level's
  dream done. A player who missed it (late, or dead) still gets an unplayed dream
  for that level later, or just levels up if none is left. A played dream is never
  repeated for the party. The bunker dream (level 2) is played once per world.
- **Dying in a dream:** you spectate until it ends. When every dreamer is dead, the
  dream ends as a death outcome.
- **Rewards:** dream outcome items are given to each dreamer. The outcome's world
  events reach every player, including one who died in the dream. A failed or
  abandoned dream gives nothing and is not marked done.
- **Same names, two worlds.** Dream scenes reuse object names from the overworld
  (the bunker door, for one). Every dream lookup is scoped to the dream pad so a
  player in the overworld is never affected.
- **Host migration during a dream** carries the dream over to the new host (done in
  code, not yet playtested).

---

## 14. Prologue, tutorial and ending

- **Everyone plays their own prologue.** The opening movie and the prologue are
  personal, as in single player. The host's prologue runs vanilla and stays
  connected. A player new to the world plays it after the world download, offline,
  then reconnects. A player who joins after the host skipped the prologue (or in a
  later chapter) starts fresh in the hideout with that chapter's starting pack. A
  returning player never replays it.
- **Nothing in a prologue reaches the others,** apart from journal pages it wrote,
  which go to the shared journal afterwards. No saves happen during a prologue.
- **Day 1 waits** for everyone's prologue (at most 45 minutes).
- **The ending.** The credits start when every player in the ending has finished
  reading, or after 2 minutes. Party dream openings and endings run their
  player-body steps (clothes, positions) for every dreamer.

---

## 15. Locations and travel

- Players can be anywhere on the map at once. Locations (villages, bunkers,
  basements) are loaded as pads at fixed spots that the host assigns, so every
  machine has the same pad in the same place, including late joiners.
- Each location runs for the players inside it. The host keeps every occupied pad
  running and frees a pad when the last player leaves.
- Cutscenes, slow motion and camera steps follow the scene's location, so a player
  elsewhere is not frozen by someone else's scene.
- One-shot moves and entry events are delivered to each player once.
- Rain keeps its schedule while a player is indoors.

---

## 16. World objects: doors, lights, fire, traps

- **Doors** open and close through the host. A barricaded or broken door cannot be
  opened. Door health travels with the door. Kicking a door alerts creatures the
  same way for every player.
- **Barricades and constructions** are shared world objects.
- **Lights:** lamps, generators, flares and held lights are synced; every machine
  runs a flare's own burn clock from its age, so flares go out together. A player's
  lantern, flashlight and torch light are visible on their stand-in.
- **Physics props** are sent 10 times a second from the host. Dragging a body is
  owned by the dragger's machine and replayed smoothly for everyone else.
- **Traps** are registered and saved by the host. Only the host springs traps on
  enemies.
- **Chains, shadow armor and burning world objects** each have their own host
  state that late joiners receive.
- **Cosmetic randomness looks the same everywhere.** Sprite tints, flips, rotations
  and heights (`SpriteRandomizer`), random animation clips, start frames and replay
  delays (`AnimationPlay`), parallax drift and vine rotations are rolled by every
  machine on a seed made from the object's identity, so the same object gets the
  same look on every machine. The seeds of saved objects are written next to the
  save (`savcos.dat`) and travel with the world download, so a loaded world looks
  exactly like the host's live one, and a reload looks like the session before it. A
  creature or prop that moved since it rolled carries the host's key to a machine
  that got it later (a creature with its sync data, a prop in the late-join catch-up).
  Each object still gets a random-looking roll; it is just the same one everywhere.
  This runs in co-op worlds only: a world started in a session, and from then on its
  slot (the `savcos.dat` file marks it), offline too. A single-player world rolls as
  vanilla. A world loaded before hosting rolled vanilla's way, which clients cannot copy,
  so clients get it only after the host loads the save again (HOST opens the load menu
  for that; the host is told if it skips it).

---

## 17. Seeing and hearing other players

- **Stand-ins.** Each remote player appears as a stand-in body that copies their
  position (about 30 updates a second), aim, torso animation, held light and
  status (burning, invisibility at 30% opacity).
- **Own camera.** Each player has their own camera and field of view, set up from
  their own skills.
- **Sounds.** Every sound has one owner, so nothing plays twice. A peer's sounds play
  in 3D on their stand-in (with indoor reverb and wall muffling) within the sound's
  own range, never less than 650 units. Creature sounds come from the host within
  their range of a player.
- **Text chat** (Ctrl+C): relayed by the host, which stamps the sender, so a
  name cannot be faked. Messages also appear as a speech bubble over the sender.
- **Voice chat** (needs a logged-in Steam client, works over LAN too):
  - Proximity voice: full volume within 150 units, silent beyond 650, muffled
    through walls.
  - Walkie-talkie: hold a `walkie_talkie` and right-click to transmit. Carrying one
    lets you hear the radio from anywhere, with static.
  - Push-to-talk (`V`) or open mic.
  - While spectating, you hear from the player you follow.

---

## 18. Balancing for the party

Darkwood is balanced for one player. The mod changes the balance in only these
places. All of them are host settings, sent to clients, so every machine applies the
same numbers.

| Setting | Default | What it does |
|---|---|---|
| `LootShareMode` / `DoubleItemsEnabled` | `ScaleWithPlayers` / on | Taking a hideout furnace fuel (odd mushrooms, odd meat, red eggs, embryos, dead rats, fish, insects, the large odd mushrooms, and any item vanilla flags as an experience item) gives the taker the stack multiplied by party size. The container keeps its stack, so each player can take their share. Not scaled: wood, nails, regular dog meat, unique items, items players put in containers, dropped items. |
| `NamedNpcScaleEnabled` / `NamedNpcAllowlist` | on / `ChomperBlack` | In dreams only, one extra black chomper per extra player, spawned around the original when it first appears and fighting with it. Night hideout monsters are not scaled. |
| `FriendlyFireEnabled` | on | See [section 9](#9-player-combat-and-friendly-fire). |
| `MaxPeerDamage` | 200 | Upper bound on one client-reported hit (anti-grief). |

Other party-relevant rules that are not settings:

- Creature health, damage and numbers are not scaled with party size (apart from
  the dream chompers).
- Players in the level-up menu, a dialogue or the pause menu are protected because
  the world no longer pauses for them (it pauses only when everyone is in the pause
  menu).
- Nightmare and Hard last lives end the run only on a full party wipe
  ([section 7](#7-death-spectating-and-difficulty)).
- The oxygen tank is given to everyone ([section 10](#10-items-containers-trade-and-crafting)).

---

## 19. What players cannot do

By design:

- **A client cannot save or load.** Only the host saves the shared world.
- **One player cannot pause the world.** The in-game menus, the pause menu included,
  pause it only when every player is in the pause menu
  ([section 5](#5-time-pause-and-the-clock)).
- **Nobody can sleep or skip time** (the game has no such mechanic).
- **A client cannot change balance settings.** Friendly fire, loot sharing and the
  party multiplier come from the host.
- **Two players cannot talk to the same NPC at once.** The second one listens in.
- **A shared story item cannot be handed over twice.**
- **A night-dead player cannot respawn before morning,** and F4 cannot release them.
- **Peers cannot mix mod versions.** The protocol must match.
- **Joining during a dream** is refused unless the host allows it.
- **A Steam host cannot be joined from a GOG copy** over Steam; use LAN.
- **A client cannot join a host that is still on the title screen.**

---

## 20. Known gaps and parked items

Explicitly parked or left as is:

- A client whose chapter load failed has to rejoin by hand.
- Two players drawing a line from the same examine pool in the same instant can both
  read it (the decks agree again right after). Ruling it out would make every
  client's examine text wait for a round trip to the host, which breaks "the client
  must feel like the host".
- A dream waiting to start (a level-up or dialogue dream the host could not take at
  once) waits while the host has the pause menu open.
- World sounds keep playing behind the pause menu until the whole world pauses, the
  same as behind the map or the journal.
- A host that leaves is not covered by the quest-item drop (the session ends with it,
  or the host role moves).
- The listen-in dialogue view does not show trading, the cooking menu or journal
  pages.
- No exclusive lock on containers or the workbench: two players may have the same
  one open, and the host settles races.
- Presentation-only edge cases in spectator mode, dialogue overlays and lost
  dream-chain packets (not world-state gaps).

Runtime verification still pending:

- A full dual-box and three-player campaign soak. Every domain is code covered;
  the recent releases are built and unit-tested but not playtested. See
  [COOP_COVERAGE.md](COOP_COVERAGE.md) for the per-domain
  status.

---

## 21. Rules for contributors

The full rules are in [ARCHITECTURE.md](ARCHITECTURE.md). The
short version:

- **Read the decompile first.** Copy vanilla behaviour wherever one player is
  involved; confirm a mechanic is alive before handling it.
- **Host decides, client shows.** A new world mechanic gets a host-side
  authority path, a client-side suppression of the vanilla code, and an apply path
  for the result.
- **Reverse-check both arrows.** A fix is not done until it has been traced with the
  host and the client swapped, with a third player, and with a late joiner.
- **Late joiners.** Any state a save file does not carry needs a late-join step.
- **Wire changes bump the protocol.** A new message id goes after the highest one;
  every message reads exactly what it writes; each type has exactly one handler;
  host-only and forwardable types are marked as such.
- **Session state resets.** Every static either registers a reset or is marked
  process-scoped; per-peer state lives in the session objects.
- **Harmony patches** no-op outside a session unless they are pure vanilla parity,
  and follow the Prefix/Postfix/Finalizer rules the tests check.
- **No crutches** ([section 1](#never-crutch)).
- **Fix bugs when found,** and write the CHANGELOG entry in the same change.
- **"Works" means a dual-box playtest** with both logs read, not a green build.
- **Keep this document current** when a rule in it changes.
