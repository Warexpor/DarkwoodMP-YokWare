# Changelog

## Versioning

The current product line is `0.8.x`. The plugin and display version are
**0.8.210**. The current Horde wire protocol is **53**. Every install in a session
needs the same DLL; the protocol is checked on join.

Protocol history (newest first):

- **53**, 0.8.200: the world share package gains `savplc.dat` (container slots a player
  stored items in). No message layout changed; the bump keeps every player on a build
  that reads and writes the file.
- **52**, 0.8.189 to 0.8.199: `PlayerState`'s walkie trailer becomes a state byte,
  `WalkieState`: none, off or flat, on in a pocket, on in hand, plus underground.
- **51**, 0.8.187 to 0.8.188: `VoiceData` (129) carries the mod's own voice codec
  instead of Steam Voice.
- **50**, 0.8.185 to 0.8.186: `PlayerState` gains the `CarriesWalkie` trailer.
- **49**, 0.8.184: `VoiceData` (129) gains the talker's loudness byte.
- **48**, 0.8.182 to 0.8.183: new `PlayerName` (168), `PeerRoster` entries gain the
  player's name.
- **47**, 0.8.177 to 0.8.181: `PlayerState` gains the `ClockHeld` trailer.
- **46**, 0.8.176: `NightDeathState` gains the rejoin resume and its position,
  `PlayerDied` gains the dying player's home, the entity snapshot's second flag byte
  gains the in-sight bit.
- **45**, 0.8.170 to 0.8.175: `Handshake` gains `WorldGeneratedLocally`.
- **44**, 0.8.165 to 0.8.169: `ScenarioEventFired` gains the scenario name;
  `GameEventsFired` gains the scene-piece flag in 0.8.167.
- **43**, 0.8.162 to 0.8.164: new `MapPinRequest` (166) and `MapPinEvent` (167),
  `MapMarker` (68) and `MapMarkerRemove` (72) retired, `MapStateSync` carries the
  party map board.
- **42**, 0.8.142 to 0.8.161: `WorldClock` (165) removed with the 0.8.141 rollback.
- **41**, 0.8.141: new `WorldClock` (165).
- **40**, 0.8.140: new `CosmeticState` (164), `ExamineObject` gains the drawn pool
  line, the entity descriptor gains the look key.
- **39**, 0.8.139: new `DialogHandInClaim` (161), `PauseMenuState` (162) and
  `WorldPause` (163).
- **38**, 0.8.138: new `DialogMirror` (160).
- **37**, 0.8.137: new `DialogHandInGone` (159).
- **36**, 0.8.136: new `QuestHandoff` (158).
- **35**, 0.8.135: new `OxygenTankTier` (157).
- **34**, 0.8.134: `OxygenTankStash` (70) and `CompressorTankConvert` (71) retired.
- **33**, 0.8.133: `ItemSpawn` gains `PlacerId`, `PlayerScare` gains `ScaryFace` and
  `CasterId`, `WorldSaveBegin` gains `Difficulty`, `DroppedItemSpawn` gains the drop
  velocity, `PlayerEffectSync` gains a burning byte, health, darkness, skills, the
  home oven and the in-ending flag, `PlayerBurning` the curse flag, `DeathBagSpawn`
  the location marker, `ThrowableSpawn` the recoverable weapon and the flare age
  (throw id and remaining life removed), `ShadowEvent` its end and owner, `TimeSync`
  the overworld time, `CutsceneSync` action 6 (dream entry cancelled),
  `MapElementDiscovered` the pin position, `ChapterTransition` `StartOver`,
  `DragSync` its sample time and end pose; new `PorterTransport` (150),
  `PlayerSpecial` (151), `TradeCommit` (152) and the desync check's `DesyncDigest`,
  `DesyncDetailRequest`, `DesyncDetail` and `DesyncReport` (153-156);
  `ThrowableDespawn` (125) retired.
- **32**, 0.8.132.

This file is a public ship log. Code-only status and runtime status are called
out separately. A runtime item is not considered verified until it has been
tested in the game.

---

## 0.8.210 — Radio icon redrawn

- The radio's inventory icon was a detailed upright painting shrunk to icon size: thin
  lines, an antenna taking half the frame, a different angle from every item next to it.
  It is redrawn for the size it is shown at: a boxy handset lying at an angle like the
  game's own items, filling its frame, big plain shapes (speaker bars, label plate, short
  antenna), grey only.
- The art is 240 px (eight art pixels per icon pixel) and is averaged down to 30 px, the
  size of vanilla icons (`IconPixels` 48 -> 30).
- Files: `Resources/walkie_talkie.png`, `Items/WalkieItem.cs`.
- Built but **not deployed** and not seen in game. The game folders still hold 0.8.208.

## 0.8.209 — Answer buttons of a question box spaced, MULTIPLAYER there when the pause menu opens

- **Question box:** in "Profile N belongs to a different co-op campaign..." the answers
  read "OverwriteCancel", with no room between them. The vanilla box writes each answer
  from its left edge at the places meant for "Yes" and "No", so a longer first word ran
  into the second. The two answers are now laid out after their text is set: centred
  where the vanilla pair is, with at least the vanilla room (40 units or more) between
  them. Applies to every yes/no box of the mod's menu (`VmScreen.Confirm`).
- **Pause menu:** on the first open in a game the rows moved down at once but the
  MULTIPLAYER button was made up to 15 frames later (the menu's UI pass runs every 15
  frames), so it popped in. Opening the menu now makes the next update run that pass at
  once.
- Files: `UI/VanillaMenu/VanillaMenu.cs`, `UI/MainMenuMultiplayerInject.Panel.cs`.
- Built but **not deployed** (a session was running) and not playtested. The DLLs in the
  three game folders are still 0.8.208.

## 0.8.208 — MULTIPLAYER button keeps its size after a resolution change

- **What broke:** after a big resolution change in Options, MULTIPLAYER on the title and
  pause menus stayed at its old size next to PLAY / OPTIONS / EXIT (too big after going
  down, too small after going up) until the game was restarted. Only resolutions above
  1920x1080 were involved: below that the vanilla rows do not change size.
- **Cause:** the vanilla rows carry `ScaleToResolution`. The button's art measured one
  texel of EXIT once, when it was attached, and kept that size. The button itself (a
  clone of EXIT) kept its own copy of `ScaleToResolution`, which took the already scaled
  size as its base and scaled it again, so its hitbox was also off above 1080p.
- **Now:** the clone has no `ScaleToResolution`; it takes EXIT's scale on every menu
  poll, and the art measures the texel again each frame, refitting the hitbox when it
  changes.
- Files: `UI/MenuButtonArt.cs`, `UI/MainMenuMultiplayerInject.cs`,
  `UI/MainMenuMultiplayerInject.Panel.cs`.
- Not playtested yet.

## 0.8.207 — Radio icon less coarse

- **The walkie's icon is drawn from 48 px instead of 30.** 30 px is the size of the
  game's own icons (measured: flashlight 29x30 in the inventory atlas), but those are
  drawn to fill their frame, while the radio with its antenna fills about half of it, so
  at 30 px the body was left with some 16 pixels and turned to mush. 48 px keeps the hard
  pixel edges and leaves the radio readable. File: `WalkieItem.cs`. Not seen in game.

## 0.8.206 — Radio: recorded static and knob, twice the range, your own talk from a radio near you

- **Radio range doubled.** Clear to 6000 units (was 3000), gone at 18000 (was 9000); the
  map is about 25000 across. Buildings and underground weaken it as before.
- **Recorded static under a transmission.** A clear signal carries a handheld's own
  steady static ("Walkie Talkie radio static" by SEF7); as the signal weakens, an
  out-of-range static ("Radio static" by LukaCafuka) rises over it, and it is what a
  dropout sounds like. Both replace the generated hiss, which stays as the fallback.
  They are mixed into the voice before the listener's wall muffle and room reverb, so
  they sound from the radio like the voice does.
- **Recorded knob.** Switching the radio on or off (and a flat battery's click) plays one
  of four takes cut from "Press radio knob.wav" by greatsoundstube, never the same one
  twice running. The generated speaker hiss after the click is gone.
- **Other players hear your knob.** A player near someone who switches their radio on or
  off hears the click from them (about 150 units), muffled through a wall and with the
  room's reverb. Read from the radio state already in `PlayerState`; no new message.
- **Your own transmission comes out of a radio near you.** Keying your walkie with another
  player's live radio in earshot (260 units in hand, less in a pocket), you hear it play
  you: your voice through the radio's band and static, its opening click, its roger beep
  and squelch tail when you let go, from where that player stands, at the level any
  nearby radio plays at (quieter than your own, falling off with distance, muffled
  through a wall and duller from a pocket). Nothing extra is sent: the frame just
  transmitted goes through the same radio path locally, with a 70 ms buffer instead of
  the network's 150 ms. Closer than 90 units the feedback howl still applies.
- **Occlusion and reverb, checked and completed.** Already in place: a voice or a nearby
  radio is muffled through walls and gets the room's reverb; a radio in a backpack or on
  the hotbar plays to its carrier and to players around them, quieter and duller than in
  hand. Missing and added: the talker's key beep and squelch tail heard by players near
  them now lose volume through a wall (they were only dulled) and get the reverb when the
  talker stands inside; this player's own radio sounds (key, release, knob, low battery)
  get the reverb when this player stands inside.
- Still generated: the low-battery chirp, and the opening click and roger beep a far
  radio's speaker plays.
- Files: `VoiceChatService.NearRadios.cs` (new), `VoiceChatService.Radio.cs`,
  `VoiceChatService.Speakers.cs`, `VoiceChatService.Walkie.cs`, `VoiceChatService.cs`,
  `RadioSamples.cs`, `Resources/Radio/*.wav`.
- Not playtested. Nothing here has been heard: every level (static, knob, own voice from
  a near radio) is set by measurement of the files, not by ear.

## 0.8.205 — Recorded call beep and squelch tail on the radio's talk key

- **Pressing the radio's talk key plays a real handheld's call beep** in place of the
  generated click: for the talker, and for players standing near the talker. It is a CC0
  recording ("Walkie Talkie famous beep" by SEF7, Freesound), trimmed to 0.59 s, mono,
  embedded in the DLL. If the recording cannot be read, the old generated click plays.
- **Letting the talk key go plays a recorded squelch tail** in place of the generated
  hiss, for the same listeners ("Radio Sign Off / Squelch" by JovianSounds, Freesound,
  CC0; the file is 1.2 s with 0.2 s of sound, trimmed to 0.25 s).
- New `RadioSamples` loads embedded 16-bit WAV recordings; sources and licences are
  listed in `DarkwoodMP.Mod/Resources/Radio/SOURCES.md`. The other radio sounds (power
  on and off, low battery, flat battery, and what the far radio's speaker plays as a
  transmission opens and closes) are still generated; recordings for
  them are being chosen.
- Files: `RadioSamples.cs`, `VoiceChatService.Radio.cs`, `Resources/Radio/key.wav`, `release.wav`.
- Not playtested; the level against voice has not been heard.

## 0.8.204 — Voice a little louder

- **Received voice is about 2 dB louder at the same setting.** The built-in level under
  `VoiceGain` goes from 1.6 to 2.0; the setting and its default (1.4) keep their meaning,
  and loud peaks are still soft-limited instead of clipped. Applies to plain voice and to
  the radio alike. File: `VoiceChatService.cs`. Not playtested.

## 0.8.203 — F4 line when spectating begins

- **Spectating had no hint about F4.** Nothing told a spectator that F4 moves between players.
  When spectating begins (F4, a night death, a death in a dream) one line in the hover-label
  font shows at the bottom of the screen: "F4 - next player, then back to yourself" for a
  living player, "F4 - next player" for a dead one. It fades in, stays about five seconds and
  fades away. A dead player with only one other player to watch gets no line, as F4 has
  nowhere to go.
- No protocol change. Files: `UI/SpectatorModeController.Prompt.cs` (new), `Core/Loc.Ru.cs`.
  The Russian lines and the doc passages for this went into the 0.8.202 commit, which another
  session made in the same checkout.
- Code only, not playtested yet.

---

## 0.8.202 — Host address typed in for LAN, Steam's own windows for inviting and joining

- **A LAN host can type the address the others reach it at.** The Host screen has an
  Address field and the port, with "Your address: ip:port" under them. Left empty, the
  address is found from the network cards as before; a VPN adapter or a forwarded public
  address has to be typed, since nothing on the computer tells which one the others use.
  The typed address is what the Multiplayer screen shows and what the other players are
  handed for reconnects and host hand-over. The host still listens on every address, so a
  join over 127.0.0.1 or the plain LAN address keeps working. New config key `HostAddress`.
  Files: `ModConfig.cs`, `MultiplayerScreens.cs`, `HostMigration.PeerRoster.cs`.
- **Hosting over Steam opens Steam's invite window by itself**, as soon as the lobby
  exists. Before, the host had to find "Invite friends" in the Multiplayer screen after
  hosting. Files: `LanNetworkManager.Steam.cs`, `MultiplayerScreens.cs`.
- **Join > Steam friends > Open Steam friends list** brings up Steam's own friends list
  over the game; "Join Game" on a friend there, or an invite in a chat, joins the same
  way a row of the in-game list does. Nobody needs the lobby id; the field for it stays
  at the bottom for the invite-only case.
- **Steam overlay switched off:** both buttons say so instead of doing nothing.
- Host screen order is now Steam, then Local network with its address and port.
- Not playtested: the Host and Steam friends screens have not been seen on screen, and
  the overlay calls need two Steam accounts to try.

## 0.8.201 — Map board in the game's own UI, no stamp under the cursor, sharper names

- **No stamp follows the cursor on the map.** The chosen stamp used to hang under the cursor
  the whole time the map was open. It is hidden now. The first wheel notch brings it up (its
  name shows at the bottom of the map), further notches change it, and it fades 2.5 seconds
  after the last one. Right click still places the chosen stamp at any time.
- **Map board texts are the game's own UI.** The grey IMGUI boxes are gone. A hovered pin is
  described by the map's location popup (stamp as the title, then the label, who placed it and
  the day). Pin labels, the label being written (typed straight under the pin, with a caret)
  and one dim line of controls at the bottom of the map are in the hover-label font. The
  controls line follows what the cursor is on: placing, a hovered pin, or writing a label.
- **Names over players looked smeared.** The hover-label font is an 11 px bitmap; the names
  drew it at 1.5x and at fractions of a pixel. They are at the hover label's own 2x now, on
  whole pixels.
- **"Host" after the host's name** was not added by the mod: the host install's config had
  `PlayerName = Warexpor Host`. Set to `Warexpor` (local config, not in the repo). A client
  that set a voice volume for the old name sets it again.
- No protocol change.
- Files: `Domains/Map/Pins/MapPinOverlay.cs` (rewritten, no `OnGUI`), `MapPinView.cs`
  (`ShowPopup`, wheel reveal), `UI/Nameplates.cs`, `Core/Loc.Ru.cs`.
- Code only, not playtested yet.
- Dreams, checked and left as they are (confirmed by the user): a dream's story end wakes the
  whole party at once, so nobody stays behind; a player who dies in a dream spectates until it
  ends.

---

## 0.8.200 — Stored stacks stay stored across reloads and joins (protocol 53)

- **The loot-bonus marks are saved with the world.** 0.8.199 marked every container slot a
  player put items into, in memory. After a reload, or for a player who joined later, a stored
  stack counted as world loot again and gave the party bonus when taken back. The marks now go
  into `savplc.dat` next to the save: written on every save, read at the start of every load,
  removed with the slot, and sent with the world (written fresh right before a share, so stacks
  stored since the last save are in it). A slot is keyed by the save id of the container's
  nearest saved object, the names below it and the slot number, the same key the cosmetic rolls
  use, so it is the same on every machine that loaded this world. A container with no saved
  object above it keeps the in-memory mark for the run.
- **Protocol 53.** No message changed; the world package has one more optional file. Every
  player needs this build.
- Files: `Domains/Inventory/PlacedLootStore.cs` (new), `ItemDoublePickupPatch.cs`,
  `CosmeticRolls.Store.cs` (`TryObjectKey`), `CosmeticRollPatches.cs` (load / save / new world /
  delete hooks), `WorldSaveShareService.cs`, `.HostShare.cs`, `.ClientApply.cs`.
- Limits: a stack stored before this build is not marked (nothing recorded it). Marks are kept
  for the slot, not the stack, so world loot left in a slot a player also used gives no bonus.
- Not yet playtested: build and unit tests only.

## 0.8.199 — Session notes: loot-bonus dupe, listening in on a talk, Steam friends list, radio and voice tuning

From the host's notes on the same session. No wire change (protocol 52). Build and unit tests
only; nothing here is playtested yet.

- **Loot-bonus dupe through a container.** Mushrooms (and every other bonus item) put into the
  workbench and taken back came out with the party bonus again, each time (host log: took 4 and
  got +4, put 2 back, took 6 and got +6, twice). Only a cursor drop into a plain container marked
  the slot as "a player put this here"; a quick transfer, a craft output or any non-`itemInv`
  container did not, and the mark was keyed by position, so a pushed wardrobe lost it. Every put
  is now marked where it is sent (`SendContainerAction`) and where it is received, by container
  object and slot. Files: `ItemDoublePickupPatch.cs`, `ContainerSyncPatches.cs`,
  `ContainerLootNetHandlers.cs`.
  - The marks lived in memory in this version; 0.8.200 saves them with the world.
- **Listening in on another player's talk.**
  - **Esc did nothing for the listener** while the talking player moved between screens that
    change the portrait (the bunker door: look, listen, handle). Vanilla drops Esc while the top
    fade screen is up, and the listener's view raises it for each portrait change. The listener's
    Esc is now read directly in that state.
  - **The talk handed over while the listener was in its own trade** (the talking player left)
    kept the other player's last screen under the new owner's options once the trade closed: old
    text, an item list, a talk still marked as running. It is cleared at the hand-over.
  - **The talk ending while the listener was in its own trade** closed the window with the
    trading screen still up; the trade is closed first now.
  - A portrait clip that finishes loading after the view closed no longer cuts the close fade.
  - Files: `DialogMirror.cs`, `LanNetworkManager.Tick.cs`.
- **A joining player's stand-up on the other screens** started when its first packet arrived,
  from the first frame, so it ran behind and was cut when the player, already up, walked off.
  The stand-in now starts the clip at the frame the player is on and plays on from there, ending
  with it. This partly reverses 0.8.160 (which played it from frame 0 because following every
  packet's frame cut it in half): the start of the stand-up is not shown when the player was
  already part-way up. `SecondPlayerAnimController.Apply.cs`.
- **Multiplayer menu.**
  - **Join > Steam friends** (new screen): friends who are in a lobby of this game right now, one
    click to join. The lobby id field moved there. Steam only reports friends-only and public
    lobbies, so a private lobby still needs an invite or the id.
  - **Invite friends** is also on the host's title screen (it was pause menu only).
  - A LAN host's Multiplayer screen shows **Your address: ip:port**.
  - Files: `MultiplayerScreens.cs`, `SteamCoopTransport.cs` (`FindFriendLobbies`), `Loc.Ru.cs`.
- **Radio.** A battery lasts 60 minutes on standby (was 25) and 20 minutes of talking on top (was
  8). The inventory icon is brought down to 30 px with point filtering, the density of the game's
  own icons (it was a 256 px image with smooth filtering). `VoiceChatService.Walkie.cs`,
  `WalkieItem.cs`.
- **Voice is louder.** Received voice gets a built-in 1.6x under the `VoiceGain` setting (existing
  config files keep their value and still get it), and peaks are soft-limited instead of clipped.
  `VoiceChatService.cs`.
- **Looked at, not changed:**
  - **Aggressive barking with no dog in sight.** The dog's chase and guard sounds are looping 3D
    sounds with vanilla's own carry (1000 and 1500 units; the screen is about 960 x 540 units
    from the player), and the mod plays them through vanilla's sources. In co-op a dog barks at
    the other player too, so it is heard from off screen. Nothing in the logs shows a stuck loop
    or a wrong source. Whether to cut a creature's voice short for a player it is not reacting
    to is a rules decision, not made here.
  - **The Wolfman still in the hideout after the morning ended.** Vanilla: on his first visit he
    is not the morning trader (`Location.spawnWolf`, not `spawnTrader`), `endAfterNight` removes
    only the trader, and he goes when the hideout unloads (the player is a screen or two away).
    The logs show exactly that (despawned once the host was far enough).
  - **New radio sounds** (recorded, not generated): not done; needs sound files with a licence
    that fits a public GPLv3 repo.

## 0.8.198 — First real session (Steam, English host + Russian client): ghost traps, furniture left behind, translated names on the wire

Found in the logs of a 40-minute Steam session with a friend. No wire change (protocol 52), but the
identifiers below changed meaning, so every player needs this DLL.

- **A trap one player disarmed far from another stayed armed for that other player.** The client
  later walked into a bear trap the host had removed half an hour earlier and was caught by it
  (`[Trap] host: player 3 trapped` on a trap the host no longer had). The game switches off the
  world away from the local player, a switched-off trap has no collider, and the removal looked
  for traps by collider only (the scene scan was skipped for trap names). Removal now also reads
  the trigger registry, which holds switched-off traps. The host's trap ledger is told about a
  trap destroyed while it never woke on the host (Unity sends no `OnDestroy` there), so a later
  joiner does not get it back. Files: `WorldPhysicsSyncService.Pickups.cs` (`DestroyObjectByPos`),
  `TrapLedger.cs`.
- **Furniture pushed while another player was far away stayed put on that player's machine.** The
  host's wardrobe ended 85 units from the client's copy; the client then could not open it
  (`no activate target near … Wardrobe_Big_1_Burned`, `HandleContainerStateRequest: no inventory`)
  and its own pushes were refused (`ObjectApply failed=1`). The client dropped every free-body
  state outside its interest range. It now sets the pose of its (switched-off) copy directly, with
  no resolve cascade, interpolation or sound. The interpolation tick also moves a switched-off
  body by its transform (a rigidbody outside the physics scene ignores `rb.position`). Files:
  `WorldPhysicsSyncService.Apply.cs`, `.ObjectResolve.cs` (`SnapFarObject`), `.Interpolation.cs`.
  A copy that is already more than 50 units off (an old save from before this fix) is not pulled
  back by this; it heals on the next world download.
- **Translated names were used as network identifiers.** `Item.name`, `Door.name` and
  `Window.name` are vanilla display names in the player's own language, not object names. With an
  English host and a Russian client:
  - picking up a sprung bear trap asked the host to remove "Металлолом", which matched nothing
    (the trap check looked for the English word "scrap");
  - the desync check reported every door twice, forever (`Wooden door@…` host only,
    `Деревянная Дверь@…` client only), and would have done the same for dropped items;
  - lamp switch states, the locked-door attempt, door open / unblock fallbacks and dream prop
    colliders matched by name only when both players used the same language.
  All of these now send and compare the object name. Files: `DroppedItemSyncHelpers.cs`,
  `DesyncCheck.Sections.cs`, `DoorSyncPatches.cs`, `DreamDoorSyncPatches.cs`, `DoorNetHandlers.cs`,
  `WorldLateJoinNetHandlers.cs`, `WorldPhysicsSyncService.Lights.cs`, `.Interpolation.cs`.
- Not yet confirmed in a playtest: build and unit tests only.

## 0.8.197 — A player down at night or in a dream leaves a body; ghost Wolfman after loading

- **Dying at night or in a dream now leaves the body where the player fell**, on every machine,
  the dead player's own included. Before, the body vanished: the dead player's own body moves under
  the player they spectate, and their stand-in on the other machines was parked under the ground.
  The body is a copy of the player lying on the last frame of the death clip. It has no collider,
  so players and creatures walk over it.
  - **Night death:** the body is also the death bag. Vanilla still drops the bag on the spot; while
    the body lies there the bag's sprite is hidden under it, so pointing at the body loots the bag
    as usual (shared, synced, map marker unchanged). The body goes at the morning release,
    and anything left in the bag shows as a bag again.
  - **Dream death:** the body stays until the dream ends. It drops no bag (the player keeps their
    kit, as before), so it is only a body.
  - **Day death is unchanged:** bag, then vanilla's respawn at home, no body. A one-life death by
    day (Nightmare, or the last life on Hard), which is also held until morning, counts as a day
    death here.
  - Files: `Domains/Players/Runtime/PlayerCorpses.cs` (new; ticked from `LanNetworkManager.Tick`),
    `DeathStateTracker` (records whether a death fell in the night window), and
    `FinalDreamsceneManager` (where the local player fell in the dream). No wire change; every
    machine builds the bodies from the death state it already has. New pilot command `corpses`;
    pilot `loot` also takes death bags. Verified in a 3-player pilot run:
    - Night death: the body shows on all three machines, on the last death-clip frame, with no
      solid collider and the bag hidden under it.
    - A client looted the body, then the host did. Each time the bag left every machine and the
      body stayed.
    - Morning removes the body.
    - A day death drops a plain bag and leaves no body.
    - Dream death: the body shows on all three until the dream ends, with no bag.
- **A client could see and talk to a Wolfman at the hideout that the host did not have.** A
  save written while he visited holds him, and every machine loads him from it. The host's
  morning on that load sends him away before any client joins, so he is never streamed and the
  host's despawn never reaches the clients (a client may not despawn him itself). A client now
  drops a hideout Wolfman the host has never driven while the host's `wolf_inPlayerHideout` flag
  says he is not there (`WolfVisitorGhostSweep`). Found in a save and rejoin pilot run
  (desync check: `Creatures local:Wolfman_att: host=<none>`).
- **The host kept an empty death bag after a client emptied it.** Every client dropped its copy
  at the host's "bag looted" message, but the host left its own copy standing. Only the host saw that
  empty bag, and it went into the host's save. The host now drops its copy too, unless it has that
  bag open itself; that one still goes when the window closes, as before (`TryHostFanDeathBagEmptied`).
  The host's own take that empties a bag now ends it for everyone at once, as a client's take
  does. Before, the clients' copies stood empty until the host closed the window
  (`ContainerSyncPatches.SendContainerAction`).
- Pilot softlock check (3 players): a client died at night inside a location, waited for the
  morning and walked out. The host and a client each went into a location and out again after the
  night deaths. A clock jump out of the night released the dead client. A save at night, then quit
  and rejoin. Every step passed.

## 0.8.196 — Pilot-run fixes: stuck pad exit, night-dead past dawn, wreck physics, oven portrait callback, join weather, client rain, chapter teardown

Found by an unattended three-player story crawl (host + two clients, every chapter 1 location).

- **Leaving a cellar or other pad by day could leave the player stuck in it.** Vanilla walks out
  of a pad with a Save and finishes the walk-out (clears `returningToWorld`, gives inputs back,
  lifts the black screen) only when that Save reports done (`SaveManager.onSaved`). Co-op skips
  that Save for a connected client (the host owns the world save) and for a night-dead player,
  and a skipped Save never reported done. A client waited for the host's next coordinated save
  to finish its walk-out; a night-dead host never sends one, so the host and its clients kept
  `returningToWorld` set and every later exit was ignored. A Save a co-op rule skips now reports
  done the way vanilla's own `dontSave` skip does (`SkippedSaveAnswersOnSavedPatch` in
  `Patches/SaveSyncPatches.cs`).
- **A player who died at night stayed dead (spectating) for a whole extra day** when the clock
  left the night by a jump instead of passing midnight: entering the doctor's house sets the time
  to 100 (a vanilla `tweenTime` GameEvent), so vanilla's `startDay` never ran and the morning
  release with it. The host now treats a jump out of the night (not a dream's own clock) as the
  morning for the night-dead (`DeathStateTracker.HostCheckClockLeftNight`, run from the host
  clock step in `ClientTimeAuthorityPatches.cs`).
- **The host threw a NullReferenceException (or changed the wrong NPC) when a client used an
  oven while the host talked to someone.** A changePortrait board (oven lookAt*, lookKeyhole)
  swaps the portrait and steps the board in a 1.5 s delayed callback that reads the dialogue
  window's NPC when it runs. On the host replaying the client's board, the window was closed by
  then (NPC null: the exception) or open on the host's own conversation, which got the oven's
  portrait and a board step. Scheduled during a host replay, the callback now runs only while
  that replay still holds the window on the same NPC; otherwise it only sets the portrait type
  on the NPC the board belonged to (`DialogHostPortraitInvokePatch`).
- **A client turned the tank wreck (and any body the game keeps kinematic) into a free physics
  body.** Network smoothing froze a body while it followed the host and then let go by clearing
  `isKinematic`, also on bodies that were kinematic by design. PhysX rejected the wreck's
  non-convex collider on every release (`Non-convex MeshCollider with non-kinematic Rigidbody`,
  `Dynamic actor with illegal collision shapes`). Sync now lets go only of bodies it froze itself
  (`LockKinematic` / `ReleaseKinematic` in `WorldPhysicsSyncService.cs`).
- Desync check: a trader switched off far away (culled, or hidden by the story) is no longer
  compared. A client keeps the host's stock for it in the trade queue until it is out, so its
  copy was always the old roll and every morning restock showed up as a trader "desync"
  (`CollectTraders` in `DesyncCheck.Sections.cs`).
- **A client joining while it was raining logged a NullReferenceException** (`Rain.startRain`).
  The host sends weather at handshake, while the joiner still sits on the title screen with no
  player; vanilla `startRain` reads `Player.Instance.whereAmI`. A client now ignores weather until
  it has a player in the world; the late-join bulk sends it again once it does
  (`WorldWeatherTimeNetHandlers.HandleWeatherSync`).
- **Clients logged ~30 NullReferenceExceptions in `Character.OnDestroy` on every chapter change.**
  The chapter scene load tears the old scene down in no fixed order; once the FX/UI SpawnPools are
  gone, `Core.RemovePooledPrefab` indexes a missing pool (null) for each character still carrying
  a message or effect, and the rest of that character's cleanup is skipped. With a pool gone the
  object is destroyed instead, as vanilla does with an object no pool owns
  (`Patches/PooledPrefabTeardownPatch.cs`). Each banshee also threw there: vanilla
  `Character.OnDestroy` runs `onBansheeOutOfSightOfPlayer`, which parents a new overlay under a
  UI already destroyed; it is skipped once the UI or the player is gone
  (`BansheeTeardownOverlayPatch`). Verified: chapter change with two banshees spawned, 0 NREs on
  host and both clients.
- Desync check: containers and pickups around a client in a place the host never went showed up
  as `host=<none>` (the registry only knew objects that had woken on that machine). The first
  check after each scene load now also takes every object of the type, inactive ones included
  (`DesyncRegistry`).
- **Rain falling for the host could stay off for a client.** A client applied the host's rain
  through vanilla `startRain`, which re-runs the "no rain on day 1 or night scenario 1" gate
  against the client's own `NightScenarios.scenarioId`. Only the host's night pick sets it, so a
  client could sit on 1 and refuse the rain (desync check: `World raining: host=1 client=0`).
  The client now starts the host's rain with `ignoreDay` (the host already passed that gate).
  New pilot command `rain [on|off]`. Verified: host rain off/on reaches both clients, and both
  clients rejoining while it rains come up raining with no weather exception.
- Desync check: a dead player's health is no longer compared (the dead machine refills its own
  bar for the respawn while the host's copy stays at 0).
- Not a mod bug: vanilla writes `savch.dat` as plain text and always decrypts it when a new
  chapter starts, so it logs a `FormatException` and falls back to the chapter save kept in
  `savs.dat`. Harmless, and the same without the mod.

## 0.8.195 — The apply-changes question only for changes with consequences

- **Leaving a Multiplayer settings screen asks "Do you wish to apply these changes?" only for
  a change that matters to others**, not for small adjustments. Asked: your name (everyone
  sees it), Voice chat off / push to talk / always on (whether your mic goes out to
  everyone), and, while hosting, the rules (friendly fire, extra loot, night monsters,
  creatures hear voices), which change the game for everyone at once. Not asked: volumes,
  keys, microphone device, player names display, text chat, other players' steps, each
  player's volume, and the host's players / lobby / port / password before hosting. Those
  are kept on Return as before 0.8.194.
- No on the question now puts back only the settings it asked about; small adjustments made
  on the same screen stay.
- Code: `VmSetting.Ask` in `UI/VanillaMenu/VanillaMenu.cs`; the marks in
  `MultiplayerScreens.cs` / `MultiplayerScreens.Voice.cs`.

## 0.8.194 — Apply and Revert to default on the Multiplayer settings screens

- **Settings, Voice, Voice > Players and Host settings now have Apply and Revert to default**,
  on the Return row where vanilla Options has them (Apply at x 0, Revert to default at x 168,
  the game's own words for both). They work the vanilla Options way: a change still takes
  effect at once (the mic meter, volumes and host rules show it live), Apply keeps it, and
  Return or Esc with changes not applied asks the game's "Do you wish to apply these
  changes?"; No puts back what the screen had when it was opened. Opening a child screen
  (Voice from Settings, Players from Voice) does not count as leaving. Apply is greyed while
  nothing changed, Revert to default while every setting on the screen is at its default.
- **Revert to default** puts the screen's own settings back to the values bound in code
  (Settings: name, player names, text chat, other players' steps; Voice: mode, the three
  keys, microphone, both volumes; Players: each listed player back to 1; Host settings: the
  rules, and players, lobby, port and password only while not hosting, as those rows are
  locked online). It is not applied until Apply, so No on leaving undoes it too.
- Code: `VmSettingsPage` / `VmSetting` and `VmBuilder.OptionsButton` / `Enable` in
  `UI/VanillaMenu/VanillaMenu.cs`, `VmScreen.OnOpen`; `ModSetting<T>` keeps its `Default` and
  `Id`. No config keys or protocol change.

## 0.8.193 — A hand-painted walkie icon

- **New walkie icon, painted for the mod in the game's inventory style.** 0.8.192's borrowed
  vanilla icon (`radio_small_01`) is a 25-pixel atlas cell and looked blurry blown up to a
  slot. The new one is painted at 1024 and shipped at 256: a battered boxy handheld in worn
  dark metal, lit from the top left like every vanilla item, standing nearly upright with a
  slight lean as the game stands its boxy items (the 9V battery), a stubby rubber aerial, two
  ridged knobs, a sunk speaker grille, a push-to-talk bar, a coiled
  cord, black tape wrapped round the middle (it is made at a bench from scrap), a scratched
  label plate with screws, a little old blood at the bottom, and the soft dark shadow the atlas
  icons have. Greyscale with a breath of warm, like the atlas.
- **The icon no longer shimmers when shrunk.** The old 500 px icon was loaded without mipmaps and
  drawn at about 50 px, so it jagged. The loader now builds a mip chain with trilinear filtering.
  The unused fallbacks (a PNG beside the DLL, a second resource name) are gone.
- The icon is painted by a script, `scripts/paint-walkie-icon.py`, so it can be repainted.

## 0.8.192 — The walkie wears the game's own radio icon

- **Walkie icon from the game.** The inventory atlas (`InventorySprites`) already holds a
  handheld radio, `radio_small_01`, the icon of the "damaged handheld radio" journal note:
  grey, diagonal, painted in the same hand as every other item. The walkie now uses it. The
  mod's own icon (a clean black handset with red buttons, loaded from an embedded PNG onto a
  copied scrap-metal quad) stood out next to vanilla items and is gone, with its loader and
  `Resources/walkie_talkie.png`. The atlas also has an unused `walkieTalkie` sprite; at 9 by 25
  pixels it reads as a sliver, so it was passed over.
- Nothing is added to the sprite collection any more, so the one-time
  `FindObjectsOfTypeAll` scan for it is gone too.

## 0.8.191 — Radio talk key rebindable

- **Settings > Voice > Radio talk key** (config `VoiceRadioTalkKey`, default `Mouse1`, right
  mouse as before). Talking on the walkie was hard-wired to right mouse; it is now any key or
  mouse button, the side buttons included (`Mouse3`, `Mouse4`). It still keys the radio only
  with the walkie in hand and only while playing (no menu, container, dialogue, map or chat).
- Key settings on the Multiplayer screens show mouse buttons by name (Right mouse, Middle
  mouse, Mouse 4 and Mouse 5 for the side buttons) instead of Unity's `Mouse1`..`Mouse4`.
- Not tested in game: side buttons reaching the game on Linux and under Wine.

## 0.8.190 — The walkie in the item list

- **Item givers can give the walkie.** The walkie was answered only by `ItemsDatabase.getItem`
  and `hasItem`, never listed in the database's name list (`itemsDict`), so anything that lists
  items from it (the vanilla debug Items window, item-giver mods such as Darkwood Item Spawner's
  Browse Items) did not show it. It is now in that list (re-added after `populateDict`, which
  clears it). Nothing loads its path: `getItem` still answers for it. Loot is unaffected (loot
  tables, not this list). `Items/WalkieItem.cs`.
- Not tested in game: the third-party item spawner's own grant path.

## 0.8.189 — The walkie as a real radio: knob, battery, pocket, range, doubling, feedback, roger beep

- **On/off knob.** `B` (Settings > Voice > Radio on/off key, config `VoiceRadioPowerKey`) turns
  the walkie on or off, only with it in hand, as a knob on the radio would. On: a click and the
  speaker hissing alive; off: a click and a short dying hiss. Off, it neither receives nor
  transmits, nor plays out loud for anyone near, nor makes a sound creatures hear. It stays as
  set across games.
- **9V battery.** The walkie runs on the vanilla `battery9v`, the way the flashlight does: its
  durability bar is the charge, a full battery lasts 25 minutes on (talking drains it faster,
  a full one about 8 minutes of talk), only while the game runs. Under 10% it chirps twice
  every 30 s; flat, it dies with a click and counts as off. The vanilla Reload key (`R`) with
  it in hand swaps in a fresh battery (`InvItemClass.reload`). A crafted walkie comes charged.
  A walkie crafted before this version keeps whatever charge its old item had: if it reads
  flat, reload it. `Items/WalkieItem.cs`, `Audio/VoiceChatService.Walkie.cs`.
- **In hand or in a pocket.** In a pocket a radio plays at 55% and dulled to 2 kHz, for its
  carrier and for players near them (heard 180 instead of 260 units); creatures hear a pocket
  radio 100 units around instead of 160.
- **Range.** The signal between the talker's radio and the receiving one is clear to 3000 units
  (the map is about 25000 across) and fades to nothing at 9000; each end inside a building counts
  as 35% farther, each end underground lets 15% through (`whereAmI.inUndergroundLocation`, sent
  in the walkie byte), and nothing reaches another world (a dream, a loading location). A weak
  signal is quieter under more static and breaks up in 10-40 ms dropouts, more often the weaker;
  below 3% the radio does not open at all. Creatures near a radio the signal does not reach hear
  nothing.
- **Doubling.** A second talker keying over the one holding the channel was simply not heard on
  the radios. Now both come through: the holder garbled under a wobbling whistle (two carriers
  beating), the second one quieter under it, as on a real channel.
- **Feedback.** Keying within 90 units of another player's live radio feeds back: a howl near
  2 kHz rising over about a second, drifting and warbling, from that radio for everyone near it
  and on every radio on the channel, until the talker lets go or the radio is switched off. The
  talker hears it too. On the host, creatures hear a howling radio up to 420 units away.
- **Roger beep.** Each transmission ends on the far radios with two short tones (1250 Hz, then
  900 Hz) before the squelch tail; a radio the signal no longer reaches does not beep.
- Test pilot: `radio on|off` (the knob), `battery <percent>`; `voice` shows the walkie state,
  battery, signal quality, doubling and feedback per talker.
- Box-tested (native host + Wine client, test tone as radio talk): the host's live radio 45
  units from the keying client howls at full within a second, and not at all once switched
  off (then only the live voice); 4000 units apart with one end indoors the signal reads 0.59
  and plays on the host's pocket radio at half volume; 7500 apart nothing gets through;
  battery at 0% reads as off for the peer too; drain about 1% per 15 s on standby. Not
  heard (the box has no audio): the knob, chirp, roger beep, whistle and howl themselves.
- Note: the 0.8.188 Night monsters change was made by another session in this checkout and went
  into the 0.8.187 commit (`fc53dab`) by mistake; its CHANGELOG entry stands as written.

## 0.8.188 — Night monsters setting for the host (x1 to x10)

- **New: Host settings > Night monsters.** The host picks how many night monsters come to
  each hideout: x1 (vanilla, the default), x1.5, x2, x3, x4, x5, x7 or x10. It raises how
  many of each kind of the night's monsters may be out at once (rounded up) and the pace
  they come at (vanilla places one per spawner tick; x3 places three on average), so a
  bigger night does not just trickle in slower. Worms, shadows and scripted night events
  stay vanilla.
- **Before hosting or mid-game.** Host settings is on the title screen and in the pause
  menu. Only the host spawns night monsters and it reads the value every tick, so a change
  applies at once, also in the middle of a night (lowering it lets the extra ones already out
  stay until they die or morning). No wire change: clients do not need the value.
- **Solo too.** Above x1 the host's per-hideout night spawner also runs with nobody
  connected, so the setting works in a game hosted alone or played offline; at x1 with no
  peers vanilla's own spawner runs, as before.
- Config key `[Gameplay] NightMonsterMultiplier` (float, 1..10, default 1; a hand-edited
  value in between works and shows as the step below it). Files:
  `Domains/Night/Patches/NightSpawnProxyPatch.cs`, `Core/CoopBalance.cs`,
  `Config/ModConfig.cs`, `UI/MultiplayerScreens.cs`, `Core/Loc.Ru.cs`, docs CONFIG and
  HOW_COOP_WORKS (sections 6 and 18; the loot-share row now says the first taker carries the
  whole party's share, which is what the code does).

## 0.8.187 — Voice settings of its own; voice without Steam, on any install

- **Voice no longer needs Steam.** Voice was recorded and decoded by Steam's voice API, which
  needs a logged-in Steam client on every player (the GOG build and players without Steam had
  no voice, also over LAN), and which always uses the mic and level set in Steam's own
  settings, so a game could not offer either. Voice is now recorded through Unity's own
  `Microphone` (any device the game sees) and sent in the mod's own codec: 16 kHz IMA ADPCM,
  40 ms a packet, each packet decodable on its own so a lost one costs only itself
  (`Audio/VoiceCodec.cs`, `Audio/VoiceMic.cs`). 64 kbit/s per talker while talking. It works
  on every install, LAN or Steam session alike. Protocol 51.
- **Multiplayer > Settings > Voice**, a screen of its own (Settings keeps one "Voice chat" row
  that opens it):
  - Voice chat: Off / Push to talk / Always on.
  - Push to talk key.
  - Microphone: Default, then each input device the game lists (config `VoiceMicDevice`). A
    saved device that is not plugged in falls back to the default.
  - Microphone volume, 0 to 2 (config `VoiceMicVolume`). It is also how loud you count for
    how far your voice carries and for the creatures.
  - Microphone level: a live meter while the screen is open (the mic runs for it on the title
    screen too), showing when you count as talking.
  - Voice volume: everyone.
  - Players: one volume slider per other player in the game, 0 (muted) to 2, kept by the name
    they go by so it holds across sessions (config `VoicePlayerVolumes`,
    `Audio/VoicePlayerVolumes.cs`).
- **Always on** sends only while you speak: a gate that opens 12 dB over the room's own noise
  floor, which it keeps following (a fan switched on stops counting as speech after a while),
  with the 40 ms before the gate opened sent first so the first syllable is not cut. Push to
  talk sends that 40 ms too.
- Steam's own noise suppression and automatic gain no longer apply; the mic volume is set by
  hand, with the meter.
- Tests: codec round trip, packets decoding independently, the loudness scale.
- Test pilot: `voicetone [sec] [walkie]` sends a 440 Hz tone as if talking (no mic needed),
  `voice` shows this player's mic and every talker heard here (packets, buffer, underruns,
  loudness, volume, how they are heard).
- Box-tested (native host + Wine GOG client, neither with Steam voice): a tone sent each way
  arrives in order with no underruns, its loudness as sent (0.63 for -23 dBFS), playback
  buffer steady around 0.1-0.17 s. Settings > Voice and Players lay out like the vanilla
  Options pages; with no input device (the box has no audio) the meter says "No microphone
  found" and the log warns once. No exceptions in either log. Not tested: real microphones,
  device switching, the always-on gate on real speech.

## 0.8.186 — Walkies one way at a time, the talker's click heard around them, indoor echo on voices

- **Radios are half duplex.** A walkie that is transmitting no longer plays what others say:
  while you hold the key, your own radio is silent, as on a real handheld. And a channel
  carries one talker at a time: whoever keyed first holds it until they let go (plus a third
  of a second), and a second talker keying meanwhile is not heard on the radios (only their
  live voice, by whoever stands near them).
- **The talker's own radio is heard around them.** Players near someone keying a walkie now
  hear its click as the key goes down and the short static as it comes up, from the talker's
  position (full within about 45 units, gone by 260, muffled through walls). Before, only the
  talker heard those. Standing next to a talker, you hear their live voice and their clicks,
  and the reply from the far end comes out of their walkie (or out of yours, if you carry one).
- **Echo indoors.** A voice from inside a building now gets the reverb vanilla puts on any
  sound made inside (`AudioController` adds a default `AudioReverbFilter` when the source's
  `CharBase.isInside`): the talker's stand-in for their voice, the carrier for a radio heard
  nearby, this player for their own radio. Vanilla's reverb zones (underground places, dreams)
  already reached the voice, which plays through an ordinary 3D source.
- **Walls muffle a voice the way vanilla muffles sounds**: 0.75 volume and a 1500 Hz cutoff
  (`AudioController`'s own values), instead of 0.55 and 900, which made a voice through a door
  far duller than a step through the same door.
- Not tested in game (voice needs two logged-in Steam clients).

## 0.8.185 — A walkie playing radio talk is heard by the players and creatures around it

- **Radios play out loud.** Radio talk was heard only by whoever carried a walkie themself.
  Now every walkie plays it from its carrier's pocket: a player standing near someone with a
  walkie hears the transmission from that player's position (panned toward them, full volume
  within about 45 units, gone by 260, muffled through a wall), through the same radio sound
  (narrow band, distortion, squelch, hiss by the distance between the two radios). Carrying
  your own walkie, you hear it on yours. The talker's own radio stays quiet. Of their voice, the
  radio in your hands and a radio nearby, the loudest is what you hear, held until another is
  clearly louder.
- **Who carries a walkie** travels in `PlayerState` (new `CarriesWalkie` trailer, protocol 50),
  polled twice a second from the inventory and hotbar, whatever that player's voice settings.
- **Creatures hear radios.** On the host (Creatures hear voices on), a walkie playing a
  transmission, whisper or not, is a small sound (160 units) where its carrier stands, twice a
  second while the talk goes on. The talker's own radio makes none. `Audio/VoiceHearing.cs`.
- Not tested in game (voice needs two logged-in Steam clients). To check with three players:
  A talks on the walkie, B carries one, C without one stands next to B and hears it from B's
  side; a creature near B turns toward the radio.

## 0.8.184 — Voice from where the player stands, as far as they spoke loud; a real-sounding walkie; creatures hear talk

Review of the voice chat. What it did before: every voice played flat in the middle (no left or
right), at a volume by distance alone, with the same reach for a whisper and a shout; the
decoded audio was stretched to the mixer's rate by repeating samples (a faint metallic edge);
each talk spurt waited a quarter second to start; a spurt shorter than that hung until the
next one; late or repeated network packets played out of order; the radio was a gentle
600-6800 Hz filter with a 50 ms tick at the end; walls were found with a ray that hit anything,
floors and triggers aside.

- **Heard from where they stand.** The voice now comes from the player's character: it pans
  left and right toward them (gently when they stand close, fully from about 300 units out), and
  keeps the muffle through walls, now tested the way vanilla tests whether a creature hears a
  sound through something (`Character.heardSound`: one ray on its blocking layers) and eased in
  and out instead of switching.
- **As far as they spoke loud.** Each voice packet now carries how loud the talker spoke,
  measured on the talker's own machine (protocol 49, `VoiceData` gains a loudness byte). A shout
  carries the full `VoiceMaxDistance` (650), normal speech about three quarters of it, a whisper
  about a third. The reach rises at once with the voice and falls slowly between words, so it
  does not pump.
- **Creatures hear you.** On the host, a player talking louder than a murmur makes a sound
  where they stand twice a second, through vanilla's own `Character.alertInArea` (the call a
  step, a door or a shot makes): normal speech about as far as a walking step, a shout farther
  than running. Creatures stop and listen, and come looking if it goes on. Whispering is safe.
  Dead players make no sound. Host settings > **Creatures hear voices** (config `[Voice]
  VoiceAlertsEnemies`, default on). The host needs no Steam for it: the loudness travels with
  the packet. `Audio/VoiceHearing.cs`.
- **The walkie sounds like a radio.** A narrow 380-2900 Hz band (two second-order filters
  instead of one gentle one), driven into a little distortion, under a hiss that grows with
  the distance between the two radios (worst when the talker is in another location). The far
  radio opens with a squelch click and closes with a short burst of static, and the talker
  hears their own radio click when keying and letting go. `Audio/VoiceChatService.Radio.cs`.
- **Cleaner, quicker voice.** Decoded straight at the mixer's rate (Steam takes up to 48 kHz),
  with interpolation when the mixer is faster; volume changes ramp across each audio buffer
  instead of stepping; talk starts after 150 ms of buffer instead of 250; a short spurt plays
  out instead of waiting; a backlog after a network stall is cut to 200 ms instead of 350.
  Late or repeated packets are dropped by their sequence number. The voice packet's layout is
  written by one helper for the send path and the message (`VoiceDataMessage.WriteSlice`) with
  a round-trip test.
- Not tested in game: voice needs two logged-in Steam clients, and the test boxes' client is
  the GOG build. To check: a teammate's voice from the left or right, a whisper fading out
  sooner than a shout, the radio squelch and hiss, and a creature turning toward a shout.

## 0.8.183 — Names over the head in a softer look; chat fades smoothly; nothing said over the players

- **Chat stays in the chat.** A chat line also popped up as a speech bubble over the speaker,
  and map pins and pings from others as a remark over the local player. Both are gone: what is
  said shows only in the chat corner. `UI/ChatHud.cs`, `MapPinBoard.Notify`.
- **Chat fades out smoothly.** Lines already faded out over 2 s, but closing the chat dropped
  every line past its time at once (their alpha came from their age). Each line now keeps its
  own alpha: it shows at once (new line, or the chat opened) and eases out over 2.5 s after its
  time or when the chat closes.
- **Names over the head, on hover, softer.** The name moved from under the feet to over the head
  (the speech bubbles that sat there are gone). By default it now shows only while the cursor is
  on that player (`ShowPlayerNames` default `pointed`; Always and Off stay in Multiplayer >
  Settings > Player names). Config files written by 0.8.182 keep their `always` until changed in
  that menu. The look stays the game's hover-label font (the outlined text over a door), but
  dimmed to grey, slightly see-through and at three quarters of its size, so it no longer reads
  as stark as an object label. Names fade in and out over 0.45 s and grow faint with distance
  (full up to 220 units, gone at 700). Seven looks from the game's own fonts (`FontsDB`) were
  compared in game; they are kept in `Nameplates.Styles` and the test pilot's `namestyle <n>`
  switches them live. `UI/Nameplates.cs`, `HudText.Font`.
- Not tested in game: the final size and the hover default (the test run was stopped).

## 0.8.182 — Player names over the players, chat in the game's own text, pause-menu version label

- **Player names.** Other players' names now show under their character in the game's own
  hover-label font (the outlined text over a door or an item). A name shows only while you can
  see that player, by the sight test vanilla uses for enemies (`Player.isInSight`: the view cone
  or the close circle, no wall in between), so it never gives away someone behind a wall or out
  in the dark. Names fade in and out and are hidden in menus, the map, dialogues and cutscenes.
  Multiplayer > Settings > **Player names**: Always (default), When pointed at (cursor on the
  player), Off; config `[Network] ShowPlayerNames` (`always` / `pointed` / `off`).
  `UI/Nameplates.cs`, `UI/HudText.cs`.
- **Names reach everyone.** Until now a name only travelled with chat lines and map pins. A
  client now tells the host its name after the handshake and whenever it changes (new message
  `PlayerName`, 168); the host puts every name in the peer roster, so late joiners and a promoted
  host know them all, and a name edited mid-game updates for everyone at once. Left at the
  default "Player", a Steam player goes by their Steam name; otherwise unknown names show as
  "Player N". `Sync/PlayerNames` (`Domains/Players/PlayerNames.cs`), roster in
  `HostMigration.PeerRoster.cs`. Protocol 48.
- **Chat.** The chat was a grey Unity IMGUI box with a SEND button and plain IMGUI lines. It is
  now drawn in the same hover-label font in the lower left, with no box: "Name: text" lines
  (name in grey), long lines wrapped under their own start, map-pin notes in grey. Lines fade out
  after 14 s and all come back while typing. Ctrl+C opens a "Say:" line with a blinking caret,
  Enter sends, Esc closes, Ctrl+V pastes. Text chat can now be turned on and off mid-game (it
  needed a restart). `UI/ChatHud.cs`.
- **Settings split in two.** The Settings screen had grown to 12 rows, past what the vanilla
  Options screens hold. It now has this player's own seven (Name, Player names, Text chat, Voice
  chat, Push to talk key, Voice volume, Other players' steps), the same count as vanilla Video;
  what the host decides for everyone (Friendly fire, Extra loot, Players, Steam lobby, Port,
  Password) moved to a new **Host settings** screen, shown to everyone but a joined client.
- **Pause-menu version label.** In the pause menu the Darkwood version text sat right under EXIT,
  because the pause stack (Continue, Help, Options, Multiplayer, Main menu, Exit) is a row longer
  than the title's and the label moved only half a row. It now keeps the title screen's gap under
  EXIT. `MainMenuMultiplayerInject.Panel.cs`.
- Test pilot: `names` (each player's shown name, sight and plate), `setname <name>`.
- Box-tested (host + one client): names both ways, a live rename on both sides, the name hidden
  with the client behind a wall, chat both ways with wrapping, the pause menu and both settings
  screens; no exceptions in either log. Not tested: Steam names, the "When pointed at" mode by
  hand.

## 0.8.181 — Multiplayer menus as the game's own; F2 removed; manual saves become an add-on

- The multiplayer menus looked bolted on: the HOST/JOIN panel used a different, all-caps font at
  the wrong size, and the session controls lived in a grey IMGUI window (F2), as did the join
  profile picker and the F3 save slots. They are now built from the game's own menu pieces, so
  they look and behave like Options and Profiles: the spaced grey heading and centred entries of
  Profiles, the Video tab's grey setting names with white values, the Options "Return", the volume
  slider, and the vanilla yes/no box for every confirm. Hover, click sound and gamepad navigation
  are vanilla's; Esc / gamepad B goes back one screen (`UI/VanillaMenu/VanillaMenu.cs`).
- MULTIPLAYER is also in the pause menu now, under OPTIONS (MAIN MENU and EXIT move down a row).
  Its screens (`UI/MultiplayerScreens.cs`):
  - Multiplayer: what can be done now. Title: Host, Join, Settings (connected: Join/Enter world,
    Disconnect; hosting: Choose a profile). Pause menu: Host this game (single player opens the
    current game to others), Invite friends (Steam host: copies the lobby id, opens the invite
    overlay), Send the world again (host), Restore my character (client, when its backup exists),
    Settings, Disconnect. A status line says what the session is doing (hosting and how many joined,
    connecting, waiting for the host, download progress, failures in plain words); the mod's
    version is at the bottom.
  - Host: Local network / Steam (greyed out without a running Steam).
  - Join: Address, Port and Password typed in place, Connect; Steam lobby id and Join the lobby.
    While connecting it shows the progress and Cancel; once the world is ready, Enter world.
  - Settings: name, text chat, voice chat (off / push to talk / always on), push-to-talk key (press
    a key), voice volume and other players' steps (sliders); for the host friendly fire, extra loot,
    player count, Steam lobby type, port and password (the last four only while not in a game).
    Changes apply at once and are written to the config.
  - The host's world: the profile picker opens by itself when the download is done; occupied
    profiles ask in the yes/no box, another campaign is marked; Back asks to leave the game.
- F2 and its window are gone; everything it did is in the screens above. Disconnecting from the
  pause menu pauses the game again when you are alone, like single player.
- The push-to-talk key changed in Settings applies at once (it was read once per session).
- Manual save slots (F3) are no longer part of the co-op mod: they are the optional add-on
  `YokWare.ManualSaves` (`DarkwoodMP.ManualSaves`, plugin `com.yokware.manualsaves` 1.0.0, needs the
  co-op mod). F3 in the world opens a Saves screen in the pause menu: ten slots of the current
  profile with Save and Load and the same co-op rules as before (only the host saves, and every
  player saves with it; no save during a partial night death, dream or prologue; Load needs the
  session left). The co-op mod exposes what add-ons may use in `Bootstrap/AddOnApi.cs` (language,
  session state, save rules, the crash-safe save-set copy, the input lock). The release packs the
  add-on as its own zip per loader.
- Messages that named F2 or ENTER WORLD now name the menu entries (WORLD SHARE FAILED asks the host
  for Multiplayer > Send the world again; "press Enter world"). A promoted host is told the world is
  saved again at the game's next save (the F3 reminder pointed at a key that is now an add-on).
- The menu font has no dash or ellipsis glyphs: menu text draws them as "-" and "...".
- Russian for every new text; `LocTests` also scans the new screens and the add-on (first arguments
  of the menu calls, the choice lists, returned status lines).
- Test pilot: `pause`, `menu` (list the shown entries) and `press <label>` drive the menus.
- Box-tested (host + one client): title screens in English and Russian, pause-menu screens, Send the
  world again, F3 Save (with the client's SaveSync), Disconnect confirm on both sides, host failing
  on a busy port, host leaving and hosting again from the pause menu, client rejoining from the title
  through the picker and Enter world. Not tested: Steam (no Steam in the box), gamepad navigation.

## 0.8.180 — Russian for the mod's own text

- With Russian chosen in Options > Language, everything the mod itself shows is in Russian: the
  title button (СЕТЕВАЯ ИГРА, pixel art built like the English one from the game's Russian menu
  glyphs: С from НАСТРОЙКИ, Е from ГЛАВНОЕ, Т from АВТОРЫ, "ВАЯ ИГРА" from НОВАЯ ИГРА), the
  multiplayer panel buttons and their progress/failure labels, the F2 settings window, the F3
  manual saves window, the join profile-slot picker, connection and world-share status, the
  WORLD SHARE FAILED / WRONG SAVE warnings with their reasons, HUD lines (someone already talking,
  already taken, host promoted, …), the chat box, map pin cards, stamps and chat notices, and the
  walkie-talkie item's name and description (Рация). Every other language keeps the English text.
- Text stays English in the code and is translated where it is shown (`Core/Loc.cs`,
  `Core/Loc.Ru.cs`: whole-string entries plus patterns for lines with numbers or a nested reason),
  so status matching keeps working and an English reason sent by the host reaches a Russian client
  in Russian. A language change in Options rewrites the panel labels and swaps the title art at once.
- Tests: every literal the code hands to a display sink must have a Russian entry; the patterns
  keep their numbers and nested reasons (`LocTests`).
- Removed the F2 window's never-shown "host next step" hint (dead code).
- The title MULTIPLAYER button sat below the version text, in the dark bottom band of the menu's
  vignette overlay, at about half the brightness of PLAY/EXIT. It now takes the row right under
  PLAY; OPTIONS, CREDITS and EXIT each move down one row (60 units, vanilla's spacing) and the
  version / player-id labels half a row, so the version text stays close under EXIT. The shift is taken from the vanilla offsets, re-applied when vanilla resets
  EXIT on menu open, and undone when leaving the title; pause-menu-only rows are untouched
  (`ApplyTitleStack` / `ResetTitleStack`).
- The title art's letters sat 2 texels low in their canvas (cropped at a faint shadow pixel above
  them), 4 px off the row; they now sit on the vanilla sprite rows, so the stack is evenly spaced.

## 0.8.179 — MULTIPLAYER title button redrawn as vanilla pixel art

- The title-screen MULTIPLAYER button was an AI-generated picture (bevelled letters, blurry glow,
  sized by a guessed fraction of the row) and looked foreign next to PLAY/OPTIONS/EXIT. It is now
  real pixel art on the vanilla grid: the idle state is assembled pixel for pixel from the vanilla
  menu atlas glyphs (M, U, I from MAIN MENU; P, L, A from PLAY; E, T from EXIT; R from CREDITS; Y
  from the Turkish YARDIM, the form every other vanilla Y uses, since PLAY's Y leans left), each letter
  with its own soft edge column,
  and the lit state is made with the same idle-to-rollover look vanilla uses for its `_0` → `_1`
  sprites (fitted on PLAY/EXIT/CREDITS, cross-checked against them at about 1% error).
- The art is drawn point-filtered at exactly one texel of the cloned EXIT sprite, so its letters are
  the same height and pixel size as the vanilla buttons at every resolution. Idle and lit share one
  canvas, so the letters no longer jump on hover.
- Files: `Resources/MenuButtons/multiplayer_idle.png`, `multiplayer_hover.png` (the old `_sm` copies
  are gone), `UI/MenuButtonArt.cs`.

## 0.8.178 — The village at night, with three players

On top of 0.8.177. Protocol **47** (unchanged). Three-player pilot runs of the chapter-1 village
night (`NightVillage`): two players in the village, the third out in the world keeping the clock
running, then the third walking in late.

**Verified in game, unchanged:** at the "night is coming" warning the villagers stay while anyone
looks at them and leave together once nobody does, on every machine; a player walking in after
that finds them gone; the worm killed the host standing outdoors in the village (spectating until
morning) and never touched the client sheltering in the brother's house (vanilla's shadow ward
on it); at dawn the dead host was released and the villagers came back unseen. The village is a
location: with all three inside, the clock stops and night does not come (section 5 rule).

**Fixed:**
- **A villager stayed invisible on a client for good.** A location pad loaded after the world
  gives its bodies save ids in each machine's own load order, so the village woodcutter's id on the
  client was the id of a different host villager. The client's matcher refused him as "that
  villager's save twin", fell back to spawning a prefab that does not exist, and logged a spawn
  error every half second while he stood invisible in front of the client. A body now counts as
  another host body's save twin only under that body's name, as the save-twin lookup itself
  requires (`ClientEntityInterpolationService.SaveTwin.cs`). This covers every pad, not only the
  village.
- **The sick villagers left at night too.** Every villager is `villagerNeutral`, so the
  "friendly villagers" picked by faction included the sick lying in the houses, against the
  documented rule ("the crazy / infected villagers stay"). Villagers already standing on an
  indoor floor (vanilla's ground test, 6 of 30) now stay; the ones outdoors (the crazy man in the
  pig cottage's yard, one sick villager lying outside) go home like the rest; the list is built only once the pad's floors are live, so every
  machine picks the same ones (`NightVillage.cs`).

Pilot: `chars` shows faction, save id and an away villager; `status` shows `ward`,
`villagersAway` and `nightDead`; new `match <name>` explains a client's pending body match.

## 0.8.177 — Where time stands still, with three players

On top of 0.8.176. Protocol **47**. Three-player pilot runs of every place and moment where
vanilla stops the clock: a location (the bunker), a player dying, the Wolf's trap in chapter 2
(its personal `timeFreeze`), a player dead until morning, the morning hold, and a dream.

**Verified in game, unchanged:** host inside the bunker with the clients outside, the clock
runs; all three inside, it stops; one client out, it runs; the host out with both clients in,
it runs. The morning holds the clock for everyone until the hideout is empty (a client walking
out with the host still home keeps it; the host walking out ends it). A dream started by a client
holds the clock for everyone and the world time comes back where it was.

**Fixed:**
- **The host dying stopped everyone's clock.** Vanilla `Player.die` turns the clock off until the
  respawn, so for about seven seconds the clients' world stood still, while a client dying
  stopped nothing. The Wolf's trap did the same: the host caught in it froze time for the whole
  party, a client caught in it froze nobody. Now a player's own freeze (dying, dead until
  morning, the trap's `timeFreeze`) only takes that player out of the count, like being inside a
  location: the clock runs while any player is in the open world and not held, and stops when
  nobody is. A host dead at night and spectating no longer keeps the clock running alone either
  (both clients in the bunker: held; one walks out: it runs). World-wide holds (the morning, the
  day-1 prologue wait, a load) still stop it for everyone.
- Each client reports its own freeze on `PlayerState` (`ClockHeld`, new trailer, protocol 47).

Files: `ClientTimeAuthorityPatches.cs` (`HostSharedClockPatch` rewritten around
`CoopTimePolicy.SharedClockSteps`, `LocalPersonalHold`), `PersonalProloguePatches.cs` (the
day-1 hold is readable per step), `PlayerMessages.cs`, `RemotePlayerState.cs`,
`PlayerStateNetHandlers.cs`, `LanNetworkManager.Tick.cs`, `CoopPolicy.cs`. Pilot: `effect <type> off`.

## 0.8.176 — Nights, deaths, blood, skills and every creature with three players

On top of 0.8.175. Protocol **46**. Three-player pilot runs of nights (deaths, spectating,
a player quitting and rejoining while dead, the morning release), blood on the floor
compared splat by splat on every machine, the skills that touch other players, and a sweep
of the creatures (spawned beside a client and set on it; each game records the sounds it
plays, the sweep lists what one game heard and another did not, where each sees the
creature, and whether the client's blows land). New pilot commands: `spawn`, `aggro`,
`kill <radius> all`, `comps`, `audio` / `audiodump` (a sound tap on
`AudioController.PlayAudioItem`, pilot runs only), `perk` / `useskill`, `face … away`;
`chars` shows a creature's sight state, `status` a dead stand-in. Scripts
`enemy-sweep.sh` and `lone-aggro.sh`. Fixed:

### Blood and gore

- **A creature's hit on a client left two blood splats for everyone else** (one the client
  placed, one the host added at its own random angle about 20 units off), and the client
  saw one. A friendly-fire hit or the host's blow on a client did the same the other way:
  two on the victim's screen. The victim's own getHit splat is now the only one, and it
  reaches everyone; a blocked hit leaves none, as in vanilla (the host's extra splat
  ignored blocking). Damage that arrives over the network (`DamagePlayer`, a friendly-fire
  hit on the host) now forwards its splat too (`HitscanBloodPatch.OwnHitDepth`); the host
  splats in `HandleEnemyHitConfirm`, the host-melee-on-stand-in path and
  `BroadcastFriendlyFireBlood` are gone. Verified: three chomper bites, three splats, the
  same positions on all three games.
- Bleeding leaves no drips in this game build (vanilla's `waitToBleed` asks the FX pool for
  `BleedSplat`, which the pool does not have, and stops there), so there is no trail to
  share; nothing changed.

### Creatures

- **A swamper's spit never left its puddle on clients.** The landing spawns a trap prefab
  (`swamper_splatSpawner_chain_*`), sent by name; the client looked for it everywhere but
  `Traps/`, logged "NOT FOUND", and its splats' events waited 60 s and were dropped. The
  same lookup lost a thrown bottle's broken glass. `Traps/` is searched now; the puddle
  chain is in the same 16 spots on every game.
- **The human spider's watched state existed only on the host.** It slows and stops its
  glow while any player watches it and starts a creeping loop when nobody does
  (`InSightOfPlayer`, decided on the host from every player's view); a client's copy has
  its own sight check off, so it never glowed down nor played the loop. The host's state
  travels in the entity snapshot and the copy runs the same step
  (`CreatureSightState`).
- **A creature's first sounds were lost on clients** (a crawling hand's birth cry, a fresh
  dog's sniff): the host plays them before the creature's first snapshot has made the
  client's copy, and the client dropped them ("no char/sounds"). They wait up to 1.5 s for
  the copy now (`WorldFxNetHandlers.TickEarlySounds`).
- **A creature checking out a house went for a random room when it hunted a client.**
  Vanilla sends it to the waypoint nearest the player it hunts when that player is indoors,
  reading the host only; for a client's stand-in it now does the same
  (`HostCheckOutLocationPatch`).
- Checked and matching on all three games (positions, the attacks landing on the client,
  every sound): human spider (the thrown hand, the crawling hand, their sounds),
  centipede, banshee and her babies, kamikaze (runs at its target and blows up; its blast
  damages the client), wolfman, dogs, mutated dog, black and red chompers, swamper,
  redneck with thrown rocks, redneck with the reach attack, villagers (plain, pitchfork,
  torch, burning), banshee babies, deer, pig, raven, the bride chomper, the half chomper,
  night worms, the worm swarm, the big mutant pig, the crawling hand on its own. Creature
  loops (a dog's growl, a chomper's idle and aggressive breathing) play on every game.
  Hard-night worms came for a client out in the dark and killed it while the two players
  in the lit hideout were left alone.

### Players

- **A stand-in could stay behind its player for good.** It follows by physics, so after a
  short move across something (a teleport under the 150-unit snap, a respawn, a corpse or
  wall between) it stuck on the obstacle while the player stood still: the host fought the
  wrong spot and refused the player's blows as out of range. A stand-in more than 40
  units off its player for half a second now jumps to it.

### Deaths and nights

- **Quitting and rejoining undid a night death.** The host forgot a leaver's death, so a
  player who died at night came back alive and fighting. The host keeps this night's
  deaths by lasting identity (Steam id or install key); a rejoiner is down again until
  morning (spectating, dead for the others, counted for the all-dead morning). Verified:
  died, quit, rejoined mid-night, spectating; released at dawn.
- **A client's day death cleared the host's hideout, not the client's.** The dying client
  now sends its home, and the host returns creatures and clears traps and infection there.
  Verified: a client homed at another hideout died; the host cleared that hideout.
- **Worms:** the host in a dream stopped every player's worms, and a player in a dream got
  worms placed around the far-off dream pad. Each player is checked on its own now.
- **Dreamers who all died with the host outside the dream spectated forever** (the all-dead
  check required the host to have died in it).
- A night-dead spectator follows a living player out in the night before one in a dream.
- **A player down until morning reported full health** (the spectator mode restores the
  body), which "the player's health" story checks and the desync check read. It reports 0.
- Verified as before: a night death spectates (others see the body dead), two of three dead
  wait for the survivor, morning releases everyone home.

### Skills

- **A returning client kept the host's perks.** Its restore cleared the host's chosen flags
  but not their effects (shaky hands, weak regeneration, far sight, less stamina, the
  shadows flag) nor the activated-skill lists. The host's perks are now taken off the
  vanilla way before the client's own go on.
- Checked: a client's ninja hides it from the host's creatures (a chomper beside it never
  picked it; visible, it did at once), a client's scary face sends the host's creatures
  running and the others see the face.

### Verified in the pilot / code only

Verified with three players: the blood, the swamper puddles, the spider's sight state, the
early creature sounds, the stand-in catch-up, the night death rejoin, the day-death home
clear, the returning client's perks (the host's shaky hands and weak regeneration stayed
off it, its own runner kept), a downed player's 0 health. Code only, not run in a game: the
per-player worm dream check, the all-dead dream end with the host outside it, the
spectator's preference for a player outside a dream, and the house check-out toward a
client.

### Balance (unchanged, by design)

Night monsters come at vanilla's pace and count per hideout with a player in it, not per
player; worms and shadow waves are per player; the night type is one for the world.
Creature health and damage are not scaled. See `HOW_COOP_WORKS.md` section 18.

---

## 0.8.175 — Dreams played through with three players, the Shadows perk rebuilt

On top of 0.8.174. Protocol **45** unchanged. Every dream was played with three players in the
pilot (new commands: `dream`, `use` for objects and scene actions, `hit` for creatures and
objects, `face`, `dstate`, `pending`, `skill`, `shadows`, `shadowlist`, `hold`, `lit`; `chars`
shows targets and AI state): acid, grave meadow, bunker, church ruins, doctor 1 and 2, home,
village cellar, one chance and the ending. Clients drove the story steps (talking, lamps,
areas, objects struck), deaths in a dream (one spectating, then all dead), a player quitting
mid-dream and rejoining after, and the rewards and world events of each outcome. Fixed:

### Dreams

- **The church ruins dream crashed the host's game.** Its chomper stands indoors; the extra
  chompers for extra players were placed with vanilla's random-spot helper, which retries
  itself without end for a spot indoors and overflowed the stack. Extras now look for a spot on
  the same side of the walls as the original, a bounded search (`NamedNpcScalePatch.SpotNear`).
- **The extra chompers stood still** until a player came close. They were handed a target
  before the fight started, so the "join the original's attack" step skipped them as already
  fighting, but they had no path to it. An extra that does not see the one it chases now joins
  the original's attack. They also take the scene's own tuning of the original (the grave
  meadow's is faster and is never cut in half).
- **Doctor 1 left the clients behind.** Its ending moves on to doctor 2. When a client ended it
  (talking to the hatted man), the host ran the ending inside the network message handler,
  where the chain announcement stands down: the host went into doctor 2 alone and every client
  waited in doctor 1. It now runs as the host's own ending. The same step also reaches the host
  twice (the client's dialogue outcome fires there too, and the client asks the host to end the
  dream); the second run ended doctor 2 on its first frame and marked it played. The host now
  ends a dream once (`TryClaimHostEnd`), and a late request for a dream already left is
  dropped.
- **The bunker dream's forest spirit hunted the host** whoever walked into the forest. It looked
  for a client trigger within 2000 of the dream's origin; the forest is some 8000 out. It now
  hunts the player whose step set it off (the scene's actor).
- **A client could not break the village cellar dream's barricade.** The barricade is not
  destructible: its scene waits for the player's blow. A client's blow on such an object was
  not sent to the host, so the scene never moved on. Struck story objects now go to the host
  like destructible ones (`WorldQueryHelper.HasAttackTrigger`).
- **A reloaded ending now lets friends in.** A finished game reloads straight into the ending;
  joins are let in there and the host's dream (started before anyone joined) becomes the
  party's, so they are pulled onto its pad (`DreamSession.IsEnding`,
  `DreamSyncManager.AdoptSoloDreamForParty`). Tested: host reloaded into the ending, both clients
  joined into it.

### The Shadows perk

How vanilla works: on a hard night the Shadows night event sends a wave of 8 shadows at the
player with the perk, and puts out that player's torch and lantern. A shadow blinks in around
the player every 2 to 6 seconds, closer each time, never on a lit spot; when it appears on a lit
spot it dies; it swipes up to three times per appearance, and never at a player standing in
light (a lit spot, a light area or a lantern).

- **A client's wave did no damage and ignored light.** It was driven by a custom orbit: one
  swipe on the host's copy of the client (which never reached the client), no light check while
  orbiting, so a torch never killed or kept them away. A client's shadows now run vanilla's own
  logic measured from that client: they blink in around it, are pushed out of light and die in
  it, and their swipes reach the client as shadow hits only when it stands in the dark
  (`PeerShadows`). Tested: alone in the dark the client was killed; next to the host's lit torch
  the shadows hovered at 150 to 600 and it took no damage; a bystander next to the cursed client
  took no shadow damage.
- **A client's wave put out the host's torch.** The host's game marked the host as cursed for
  any wave, and vanilla does not light a cursed player's torch or lantern. Only the cursed
  player is marked now, and the end of a wave reaches only its owner. Tested: the host lit a
  torch during a client's wave.
- **The wave's light flicker hit the host's hideout** instead of the cursed player's.
- **Other players now see each shadow appear where it does**, instead of a copy sliding toward it.

Not covered: the doctor 1 hand-off started by the host (not a client), and reading every dream's
dialogue branches.

## 0.8.174 — The ending with three players

On top of 0.8.173. Protocol **45** unchanged. A pass over how the game ends in co-op, run with
three players in the pilot (new `epilogue` and `credits` commands start the ending and finish its
pages). How it works: the ending is a party dream, so everyone alive goes; the story's choice is
made by whoever makes it first; the credits wait for every reader (2 minutes at most); the
session ends at the credits and everyone returns to the title. Fixed:

- **Players outside the ending lost the host like a crash.** A player dead at night when the
  ending began stays in the world. When the others left for the credits, the host simply
  stopped, and the players left behind ran crash recovery and elected a new host among
  everyone, including players already on their way to the credits. The host now hands the world
  over properly to one of the players who stay (`EpilogueNetHandlers.HandWorldToPlayersOutside`,
  `TryGracefulHostLeave` takes who may be chosen). Players going to the credits no longer try to
  take the host when the old one stops during the fade (`MarkLeavingSession`). Tested: host and
  one client to the credits, the dead client took the host and woke at home in the morning.
- **A host outside the ending was dragged into the credits** when the clients in it finished.
  It now stays in the world, as a client outside it does.
- **A client stepping into the ending while the host was dead got stuck in ending mode.** The
  host waits for its morning before starting a dream, so it refused the start. The client had
  already loaded the ending's area and switched to ending mode (no UI, no items), and stayed
  that way: its world view, location and lighting were left on the unused area, and the host
  kept hearing that it had entered it. A refused start now removes that area and puts the
  player back where it was (`DreamSyncManager.DropUnstartedLocalPad`), for any dream a client
  starts straight from a story step.
- **That refused ending was never asked for again.** Only dreams started through the dream
  movie were retried; one started straight from a story step (the road home's ending) was
  dropped. It is now asked for again the same way once the host can take it (`DreamRetry`
  story-step retry). Tested: the client's ending started for all three at the host's morning.

Not covered by a run: reading the real ending pages, the crawl and the bed/crater choice (the
pilot skips straight to the last page). Reloading a finished game replays the ending, as in
vanilla; the host starts inside it and friends cannot join (joins are refused during a dream).

## 0.8.173 — Three-player runs: hidden NPCs, gas fires, a third test install

On top of 0.8.172. Protocol **45** unchanged. The automated pilot now runs three players (the
host plus two GOG clients) and covered how clients act on each other: doors, items dropped and
picked up, containers, simultaneous pickups and loots, friendly fire, a client's day and night
death, a client quitting and rejoining, nights with each player in a different hideout, the
morning walk-out, a shared dialogue, chapter 2 and a full save, quit and rejoin. All of that
matched on the three machines. Fixed:

- **Hidden story NPCs showed up on clients.** When the host changed an NPC's portrait or
  animation set (a story event does this to the act 2 Doctor, who waits switched off in every
  location), the client's lookup for that NPC switched it on. Every client then saw and could
  talk to a Doctor the host did not have. The same lookup ran for the host's trader stock (sent
  for every trader, out or not) and for the NPC id remap, so a hidden trader could appear too.
  The lookups now leave an NPC as it is. Portrait and animation changes still apply to the
  hidden NPC, as vanilla does. Trader stock waits until that trader is out on the client.
  `NpcAttackedIdSync.ResolveNpcNear`, `RemapHostUidToLocal`, `TradeInventorySync.FindNpcByName`,
  `FindNpcByNameNear(wake:)`.
- **Gas the host lit stayed unlit on a client.** When the host's ignite reached a client whose
  copy of the puddle was culled, or not yet in physics (the join-time gas state), the client
  found no puddle and logged `[GasIgnite] no flammable Liquid found`. The gas then sat there
  unlit, ready to be lit a second time. That copy is now lit too and burns out on its own, as
  on the host. `WorldPhysicsSyncService.IgniteGasAtPos`.

Checked and left alone (vanilla behaviour, the same on the host):
- `NullReferenceException` in `Core.RemovePooledPrefab` from `Character.OnDestroy` at a chapter
  change or quit. The chapter 2 swamp's Banshees carry an authored `banshee_ambient_01`, and the
  pool is torn down before them.
- `FormatException` in `SaveManager.getChapterSave`. Vanilla writes the chapter save as plain
  text but always decrypts it, then falls back to the static save.
- `Invalid (or 0 returned) gameObjects for NightEvent_weird_moveMeWiggle_01B` in the chapter 2
  hideout. The scene finds no waypoints of its kind there. Vanilla also parents it under the
  big location.
- Two bodies teleported onto one spot push apart (Unity depenetration). Only the pilot's
  teleports do this.

Tooling:
- A third install (`ThirdDarkwood`, GOG under Proton, its own prefix) saves to
  `LocalLow/.../Darkwood_Third`, the same isolation `SecondDarkwood` has. The build also deploys
  to `ThirdPlugins` when `GamePath.local.props` sets it. `PersistentDataPathPatch`, csproj.
- New pilot commands:
  - `at <unix ms> <cmd>` runs a command at one moment on several games.
  - `drop`, `pickup`, `ground` handle items on the ground.
  - `cont` lists containers and `loot` empties the nearest one.
  - `hitp` lands a friendly-fire hit on a player.
  - `obj` finds named objects, switched-off ones included.
  - `attached` lists the objects attached to characters.
  - `gas`, `ignite`, `liquids` pour, light and list gasoline.
- `endnight` now works on a client too (it asks the host, as a walk-out would).

## 0.8.172 — Vanilla parity audit: sounds, night spawns, the Shadows perk

On top of 0.8.171. Protocol **45** unchanged. An audit of where the mod blocks or thins out
vanilla content. Text (thought lines, hints, speech bubbles), the daytime random events, the
redneck ambush, night location events, weather and the random world sounds were checked and
already match vanilla per player. Fixed:

- **Far flat sounds were muted.** In a session every world sound past 650 units was cut unless
  it was 3D. Fully 2D sounds (a redneck's idle mutter, a dog's whimper, spider deaths) have no
  falloff: vanilla plays them at full volume from anything it keeps awake around the player
  (the `WorldGrid` node box, about 2400 x 1600 at 1080p). They now play within that box; only
  what is awake for a far peer alone is cut. The host also sends a creature's 2D sound across
  the whole client interest range (was 650). `AudioSuppressionPatch`,
  `LocalAudioService.WorldSoundAudible`, `EntitySoundSyncHelper.Send`, the world-sound and dream
  sound receive gates.
- **Hard-night worms came at half rate.** The party worm loop picked one exposed player per
  5-second tick, so two exposed players each met half of vanilla's worms. It also only took over
  if a client was already connected when the world loaded; a client joining later got the
  vanilla loop plus a coin flip that moved half the host's worms to the client. Every living,
  unwarded player now gets its own worm each tick, and the host's loop is the party one from
  the start (vanilla's own tick while nobody else is there). `HardNightPartySpawn`.
- **Hideout night monsters were split between hideouts.** The night's count of each monster
  (how many may be out at once) was one for the whole party, so players home in two different
  hideouts each met about half. Each occupied hideout now has its own count at vanilla's pace;
  a monster that dies or leaves frees its own hideout's slot. Players in the same hideout share
  it, as one player would. `NightHideoutQuota`, `NightSpawnFlagPatch`.
- **Scripted spawns "around the player" went to the wrong player.** A scene step spawning a
  creature around the player body went around the host, or on a coin flip around some far
  peer. It now goes around the player the scene is for (the host's own scenes keep the host).
  `SpawnCharacterAroundRedirectPatch`.
- **The Shadows perk.** Vanilla's perk works through the night event `CEvent_shadowsX4` (needs
  the perk, 80% chance), whose scene calls `tryToSpawnShadow`. Its requirements read the host's
  body only, so a client with the perk never got its waves, and the host's copy of a peer's
  scene spawned a wave around the host. The mod also added a darkness-triggered wave in co-op
  that vanilla never has (it read `Player.updateVars`' empty branch as the perk's trigger and
  missed the night event). Now a location event's player requirements (perk, health, attackers)
  are checked per player: the event is eligible when a player in a world location meets them,
  and its scene plays only where they are met. The wave is the scene player's own (a client's
  replay asks the host for it); the host no longer rejects that request for light, hard night
  or perk, which vanilla's `tryToSpawnShadow` never checks. The darkness wave is removed.
  `HostLocationEventPlayerRequirementsPatch`, `NightEventAnchor.AnchorPasses`,
  `CoopStoryPolicy.IsWorldPlayerFunction`, `HandleNightShadowSpawnRequest`.
- Removed dead redirects for `CharacterSpawner.spawnForestSpirit` (vanilla never calls it).

Kept on purpose (co-op needs them): no world pause while others play; another player's thought
lines and hints show only on that player's screen; examine description pools are one shared
deck, so each line is seen once by the party.

---

## 0.8.171 — Desync check false alarms from the story run

On top of 0.8.170. Protocol **45** unchanged. The full story run (new world, chapter 1 crawl
through every location and its pads, save and rejoin, chapter 2, chapter 2 crawl, rejoin) ran on
0.8.170 in an omabox: chapter 2 came out the same map on both (the client's own build was
replaced by the host's, player id kept), the hideout unlock no longer differed, and the client's
slow-load path stayed clean. What the check still reported were its own misreadings:

**Fixed**
- An NPC state nobody has touched (alive, no standing) is made when the NPC first loads on a
  machine, so the side that had been near it listed it and the other did not
  ("chickenLady: host=dead=0|rep=0 client=<none>", "Doctor_act2", "snail"). It now reads as none,
  as an unset flag does.
- A culled (switched off) creature's own inventory was listed as a loose container on the side
  that had it switched off ("Villager_infected1b_ch2"): the body check now looks through inactive
  parents.
- Creatures switched off on both machines were listed by the host only (a swamp camp's villagers,
  inactive on both at night, read "host=alive client=<none>"). Switched off on both is now the
  same; switched off on one side still shows.
- A ground pickup that had woken and was then culled on the host (the host culls away from its own
  player) read as missing there; pickups are compared culled or not.

**Tooling**
- `leavepad` (back to the world from a pad), pilot scripts use absolute paths so they run inside an
  omabox, and `story-run.sh` waits for the client's chapter 2 re-send before comparing the maps.

## 0.8.170 — Chapter 2 is the host's map; story crawl findings

On top of 0.8.169. Protocol **45** (`Handshake` gains `WorldGeneratedLocally`). Found by a new
unattended story run: a new world, both players walking every NPC dialogue at every location (every
option, a new branch at each decision, the items an NPC asks for handed over), save and rejoin,
then chapter 2.

**Fixed**
- **Chapter 2 was a different map on the client.** A chapter start shares the chapter save before
  the chapter exists (vanilla makes the new chapter's map at random the first time that save
  loads), so the host and every client each built their own chapter 2: hideout 5, the locations,
  all in other places (host 85 locations, client 87). The client now notes that its chapter world
  was built on its own machine; its reconnect handshake says so and the host re-sends its real
  chapter world through the existing chapter resync, which the client loads (about 20 s more).
  The client keeps its player id across that reload (it came back as a new player, p3 to p5).
  Files: `ChapterProgressionPatches.cs` (`ClientChapterWorld`), `PlayerMessages.cs`,
  `LanNetworkManager.WorldIdentity.cs`, `LanNetworkManager.SteamPeers.cs`,
  `WorldSaveShareService.SlotCommit.cs`, `ChapterSessionResume.cs`.
- **A client who lit a hideout's oven never got that hideout unlocked** (the porter's routes,
  `player_unlockedHideout_2/3`; desync check: host=1 client=none). The oven is lit when the
  dialogue closes, inside the last board, where shared flags wait for the host's replay; the host
  does not replay the lighting (it lights its copy from the oven message, which does not fan flags
  out). The lighting now writes the unlock on the speaker, and its flag sync carries it to the
  others. File: `HideoutUpgradePatch.cs`.
- **A slow client join was cut short.** At 45 s the client forced its loading flag off "as a
  failed load" while the world was still loading (a busy machine, a Wine window presenting once a
  second); the rest of the load ran with the flag off ("UniqueIDDict does not contain key" errors,
  the fresh character's home oven not found). The flag is now cleared only when the save load
  actually failed (vanilla's "ERROR WHEN LOADING DYNAMIC AND STATIC SAVE", or an exception from
  `SaveManager`). File: `ChapterSessionResume.cs`.

**Tooling**
- Pilot commands: `locs`, `npcs`, `talk`/`talkall` (walks dialogues), `talkreset`, `give`, `save`,
  `chapter N`, `pads`/`padexits` (location pad doors in and out), `vsync 0` (a Wine window on a
  hidden workspace otherwise runs at one frame a second). Scripts `scripts/pilot/story-soak.sh`
  (the crawl) and `story-run.sh` (new world, chapter 1 crawl, rejoin, chapter 2, crawl, rejoin).

## 0.8.169 — One morning trader, at the fullest hideout

On top of 0.8.168. Protocol **44** unchanged.

**Fixed**
- Players greeting the dawn in two hideouts each got a night trader: the host's hideout had
  vanilla's, and the mod's per-hideout morning spawned another at every peer's hideout, two copies
  of one man. The morning visitor (the night trader, or the Wolfman on his mornings) now comes to
  one hideout only: the one with the most living players; a tie goes to the host's hideout, else
  to the hideout of the lowest player id. If that is not the host's, vanilla's trader (or wolf) is
  moved there, not destroyed, so vanilla's reputation popup still finds him. Every surviving
  player at a hideout still gets the morning standing for that hideout (the others walk over to
  trade). Files: `MorningRepPatch.cs` (`HostAwayMorning`), `CoopPolicy.NpcNight.cs`
  (`MorningVisitorPolicy`, test added), `HOW_COOP_WORKS.md`.

## 0.8.168 — Desync check: an object on a grid line is one object

On top of 0.8.167. Protocol **44** unchanged. The fourth soak (new world, four nights on 0.8.167:
both at home, split between two hideouts, both in the second, the musician's house and home)
showed no missing scene pieces on the client any more and no night state differences. Its only
report was this.

**Fixed**
- The desync check keys objects on a half-unit grid; a wardrobe standing right on a grid line was
  keyed one step apart on the two machines and reported as missing on both sides, then resolved
  a check later. A host-only and a client-only entry of the same name one step apart that agree
  are now one object. File: `DesyncEntries.cs` (test added).

**Night soak totals (0.8.165 to 0.8.168)**
- 24 nights on four new worlds, players together, split between hideouts, in non-hideout
  locations and out in the forest, with a host death, the banshee's pull and morning walk-outs.
  Remaining logged errors are vanilla's own for nights spent outside a hideout (no event spots
  there) and creatures outside any location.

---

## 0.8.167 — Night scene pieces stay with their players and are found where each machine put them

On top of 0.8.166. Protocol **44** (`GameEventsFired` gains a trailing `ScenePiece` flag; both
installs run the same DLL). From a third soak (new world, five nights on 0.8.166: none of
0.8.165's or 0.8.166's problems came back). Not playtested yet.

**Fixed**
- **Pieces a night scene spawns still went to every client.** 0.8.166 sent a scene copy's later
  fires only to the peers who replayed it, but the pieces its steps spawn sit elsewhere (the
  knocking visitor at a door, the run-away order at the location): those still went to everyone,
  and a client in another hideout searched for them a minute. Pieces are now filed with their
  scene's peers as they spawn. Files: `ScriptedSpawnSync.cs`, `NightEventAnchorPatches.cs`.
- **A client missed its own copy of a scene piece.** A scene moves to its player; the host
  places its copy by that player's synced stand-in, the client by its own body, a few dozen units
  apart, and the client's search for the piece reached 8 units (goreFloor, its check and its
  removal never ran there). A scene piece's fire is flagged, and the client looks for it within
  the hideout (150 units). Files: `WorldMessages.cs`, `GameEventsFiredPatch.cs`,
  `GameEventNetHandlers.Apply.cs`.

---

## 0.8.166 — Second night soak: a hideout's scene stays with its players, creature event pieces on clients, sprung traps kept

On top of 0.8.165. Protocol **44** unchanged (`EntitySpawn` paths may start with `res:`, a
prefab under the Resources root; both installs run the same DLL). Found by a second unattended
soak: a new world, ten nights over six spots (both in the first hideout, split between two
hideouts, both in the second, the musician's house and home, the host in the forest, both at the
Wolfman's camp, home and the hunter's house, both far hideouts, home and the forest), on 0.8.165.
Its first two nights were clean on both sides. Not playtested yet.

**Fixed**
- **A client replaying the host's night scene gave orders to creatures.** The scene's "go for
  the window / barricade / into the house" steps ran on the client's copy, found none of its own
  creatures ("Invalid (or 0 returned) gameObjects ... Type character"), or gave a creature copy a
  second set of orders. The host runs the creatures; the replay now skips those steps
  (aggressiveness, wake-up, behaviour, add/remove activities). File: `EventCoroutineScope.cs`.
- **A hideout's scene fired at every client.** A scene copy's later fires (its timers and
  triggers: knocking, the vortex, gore) went to all clients; those in another hideout had no copy
  and searched for it a minute ("no GameEvents near ... dropped"). They now go only to the peers
  who replayed that scene. Files: `NightEventAnchorPatches.cs`, `GameEventsFiredPatch.cs`.
- **Creature event pieces never reached clients.** A creature's own scripted step spawning an
  event object (the banshee's attack spawns Banshee_attack_event_01: a sound, a run-away order,
  more spawns) ran on the host only; a client's copy runs none of its own events, so the host's
  fire of the piece found nothing and the client missed its sound. The host now sends the spawn
  (`res:events/subevents/...` on `EntitySpawn`), and its fire lands on the client's copy. Files:
  `ScriptedSpawnSync.cs`, `LocationEntityTrapNetHandlers.cs`.
- **A trap the host sprang far away stayed armed on a client.** A trap's state for a location
  the client had not loaded waited 30 s, then was dropped: a bear trap sprung at the home hideout
  while the client spent the night elsewhere was still armed there afterwards. A trap's latest
  state now waits until its location loads (one entry per trap). File: `TrapNetworkId.cs`.
- **A client's dying creature became a corpse mid-animation.** The client added the corpse item
  as the death clip started; vanilla does it when the clip ends. The rest of the clip ran its
  frame events as an item's: the banshee's death-scream frame tried to play a clip "MeleeAttack1"
  it does not have. It now waits for the clip (at most 10 s). File:
  `ClientEntityInterpolationService.cs`.

**Seen, vanilla**
- A night spent outside a hideout (the musician's house, the hunter's house, the Wolfman's camp)
  logs "Invalid (or 0 returned) gameObjects ... Type waypoint/door/item" and "No activity target":
  vanilla plays a night's scenes in any location the player is in, and those places lack the
  hideouts' event spots. The same as single player there.

---

## 0.8.165 — Night soak fixes: the right night on clients, creatures of a peer's hideout, the morning a host death ended

On top of 0.8.164. Protocol **44** (`ScenarioEventFired` carries the scenario's name). Found by an
unattended night soak (new world, prologue skipped, both players invulnerable, five nights with
the players together, split between hideouts, and out in the forest). Not playtested yet.

**Fixed**
- **Clients ran another night's events.** Night scenarios share their `nightId` (Night_h1_1,
  Night_h1_2 and the chapter's other nights all carry 0), and the client looked a fired event up
  by that id: the first scenario with it, Night_h1_1, replaced the night the host sent at dusk.
  From the second night on the client played the wrong event lists (index out of range), never
  built the scenes the host's events played in (no knocking visitor, no vortex, no gore, dozens of
  "no GameEvents near" per night), and its night state never matched. The event now names its
  scenario; the host's resend guard goes by scenario too. Files: `SyncMessages.cs`,
  `ScenarioRandomEventSyncPatches.cs`, `NightNetHandlers.Scenario.cs`.
- **Night creatures of a peer's hideout went for the host's.** Vanilla gives a spawned
  creature its door, window or barricade from `Player.Instance`'s location, and "closest to the
  player" measures from the host. A scene the host played in a client's hideout got a target
  across the map, or none with the host out in the forest ("Player location not found for
  spawned character", "No activity target"), and the creature idled outside. Targets now come
  from the player the scene plays for (the acting player, else the nearest living player in a
  world location). File: `NightSceneTargetPatches.cs` (new).
- **The host dying in the morning ended it for everyone.** Vanilla `Player.resetState` (death,
  respawn) ends the morning; with a client still in its hideout that ended the freeze and the
  trader's visit for it. It now stays while the hideout is occupied, as when the host walks out.
- **A creature's loot could land in a nearby chest.** The host rolls a night spawn's inventory as
  it spawns, before the creature reaches the client; the client then fell back to the nearest
  container by position. It now waits for the creature (a story NPC the client never maps still
  gets it where it stands). File: `ContainerPendingNetHandlers.cs`.
- Host out in the forest: vanilla's location requirement logged "No location for player found"
  before the co-op answer replaced it; it no longer runs there.
- The desync check counted a night-dead host as alive (vanilla's death screen puts the body back
  up for spectating).
- A night event's GameEvents not loaded yet (a just-spawned mushroom settles its height the next
  frame) warned on every retry; it now warns only when it gives up.

**Tooling**
- Pilot: `oven` (make the nearest oven home, as the first talk's Cook does), `time` sends the clock
  at once, `status` shows the scenario, current event and home oven. `scripts/pilot/night-soak.sh`
  (many nights over spots of the world) and `godkeep.sh`.

**Seen, vanilla**
- "Dodaję aktywność do postaci…" (a scene adds a task to a creature still running one) comes from
  vanilla `GameEvent` and was logged with one scene per hideout; left as is.

---

## 0.8.164 — Hideout oven offers Cook only after its first examine

On top of 0.8.163. Protocol **43** unchanged. Not playtested yet.

**Fixed**
- A new world's hideout oven offered Cook from the start, for every player. Vanilla's new-game
  oven only glows (`setAsDefaultExpMachine`, `isOn` false) until its first examine lights it and
  offers Cook. On join the client's hideout-oven bulk "relit" its own home whenever it was unlit,
  which turned the unexamined oven fully on (`enable`), and sent that to the host and every peer.
  The bulk no longer relights; the client's own backup puts back a lit home only if this player
  had lit it (new backup field `HomeOvenLit`, vanilla's `examinedExpMachine`; older backups go by
  the per-player `player_firstOvenInteraction` flag). An unexamined home comes back glowing, as
  in vanilla, and the join's stand-in oven no longer keeps glowing when the home is elsewhere.
  Files: `BulkSyncNetHandlers.cs`, `ClientStateBackup*.cs`, `HideoutUpgradePatch.cs`.
- A world played on 0.8.163 or earlier may already have that oven saved as lit; start a new
  world to check.

---

## 0.8.163 — Night mushrooms and dawn on clients, shared trading, leaving a shared talk, one trader stock

On top of 0.8.162. Protocol **43** unchanged (the dialogue mirror gains a message kind,
`KindPromote`, inside `DialogMirror` (160); both installs run the same DLL). Built, 434 unit
tests pass, deployed for the next playtest; not playtested yet.

**Fixed (from the night playtest logs, 0.8.160)**
- **Night mushrooms in the hideout were missing on the client.** The night scene's spawn step
  picks a random waypoint; a client replaying the scene skips item spawns as the host's, and the
  host never sent the one it spawned. The host now sends every world object a scripted event
  spawns (prefab path from the save's own prefab table, exact spot) on `EntitySpawn`; the client
  files it under its location like vanilla, and a client replay skips any such spawn
  (`ScriptedSpawnSync`).
- **No dawn on the client.** The client never ran the edges of vanilla `refreshTime`: no white
  fade, no "Day N" screen, last night's scenario never cleared, its night events never reset at
  nightfall nor ended on the clock. The client now runs them from the host's clock
  (`WorldWeatherTimeNetHandlers.ClientNightCycle`): nightfall resets tonight's scenario, a night
  event ends on the shared clock, dawn plays vanilla's white fade (sound faded, inputs held,
  invulnerable for its seconds, the host's karma step mirrored) and the "Day N" screen, and the
  new day clears the scenario. The host flushes its clock at the dawn minute and at nightfall
  (ordered ahead of the night's events); a late joiner's scenario starts from a clean night.
- **The trader's stock was not shared.** The morning Wolf at the hideout is spawned by the host
  and reaches a client a moment after his stock; that stock went to the closest other "wolfman"
  (another camp) and the hideout one kept a roll of its own, so a client's purchase came out of a
  different shelf than the host's. Trader stock now only lands on the trader at the host's spot
  (400 units) and waits until he is there. The host's trader restocks also went out as plain
  container fills by position, and a client that did not find the trader filled the nearest
  container: Piotrek's death bag got his 22-item shop. Trader rolls now go only as trader stock.
- **Closing a trade lost its story trigger** (the host's container lookup never matched a shop):
  the host now finds the trader's shop and replays `onCloseContainer`.
- **Joining a talk took two tries.** The listener's view opened from a coroutine on the dialogue
  window, inactive until a talk opens; Unity dropped it ("Coroutine couldn't be started …
  DialogueWindow is inactive"). The mirror's waits now run on the controller.
- **The night "door opens by itself" event opened a different door on the client** (its replay
  picked its own random door). The client's night-scene replay no longer runs door open/close
  steps; the host's door change arrives as `DoorOpen`.
- **The new day's weather roll** (rain today, fog) only reached clients when rain or fog next
  changed; the host now sends it on the new day.
- **A hidden game window ran at one frame a second** (vsync on a window the desktop does not
  present; Hyprland/XWayland here), and Unity's frame-time cap then slowed that game to a third
  of real time. On the host that was the whole world while the player was on the client window.
  In a session an unfocused game now runs with vsync off at a 60 fps cap; focus back restores the
  player's setting (`BackgroundFrameRate`).

**Changed (requested)**
- **Trading together.** While the talking player is in the trade screen, a player who joins the
  talk (or is listening when the trade opens) gets its own trading screen on the same trader:
  its own standing, its own bag, the host's stock. Closing it leaves Trade / Exit options; the
  mirrored Trade option works too. The host accepts trades from anyone in the talk; goods in a
  player's own buy tray are held back from its copy of the stock when a snapshot arrives
  mid-trade, the host resends its stock after closing its own trade with goods in the tray, and
  the first accepted trade wins (a later one for the same goods is refused and reversed).
- **Trader standing is personal for the Wolf ("wolfman") and Piotrek**, as for the night and
  morning traders. The Doctor stays shared (his chapter 2 story state is his reputation). The
  host replaying a peer's dialogue no longer moves its own standing, and a scripted standing
  change goes to the player the event is about.
- **Leaving a shared talk.** A listener leaves with Esc or the mirrored Exit option without
  touching the talk. When the talking player leaves while others listen, the talk is handed to
  the next of them (lock, its own main options, or its own trade goes on), the rest listen to
  it, and the NPC's close events wait for the last player in the talk.

**Diagnostics** (0.8.161, `[LightStack]`) ship in this build too.

**Not changed:** vanilla's own "Uwaga, próbuję usunąć element boarda" (a typing line finishing
in the frame its board was replaced) is vanilla's.

---

## 0.8.162 — Party map board: shared map pins with stamps, colours, labels and pings

On top of 0.8.161. **Protocol 42 → 43.** Product **0.8.161 → 0.8.162**. Built and unit-tested,
**not deployed, not playtested**.

The co-op map pins are rebuilt as one board for the whole party, owned by the host.

**New for players** (world map, any chapter; not the prologue map):

- **Stamps.** Six pin types drawn in the map's own ink: Mark (X), Danger (skull), Loot
  (rifle), Shelter (house in a ring), Camp (campfire), Grave (cross). The mouse wheel picks
  the stamp; a faint preview follows the cursor. The wheel over a pin restyles it.
- **Owner colours.** Each pin sits on a glow in its owner's colour (8 colours, no red, which
  vanilla uses for "you are here" and death bags). A player keeps the same colour.
- **Hover card.** Stamp, label, who placed it ("you" for your own) and the in-game day.
- **Labels.** Double-click a pin to write a label (up to 40 characters), shown under it.
  Enter saves, Esc cancels the label without closing the map.
- **Pings.** Middle click (or Shift + right click) pulses a red crosshair on everyone's map
  for 25 s; a player with the map closed gets "<name> pinged the map" over their head.
- **Chat lines.** `[Map] <name> marked Danger "label" on the map.` / `[Map] <name> pinged the map.`
- **Kept with the world.** The host's board is saved next to its save
  (`prof{n}/dwmp_map_pins.json`, stamped with the campaign id) and comes back when the world
  is hosted again. Every player keeps a copy, so a player promoted by host migration (or later
  hosting that copy) still has the pins. Right click on any pin erases it for everyone.
- A strip at the bottom of the open map lists the controls.

**Bugs fixed in the old pins:**

- **Pins landed off the click on screens above 1080p** (and drifted further toward the map's
  edges). The click was turned into a map spot without the map's resolution scale. It now goes
  through the icon holder's own transform.
- **Erasing a pin could erase the wrong one on the other machines.** Removal matched the first
  pin within a radius computed from the receiver's map scale, so with two pins close together a
  peer erased its neighbour; the drawn sprites were also matched by list index and fell out of
  step when a sprite failed to build (and could throw). Pins now have host-assigned ids.
- **Pins doubled after a rejoin.** Pins were filed under the player id; a rejoin with a new id
  re-sent the backup's pins under it while the old copies stayed under the old id (on the host
  and in every late-join sync). Owners are now a hash of the install key; the host refuses a pin
  of the same owner on the same spot.
- **Every peer's pins were the same green** (a tint over dark ink), so with 3+ players nobody
  could tell whose was whose.
- **The host's pins were lost when the game closed** (memory only); a client's lived in its
  backup only.
- **Pins were not filed per map.** A pin placed on the prologue map and the world map's pins were
  drawn on each other's map, and chapter 1 pins on chapter 2's. Pins are world-map only now and
  filed per chapter.
- New pins draw above vanilla's icons and carry no collider: vanilla's hover takes the topmost map
  element, so a pin with one would hide the name of a location under it. Hover over pins is by
  screen distance (nearest pin wins).
- **Typing in chat (or a pin label) opened the map or the journal.** `InputScript` reads the Map
  and Journal keys whatever `Core.forbidInputs` says. `Map.tryOpenClose` and
  `Journal.tryOpenClose` are skipped while an overlay holds the keyboard (`UiInputLock`).

**Wire / code:**

- `MapPinRequest` (166, client→host: place, erase, restyle, label, ping; the sender is the
  socket's) and `MapPinEvent` (167, host-only: put, remove, ping with the full pin).
  `MapStateSync` carries the whole board (pins replace the old position/owner arrays).
  `MapMarker` (68) and `MapMarkerRemove` (72) retired.
- `Domains/Map/Pins/`: `MapPinBoard` (board, host authority, seeding), `MapPinStore` (world file,
  written 2 s after a change: by the host into the world it loaded the board from, by a client only
  while it plays in the host's world), `MapPinView` (drawing and input), `MapPinOverlay` (labels,
  hover card, controls strip, label field). The marker code in `MultiplayerMapManager` is gone;
  discoveries are unchanged.
- Client backups no longer store pins. An older backup's pins are sent to the host's board once
  on restore (as Mark pins).
- `ChatHud.AddSystemLine` for mod lines; `UiInputLockMapKeyPatch` / `UiInputLockJournalKeyPatch`.
- Docs: `HOW_COOP_WORKS.md` (pins moved from personal to shared, new "Party map board" section),
  `PLAYTEST.md` (board checklist), `COOP_COVERAGE.md`, `ARCHITECTURE.md` example.

## 0.8.161 — Stacked light check (diagnostic)

On top of 0.8.160. **Protocol 42 (unchanged).** Product **0.8.160 → 0.8.161**. Built, not deployed,
not playtested.

- **Why:** a playtest suspicion that every light looks too bright. A code review found no global
  cause: both installs run gamma 0.7 and light quality 1, the mod never touches gamma, brightness
  or the light camera, and the client's ambient colour follows the host clock through vanilla's
  `updateAmbientLight` with the same night table. Darkwood draws each `Light2D` as its own mesh,
  added into the light buffer, so a spot only gets brighter when two lights draw there: a light
  spawned twice, or two lights sharing one mesh (a live light copied by `Instantiate`).
- **New:** `Logging/StackedLightProbe.cs`, ticked from the network update on both peers while in
  the world. Every 5 s it scans the drawn lights. Each new stack is logged once as a warning:
  `[LightStack] <role> N lights drawn on one spot` (same position, radius, cone and facing) or
  `N lights share one mesh`, with each light's path, radius, intensity, colour and position.
  Every 60 s it logs a `[LightStack]` summary of total and drawn lights at the player's position,
  so host and client counts in the same place can be compared. Reset on session end.

## 0.8.160 — Gas bomb gas on clients, health bars after a client hit, no hover labels behind the pause menu, single gunshots, whole stand-up

On top of 0.8.159. **Protocol 42 (unchanged).** Product **0.8.159 → 0.8.160**. Built and
unit-tested; **runtime is not playtested**.

- **A player getting up (respawn after death, loading a save) no longer starts halfway on the
  other screens.** Vanilla starts the get-up clip (`Sleep`, also `GetUpFromBed`) while the
  player is still loading or respawning. A client sends no state until its load ends, so the
  watcher's first packet was already mid-clip, and the proxy jumped to the sender's frame: the
  stand-up started late and cut in the middle. The proxy now plays these play-once clips from
  their first frame at their own rate, and does not restart one it already finished while the
  sender is still on its tail (`SecondPlayerAnimController.IsGetUpClip`).
- **A gunshot sounds once on the other screens, not stacked.** Peers played the shot from
  `PlayerFiredWeapon`, and the shooter also forwarded its own play of the same sound (the
  fire clip's `Attack1Sound` frame plays the firearm's attack sound on the player) as
  `PlayerAudio`, so every pistol shot and its echo tail played twice, slightly apart. The
  shooter no longer forwards its held firearm's attack sound
  (`LocalAudioService.IsCurrentFirearmShotSound`, checked in `PlayerAudioHelper.ForwardSound`
  and the parentless `Play` forward); `PlayerFiredWeapon` is the one path, every shot, at the
  shot's pose. Both directions (host shooting, client shooting) use the same send filter.
- **A gas bomb leaves its gas on the clients too, whoever throws it.** The gas cloud
  (`Gas_flamable`) is the bomb's `ThrownItem.prefabToSpawnOnLand`, spawned by its landing after
  the `Explodes` activation, not one of the `Explodes` secondaries. Every peer's copy of a throw
  is muted (no land spawn, so no second cloud), and the host sends only the secondaries spawned
  inside `Explodes.onActivate`, so the host's cloud never went out and the clients had none.
  The host now sends a thrown item's land spawn as `ExplosionSpawnObject` while its landing runs
  (`ThrownItemLandScope`); the late-join gas state already carried such clouds.
- **Hover labels no longer show through the pause menu while the world runs.** Vanilla's menu
  pauses the game, and `Player.Update` reads no input while paused; in co-op the world keeps
  going until every player is in the menu, so the input step went on under the menu and the
  cursor still picked world objects. While the pause menu is open the player's input step
  (`FindInput` / `FindInputController`) is skipped as vanilla's pause would, the walk input is
  zeroed (a key held as the menu opened kept walking), and the object under the cursor and the
  aim are let go as the menu opens (`PauseMenuNoInputPatch`). Movement and animation still run.
- **A joiner waiting on the title no longer gets the host's container fills.** From the logs: a
  client connected while the host generated a new world received every container the host's
  world generation and first location activations filled (326 `ContainerStateSync`), with no
  world to put them in: 328 "no inventory at" warnings, each after a full inventory scan. The
  host now sends container fills only to peers playing in the world
  (`LanNetworkManager.SendToPeersInWorld`: sent in-world PlayerState, not loading), and a
  client drops one while it has no world (`ClientCanApplyWorldBulk`). The world package carries
  the contents, and opening a container asks the host for its state as before.
- **Health and durability bars on a client show the host's numbers after a hit.** Vanilla
  shows the enemy health bar inside `getHit`, after the damage. A client's hit lands on the
  host, so the client shows the bar from its own swing or shot with the health before the hit,
  and the host's result was then written straight into the fields with no refresh: a dog the
  client killed kept a full bar (the dead-body path returned before the refresh), a door or
  barricade the client broke kept its pre-hit "almost broken" bar. Every place a peer's health
  lands now refreshes the bar if it shows that object (`HealthBarRefresh.IfShowing`): creature
  health and death, doors, barricades, windows, destructible items, chains.
- **The host no longer gets a health bar for a client's hit.** The host applies a client's
  attack as a player hit (`byPlayer`), and vanilla shows the bar to whoever hit, so the bar
  popped up on the host's screen for a creature the client hit far away. The host now applies
  it without the bar, and refreshes one it already shows for that creature.
- Checked from the logs, no change: one pistol shot killing a dog is vanilla (pistol damage 20,
  dog health 20), and dogs eating a corpse that ignore the shooter are vanilla too (very hungry
  eating dogs skip their enemy-near reaction).
- **MelonLoader: `LiteNetLib.dll` goes to `UserLibs/`, not `Mods/`.** MelonLoader 0.7 loads
  `UserLibs/` before any melon and treats `Mods/` as the melon folder; a plain library there
  happened to load (it is scanned and added to the resolver) but is not where the loader
  expects one. The Melon deploy target copies it to `UserLibs/` and removes a stale
  `Mods/LiteNetLib.dll`; the MelonLoader release zip now mirrors the game folder
  (`Mods/DarkwoodMP.Mod.dll`, `UserLibs/LiteNetLib.dll`) so it extracts straight into
  `Darkwood/`; README and INSTALL.txt say so (`pack-release.sh`/`.ps1`). The BepInEx zip is
  unchanged.
- **Log and config hints name the running loader.** The startup banner listed BepInEx paths
  with a Melon aside, and the F2 footer showed only the config file name. Both now show the
  build's own paths (`ModLog.LoaderLogPath` / `ConfigPathHint`: `BepInEx/LogOutput.log` and
  `BepInEx/config/…cfg`, or `MelonLoader/Latest.log` and `UserData/YokWare/…cfg`). Removed
  the unused `HostLogHint`/`ClientLogHint` constants.
- Checked, no change: the Melon build needs no counterpart to BepInEx's `DiskLogFlush`.
  MelonLoader 0.7 writes `Latest.log` from its bootstrap through a `StreamWriter` with
  `AutoFlush = true` (every line on disk at once), and the managed logger hands each line
  over synchronously, so the tail before a quit or crash is not lost.
- The MelonLoader build is still not playtested; one dual-box session on it (both boxes) is
  needed before calling it as stable as BepInEx.

## 0.8.159 — Bear traps, rebuilt furniture, no prologue chat line

On top of 0.8.158. **Protocol 42 (unchanged).** Product **0.8.158 → 0.8.159**. Built and
unit-tested; **runtime is not playtested**. Merged to `main` with 0.8.147–0.8.158, which the
long 0.8.158 playtest confirmed.

Playtest of 0.8.158 (long dual-box session): no exceptions on either side. Fixed from the logs
and the user's report:

- **"Day 1 waits: N player(s) still in the prologue." no longer shows in the top-right chat.**
  This was the "dev info on players" text. The day-1 hold still works; the start and end of
  the wait go to the log only, on host and client (`PersonalPrologue.HoldDayOne`,
  `ClientNoteHold`). Its helper `ChatHud.AddLocalSystem` had no other caller and is removed.
- **A client stepping into a bear trap: the host's sprung state reaches everyone again.** The
  host settles a client's `TrapTriggered` and sends the sprung trap back, but that send ran
  while the message was being applied, where `SendTrapState` sends nothing (no `[TrapSync]`
  line in the host log). The other players never saw the trap sprung by this path, and the
  client never heard back. The host now broadcasts it directly
  (`WorldObjectSendNetHandlers.BroadcastTrapState`).
- **A trap a client sprang is no longer re-armed under the caught player.** A host scan sent
  before the host had the trigger still said "armed"; the client applied it on arrival and
  opened the trap it was standing in (`[TrapApply] beartrap id=1 … triggered=False` right after
  `Client sent trap triggered`). The client now keeps a trap it sprang until the host's answer
  comes (at most 3 s) and drops older "armed" states for it (`ClientOwnTrapTriggers`).
- **Furniture the host rebuilt is rebuilt on the client too.** A burned wardrobe the client had
  dragged sat a unit or so apart on the two machines; the construct message matched sites
  within 0.75 units only, so the client queued it forever (`[ConstructibleSync] queued (not
  loaded yet)`). Sites now match within 2 units, as damaged items do
  (`LockNetHandlers.ConstructibleMatchRadius`).
- **Log:** `[Trap] host/client: player N trapped` is written when the trapped state changes,
  not with every player state (a dozen lines per second while caught).

Checked in the logs, working: the client's push scrape on the host (starts and stops cleanly
with the push), the joiner's lit oven, molotov fires and burning doors and wardrobes on both
sides, a client's day death and the death bag, the dog chase between both players.

## 0.8.158 — A client's push sounds on the host (the real cause)

On top of 0.8.157. **Protocol 42 (unchanged).** Product **0.8.157 → 0.8.158**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158.

Playtest of 0.8.157: the host still heard a client's push start and fade at once. No exceptions in
either log. 0.8.157's longer hold was not the cause: the host log has only two scrape starts in the
session, one ended by `body-push skip jump d=1.272 Stool` right after it.

- **An ordinary push counted as a jump.** On the host, a client state that moved the body more
  than `BodyPushMaxArmDelta` since the last one was treated as a post-drag jump: no scrape, and
  a running scrape stopped. That limit was 1.25 game units, written as if units were meters (a
  body is about 40 across). A stool pushed at walking pace moves about 1.3 per 0.1 s state, so
  nearly every state of the push stopped the scrape, and the next ones did not start it again.
  The limit is now 30 units per state, past any push, still under a drag hand-off jump
  (`WorldPhysicsSyncService.Apply`).
- **The client's matching safety net** (soft-stop when no moving state for a while) was 0.15 s,
  barely over the 0.1 s state gap, so one late state faded the host's push mid-way. It now uses
  the same 0.3 s hold as the host (`WorldPhysicsSyncService.Interpolation`).

---

## 0.8.157 — A client's push sounds steady on the host; peer steps a little louder

On top of 0.8.156. **Protocol 42 (unchanged).** Product **0.8.156 → 0.8.157**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158.

Reported after the 0.8.156 playtest: when the client pushes something, the host hears the
scrape start and fade out at once. Requested: other players' movement a little louder, still
under its old level.

- **A client's push faded out at once on the host.** Each moving state from the client pusher
  kept the host's scrape alive for `BodyPushSoundHold`, 0.05 s. States come every 0.1 s
  (plus jitter), so the hold ran out between nearly every two states. The host log shows
  `body-push start Stool` / `body-push stop Stool` alternating through each push. Each stop
  faded the scrape, and the post-stop suppress (0.45 s) kept the next starts out. The hold is now
  0.3 s. A real stop still ends it promptly: the pusher's last states are quiet ones (two quiet
  ticks stop it), or the states end and the hold runs out (`WorldPhysicsSyncService`).
- **Peer movement volume 0.75 → 0.85** (`Gameplay.PeerMovementVolume` default; both dual-box
  config files set to 0.85, since existing files keep their old value).

---

## 0.8.156 — Shared events received on the title wait for the world

On top of 0.8.155. **Protocol 42 (unchanged).** Product **0.8.155 → 0.8.156**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158.

Playtest of 0.8.155 (new world, client joined during the host's world generation): no
exceptions in either log, and no bug reported by the playtester.
- The joiner's hideout oven is lit: `fresh character's home oven: exp_machine_oven_01 ...
  lit=True` at the load and again on arrival.
- Stool pushes both ways show steady states, with no failed applies in the sampled lines.
- The chair/stool jump and the quieter peer steps (0.8.154) were not called out by the
  playtester; not yet confirmed by eye.

- **A shared event received on the title was tried against the menu scene.** A `GameEventsFired`
  that arrives before the world is queued, as meant, but the queue flush ran on the title too.
  Every try found nothing (`no GameEvents near ... GainRecipes_med_cottage_tree_01` warned), and
  the event's queue age ran down before the world arrived. The flush now waits until the client is
  in the world (`GameEventNetHandlers.TryFlushPendingGameEvents`).

---

## 0.8.155 — A pushed chair or stool no longer jumps on the watcher's screen

On top of 0.8.154. **Protocol 42 (unchanged).** Product **0.8.154 → 0.8.155**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158.

Reported: on the watcher's screen a pushed chair or stool makes periodic big jumps while it is
being pushed (the pusher's own screen is fine).

- **The "teleport" distance was a fifth of a body.** The watcher's copy follows each state with
  a fixed 0.2 s interpolation that restarts at every state, so it trails the pusher by about
  speed x 0.2. A state farther than `ClientPushSnapDistance` from the copy was set at once as a
  teleport, and that distance was 8 units (a body is about 40 across). A light chair or stool
  shoved at walking pace trails by more than that, so every few states it jumped to the pusher's
  pose. The heavier lamp and wardrobe move slower and stayed under it. The distance is now 300
  units, beyond any push in one state; pad teleports and objects carried across the map (thousands
  of units) still snap (`WorldPhysicsSyncService`).
- **Lost states while the copy trails far.** The receiver finds the object by name near the
  reported spot, its last match within 25 units, or a 15-unit sphere. A full scan runs at most
  every 2 s. A copy trailing past those radii matched nothing, every state failed until the next
  full scan, and the copy stood still, then jumped. The copy a name is already driving now
  matches when its interpolation target is near the reported spot (after the exact-spot match,
  so two identical chairs still do not swap) (`WorldPhysicsSyncService.FindOrSpawnObject`).

---

## 0.8.154 — A joiner's hideout oven is lit (second try); other players' steps a little quieter

On top of 0.8.153. **Protocol 42 (unchanged).** Product **0.8.153 → 0.8.154**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158.

Playtest of 0.8.153 (new world, client joined during the host's world generation):
- The title-screen error flood is gone (client log 1,127 lines, no exceptions; it was 27,016 lines with about 6,000
  NREs).
- The pushed chair still snaps. The logs show it is the `Stool` (collider off-centre like
  `Chair_1`'s), pushed by the client with the host watching, and the host pushing it with the
  client watching. The cause is not found yet; asked which screen shows the snap.
- The oven fix did not work: the client log has `no default oven in the hideout for the fresh
  character` twice.

- **A joiner's starting oven unlit (0.8.153's fix failed).** The lookup required the oven's
  `isDefaultExpMachine`. The hideout a world generates comes from a location preset whose oven
  does not carry that flag (only the hand-built scenes and `exp_machine_oven_01B` set it), so
  both lookups found nothing. Vanilla never relies on the flag for a new game: the save names the
  player's home oven (`Player.SaveState.expMachineId`), and on a fresh world that is the
  hideout's. The fresh character now takes the oven in `WorldGenerator.playerBase`. Before the save
  has named the hideout (at `loadValues2`), it takes the save's own home oven. It is set again
  when the character is placed in the hideout, and the log line now names the hideout and how
  many ovens it holds when none is found (`PersonalProloguePatches`).
- **Other players' movement a little quieter (requested).** New config key
  `Gameplay.PeerMovementVolume` (default **0.75**, 0..1). It scales the stand-in's footsteps,
  clothes rustle, the extra wood/branch step sounds and the sender's torso-clip steps (dodge,
  window-jump landing). Shots, hits, tools and other sounds stay at full volume
  (`WorldProxyEffectNetHandlers.PlayProxyOneShot`, `WorldFxNetHandlers.HandlePlayerAudio`,
  `docs/CONFIG.md`).

---

## 0.8.153 — A pushed chair turns smoothly; a joiner's hideout oven is lit

On top of 0.8.152. **Protocol 42 (unchanged).** Product **0.8.152 → 0.8.153**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158.

Reported after the 0.8.151 playtest: furniture pushing is fine now except the chair, which
still snaps; on a fresh world the oven in the starting hideout was not lit.

- **A pushed chair snapped.** The free-body scan counted a body as moving only when its position
  changed. Furniture turns about Y as it is pushed off-centre, and a chair (mass 2, drag 10,
  directional sprite) turns a lot and visibly. A turn without much travel sent nothing, so the
  other peer's chair kept its old facing and snapped round at the next state or resync. A turn of
  half a degree since the last scan now counts as motion too, and so does the quiet window after
  it (`WorldPhysicsSyncService.ScanPhysicsAround`, `LastRot`).
- **A joiner's starting oven unlit on a fresh world.** A fresh character skips the save's player
  block. In its place `SetNewGameHome` lights the hideout's default oven (vanilla
  `setAsDefaultExpMachine`). It looked for the oven under `WorldGenerator.playerBase`, but the
  save sets that only after the player block (the world generator's state loads later). So
  the lookup found nothing and returned without a word (the log had no `home oven` line), and the
  oven stayed dark. It now finds the world's default oven directly when the hideout is not known
  yet (not one on a prologue pad). It runs again once the fresh character is placed in the hideout
  (`PersonalProloguePatches`, `PersonalPrologue.ArriveFresh`).
- **The "log" text in the top right**: not found in the mod or in the game's on-screen code;
  waiting on a screenshot from the playtester.

---

## 0.8.152 — No world pieces built on the joiner's title; no doubled puddles or splats on a rejoin

On top of 0.8.151. **Protocol 42 (unchanged).** Product **0.8.151 → 0.8.152**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158.

Playtest of 0.8.151 (new world, client joined during the host's world generation): no bug
reported by eye. The logs show a client lamp push applied on the host with its scrape
start/stop; the host pushing after the client is not in this run. The logs did show the
problems below.

- **About 6,000 errors on a joining client while it waited on the title.** While a new world
  generates, every prefab it places goes through `Core.AddPrefab(string)`, and the spawn sync
  sent each one live (87 this run). A client still on the title built them into the menu scene:
  no item database there (`No item type meat`, `InvSlot.createItem` NREs), and their sounds and
  triggers ran without a player (`SoundArea.Update`, `LoopingAudioObject.waitToCheckPlayer`,
  `EventTriggers.OnTriggerEnter` NREs every frame until the load). Those pieces are the world
  itself and reach a joiner in the world package.
  - The host no longer sends spawns while it is generating or loading a world
    (`CoreAddPrefabPhysicsSyncPatches`).
  - A client takes a live spawn, a gas trail or an explosion's spawned object only once it is in
    the world (`LocationEntityTrapNetHandlers`, `CombatFxGasBurnNetHandlers`,
    `CombatFxImpactNetHandlers`), the same gate the journal got in 0.8.150.
- **Doubled gas puddles and infection splats after a rejoin.** The late-join state resends every
  puddle (16 this run) and every infection splat (10). A joiner that loaded the world already
  had them. The duplicate check was a physics search, and that sees only active colliders,
  while the joiner's puddles and splats away from it were culled (inactive). So each one got a
  second copy, a second fire or a second infection trap. The check now also looks through the
  scene, culled objects included (`WorldPhysicsSyncService.HasLiquidInScene`,
  `InfectionSyncHelpers.HasInfectionAt`).

---

## 0.8.151 — The host's push shows on a client that pushed before

On top of 0.8.150. **Protocol 42 (unchanged).** Product **0.8.150 → 0.8.151**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158.

### Fixed

- **After the client dragged or pushed the lamp, the host's push of it did not move it on the
  client.** Three parts of one loop in the free-body (`PhysicsState`) sync:
  - A peer sent motion it did not make. The client's copy following the host's push was seen
    moving by the client's own scan and sent back as the client's push. The host then held the
    lamp kinematic against its own push, and the echo claimed the lamp on the client, which
    then ignored the rest of the host's push. A body still following another player's states
    (until a scan after its last pose lands) is no longer sent.
  - The client's full resend (every 5 s, every body within range) claimed each body for 4 s,
    touched or not. Only bodies the client moved now count, and a client resends only bodies it
    moved itself lately (a lost last state). The host owns the rest, and a lagging client copy
    sent back would pull the host's body to it.
  - The claim after the client's own push lasted 4 s, dropping a host push of the same body for
    that long. It now lasts as long as the push authority's grace (1.25 s), which covers the
    host's echo of the client's push.

---

## 0.8.150 — No journal pages on the title

On top of 0.8.149. **Protocol 42 (unchanged).** Product **0.8.149 → 0.8.150**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158. Found in the 0.8.149 playtest logs.

### Fixed

- **A client waiting on the title threw on the host's journal pages** (`NullReferenceException`
  in `Journal.addJournalEntry`, `Handler for JournalItem from p1 threw`). The host's load adds
  pages before the world is shared; a peer on the title has only the menu's journal. A client
  takes journal pages only once it is in the world (`ClientCanApplyWorldBulk`); the world
  package carries the journal.

### Playtest status (0.8.147 to 0.8.149)

- The world share initialized 214 map pieces before its save (`[MapShare]`), no prologue pad
  traffic, no duplicate despawns. Sound and push fixes: waiting on the user's report.

---

## 0.8.149 — Pushed furniture: no snap, a steady scrape

On top of 0.8.148. **Protocol 42 (unchanged).** Product **0.8.148 → 0.8.149**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158.

### Fixed

- **Furniture another player pushed snapped when the push ended** (client pushing, seen on the
  host; host pushing, seen on the client). Cause: the pusher's stand-in is a physics body and
  still collided with the object here. While the object followed the pusher's states (held
  kinematic), the stand-in sank into it; when the push ended and the object went back to
  physics, it was shoved out of the stand-in. The stand-in now passes through pushable things
  (an `Item` on its own rigidbody; not doors, characters or a throw in flight), set ahead of
  contact every physics step (`RemotePlayerProxy.IgnorePushablesNearby`). Only the pusher's game
  moves the object, as `HOW_COOP_WORKS` now says.
- **The scrape of furniture another player pushed was missing, or its start looped.** The old
  guard for the stand-in touching furniture zeroed the object's velocity and stopped its scrape
  on every physics step of contact. With no native scrape running, it stopped every playing copy
  of that sound id, the remote scrape loop among them, which the next state restarted. The guard
  is gone with the contact.

---

## 0.8.148 — Peer sounds silent at the edge of their range

On top of 0.8.147. **Protocol 42 (unchanged).** Product **0.8.147 → 0.8.148**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158.

### Fixed

- **Another player's sounds broke into sharp, chopped bits near the edge of hearing range**
  (footsteps, shots, throws, anything a peer makes, both ways). Cause: a peer's sound is played
  here with its own 3D falloff (many are 2D in the game, played for their owner only), and that
  falloff was set on the source after `AudioController.Play` had already started it. The audio
  thread mixed the first moments with the prefab's own settings, at full 2D volume. Near the
  peer the sound itself covered that; toward the edge, where the 3D sound is silent, only those
  first moments came through. The falloff now goes on the source inside `AudioObject`'s own
  start, after the game has set the source up (`PeerSpatialPlay`, patches on
  `AudioObject._PlayDelayed` / `_PlayScheduled`). Covers the stand-in's PlayerAudio sounds,
  footsteps and clothes, shots and explosions.
- **A pooled audio source kept a peer's falloff for later sounds.** The game's pool restores only
  its item overrides, so a later local sound on the same source played with the peer's linear
  falloff and ranges. The source's own settings (spatial blend and rolloff curves, rolloff mode,
  distances) now come back when it returns to the pool.

---

## 0.8.147 — Map pieces in the shared world, prologue pads kept to themselves

On top of 0.8.146. **Protocol 42 (unchanged).** Product **0.8.146 → 0.8.147**. Built and
unit-tested; **runtime confirmed** in the long dual-box playtest of 0.8.158. Found in the 0.8.146 playtest logs.

### Fixed

- **A joiner's map lacked pieces of the new world, among them the road by the hideout.** The host's
  discovery of `road_forest_1_7a` found no piece by that name on the client and was dropped after
  300 s. Cause: a new world is shared before vanilla's `Map.initialize`, which runs at the wake-up
  after the opening. That is where every map piece takes its name (a road's from its sprite) and
  joins its map's list, and a save keeps both; a load takes them from the save and never
  initializes again. The package carried 39 road pieces unnamed and unlisted. Before the share's
  save the host now initializes the waiting pieces once each, as the wake-up would; vanilla's
  later `Map.initialize` takes only the ones that start after that (`MapShareInitialize`, log
  `[MapShare] initialized N map piece(s) before the world share save`). Worlds shared before this
  keep the gap on the joiner's copy; start a new world to test.
- **The host's prologue chase reached the client.** The chompers of the prologue's last pad
  come from `CharacterSpawner.spawnCharacterAround`, under the global holder rather than the
  pad, so they got network ids: their states, sounds, corpse loot and despawns went to a
  client in the overworld, which had nothing to apply them to. What stands in a prologue pad's
  slot (the 25000-unit grid vanilla places outside locations on) now counts as the pad's
  (`PersonalPrologue.IsOnProloguePad`).
- **Each creature's removal was announced up to three times.** `removeMe` (which can run twice)
  and `OnDestroy` each sent it; a body is announced once now (`CharacterTracker.MarkDespawnSent`).
- **World generation sent every rolled container to connected peers.** A peer still on the
  title had no world and logged hundreds of misses; the world package carries the contents.
  Rolls during world generation are no longer fanned out.
- The host no longer logs `sent LocationEnter` once a second for its prologue pad, which the
  prologue filter never sends.

---

## 0.8.146 — The joiner's prologue title on black

On top of 0.8.145. **Protocol 42 (unchanged).** Product **0.8.145 → 0.8.146**. Built and
unit-tested. **Playtest: the logs show the arrival held back (`screen stays dark for the opening`); not yet confirmed by eye.**

### Fixed

- **The client's "PROLOGUE" title showed over white noise with the inventory HUD on top.** The
  intro movie itself was fine, and the host's own prologue was fine. Cause: a joiner's
  prologue pad arrives before its opening, the reverse of a new game. The arrival's vanilla
  `OutsideLocations.hideScreen` fires about a second after the pad is in, and it landed on the
  opening `PrologueIntro` had just begun. It faded the black screen out and turned it off,
  unlocked input and showed the cursor. With the world camera off for the movie (as in a new
  game), nothing cleared the frame, so the UI's noise overlay built up into white noise behind
  the title, with the HUD over it. Until the joiner wakes, the arrival now keeps only its audio
  step (`PrologueJoinerArrivalScreenPatch`, log `[Prologue] pad arrival: screen stays dark for
  the opening`), and vanilla `activatePlayer` uncovers the screen as in a new game.
  `PrologueIntro.Play` also stops any fade still running on the black screen, and sets the
  camera's render targets as `tweenLoading` does.

---

## 0.8.145 — Joining a world shared during the host's prologue

On top of 0.8.144. **Protocol 42 (unchanged).** Product **0.8.144 → 0.8.145**. Built and
unit-tested. Playtested up to the client's own prologue: the join load passes (`[Load] saved in
location 'dream_tutorial_00' ...`), the host shares the world once, and the client's prologue
starts. Quitting during the prologue logs no error (the 0.8.144 dream-teardown fix). Not yet
seen: looks side by side after the prologue.

### Fixed

- **The client's join load got stuck with a `NullReferenceException` in `SaveManager.Load`.**
  Playtest: the host started a new game while hosting, and the client received the world and
  loaded it. The load stopped at IL `0x0a9e`, which is
  `spawnedLocations[currentLocationName].enter(force: true)`. Vanilla `ChapterResume` then cleared
  `loadingGame` after 45 s, and the client never got in. Cause: the share's save ran while
  the host stood in its prologue dream pad. A pad is never saved, but vanilla still writes its name
  as the player's current location, so the load called `enter` on nothing. The same save would
  also break the host's own reload. A load now puts a player whose saved location is not in the
  save in the overworld. If the save resumes a dream, that dream places the player itself; if not,
  the player goes back to the spot it left from (`OutsideLocationMissingOnLoadPatch`, log
  `[Load] saved in location ...`).
- **The host sent a new world twice.** The new-world share went out to the waiting client. Then
  the host-ready gate counted that client as still waiting and shared the whole world again, with
  another force save that froze the host. The gate now skips peers that are already loading the
  package (`TickHostWorldShareWhenReady`). Before 0.8.144 this was hidden: the gate never opened
  for a new world.

### Confirmed in playtest (0.8.144)

- A new game while hosting is a co-op world (`[Cosmetic] co-op world (in a session)`). The
  share carries `savcos.dat` (52202 seeds), the client's copy loads as a co-op world, and it reads
  all 52202 seeds.

---

## 0.8.144 — New games hosted from the menu are co-op worlds

On top of 0.8.143. **Protocol 42 (unchanged).** Product **0.8.143 → 0.8.144**. Built and
unit-tested; runtime partly playtested (see 0.8.145).

### Fixed

- **A new game started while hosting was treated as single player.** Playtest: the host
  hosted, started a new game from the menu, and the client joined. The host never logged
  `[Cosmetic] co-op world`. It wrongly warned "Hosting a world loaded before hosting", then shared the
  world without `savcos.dat`, and the client's copy loaded as `single-player world`, so the looks
  did not match. Cause: the new-world hook sat on `Controller.generateChapter`, but a new game from
  the menu loads the chapter scene directly and never calls it. The hook now sits on
  `WorldGenerator.generateWorld`, where every generated world starts, both a new game and a chapter
  change (`CosmeticKeyStoreNewWorldPatch`).
- **Quitting during your own offline prologue logged a `NullReferenceException`**
  (`Dreams.destroyDream` from `DreamSyncManager.ForceLocalDreamCleanup`). The network stop on quit
  ran the session's dream teardown on the player's own offline dream while the game was already
  destroying it. The same teardown would also have ended an offline dream when hosting from the
  pause menu. The network-stop reset (`DreamSyncManager.OnNetworkStopped`) now leaves the world alone
  when the game is quitting or no session was running; session dream statics still clear.

---

## 0.8.143 — Single player stays vanilla

On top of 0.8.142. **Protocol 42 (unchanged).** Product **0.8.142 → 0.8.143**. Built and
unit-tested; **runtime is not playtested**.

### Changed

- **The matching looks of 0.8.140 run in co-op worlds only.** They also ran in a plain single-player
  game, which then no longer rolled its looks as vanilla and gained a `savcos.dat` file. A world is a
  co-op world when it starts in a session (host or client), or when its slot has `savcos.dat` (a
  world hosted before, or a client's copy of the host's world), so it keeps its looks offline
  between sessions. Anything else rolls as vanilla and writes no file (`CosmeticRolls.Active`,
  decided when a world is generated or loaded; log `[Cosmetic] co-op world ...` or
  `[Cosmetic] single-player world ...`).
- **A world loaded before hosting is shared after the host loads it again.** Its looks were rolled
  vanilla's way, and a client cannot copy them. The host share gate (`HostHasShareableWorld`) now
  waits for a co-op world; HOST already opens the load menu, and a host who backs out of it is told
  once (status line and log). Loading any save while hosting makes it a co-op world.
- **A world share always carries the seed file.** A single-player save loaded in a session has no
  `savcos.dat` until its first save; the host writes it before packing the share
  (`CosmeticRolls.EnsureStore`), so the client's copy is a co-op world too.

### Fixed

- **A deleted slot kept its `savcos.dat`.** Vanilla `deleteSave` removes only its own files, so a new
  game in that slot took seeds stored for another world's save ids (and would now count as a co-op
  world). The seed file is deleted with the slot (`SaveManager.deleteSave` postfix).

---

## 0.8.142 — Animation clock dropped

On top of 0.8.141. **Protocol 41 → 42.** Product **0.8.141 → 0.8.142**. Built and unit-tested;
**runtime is not playtested**.

### Removed

- **0.8.141 is rolled back in full.** It added too much always-running sync for a cosmetic gain:
  a ping/pong clock between host and clients, a prefix on every visible `tk2dSpriteAnimator`
  each frame (about 44,000 of them), and clock-timed twitches and replays that ran in single player
  too. Removed: `AnimClock`, `AnimPhase`, `AnimSchedule`, `ClockSync`, `AnimTiming`, the
  `WorldClock` message (165) and the tk2d random-frame seeding. World animations, `AnimationPlay`
  replays and twitches run as vanilla again, so their phase can differ between machines (an
  accepted cosmetic difference). The 0.8.140 work stays: matching looks (`savcos.dat`) and the
  shared examine deck. The highest message ID is 164 (`CosmeticState`) again.

---

## 0.8.141 — Animations run in step on every machine

On top of 0.8.140. **Protocol 40 → 41.** Product **0.8.140 → 0.8.141**. Built and unit-tested;
**runtime is not playtested**.

### Fixed

- **The same fire, tree or twitching body was at a different point of its animation on every
  machine.** Vanilla's `tk2dSpriteAnimator` advances a clip only while it is on screen, from the
  moment that machine created the object, so about 44,000 world animators (trees, grass, water,
  fire, flies, lamps) each ran at their own phase per machine, and the random choices made in
  0.8.140 still played out at different times. Now:
  - **A shared animation clock** (`Sync.AnimClock`): the host's uptime minus the time the world
    stood in a shared pause, held still during one. Clients estimate it by ping/pong (new
    `WorldClock`, 165): half the round trip of the least-queued of the last 8 pings, slewed at 1%
    so jitter never shows, snapped on a jump (host migration). The host also sends it on every
    pause and resume.
  - **Looping world animations take their frame from the clock** (`Sync.AnimPhase`): loop, random
    loop, ping-pong and the looping part of a loop section, at the phase their clip started at
    (frame 0, or the seeded random start frame). An animator coming into view snaps to its frame
    without firing frame events on the way; one already running is pulled toward it at most 30%
    faster or slower. A machine in slow motion (a cutscene) runs as vanilla and is pulled back
    afterwards. Creatures and players are left alone (their animation is the host's, sent with
    them), and so are interface animations.
  - **`AnimationPlay` timing is a function of the clock** (`Sync.AnimSchedule`): replays after a
    random delay (the mimic bodies under the church) fall due at seeded times whose gaps stay within
    vanilla's [min, max], and their one-shot clip runs from that time; twitching (the zombies in the
    Musician's house) shows the clock's twitch frame (out to a seeded frame, back, a rest of about
    1-5 s). Nothing is carried over time, so a late joiner and a reload agree at once.
  - **tk2d random-frame clips** picked their first frame from the global random stream; seeded per
    object and clip, stored with the save like the other seeds.

### Accepted differences

- An animation started by an event (a door, a trap, an explosion) starts on each machine when that
  machine hears of the event, so it is behind by the network delay. Nothing can show an event
  before it has arrived.
- The clock estimate assumes the way to the host and back take equally long (half the round
  trip), the limit of any clock sync without shared hardware time. On an uneven route a client's
  animations can be a few milliseconds off, far under one animation frame.

---

## 0.8.140 — The world looks the same on every machine

On top of 0.8.139. **Protocol 39 → 40.** Product **0.8.139 → 0.8.140**. Built and unit-tested;
**runtime is not playtested**.

### Changed

- **Rule: "the client must feel like the host" wins over cosmetic differences.** `HOW_COOP_WORKS.md`
  "Cosmetic divergence is allowed" became "Cosmetic divergence is a last resort": a client sees
  what the host sees, cosmetics included, unless it really cannot be matched, and each accepted
  difference is written down with why. `COOP_COVERAGE.md` no longer parks cosmetic randomness.

### Fixed

- **Grass, debris, trees, creatures and animations looked different on every machine.** About
  70,000 `SpriteRandomizer`s in the game roll a tint, a flip, a rotation, a height, a sprite or a
  clip from the shared random stream, so each machine rolled its own look (a dog's tint, which way a
  bush faced, which corpse sprite lay there). Even one machine changed: 85% of them roll again on
  every load, and the rest fall back to the plain prefab look after a load. Now every roll runs on
  a seed made from the object's identity and puts the random stream back afterwards, so the same
  object rolls the same look everywhere and every time (`Sync.CosmeticRolls`).
  - The seed comes from what every machine spawning the object live has bit for bit: the
    location's name and placement and the authored path down to the object (names and offsets),
    or the name and world position outside a location.
  - Positions pick up float noise through each save and load (measured on a real save: about 1% of
    objects sit within that noise of any rounding edge), so a seed is not made again from them. Every
    save writes the seed of every roll under a saved object, keyed by that object's save id and the
    names below it, to **`savcos.dat`** next to `sav.dat`; every load reads it, and the world
    download carries it (also into a reused "same as host" slot).
  - A roll runs when vanilla's does (a location loaded live is still at its authored spot then, the
    same on every machine); only a parallax set up while a save object loads waits for that
    object's save id, so it finds its stored seed.
  - A load rolls every randomizer (vanilla skipped the ones not marked `randomizeOnLoad`) and keeps
    a saved object's saved rotation and height (vanilla stacked another height offset on each load
    and re-rotated colliders away from the saved pathfinding graph).
  - A creature or prop that moved since it rolled keeps that key. A client's copy made somewhere
    else (a creature the host spawned, which the client first sees mid-walk; a prop spawned after
    the save) takes the host's key and rolls again: creatures through their entity descriptor,
    props in the late-join bulk (new `CosmeticState`, 164).
  - Runs in single player too, so a world played before hosting already looks the way its clients
    roll it.
- **Random animations ran differently on every machine.** `AnimationPlay` picks a clip, a start
  frame, replay delays and twitch frames at random. Each one now draws from its own stream, seeded
  the same way and stored with the save; its coroutines draw only from that stream.
- **Parallax layers drifted differently** (each layer's ease was a random roll): seeded the same way.
- **Vines turned differently.** `VineSpawner` rotations roll on the spawner's seed.
- **Examine lines from a random pool were per machine.** A client drew its own line, and the host's
  re-run of that examine drew a second, different one, so lines came up again for other players.
  The pool is now one deck: the examiner draws at once and its examine carries the line, the host
  takes that line out of its deck and tells everyone else (`ExamineObject` carries the line, and
  whether the draw refilled the pool), and a late joiner gets every deck in its bulk
  (`Sync.DescriptionDeck`).

### Accepted differences

- Two players drawing from the same examine pool in the same instant can both read the same line
  (the decks agree right after). Ruling it out would make every client's examine text wait for a
  round trip to the host.

---

## 0.8.139 — The pause menu in co-op, and one reward per story hand-in

On top of 0.8.138. **Protocol 38 → 39.** Product **0.8.138 → 0.8.139**. Built and unit-tested;
**runtime is not playtested**.

### Changed

- **The pause menu (Esc) no longer stops the world for one player.** Vanilla's in-game pause menu
  pauses the game. In co-op that froze the whole world for everyone when the host opened it, and
  froze only a client's own game while the world went on around it. Now opening the menu pauses
  nothing by itself: the player in it is protected the way they are in a dialogue or the level-up
  menu (creatures ignore them, nothing hurts them). **When every player has the menu open, the
  whole world pauses** on every machine; the first player to close it resumes at once and the host
  resumes everyone else. A player still joining or loading keeps the world running. The menu music
  keeps playing during the shared pause. Clients report their menu to the host (`PauseMenuState`,
  162), the host decides and tells everyone (`WorldPause`, 163) (`PauseMenuSync`, `MenuShield`,
  `CoopPausePolicy`).

### Fixed

- **A player in the pause menu counted as "not in the game" for the mod.** Vanilla's pause menu is
  the title screen's menu opened over the chapter and sets the same flag (`Core.mainMenu`), and
  about 45 co-op checks read that flag as "on the title screen". With the menu open: a client stopped
  sending its position, effects and menu protection; a host save was skipped by a client (its copy
  and character fell behind the host's); leaving through the pause menu's quit skipped the exit
  backup of the character; a host in the menu accepted any client's "I already have this world"
  claim unchecked; a client in the menu when the host dropped left the session instead of
  taking part in host migration; a player in the menu stopped
  counting as out in the open world, so the shared clock could stop; and the title join flow
  (world request, slot picker, Steam launch lobby) could act in game. Those checks now tell the
  title screen from the pause menu (`GameScreen.AtTitle` / `InPauseMenu`). Checks that are about
  input (F3, F4, chat, push-to-talk) still treat the pause menu as a menu, and a dream waiting to
  start still waits until the host closes it.
- **Two players handing the same story item to two NPCs at once could both get the personal
  reward** (the Wolf's pistol for the egg), even though only the first hand-in counted for the
  story (left as is in 0.8.137). A client ran its own boards, reward included, before the host
  replayed them. Now a client's talk asks the host before it goes on to a board that hands over a
  shared journal item (`DialogHandInClaim`, 161). The host grants the first claim and holds the
  item for that player until the talk hands it over, ends (the client releases it) or 20 seconds
  pass; a second claim, or the host's own talk, gets "Someone already handed that over." The
  wait is one round trip on that board (`DialogHandInArbiter`).

### Docs

- **`PLAYTEST.md` rebuilt.** The checklist had grown into a stack of per-release lists with
  stale items (a refund gap closed in 0.8.127, "sleep" dreams, local-only pilot scripts presented
  as repo tooling). It is now one check list per gameplay area, in the order of
  `HOW_COOP_WORKS.md`, with a short "current release first" section on top, a "both arrows"
  rule (every check also runs host↔client swapped and with a third player), and checks added for
  areas that had none: the prologue, morning away from the host, permadeath wipes, loot sharing,
  the oxygen tank, trade, the workbench, friendly fire off, quest items left by a leaver, the
  ending and the desync checker.

---

## 0.8.138 — Listen in on another player's dialogue

On top of 0.8.137. **Protocol 37 → 38.** Product **0.8.137 → 0.8.138**. Built and unit-tested;
**runtime is not playtested**.

### Added

- **A second player can join a dialogue and watch it.** Talking to an NPC someone else is already
  talking to used to say "Someone is already talking to them…". Now it opens that player's
  dialogue window for you, as they see it: the same portrait (and its changes and their black or
  white fade), the same lines typing out at the same speed (skipped when they skip), the same
  decisions, main options, "show item" list with its icons and greyed-out entries, and the option
  they are pointing at. Only the talking player acts; your mouse, clicks and controller do nothing
  in that window, and **Esc** leaves. When they close the talk, yours closes with it. Joining
  midway shows the screen they are on (finished if their text already is). Choices stay one
  player's, as before: they reach the world from the talking player only.
  - What you see is what their window built (each line's text, place, colour, icon, typing speed),
    not your own copy of the dialogue, so your bag, flags or location never change it, and
    nothing you watch runs on your side (no flags, items, journal pages, trips or dreams; leaving
    fires no "close dialogue" events and does not save).
  - Not shown: the trading screen (you keep their portrait while they trade), the cooking
    (oven) menu after a talk, and the journal page a talk opens (you see the talk wait for it and
    go on when they close it).
  - Two players starting the same talk at once: the one the host turned down is closed as it was
    (it used to keep going while still opening, and could show the NPC's welcome and exit lines)
    and joins the other's talk to listen.
  - The host listening in holds back its replay of other players' talks until it leaves the view
    (its one dialogue window is the view meanwhile).
  - New `DialogMirror` (160): talking player → host for each screen, host → that talk's listeners
    (and a snapshot to a player joining), listener → host to join and leave (`DialogMirror`,
    `DialogMirrorPatches`, `DialogDisplayNextBoardPatch`, `NpcDialogueLockPatches`,
    `DialogNpcLockNetHandlers`).

### Fixed

- **The host's own dialogues stopped autosaving on close after replaying a client's dialogue.** The
  silent close of that replay sets vanilla's "don't save on exit", which vanilla only clears after
  a save. A player's own talk now starts with it cleared.

### Docs

- **New [DarkwoodMP.Mod/docs/HOW_COOP_WORKS.md](DarkwoodMP.Mod/docs/HOW_COOP_WORKS.md)**, linked from the README: one place for
  the co-op design philosophy, the techniques used to adapt single-player systems, who owns what
  (shared, personal, duplicated or scaled), the rules for every gameplay area, what players cannot
  do, and the open and parked items. It names one open gap found while writing it: vanilla's
  in-game Esc pause menu still sets the game speed to zero on that machine in a session (every
  other menu's pause is suppressed).
- `MaxPeerDamage`'s description (config file and `CONFIG.md`) said there is no per-peer rate
  limit; the host has had one since the combat-authority pass (20 hits/s, burst 40; 1000 damage/s,
  burst 3000). Corrected.
- Stale "host migration during a dream is deferred" lines in the README and `COOP_COVERAGE.md`
  now say done in code, not playtested. The new doc is added to the "docs do not restate the
  version" test.
- **README rewritten** for players first: what the mod is and how it works, status, install,
  hosting and joining as the title screen actually labels it (MULTIPLAYER, HOST/JOIN LAN or STEAM,
  CHOOSE SLOT, ENTER WORLD), controls, settings, bug reports, known limits; building, tests and
  dual-box notes follow, then a documentation index. The duplicate "Current ship" line is gone
  (the table carries the version; `ReleaseConsistencyTests` no longer requires the line).

---

## 0.8.137 — Story choices one player at a time, trips and ambushes for every player

On top of 0.8.136. **Protocol 36 → 37.** Product **0.8.136 → 0.8.137**. Built and unit-tested;
**runtime is not playtested**.

### Fixed

- **Two players could hand the same story item to two different NPCs.** Chapter 1's big choices
  are who gets an item: the sister's key goes to the Wolf or the Musician, the egg to the Wolf or
  Piotrek. Both are journal items, and the journal is shared, so both players had the item on
  their "show item" list. Two players talking to the two NPCs at the same time could both hand it
  over, and the host ran both outcomes (both NPCs' flags and world events: the Wolf steals the
  sister *and* the Musician gets the key). Now a dialogue board that takes a shared journal item
  only runs while the item is still there: the speaker goes back to the NPC's main options with
  "Someone already handed that over.", and the host refuses a client's board that arrives after
  someone else's hand-in and tells that client (new `DialogHandInGone` (159)). A board that takes
  several variants at once (the Wolf takes every version of the church box) runs while any of them
  is held. Talking to the *same* NPC was already one player at a time (the dialogue lock); this
  covers two NPCs who want the same thing. Left as is: a client's personal reward (the Wolf's
  pistol for the egg) when both hand-ins land within one network round trip; the story outcome is
  still only the first one. (`DialogHandInArbiter`, `DialogDisplayNextBoardPatch`.)
- **The Wolf's lift to the Doctor's house moved the host, not the client who said yes.** A
  dialogue trip (`transportToOutsideLoc`, and the `returnToWorld` back) was deferred on the
  speaking client like a world outcome, so the client never went; the host, replaying the
  client's board, then ran it and was carried off itself. A dialogue trip now carries its speaker
  like a door into a location does: the client travels, and the host replaying that board stays
  where it is (`DialogPeerTrip`, `DialogClientWorldDeferPatches`, `RemotePadSpawn`).
- **The daytime redneck ambush only ever came for the host.** From day 2 vanilla sends a
  hit-and-run redneck at the player when it is out on the road (not under a roof, not in a
  location). Only the host's body was checked: with the host at home a client walking the forest
  never met him, and with the host on the road the far-player redirect could drop him next to a
  client sitting in a hideout. Any living player who meets vanilla's condition can draw him now,
  one at random (`HostRedneckPartyPatch`).
- `QuestHandoff` (158) now rejects an out-of-range item count as malformed instead of reading it
  as an empty list, like every other list message.

---

## 0.8.136 — A leaving player's story items stay in the world

On top of 0.8.135. **Protocol 35 → 36.** Product **0.8.135 → 0.8.136**. Built and unit-tested;
**runtime is not playtested**.

### Changed

- **Story items no longer leave with a player.** Vanilla's quest items (Piotrek's six car parts,
  the violin, the brother's hat, the musician's card) sit in one player's bag and are handed in by
  whoever carries them. A client that left for good took them along and the quest stalled for
  everyone else. Now, when a client leaves and is not back within 60 seconds (a brief drop and
  reconnect changes nothing), the host drops that player's story items on the ground where they
  stood. If they stood in a house interior, an outside location or a dream, the items drop by the
  host instead (once the host is in the open world). The host keeps a record of each drop per
  player (Steam id, or the LAN install key), written next to the host's save at the moment the
  world is saved, so it always matches the saved world. When the player comes back, a drop still
  lying there is taken off the ground and they keep their own. A drop someone else picked up is
  removed from the returning player's bag (new message `QuestHandoff`, 158, both directions:
  the list from the host, the player's confirmation back after its saved character is updated).
  Oxygen tanks (every player has one) and journal items (shared already: keys, notes, the church
  box, Piotrek's eggs) are not dropped. A host that leaves is not covered: the session ends with
  it, or the host role moves to another player.
  (`Domains/Inventory/QuestItemHandoff.cs`, `PeerItemPresence.CopyOf`, `LanNetworkManager.PeerEvents`.)

---

## 0.8.135 — Oxygen tank for every player

On top of 0.8.134. **Protocol 34 → 35.** Product **0.8.134 → 0.8.135**. Built and unit-tested;
**runtime is not playtested**.

### Changed

- **Every player gets the oxygen tank (reverses the 0.8.134 removal).** Vanilla has one tank: an
  empty one (the Elephants' talk or their body, the mask family's shed body), filled at the hideout 5
  compressor, which fills only its user's. A full tank in the bag is what lets a player dive (the
  mi17 hole, the burned cottage pond, the village cellar passages). With one tank in the party,
  everyone else was stuck at the water whenever its holder was away or offline. Now the host keeps
  the best tank anyone in the party has held this session (empty, then full) and sends it to every
  peer (new host-only message `OxygenTankTier`, 157, also part of the late-join bulk). Each machine
  tops its own bag up to it once: a missing tank is added, empty ones are filled. A backup restore
  that replaces a joiner's bag re-checks it. Nothing is added while the player is dead, dreaming
  or in their prologue; that waits until they are back. A tank dropped or stashed later is not
  handed out again. The old sync's wrong item names (`oxygentank_*`) are gone; this one uses the
  game's `oxygenTank_empty` / `oxygenTank_full`.
  (`Domains/Inventory/OxygenTankParty.cs`, `PeerItemPresence.AnyRemoteHas`.)

---

## 0.8.134 — Scene data pass: every unique location and dream

On top of 0.8.133. **Protocol 33 → 34.** Product **0.8.133 → 0.8.134**. Found by walking every
unique location, border scene, dream and epilogue part in the vanilla scene export (component
census, every scripted event and trigger, every step aimed at the player) against the mod, not by
a report. Built and unit-tested; **runtime is not playtested**.

### What the pass covered

Chapter 1 (hideouts 1-3, the village and its well, cellar and brother's house, church ruins and
both church undergrounds, the bunker and its underground, the doctor's house, pig sheds, Piotrek,
the hunter, burned houses, the musician's house and hideout, the cottage trailer wedding, the train
wreck and the doctor's trap, the Wolfman's hideout and border gates), chapter 2 (hideout 5, the
swamp lake, junkyard, mask family, mushroom granny, the snail and its cottage, the radio tower and
oneChance underground, the tree village gate and its cellar, the Mi-17, the villagers' quarry, the
Wolfman's arena, the road home and the doctor's camps), the dreams (acid, bunker underground,
church ruins, doctor 1 and 2, grave meadow, home, oneChance, village cellar) and the epilogue
parts. Scripted steps, trigger types and requirement types the scenes use were each checked
against the mod's host/replay rules; the names the mod looks up were checked against the data.

### Fixed

- **Oxygen tanks: a sync that never worked, removed.** The mod gave every player an empty tank when
  one picked one up and converted every player's tanks when anyone used the hideout 5 compressor.
  It looked for `oxygentank_empty` / `oxygentank_full`; the game's items are `oxygenTank_empty` /
  `oxygenTank_full` (case-sensitive), so none of it ever ran, and copying a unique item to every
  player is against the shared-world rule anyway. The compressor needs nothing extra: its convert is
  the user's own event step (`addOrRemoveInvItem` on the player), which the host runs for a client's
  use and replays for that player only. The compressor's events also lost their exemption from the
  client one-shot rule (they now run like any other event). Messages 70 and 71 retired
  (`CompressorSyncPatches.cs` deleted, `GameEventsFiredPatch`, dispatch, `JournalNetHandlers`).
- **A body the host destroyed stayed on clients, frozen in its last pose.** Only `Character.removeMe`
  told peers; vanilla destroys creatures and NPCs directly in many places: a story step replacing a
  character (the Wolfman at the doctor's house is swapped for another body when he dies), the morning trader and the porter when a hideout empties, the
  trader when the talking tree's burning ends, old corpses cleared on entry, the spawner's despawns.
  The host now sends the despawn whenever a tracked character is destroyed during play (not during a
  scene load, a save load or world generation) (`CharacterDestroyPatch`).
- **A client walking out of a hideout destroyed its copy of the morning trader (and the porter).**
  Vanilla's location exit despawns them; on a client those are the host's bodies. The wolf already
  had this guard; the trader and porter despawns are now host-only as well, and the host's despawn
  reaches clients through the change above (`TraderDespawnClientPatch`, `PorterDespawnClientPatch`).
- **Entering the doctor's house turned a client's clock to the middle of the night until the next
  time sync.** Its repeatable on-enter event sets the hour; the client ran its own copy. A client
  never tweens the clock now; the host's run (it activates the house for the entering player)
  moves the shared clock as before (`GameEventFireScopePatch`).
- **dream_home (a level-up dream) left a dreamer in the dream's clothes after waking.** The dream's
  opening changes the dreamer's clothes and gets them out of bed, and its exits change them back
  (or take them off through the hole), each on "the player". The opening ran only for the host's
  body, and an exit only for the player who used it, so the host or a client woke up dressed for the
  dream. A party dream's opening (its pad's on-spawn, on-enter and on-exit events) and its endings
  (events with an end-dream step), plus everything they fire, are now every dreamer's: their steps
  on the player body run for each dreamer. Same for the epilogue room's clothes and the bunker
  dream's "in the dream" flag (`PartyDreamScene`, `GameEventPersonalActorPatch`).
- **The Wolfman's arena took the table leg from one player only.** Killing the Wolfman or walking out
  victorious drains "the player's" table leg; only the killer or the first one out lost theirs, and
  a teammate kept a full one. Every player still in the arena loses it now (`WolfArena`).

### Checked and left as they are

- The village cellar dream's closing corridor moves as each body enters its trigger, on every
  machine for every body; peers see the same entries, so it stays in step. Worth watching in a
  playtest with players far apart in that corridor.
- "Has item" requirements stay a party check (any player holding it), the existing design that
  keeps unique items (keys, the full oxygen tank for diving) from soft-locking a 3-player party.

## 0.8.133 — Decompile audit pass: traps, night, enemies, players, lights, items, explosions, quests and death

Branch `dev-entity-sync-remaster`, on top of 0.8.132. **Protocol 32 → 33.** Product
**0.8.132 → 0.8.133**. Found by reading the mod against the vanilla decompile, not by
a report. Built and unit-tested; **runtime is not playtested**.

### Every player plays their own prologue

- **The prologue was the host's, shared.** The host played the opening movie and the two
  prologue dreams (`dream_tutorial_00`, `_01`) as party dreams; every client was held on
  the host's movie, pulled into the host's dreams and woke when the host did. A newcomer
  joining mid-prologue landed in the middle of someone else's prologue, and one joining
  later never had one. Now each player has their own, as in single player:
  - **The host's own prologue** (a new game) runs vanilla and stays connected. Its
    prologue dreams are not party dreams (no session, no entry freeze, joins are not
    refused during them), and what it does on its prologue pads is not sent: its
    cutscenes, items, journal pages, dialogue, pickups and location announcements while
    in the prologue, the creatures and GameEvents under its pads. A client's level-up
    dream waits for it, as for a dead host (`host_prologue`, `DreamRetry`).
  - **A joiner new to the world** (chapter 1, the host did not skip the prologue, and this
    machine has no character of this player in the campaign) plays it after the world
    download, still offline (join phase 2), so every co-op patch stays out of the way and
    its creatures, items and dreams run as in single player. Its own opening movie, the
    prologue dreams, then it wakes in the hideout and reconnects (phase 3) as usual. It
    starts with a new-game character: the save's player block is the host's, and a new
    joiner used to load as a copy of the host (level, skills, recipes, bag, home oven).
  - **A new joiner when the host skipped the prologue** (or a later chapter) also starts
    fresh, placed in the hideout with the chapter's starting pack and level, as vanilla's
    skip does.
  - **Returning players** load as before. A small marker next to the character snapshot
    records that the player has a character in the campaign, so one who left right after
    the prologue (no snapshot yet: nothing worth one) does not replay it.
- **Day 1 waits for everyone.** On a world whose first morning has not begun, the host's
  clock holds at 05:00 while anyone is still in the prologue: the host, or a joiner sent
  the world for one (until it comes back, or 45 minutes). The host and clients get a
  line when it starts waiting and when day 1 begins (`TimeSync.PrologueHold`). A player
  who joins later plays the prologue without holding anyone and arrives at the current
  time.
- **Journal pages a joiner's prologue wrote** reach the shared journal: once back in the
  session it sends them (the join bulk only adds the host's pages to the joiner's).
- Wire (protocol 33): `WorldSaveBegin.PrologueOffered`, `TimeSync.PrologueHold`,
  `PlayerEffectSync` `InPrologue` bit; `CutsceneSync` actions 4/5 (shared opening movie)
  retired. Files: `Domains/World/PersonalPrologue.cs`, `Domains/World/PrologueIntro.cs`
  (was `PrologueSyncPatches.cs`), `Domains/World/Patches/PersonalProloguePatches.cs`; the
  shared-prologue paths in `CutsceneNetHandlers`, session handlers, the late-join steps,
  `DreamSession.IsFirstPlayTutorial` and the tutorial special cases in dream cleanup and
  story end are gone.
- Tested with the pilot (new game in an empty slot, client joining during the host's
  prologue): both prologues side by side; client first (it waited in the hideout with the
  clock held) and host first (held at 05:00, "Everyone is here — day 1 begins" on the
  client's return); skipped prologue; rejoin of a known player. Prologue content was
  stepped through with dream-end commands, not played; the dreams' own scripted endings
  were not exercised. The desync check found nothing after both prologues.
- Found on the way and fixed: a joiner's loaded save names the host's prologue pad with no
  object behind it (pads are not saved); the joiner's prologue drops it and spawns its own.
  The movie starts only once the pad's arrival is done and keeps input locked (the arrival
  unlocked it: the player could walk under the movie and could not skip it), the main
  camera is off under it as in a new game, and the wake-up's white screen gets vanilla's
  fade (startDreaming's), which in a new game runs after the movie.
- Desync check: a broken door's health and trader copies only one machine has woken are no
  longer differences (trader stock keyed by name and spot).
- Pilot: `newgame:N` / `newgameskip:N` (a new game in an empty slot), `prologue`,
  `skipmovie`, `dreamend <outcome>` (refused during a cutscene), `padcycle`, `ui`.

### The prologue, played for real (pilot run through its scripted endings)

The host walked its prologue by its own triggers (the doctor's dog, the forest path, the
dead body that ends the first dream, the second dream's opening cutscene, then a monster
hit that ends it), the joiner ended its own through the house exit; a code audit of the
host's sends during its prologue ran alongside. Found and fixed:

- **A joiner back before the host finished its prologue sat in the afternoon.** A new
  game's clock reads 600 until the host's own prologue ends (vanilla sets the morning only
  then; the first dream even tweens it to night), and that is what the host shared, with no
  day-1 wait shown since "day 1 not begun" read the same placeholder. While the host is in
  its prologue the world's time is now the prologue's wake-up (05) and day 1 counts as not
  begun. A host alone in its prologue gets no "day 1 waits" line (nobody else is waiting).
- **The host lost the hideout's opening journal pages and recipes.** The joiner's
  stand-in walking into the hideout fired the hideout's one-shot lesson
  (`GainRecipes_med_cottage_tree_01`: recipes, journal pages) on the host while the host
  was still dreaming: its pages were dated "in a dream" (vanilla dates a page by whether the
  local player dreams) and cleared at the host's wake, and the event, removed once fired,
  was gone when the host walked in. A world event that runs here while this player is in a
  dream of its own now writes world pages (`GameEventWorldPageScope`), and a lesson (an
  event of recipes, messages and journal pages) is served once to each player, like the
  one-shot moves and hints, and stays in the world for the others (vanilla removes it once
  fired; kept also on a joiner between the world download and its reconnect, so its copy
  stays the host's world). The host walking into a volume a stand-in already occupies now
  gets such an event too (vanilla fires an area only for the first body in), and a client
  that had the lesson already (the joiner fires it offline on waking) skips the host's
  replay of it instead of searching for a removed object for a minute ("no GameEvents
  near … GainRecipes", 20 lines).
- **Journal pages carry whether they are a dream's** (`JournalItem.InDream`). A receiver
  used its own state: pages a peer found in the world while this player dreamed alone
  (the host in its prologue) were cleared at this player's wake (the desync check showed the
  host missing four pages the joiner had). A dream page from a dream this player is not in
  is not added, and the join bulk and the desync check leave dream pages out (and dream
  places in the journal's places list: each player walked their own prologue).
- **The host's prologue leaked to clients:** the drag of a pad object (it could pull a
  same-named overworld object onto the pad), NPC reputation changes by name, prefab spawns,
  burning creatures, pad doors and traps, pad creatures given ids clients do not know,
  pad physics bodies, and, when someone joined during it, the pad's location, locks,
  doors, drops, traps and fired GameEvents in the late-join bulk (a reconnecting client
  logged "no GameEvents near" for the pad's events dozens of times). What happens on a
  prologue pad now stays there, also in the seconds after its end while vanilla frees the
  pad (`PersonalPrologue.IsOnProloguePad` follows the pad object, not the prologue flag,
  and while the pad's scene loads, the location around an object). Pad creatures get no
  entity id at all (`CharacterTracker`), so nothing keyed by id (sounds, burning, despawn,
  container contents) can carry them; the periodic physics, door, generator and trap scans
  skip pad objects; the late-join bulk skips them in every step (locations, locks,
  interactives, constructions, world lights, generators, doors, barricades, traps and the
  trap ledger, gas, infection, fire, stations, chains, shadow armour, traders, fired
  GameEvents, which also stopped counting the host's prologue as a dream); live sends of
  door closes, trap springs, lights, generators, constructions, switches, padlocks,
  barricades (the prologue teaches barricading), trap disarms and fire on a pad stay home;
  the host's drops and thrown flares in the prologue get no co-op id; pickups there skip
  the co-op claim log. Entity burn sends also refused id 0 only by accident (`< 0`).
- **The host's prologue pack counted as a party item** for world triggers that need an
  item in someone's pack, and the host's recorded entry kept listing it after the
  prologue. The local player's live pack is the only count for it now, and none in the
  prologue.
- **No saves in the prologue** (manual or a client's save request): vanilla never saves
  there, and such a save loads as a broken prologue.
- **Fresh characters start as vanilla's chapter start**, not empty-handed: the chapter's
  starting pack and level (`ChapterPreset.initInventory`, its player level). The joiner's
  prologue keeps that pack through it, as vanilla's does, instead of the host's pack from
  the save.
- **Joiner bookkeeping:**
  - Leaving a join half-way and then loading a single-player save or hosting skipped that
    load's home oven and dream state (the "fresh character" flag outlived the join). Any
    other load now ends what a join left behind, and the patch checks the join is still
    the one loading.
  - A player who finished the prologue but left before a character snapshot existed loaded
    the host's character on return (the marker counted as a character). Now it comes back
    fresh in the hideout without replaying the prologue.
  - A prologue whose pad never came up gave up but left the dream prepared: the pad
    arriving later would start it online. Giving up cancels it and puts the player in the
    hideout.
  - Prologue pages a join never got to send no longer go to the next host joined.
  - The host waits on day 1 only for joiners that got the whole world (a download cut short
    held the day for 45 minutes), and stops waiting for one that came back with another
    world.
- **A join woke inactive NPCs on the host:** the late-join NPC visuals lookup used the
  dialogue apply's finder, which activates an inactive match (the night trader by day) and
  warns on a miss. It looks only now (`FindNpcByName(…, lookupOnly: true)`).
- **Trader stock arriving with no trade open logged "refreshReputation skipped"** (vanilla
  reads the open trade's panes): it refreshes only the open trade now.
- Checked in the last pilot run (new game, joiner done first): the joiner home at 05:00 with
  "day 1 waits" until the host woke, both with the chapter start and four recipes, the
  hideout lesson once for each, journal pages equal, no warnings or errors on either side,
  the desync check clean. Not run: a joiner arriving after the host, three players, and a
  prologue walked by hand (the pilot teleports into its triggers and fires its end events).
- Pilot: `events [radius]` lists the GameEvents of the current dream pad (or nearby) with
  their steps, `fire N` fires one as its trigger would, `dlg [N|next]` reads or answers the
  open dialogue, `inv` shows pack, hotbar, experience and recipes; `kill` works offline.

### Remote players' lights (found by the test pilot)

- **The host logged "Mesh.colors is out of bounds" every frame once a client joined** (about
  100 errors a second, each a slow Unity error write). Vanilla `Light2D` keeps its mesh in a
  public, serialized field (`_mesh`), so `Instantiate` of a live object copies the reference:
  the remote player stand-in is a copy of the local player, and its copied lights drew into
  the local player's light meshes. Each light rebuilt its own vertex list and then wrote
  colors sized to it, so one of them always wrote colors that did not fit the vertices the
  other had just set, and both lights could take the other's shape. Worse, a copied light
  that wakes with the shared mesh destroys it (`CreateMeshObject`). The copies now drop the
  reference before they wake and build their own mesh (`Light2DUnshare`, applied to the
  stand-in and to a remote match light copied from a live one). Checked in a pilot run: 9
  light meshes, none shared, no errors.

### Hitches from scene scans

- **The game froze for a moment many times a session: about a third of a second on the host
  when someone joined, then short stutters for several seconds after, a 36 ms stutter every
  10 seconds on the host the whole session, and hitches on clients when an event, a map
  discovery, a door, station, journal pickup, examine, explosion, cutscene, push or porter
  update or a cursor action arrived.** Almost every one was the mod
  searching the whole world for one kind of object (`FindObjectsOfType`, 35-50 ms each in this
  world): the light late-join bulk did about 13 of them in one frame, each phase of the heavy
  bulk did one, the host's trap ledger did one every 10 s, and the net handlers did one whenever
  their cached copy was older than 3 s or the object was not near a collider.
- Those object kinds now have live registries: filled once when the world finishes loading
  (one pass over every behaviour, inactive ones included, so a trader or a door shell that was
  never switched on is still found; it runs in vanilla's last load frame, under the loading
  screen, and logs its time as `[SceneRegistry] world seeded`), then kept by the objects' own
  wake-up (Awake, or OnEnable / Start where vanilla has none), the whole object on Item / Door /
  NPC / Character / thrown item wake (locks, constructibles, examinables, explosives and journal,
  key and quest pickups have no wake-up of their own), Door.lockMe, and every additively loaded
  scene (outside locations and dream pads, inactive parts included; only a full scene load, a
  new world, drops back to the old search until that world is seeded). Destroyed objects drop
  out on their own. Items, doors, windows, NPCs, characters, GameEvents, triggers, chains,
  hideout machines, saws, feeders, lures, shadow armor, fires, padlocks, interactive items, gas,
  infection, death bags, cursor actions, generators, inventories, dialogues, constructibles,
  locks, locations, unique objects, item sounds, cutscene managers, examinables, explosives and
  journal / key / quest pickups read them. Before the world has finished loading (title,
  generation, an epilogue scene) the 3 s cached search is still used. Only the trap id rescan
  on host promotion still searches the scene.
- A pushed or moved object the client could not find near the reported spot was looked up with
  a whole-world Rigidbody search every 2 s. It now takes one wide physics query (50 units, the
  same bound) plus the Item registry for props whose collider is off.
- The host's anim-library fan-out after a story event matched characters with a list lookup
  per character (quadratic in the character count); it uses a dictionary now.
- The trap ledger watches a trap when its trigger is registered and sweeps the registry once
  when hosting starts or a world loads, instead of rescanning every 10 s.
- Map discoveries (late-join bulk and each live discovery) read vanilla's own map element lists
  (each map type's elements and `elementsToInitialize`) instead of searching for MapElements.
  The name index is rebuilt only when those lists change.
- The door / generator trackers' reset on disconnect reads the registries too.
- `SceneRegistry.cs` (new), `WorldQueryHelper.cs`, `TrapLedger.cs`,
  `MultiplayerMapManager.Discoveries.cs`, `BulkSyncNetHandlers.cs`, `EntityTrackers.cs`,
  `WorldPhysicsSyncService.ObjectResolve.cs`, `GameEventAnimLibraryHostFanPatch.cs`,
  `ModRuntime.cs`.

### Creatures switching between players

- **Dogs that saw both players went for the client first and, while closing in, snapped back
  and forth between the host and the client many times a second** (automated run: host and
  client about 70 apart, three dogs). The earlier fix (keep a chased player unless the other is
  clearly nearer) only acted while a creature was already chasing, and it was one of about a
  dozen places that each picked "which player" by their own rule and at their own pace:
  - vanilla's sight check (every 0.5-1 s) makes every seen character the target in turn, so the
    last one in its sight list wins, and turns the creature to listen to each in turn;
  - the mod's own sight code then picked the closest player (twice, by two rules), preferred the
    client when the host was "not yet chasing", and kept the host when it was;
  - a separate host tick every 0.5 s pulled any hostile creature that could see a client up
    close onto that client, whatever it was chasing;
  - an unseen client inside a creature's smell radius counted as seen, the host did not (a dog
    sniffing around the host still "saw" the client behind it);
  - the closer-enemy check (every 2.5-3.5 s) picked the closest of everything, also for
    non-player targets, and `attackPlayer` the nearest body.
  With two players these disagreed on almost every tick.
- **One arbiter now decides which player body a creature targets**
  (`PlayerTargetArbiter`, decision in `PlayerTargetPolicy`). Every player body counts alike (no
  host or client preference, any number of players). A creature acquires the nearest player it
  sees, then keeps that player in every behaviour (approaching, listening, defensive, chasing)
  while the player is alive, visible to AI and seen, or was seen in the last 2 s. It moves to
  another player only when the current one is lost (then at once, as vanilla turns to whatever
  it sees) or on vanilla's own closer-enemy check when the other is under 75% of the distance,
  at most once per 2.5 s. With nobody else in sight a lost target is kept and vanilla's
  `lostEnemy` timing decides. Non-player targets (other creatures, doors, windows, lures) stay
  vanilla's.
  - Sight check: runs after vanilla's; stand-ins vanilla's ray misses are seen by the same range
    and field of view with vanilla's per-sighting effects, the target among players is the
    arbiter's, and "stop and listen" fires once, toward that player, under vanilla's conditions
    (vanilla's per-body listens are held back).
  - Closer-enemy check: vanilla's first-in-list pick again; when that is a player, the arbiter
    says which player.
  - Routed through it: `attackPlayer` (the player the creature is after, else the nearest), the
    sniffer (among the players inside its smell radius), bird areas (the player who walked in,
    the host included), the banshee's victim (kept while that player still sees it), scripted
    activities, the ward and constant-attack checks, the bunker dream spirit (stays on the player
    who triggered it). Hits and bumps turn a creature on that player, as in vanilla.
- **Removed:** the 0.5 s proxy aggro tick, the sight patch's closest-player / host-sticky /
  proxy-preference rules and its duplicate ward and Enemy of the Forest branches (vanilla's
  own handling covers them), smell counted as sight for stand-ins, `PlayerChaseTarget`, and the
  `forceAttackClosestCharacter` postfix. `attackCharacter` on a stand-in no longer refuses
  creatures that are not hostile to players (a deer hit by a client now turns on him as it does
  on the host) and no longer wakes a sleeper and chases at once (vanilla only wakes it). A bump
  into a stand-in is vanilla's collision reaction (`reactToCharacter`), not "chase if hostile".
- **Diagnostic:** `[TargetSwitch]` (AI trace) logs every switch between two player bodies with
  distances, behaviour and which path made it; `[TargetHold]` logs when the arbiter kept a
  creature on its player against vanilla's pick. See `docs/LOGGING.md`.
- Single player and a host without remote players run vanilla untouched.
- `PlayerTargetArbiter.cs` (new), `CoopPolicy.Targeting.cs` (new),
  `HostAIPatches.Perception.CanSee.cs`, `HostAIPatches.Perception.cs`,
  `HostAIPatches.Targeting.cs`, `HostAIPatches.Identity.cs`, `HostDetectionGapPatches.cs`,
  `HostBodyRedirectPatches.cs`, `HostWardScopePatches.cs`, `BirdAreaSyncPatches.cs`,
  `DreamForestSpiritSpawnPatch.cs`, `NightSpawnRedirectPatches.cs`,
  `WorldProxyLifecycleNetHandlers.cs`, `LanNetworkManager.Tick.cs`, `ModRuntime.cs`;
  `PlayerTargetPolicyTests.cs` (25 tests). Built and unit-tested; not yet run in the game.

### Lantern light and client view distance

- **Other players never saw a player's lantern.** In vanilla the lantern does not need to be
  held. While it sits anywhere on the hotbar it widens the player's own light dot
  (`InvItemClass.checkForActiveSwitches` calls `Player.modifyLightDot(lightRadius)`). Taking it
  off the hotbar, or a shadow wave, puts the dot back to its normal 120. The dot has no
  flicker of its own. The sender already follows every `modifyLightDot` call and also checks
  the dot's radius, so the lantern going on or off (hotbar, shadows, a burn-out drain) reaches
  the stand-in. The owner did send this: in the playtest log, the client sent
  `lantern r=450` when the lantern went on the hotbar, and the host built the stand-in's
  lantern light (`remote lantern ON`). That light was made in code
  (`Light2D.Create`), so it was on the Default layer. The light camera draws only the "Light"
  layer (`CamMain.setPlayColors`: `LightCam.cullingMask = 256`), so the light was never
  drawn, for any player. The stand-in's lantern now uses the local player's light dot layer,
  sorting, position and look. The radius, on/off and late join already followed the owner.
  The other lights made in code on a stand-in had the same fault and are fixed too: a held
  item light, the fallback flashlight cone, and the fallback match light. Turning the lantern
  off now removes every logic-light entry for it, because vanilla `Light2D.Start` lists it a
  second time. The `remote lantern ON` line now logs the layer.
  (`PlayerLightFxAmbientNetHandlers.cs`, `PlayerLightFxApplyNetHandlers.cs`,
  `PlayerHeldLightApplyNetHandlers.cs`, `PlayerHeldLightApplyNetHandlers.Flashlight.cs`.)
- **The client could look much farther than the host (still there in the next run).** Vanilla
  moves the camera toward the cursor by the cursor's offset divided by `CamMain.seeDistance`
  (`CamMain.FixedUpdate`), and that distance is set by `PlayerSkills.setfarsight` (5.2, or 3.4
  with the Farsight skill) when the character's skills are initialized: by `PlayerSkills.Start`
  on a new game, by the save's skill load on a load. A client new to the host's world loads it
  as a fresh character, and the mod skips the save's player block (the host's character), skill
  load included, so its skills were never initialized and `seeDistance` kept its default of
  1.5: the camera ran about three and a half times as far toward the cursor as the host's. The
  fresh character now gets vanilla's new-game skill setup (skill prefabs unchosen, then
  `initialize`), which sets the look distance like any new game
  (`PrologueFreshCharacterPatch.InitNewGameSkills`). A returning client's skills were already
  restored through `initialize` from its backup.
- Separately, with `FreeCursorForDualBox` on (the pointer is not confined), the game's cursor
  (`Core.MouseKeyboardCursorPos` / `ControllerCursorPos`: camera look, aim, throws) now stays
  inside the window as the vanilla confine would keep it: the Wine/Proton client kept reading
  the pointer over the other game window, past its own edges (the `[Cursor] pointer outside the
  window` lines in the next run confirmed it). With the option off nothing changes.
  (`CursorConfineFocusGuard.cs`.)

### Doors broken by a client, dragged objects

- **A door the client broke stayed whole for the client (only the host saw it broken).** The
  client's hits went to the host, the host broke the door and sent the result back, and the
  client found the door, but it never broke it. Vanilla `Door.getHit` takes the whole hit off
  the door's health, so a broken door sits below zero (the playtest door went 4, then -4, -12,
  -20). The apply used `MainHealth >= 0` to mean "this event carries door health" (-1 was the
  "no health" value), so every break, with its negative health, was skipped. The client
  logged `[Barr] door destroyed` (the board branch, nothing to do on an unbarricaded door)
  and kept a whole door that it then tried to open, which the host refused (`not opening
  barricaded/destroyed 'Doorway'`). The desync check had flagged the same fault on another
  door (`Doorway` on the host, `Wooden door hp=4` on the client). It happened the same way
  when the host broke a door (the client kept it whole) and on any other client. "No health"
  is now its own value (`BarricadeEventMessage.NoMainHealth`). The door's real health always
  travels, so health at or below zero breaks the door on every peer, and the peer keeps the
  host's health value. A late joiner's snapshot sends a broken door's real health too, never
  above zero. Board-only removals carry no health.
- The client that broke the door also heard and saw it break. Its own hit muted the whole
  apply, break effects included, though the client had only played the hit effects. Only the
  hit effects it already played are muted now. A swing the client's synced health says will
  break the board or the door plays no hit effects, as in vanilla; the break effects come with
  the host's result.
- (`BarricadeNetHandlers.cs`, `BarricadeNetHandlers.Bulk.cs`, `BarricadeSyncPatches.cs`,
  `BarricadeMessages.cs`, `ClientWorldMeleePatches.cs`.)
- **Another player's dragged object moved in steps and turned on its own.** An observer
  teleported its copy to each `DragSync` sample when the packet arrived (about 30 Hz, so it
  stepped and stuttered with network jitter). The host's copy also stayed dynamic, so between
  packets collisions with the dragger's stand-in or the host player turned and pushed it, and
  the next packet snapped it back (the playtest stool was found up to 1.8 units off each
  packet). The dragger's machine now owns the body while it drags, as vanilla hinges it to
  that player. Every observer, the host included, holds its copy kinematic and plays the
  dragger's poses back on a timeline a short delay behind the dragger's clock, like the
  creature timeline: a per-sender clock estimate, a delay of the measured send interval plus
  the arrival jitter, position interpolated between samples, rotation slerped from the
  quaternion (no Euler interpolation, so no wrap or gimbal flips at the lying x=90 pose). The
  copy is posed every frame through its transform and its shadow is moved with it (vanilla
  places the shadow earlier in the frame). Before, it was posed through `Rigidbody.position`,
  which shows only at the next physics step. The sender stamps each sample with the moment the
  pose shows (its last physics step for a non-interpolated body). The release now sends the
  pose the drag ended on, so observers play their copy out to where it really stopped, then
  make it physical again. Until then PhysicsState leaves it alone. Out-of-order samples (a late
  unreliable one after a reliable quiet tick) are dropped by their time. The dragger's own
  body is still never moved by echoes: its own DragSync is ignored, and PhysicsState skips
  claimed bodies. If the dragger goes silent for 2 s, or the host releases a disconnected
  dragger's claim, the copy is released at once. New `[DragTimeline]` start, stats (every 2 s:
  interval, delay, jitter margin, yaw) and end log lines; `[DragSync] … ->` now logs yaw and
  the sample time.
- Wire (protocol 33): `DragSync` gains `SendTime` and `HasPose`.
- (`RemoteDragTimeline.cs` (new), `PlayerInteractNetHandlers.cs`,
  `PlayerInteractNetHandlers.DragSpawn.cs`, `LanNetworkManager.Tick.cs`,
  `LanNetworkManager.Tick.Drag.cs`, `DragClaimPatch.cs`, `WorldMessages.cs`.)

### Other players' and creatures' sounds

- **When another player jumped through a window, others heard the vault but not the landing.**
  The `JumpWindow` torso clip plays `player_jump` (a frame sound, sent like any player sound)
  and then two `FootHitGroundRun` steps, which vanilla plays through `Player.checkFrameTrigger`
  → `playFootHitGround`. Player steps are never sent, because a stand-in plays its legs' steps
  itself. Through a jump (and a dodge, whose clip has a step too) the stand-in's legs are hidden
  and stopped, so those torso steps were lost both ways. Steps from the owner's torso clip now
  go out as PlayerAudio with the owner's own ground sound and volume, and play on the stand-in
  with the same falloff as its leg steps. Leg steps stay local as before. The host relays them
  to other clients. A client's torso step now also alerts the host's creatures (150, or 350 for
  a running step, as vanilla) through `PlayerSound`, as its leg steps already did.
- **When the host opened his inventory, the client heard it as if he had opened his own, dry,
  with no muffle or echo.** The bag sound is `get_item_01_player`, a frame sound of the
  `InventoryGet` clip played on the player's body. A peer played every `get_` / `hide_` id
  (and the held item's get / hide sounds) 2D at the listener, and stripped the reverb and
  low-pass filters. Now every sound of a player plays on that player's stand-in, 3D, parented
  to it, so the game adds its indoor reverb (the stand-in's ground is refreshed first) and the
  wall muffle toward the listener. UI sounds (`UI_*`: slot grab / place / select, menus) stay
  local as before. The mod also played an extra `open_drawer` on the player's own body when
  he opened his personal bag in a live session. Vanilla plays none, so it is gone.
- **With 3+ players, a client being hit was not heard by the other clients, and some hits were
  heard twice.** Vanilla `getHit` decides the hit sound: `player_melee_hit`, `door_hit_metal`
  when the hit is blocked, or `shadow_hit` from `getHitByShadow`. The victim's machine sends it
  as PlayerAudio, and the host relays it to the other clients. A hit dealt by the host or by a
  friendly-fire attacker reaches the victim as `DamagePlayer`, and its `getHit` runs inside the
  network apply scope, which blocks all sends. So that hit was never sent. Instead the host
  played a guessed `player_melee_hit` on the stand-in that only the host heard, always the
  unblocked one. The attacking client played its own guess for friendly fire. And for a
  creature hit the client had already sent, the host played the sound a second time on
  `EnemyHitConfirm`. Now the local player's own `getHit` / `getHitByShadow`
  (`LocalPlayerHitScope`) always sends its hit sound, even inside the apply scope. The three
  guesses are gone. Every hit is heard once by every other peer, the attacker included, on
  the victim's stand-in, and blocked hits sound blocked. The victim still hears its own hit
  locally as in vanilla. Checked for each case: a creature hitting the host, a creature
  hitting a client (its re-created attack), host melee or gun friendly fire on a client,
  client friendly fire on the host or on another client, and the host's shadow sensors on a
  client. Peers play hit sounds sent over PlayerAudio with the game's own range for the id
  instead of an 80-unit cap. Another player's gunshot, already on the stand-in, now refreshes
  the stand-in's ground first, so it gets the indoor reverb.
- **A dog that chased the client and walked away kept barking for the client.** The logs show
  the barks were the dog's vanilla daytime idle call: `CharacterSounds.idle` is `dog_bark`,
  played every 15-30 s whatever the dog is doing (`Character.waitToPlayIdleSound`). The loud
  NPC prefab carries it 1500 units. The client heard it at 880-980 units while the dog walked
  off. The host, about 2000 units away, heard none (its `[AudioCull] dog_bark d≈2000` lines
  repeat every 15-30 s to the end of the session). The loop path was checked against the
  decompile. Only `CharacterSounds` touches the loop object, and every vanilla path that
  starts, swaps or ends it goes through `playIdleLoop` or `destroySounds`, so the intent the
  host sends follows them: chasing → `dog_defensive`, defensive → `dog_aggressive_loop`, idle
  or walking (end of defensive, back to the waypoint, re-enable) → the dog's empty idle loop,
  which stops it, plus death / removal and underwater. On the client, slot 0 stops the live
  loop with vanilla's fade at the next snapshot. The client used to log only loop starts. It
  now also logs stops (`[EntityLoop] Dog → none (stopped dog_aggressive_loop)`), and the host
  logs each intent change with the creature's behaviour (`[EntityLoop] host Dog intent
  dog_aggressive_loop → none behaviour=walking`). The next playtest shows whether any loop
  outlives its creature's AI.
- The dream forward's equip filter is renamed `IsEquipGetHideSound`. It is no longer used for
  playback.
- (`PlayerSoundSyncPatches.cs` (`PlayerTorsoFrameTriggerScope` and `LocalPlayerHitScope` new,
  `PlayerOpenInventorySoundPatch` removed), `ClientSoundPropagationPatches.cs`
  (`ClientTorsoStepAlertPatch` new), `WorldFxNetHandlers.cs`, `WorldProxyEffectNetHandlers.cs`,
  `WorldSendNetHandlers.cs` and `LanNetworkManager.PublicApi.cs` (`SendPlayerAudio` `ownOutcome`),
  `LocalAudioService.cs`, `EntityLoopSync.cs`, `PlayerFXNetHandlers.cs`, `HostCombatPatches.cs`,
  `ClientCombatPatches.cs`, `EnemyAttackNetHandlers.cs`, `DreamAudioPatches.cs`.)

### Molotovs, gas bombs and flares

- **A client's molotov mostly had no blast for the client: no explosion, no boom, only fire
  slowly appearing.** Every peer flies its own copy of a throw. When the host's copy blew up,
  the host sent a remove for it ("left the world"), meant for knives stuck in a creature. That
  remove reached the client's copy while it was still unexploded at the landing spot and
  deleted it (`[ObjectDestroy] destroyed "Molotov" … d=1.0`), so it never exploded. The host
  did not send its own blast for a client's throw, because it expected the thrower's copy to
  send one. When the client's copy did land first, it sent its blast. The host then set off its
  own copy mid-air at that spot inside a network apply, so the molotov's six gasoline puddles
  were never sent and the client got only the spread ignites (as new pour trails). An item that
  destroys itself on landing (molotov, gas bomb, `destroyOnLand`) now sends no remove: each copy
  removes itself as it lands. A player's throw now sends no blast at all. Each copy blows up
  where it lands, with vanilla's look and sound. The host's copy alone deals damage and lays the
  puddles (sent as before). Knives stuck in a creature and items lost in water still send the
  remove. (`ThrownItemCombatDespawnSyncPatch.cs`, `PlayerActionSyncPatches.Combat.cs`,
  `WorldPhysicsSyncService.Thrown.cs`.)
- **Molotov sound doubled (for any thrower).** A peer's copy blew up with its sound and
  explosion prefab. Then the thrower's blast message arrived, found no copy (it was already
  gone) and played the sound and a second `explosion_molotov` again
  (`[ExplosionVisual] no local Explodes … fallback prefab`). On the host a client's blast also
  replayed the boom after the host's copy had exploded. Fixed by the item above: now a peer gets
  no blast message for a player's throw.
- **Fire on a lit pour trail or gas-bomb trail had no flames on clients, with a
  `NullReferenceException` in `Liquid.<startBurning>b__11_0`.** The mod stops a client from
  spawning gasoline trails on its own, and it matched trails by "GasolineTrail" anywhere in
  the name. That also matched the trail's fire, `fire_flames_GasolineTrail` (its
  `Liquid.burnPrefab`). Vanilla spawns that fire up to 0.2 s after lighting, outside the
  network apply, so on a client it was refused: no flames, and vanilla read `.transform` off
  the null. On the host every trail fire was sent out as one more trail
  (`[GasTrailSync] host flushed 1 trails` after each lit trail). Only the trail prefab itself is
  matched now. (`GasolineSyncPatches.cs`.)
- **Fire dying early, or fire the host never had.** Several causes:
  - The mod looked for puddles with a 3D sphere at the reported spot. A puddle is spawned at
    blast height and then drops about 36 units to ground-item height on its own
    (`GasolineTrail.init` and the item's height snap). A lookup at the spawn height missed it.
    The host, adopting a client's ignite at a puddle it had, then laid a new trail there and lit
    that (`[GasIgnite] spawned+ignited trail` followed by `host adopted client ignite`). Lookups
    now search a vertical column, like vanilla's own liquid raycast, and match on the ground plane.
  - The host never invents a puddle for a client's ignite now; it only lights one it has.
  - Clients ran parts of the fire spread themselves and sent the results to the host. These were
    a new puddle catching from a lit neighbour (`Liquid.checkIfWantToBurnMe`) and a muted throw
    copy lighting the gasoline it landed in. The host's own puddles and copy do both and send
    every ignite. A client does neither now. A client still sends its own torch, melee or pour.
  - A client's puddle sent its own `LiquidStopBurning` when vanilla's 20 s timer ran out. Its
    copy could have been lit before the host's, so this put the host's fire, and everyone
    else's, out early. Only the host sends that now; a client's is not applied or relayed.
  - A stop put out the first puddle in a 1.5 sphere, lit or not. Vanilla `stopBurning` also
    deletes the puddle, so a neighbour still burning on the host went out, or an unlit one
    vanished. It now takes the nearest lit puddle.
  - (`WorldPhysicsSyncService.CombatFX.cs`, `CombatFxGasBurnNetHandlers.cs`,
    `GasolineSyncPatches.cs`, `FireSyncPatches.cs`.)
- **A late joiner saw a molotov's puddles as pour trails.** The joiner's gas state sent every
  puddle as `GasTrailSpawn`. A molotov's `Gasoline` puddles now go as themselves
  (`ExplosionSpawnObject`, as they did live), then their ignites. A puddle of the same prefab
  already lying on the spot (a world-placed one) is not spawned twice.
  (`CombatFxGasBurnNetHandlers.cs`, `CombatFxImpactNetHandlers.cs`.)
- **A thrown flare vanished for its thrower when it burned out, while the host still showed
  its body fading.** A flare has two vanilla clocks. One is `Flare` (glow and fade), which
  copies already start at the thrower's age (`FlareClock`). The other is
  `ThrownItem.init → waitToStopBurning`, which after `burnTime` removes the flare's lights and
  the flare (or swaps its sprite). On the thrower that clock starts when the flare is lit in the
  hand; on a copy it started at the copy's spawn, the hand time later (3.2 s in the playtest).
  So the copy showed the flare through the fade and after it. A copy now starts that clock the
  same age in. A late joiner's flare already on the ground never started that clock (vanilla
  `init` lands an on-ground item instead), so it would have stayed forever; it now starts it.
  It also no longer replays the landing (collide sound, AI alert, lighting the gasoline under
  it): it is marked like an item loaded from a save. (`WorldPhysicsSyncService.ThrownSpawn.cs`.)
- **The fallback light the mod adds to a thrown flare with no `Light2D` was never drawn.** It
  was made in code on the Default layer, and the light camera draws only the "Light" layer.
  It now goes on the light layer, as with the stand-in lights in "Lantern light and client view
  distance". No other light in this area is made in code: blasts and fires use the game's
  prefabs. (`WorldPhysicsSyncService.Thrown.cs`.)
- **The bottle's flight sound missing for the client: not found.** The molotov and flare
  prefabs' sound fields are not in the decompile, and the logs do not show which sound this is.
  On a client's own throw, the remove that deleted its unexploded copy (first item) also cut any
  sound attached to it. A new `[ThrowableFacts]` line, logged once per thrown item type,
  records the prefab's flight loop, start sound, landing sound, blast sound and burn-out
  fields, so the next playtest shows which sound it is.

### Creature movement and animation on the client

- **Walking, running and idling creatures froze on the client after one step cycle until their
  clip changed.** Vanilla `processAnims` calls `Play` every frame, and tk2d restarts a finished
  once clip on `Play`, so Walk / Run / Idle / DefensiveLoop (once clips) keep cycling on the
  host. The client only replayed clips with a looping wrap mode. The host now sends whether its
  animator keeps the clip going (new flag), and the client restarts a finished clip every frame
  while the host does; a clip that ends and holds on the host (aim, a reaction, death) holds.
- **Clip frame alignment did nothing.** `SetFrame(.., false)` after `Play` changes only the
  sprite, not tk2d's clip time, so the next frame went back to frame 0. A new clip now starts
  with `PlayFrom` at the host's frame plus the time since it was sampled (wrapped for a looping
  or replayed clip, held on the last frame for a once clip); attacks from `EnemyAttack` too. The
  host sends a frame only when the animator shows the clip it sends, and a finished once clip as
  its last frame (it read one past the end).
- **A creature first seen dead (joining late, walking back to it, an old corpse) played its
  whole death again.** Only a body this client saw alive or down plays its death; any other
  lies on the clip's last frame, the way vanilla `Character.init` puts a dead body down.
- **A creature whose animation library a story event swapped kept its old frames**: clips are
  compared by clip object, not name.
- **Creature copies skipped all of `Character.Update`, its presentation too**: shadows stayed
  where the creature was first seen, legs did not follow the body, a flying bird stayed small
  and on the ground (its altitude only grows in `processAnims`). The client now runs that part
  each frame after placing the body (shadow rotation and ground spot, legs, flier altitude,
  scale, in-flight fade and sky shadow). Flight and diving travel as two flags and the altitude
  follows vanilla's climb / dive rates, so a bird in flight also cannot be hit by the client's
  melee, as on the host. A bird that vanishes in flight is no longer set visible again by every
  snapshot.
- **Animation frame events moved the client's copy**: Teleport, Move, Push, PushRelative, Stop,
  SetMass, and the instant-turn and stop triggers jumped or shoved the body for a frame before
  the host pose put it back. They are the host's now; sounds, particles and shadows stay.
- **A clip finishing on a copy ran vanilla's AI reaction** (`OnAniFinish`): recover after an
  attack, run from a target the copy does not have (an error), end a turn, pause on Aim, which
  froze the creature on the client for good. The copy keeps only the presentation: the
  defensive loop's speed and, when its death clip ends, the corpse (components dropped, lootable
  body set up) without vanilla's NPC save, which asked the host for a save.
- **Short clips were missed** (a turn showed start then end, never its loop): the host logs each
  creature's clip starts (a hook on tk2d `Play`) and a snapshot carries the clip a creature
  started and left since its last send, with its host time; the client shows it at that moment.
- **Creatures popped and stalled while moving.** Five causes:
  - The host sent every 53-67 ms instead of 50 (the timer restarted on the frame it fired), and
    LiteNetLib held each burst up to 15 ms for its send thread. The remainder now carries over and
    the burst is handed to the send thread at once (`TriggerUpdate`).
  - The client drew 75 ms behind the host clock, often less than the gap to the next sample, so
    it ran past the newest one and coasted or stopped. The delay is now each creature's measured
    send interval plus the stream's measured lateness (separate bounds near / far), and the host
    clock estimate is the best packet of the last 4 s, followed at 5% speed instead of jumping.
  - A fast creature looked like a teleport: its new host pose was compared with the pose drawn
    100 ms behind it, so it snapped and froze. Teleports are judged between host samples now
    and drawn as a jump at their moment.
  - Past the newest sample the body coasted at full speed, also out of a client-made hold, then
    jumped back. The coast now slows to a stop within one send interval, never out of a hold or a
    teleport, and new samples blend the difference out over 0.1 s. A body waits for its next
    sample only after three missed intervals (it froze after one lost packet far away).
  - A creature the host stopped sending (resting) was let go after 0.3 s, so the client player
    could push it until the next resync snapped it back. It is held in place while it is driven.
- **Creatures on the 1400 range edge flapped** (state wiped, snapped, sound loop restarted):
  host send range and client interest now leave at 1500 and enter at 1400.
- **Host-spawned creatures were invisible for 1.5 s**, and a save creature could get a phantom
  beside its real body (playtest: a rabbit, 40 s). Save creatures (worldgen roamers, location
  and story characters) have the same save id on every peer: the host now sends it, and the
  client finds its own copy in vanilla's id dictionary, asleep on an inactive grid node too, and
  wakes it at the host pose; a body bound earlier by position or a phantom gives way to it, and
  the position match never takes another creature's save twin. Only a creature with no copy
  gets a phantom, after 0.2 s, or once the client's world or location has loaded.
- Host: the "far band" pass of the snapshot scan never sent anything (its band was the whole
  range) but scanned every creature again; one pass now puts bodies near a player first.
- Host time stamps are seconds since the host started broadcasting, not the process uptime as
  a float (8 ms steps after 18 h); the client keeps its clock the same way.
- Host cost: the player-animation hook looked up the player's legs on every creature's
  every-frame `Play`; snapshot log strings were built with logging off; reaction / death clip
  names were scanned per creature per snapshot; a creature whose only change was its clip frame
  was sent again at full rate (the client plays the clip on by itself now).
- **Wire format (protocol 33):** the entity snapshot gains a second flags byte (animating,
  in flight, diving, pass-through clip follows), the optional pass-through clip and its age, and
  the save id in the descriptor. Host and client must run the same build.
- **New log lines:** client `[EntTimeline]` every 5 s with the perf probe or entity tracing on
  (share of frames drawn between samples, coasting, holding; render delay; gap between a
  creature's samples; batch lateness and jitter margin; clock offset; the creature that waited
  most); host `[Perf]` gains `hostEntTick ms avg/max`; `[ClientMatch] by-save-id`,
  `[ClientPending] save-id`.
- Not changed, on purpose: the clip still travels as its name (an index into the creature's
  animation library would map to a wrong clip whenever the two peers' libraries differ, and the
  name costs a few bytes); the host still sends one batch to every peer (a per-peer set needs
  per-peer change tracking; each client drops what is outside its own range).
- `EntityTimeline.cs`, `WorldMessages.cs`, `EntityStateBroadcastService.cs`,
  `HostEntityClipStartPatch.cs` (new), `PlayerAnimationTriggerPatch.cs`,
  `ClientEntityInterpolationService.cs` / `.Snapshot.cs` / `.Tick.cs` / `.Presentation.cs` /
  `.Pending.cs` / `.Spawn.cs`, `.SaveTwin.cs` and `.Stats.cs` (new), `ClientAIDisablePatches.cs`
  (`ClientCopyAniFinishPatch`, more blocked frame triggers), `DefenderAttackPatches.cs`,
  `EnemyAttackNetHandlers.cs`, `CharacterTracker.cs`, `PlayerPresenceNetHandlers.cs`,
  `LanNetworkManager.Transport.cs`, `CoopPerfProbe.cs`, `GameplayConstants.cs`, `LOGGING.md`,
  `PLAYTEST.md`.

### Creature sounds on the client

- **A dog turned to the client and played its warning animation in silence, then attacked (the
  attack was heard); the host was far away.** The dog's warning growl-bark is not an animation
  sound: vanilla starts it as the creature's `defensive` loop (`setBehaviour` →
  `playIdleLoop`; on the dog that field is `dog_aggressive_loop`), and the client copies the
  loop the host sends in the entity snapshot. The host sent the loop it was itself playing, and
  it had refused to play it: the dog's sounds use the loud NPC prefab, which blends from 2D at
  the source to 3D further out and carries 1500 units, but the mod read only `spatialBlend` (the
  curve's start, 0), took it for a 2D sound, and culled it 690 units from the host player. So no
  loop went out and the client heard nothing. Every creature loop on that prefab was dropped the
  same way whenever the host player was not near, and its one-shots stopped at 650 units.
- The host now sends the loop the creature's AI chose (the last vanilla `playIdleLoop` /
  `destroySounds`, by vanilla's own rules: underwater, underground, the chasing variant),
  whether or not the host could hear it; each client's own range check decides if it plays.
  The client logs each loop it starts on a creature (`[EntityLoop] Dog → dog_aggressive_loop
  playing|culled`, trace, once a second per creature).
- A sound counts as 3D when its source has any 3D share (the spatial-blend curve is read, not
  only its start), and then carries the game's own max distance. The dog's bark, attack and
  loops now carry 1500 instead of 650. The host sends creature sounds within that range of a
  player, capped at the client's 1400 interest range (past it a client's copy is not driven and
  drops the sound), so both sides agree.
- The occasional dog "whimper" was the dog's own idle call (`dog_bark`, every 15-30 s by day),
  sent by the host as before. The client's hit and death sounds now restore the sound-scope flag
  they found instead of clearing it.
- Logs: a creature sound applied on the client now says whether it actually played, with its
  distance and range; a creature sound refused by the distance cull is logged (rate-limited).
- `EntityLoopSync.cs`, `EntitySoundSyncPatches.cs` (`CreatureLoopIntentPatch`,
  `CreatureLoopIntentClearPatch`), `LocalAudioService.cs`, `AudioSuppressionPatch.cs`,
  `WorldFxNetHandlers.cs`, `ClientEntityInterpolationService.cs`.

### Creatures chasing the client

- **A dog chasing the client stutter-chased him: it ran a little, turned on the spot, ran again
  toward where he had been, and was easy to dodge; on the client its movement also looked
  stuttery.** Five causes:
  - **The host's dog never counted the client as seen (the playtest: only the client near the
    dog).** Vanilla's ray often misses the client's stand-in, so the mod senses it itself (sight
    ray to the stand-in, or smell) and adds it to the dog's sight list, but vanilla gives every
    seen character more than a list entry: `canSeeEnemyFar` (and `canSeeEnemyNear` up close), and
    the `lostEnemy` countdown stopped. Without them, a chase refreshes its chase point
    (`lastKnownTargetPosition`) every frame only while `canSeeEnemyFar` is set; otherwise only in
    vanilla's one-second relentless bursts after losing sight. So the dog ran to where the client
    had been, again and again. A sensed stand-in now gets the same consequences a seen player does
    (`HostCanSeeEnemyPatch.ApplySeenStandIns`, now `SenseStandIns`, which also drops the smell
    part: see "Creatures switching between players"), and a trace line (`[AIChase]`, once a second per
    creature chasing a stand-in) logs how far its chase point lags behind the client.
  - **With both players in view, the host's dog kept switching between them.** Vanilla's sight check sets the
    chase target to every character it sees in turn, so the last one in its sight list wins every
    0.5-1 s, and that list follows the physics overlap order, not distance. The mod's "closer
    enemy" check (every 2.5-3.5 s) then picked the nearest. With one player both agree; with the
    host and the client in view they disagreed and the dog re-pathed and turned (`Run` →
    `RotateLeft/Right_Start`) on every flip. Now a creature chasing a player keeps that player
    while they are alive and it still sees or smells them, and moves to another player only when
    that one is clearly nearer (under 75% of the distance). Single player is unchanged.
    `PlayerChaseTarget` and `HostCanSeeEnemyPatch` (`HostAIPatches.Perception.CanSee.cs`),
    `HostCheckForCloserEnemyPatch` (`HostDetectionGapPatches.cs`). Not enough: replaced by one
    arbiter for every path, see "Creatures switching between players".
  - **Clips ran ahead of the body on the client.** The body is drawn 75-150 ms behind the host
    (the timeline), but the host's clip was played as soon as its packet arrived, so the dog turned
    or stopped before its body did and slid. The clip now rides in the timeline sample and plays
    when the drawn pose reaches it. Attacks, hits, going down, death and getting back up still
    play on arrival, with the damage and sounds they belong to, and older clips do not override
    them (`EnemyAttack` and the client's own hit hold the timeline). `EntityTimeline.cs`,
    `ClientEntityInterpolationService.Snapshot.cs` / `.Tick.cs`, `EnemyAttackNetHandlers.cs`,
    `ClientCombatPatches.cs`.
  - **The body only moved at the physics rate.** The drawn pose was handed to
    `Rigidbody.MovePosition`, which on these bodies only moves them at the next physics step
    (100 Hz), so the creature stepped every 2-3 frames. The pose is now written to the transform
    every frame and to the body for collisions and hits, with its velocity kept at zero and
    Rigidbody interpolation off while the client drives it (given back when it stops driving it or
    is promoted to host). `ClientEntityInterpolationService.Tick.cs`,
    `HostMigration.Handoff.Promote.cs`.
  - **A hitch of about 47 ms on the client for each host-spawned creature.** Its pending match
    timed out and searched the whole world for a sleeping save body of that name, which a
    creature the host spawned never has. The search reads the Character scene registry now (no
    scan), and a host-spawned creature's prefab starts loading in the background when it is
    first seen, not inside the spawn frame (the first load was another 20-35 ms).
    `ClientEntityInterpolationService.Spawn.cs` / `.Snapshot.cs`.

### Story thoughts and hints missing (host and client)

- **The character's thought lines ("yesterday I barricaded that window" and the like) and some
  tutorial hints never showed, on host or client.** A scripted event shows them in a later step,
  and the mod decided whether this machine's player should see it by measuring the event object
  against the listener (60 units). A location's event object usually sits away from the volume that
  sets it off, so the player who walked in lost the line too. The rule is now ownership, the same
  one the event's other personal steps use: the thought or hint shows for the player who set the
  event off (the event's actor, carried into every step by `EventCoroutineScope`) and is hidden
  for everyone else. Speech bubbles over characters and objects are the world's and are no longer
  filtered (only the host's re-run of a client's examine hides them, as before).
  `PersonalFlavorHud`, `GameEventFireFlavorSourcePatch` (`ExaminableSyncPatches.cs`).

### Black chompers never multiplied in dreams (party balancing)

The co-op balancing that adds one black chomper per extra player in dreams
(`NamedNpcScaleEnabled` / `NamedNpcAllowlist`, default ChomperBlack) never did anything.
It only ran when a black chomper was spawned from its prefab (`Core.AddPrefab`). The vanilla
scene data shows that no dream spawns one that way: every dream black chomper (the church
ruins, grave meadow, village cellar, doctor dream 2 and the six of the oneChance escape) is
placed in its scene inactive, and a story event shows it (`activateGameObject`,
`gameObject setActive`, or `replaceCharacter` with a target). Now:

- The extras come when the scene's chomper first appears (`Character.Start`, host only, on
  the dream pad), at its spot, outside walls on the dream's walk graph. The one placed
  active in doctor dream 2 gets its extras when the dream starts.
- Extras are put in the dream pad, not loose in the world. Vanilla destroys the pad when the
  dream ends. Loose, the extras would have stayed behind at the pad's far-off spot.
- They copy the original's scene setup (how it reacts to the player, relentless pursuit). When
  a story event or activity sends the original to attack, its idle extras join that fight
  (`HostAttackCharacterPatch` → `NamedNpcScalePatch.OnAttack`). Each one still goes through
  the target arbiter.
- Client: a creature the client has to create for a host body in a dream (a phantom, such as
  an extra) now goes in the dream pad, so it is destroyed with the dream. It used to stay
  behind on the client.

Files: `Domains/Dream/Patches/NamedNpcScalePatch.cs`, `HostAIPatches.Perception.cs`,
`CoreAddPrefabPatches.cs` (old AddPrefab hook removed),
`ClientEntityInterpolationService.Spawn.cs`.

### Hideout oven unlit for a new client character

- **A client new to the world found the first hideout's oven cold, and examining it gave the
  "unlit oven" greeting instead of the oven's first conversation.** Vanilla makes the oven home and
  lights it while a save loads: `Player.SaveState.loadValues2` calls
  `setExperienceMachine(home, doEnable: false)` → `setAsDefaultExpMachine` (light, hum, smoke, lit
  portrait). The mod skips that step for a new client character, because the save holds the host's
  home, and put nothing in its place. The skip now does what vanilla does on a new game: the
  hideout's default oven (`isDefaultExpMachine`, checked against the scene data) becomes the
  joiner's home and is lit. A `[Prologue] fresh character's home oven` line logs it
  (`PrologueFreshCharacterOvenPatch`).
- **Dialogue sync gave every oven the same lit/unlit portrait.** All ovens share the `oven_act1`
  dialogue, and an oven's portrait is its own lit state (`dontGetPortraitTypeFromDialogue`, set
  by `ExperienceMachine.enable/disable`), but the dialogue-tree apply copied the dialogue's
  portrait onto every NPC using it. It now skips NPCs that keep their own portrait, as vanilla
  `NPC.init` does (`DialogTreeSync.ApplyPayload`).

### Found with the vanilla scene data (YAML export audit)

The vanilla scenes and prefabs are now exported with every editor-set field
(`scripts/unity-yaml.py`). An audit checked the mod's assumptions about that data; everything below
was proven against it and the decompile.

**Story events and entrances**
- **A client was moved when another player walked through a gate.** Repeatable area triggers (the
  border gates into the village and the doctor's house, returnToWorld exits, transport volumes; 51 in
  the data) run on each client for the other player's stand-in, and that copy ran the transport on
  the client's own body. The client copy now runs as the walker's event with personal steps left to
  the walker, as the host's copy already did; the walker's thought text no longer shows on others
  either (`EventTriggersProxyPatches`).
- **Scripted steps aimed at the player body happened to every player.** Diving in and out of water,
  fake death, lying down and paused animation (doctor's failed trap, wagon trap), the hatted man's
  scare and being eaten (getHit/moveTo/rotate), the crater ending's fade and rotation. Only six named
  functions counted as personal; now any step targeting the player is the scene owner's, except a
  decompile-checked list of world functions (shadow event, world-event refresh, the story placements)
  (`GameEventPersonalActorPatch`, `CoopStoryPolicy`). The scene owner's client now also takes a
  scripted hit on its own body (skipped on every replay before, `EventCoroutineScope`), and the
  Wolfman's first visit spawns him in the scene owner's hideout, not the host's location
  (`SpawnWolfInCurrentHideoutPatch`).
- **Overworld events with "dream" in their name were dropped.** The church underground entrance
  opening and the priest after the church dream, the doctor-dream aftermath in hideout 5, the
  oneChance dream-end outcomes and the cellar's dream start never reached peers: dream events were
  picked out by a "dream_" substring. They are now picked out by the dream pad itself (the host by
  the event sitting on the loaded pad, clients by its position in the pad's slot), on send, apply,
  the pending queue and late-join bulks (`GameEventsFiredPatch`, `GameEventNetHandlers*`,
  `DreamSyncManager.Session`).
- **Clients could not use chapter-1 entrances** (bunker entrance, village well, church underground,
  tree-village cellar, radio-tower underground). The host only read transport-to-object steps; these
  use transport-to-outside-location with the location name, so the request ended in "could not
  resolve dest". Both are read now, the trigger is picked by vanilla's requirement check (the church
  hatch's two entries hang on the church-dream flags), and an "_enter" action that moves nobody (the
  closed hatch, the dream-home hole) is activated normally for the requester so its message and
  effects show (`CustomCursorActionSyncPatches`, `CursorActionNetHandlers`).

**Flags, trader standing, dialogue**
- **A joiner's standing with the night trader was replaced by the host's on every join**, and talking
  to a trader pushed that player's standing to everyone. The fallback check used `NightTrader` /
  `TheThree`; the game's names are `nightTrader`, `theThree` and `soldier_underground` (also a
  night trader). Names fixed, case-insensitive; dialogue-tree sync no longer carries trader standing
  (`CoopPolicy`, `DialogTreeSync`).
- **Help popups and night outcome were shared world flags.** The map, active-skill,
  secondary-attack and drained-reloadable popups, the first oven talk, and survived / died during the
  night: a client never got its own popups and the trader greeted everyone by the host's night. These
  flags (`PerPlayerFlagPolicy`) now stay local in live sync, join bulk, desync check and the host's
  replay of a peer's dialogue or event; they are kept in the client's character backup and start off
  for a fresh character. Clients set `player_survivedNight` at their own dawn unless they died, and
  only the player who closed the trader has its night flags cleared.
- **Joining overwrote the joiner's own location flags** (`player_atDoctorHouse` hides talk options,
  `player_enteringRoadToHomeFromRadioTower` picks the entry spawn). The join flag bulk now skips
  per-player flags on both ends (`FlagNetHandlers`).
- **Two players could not talk to two ovens or traders at once.** The dialogue lock and the host's
  replay were keyed on the NPC's name, which is not unique (10 ovens, 24 doctors, 8 musicians, every
  hideout's morning trader): players at different hideouts blocked each other and the host could
  replay a client's choices on the wrong NPC. The lock and dialogue-outcome messages now carry the
  NPC's position and world (wire change to `DialogNpcLock` and `DialogOutcomeSync`) (`NpcDialogueLock`,
  `DialogOutcome*NetHandlers`, `TradeCommit`).

**World objects and creatures**
- **Mimic corpses (24 in chapter 1) sprang only for the player who opened them**; others still saw
  an armed corpse and could be poisoned again. The world-trap test knew bear, chain and mutated traps
  plus name fragments; it now reads the trap's own setup, so mimics count and repeating hazards and
  scripted triggers don't. A sprung trap applied on a peer also loses its "Open" action and selection
  as in vanilla (`TrapNetworkId`, `WorldPhysicsSyncService.TrapsDoors`).
- **Another player's lit match looked about four times too bright and too orange.** The light sent
  made-up constants over the Matchstick prefab's light; it now sends the live light
  (`PlayerHeldLightPack/ApplyNetHandlers`).
- **A flare's stick stayed on peers after it vanished for the owner.** The copy aged the glow but
  not the 80-second removal timer (`WaitAndDie`), which now starts at the owner's lit time
  (`FlareClock`).
- **In the trailer cottages door and window events could land on an invisible twin** (each scene
  has an inactive duplicate at the same spot), whose stale state was also sent (re-boarding opened
  windows on join, doors flapping). Lookups prefer the active object and inactive twins send no state
  (`EntityTrackers`, `WorldQueryHelper`, door/barricade bulks).
- **Clients stopped hearing a redneck's pain grunts after the first hit, and its real death yell.**
  The grunt is the same clip as its death line, and any play of that clip was sent as the once-only
  death sound. Only the play made by the creature's death (`die2`) is now (`EntitySoundSyncPatches`).
- **A fleeing dog that summons help kept re-picking its escape route while a client was near.** The
  mod's chase check dropped vanilla's `summonsAfterEscaping` early return (`HostAIPatches.Perception`).
- **Friend / Enemy of the Forest did nothing for a client spotted from afar** (vanilla checks only
  the local player); both checks now use whichever player is the target (`HostDetectionGapPatches`).
- **Bumping into a fleeing animal as a client made it despawn**; bumps now run vanilla's reaction.
- **A client walking while aiming still alerted creatures** and stepped at the wrong volume. Player
  state now carries the aiming flag (new trailer field, both DLLs must match) and steps follow
  vanilla's volumes (`WorldProxyEffectNetHandlers`, `PlayerMessages`).

**Dreams and the prologue**
- **After the church ruins dream a client could get its own extra Wolfman; after the oneChance dream
  the sluice door, rubble and levers stayed shut for clients; a host who died in a won dream fired
  the "failed" event.** Clients ran the outcome's world events as their own, the host never sent the
  repeating ones, and the death downgrade swapped the world events with the rewards. Clients now
  replay the host's outcome on every exit path (the Wolfman spawn stays host-only), and a peer that
  died keeps the party's world events (`DreamSyncManager.OutcomeWorld`, `DreamSyncPatches.Lifecycle`).
- **The dream bunker door's backup force-open never ran**: it looked for `door_underground` in
  GameObject names, but that is the NPC's name field (the object is
  `Door_talkable_outside_bunker_underground_02`) (`DreamDoorSyncPatches.Aftermath`).
- **Creatures in the epilogue dream's sublocations stayed frozen** after the host's entry video; the
  check read only the nearest Location's name and now checks every enclosing one (`EndFreeze`).
- **A returning prologue player with no saved character came back without the prologue's 2
  mushrooms**; it now gets `dream_tutorial_01`'s default reward. A joiner's own prologue could leave
  `doctor_dogKilled` set in its copy of the world; the prologue's world flags now go back to the
  host's values when it wakes (nothing ever reached the host) (`PersonalPrologue`).

### Another player's legs frozen mid-step

- **After the host was freed from a bear trap, the client saw the host standing with one leg
  forward until the host walked again.** The stand-in's legs are hidden during the trap, vault,
  dodge and the like, and hiding them stopped the walk clip on whatever frame it was on; a run
  ending stopped the same way. They now stop on the walk clip's standing frame (`FeetNeutral`), as
  vanilla's legs come back standing (`SecondPlayerAnimController.StopLegsAtNeutral`). Same code on
  both sides, so a client caught in a trap looks right to the host too.

### Map would not open (both players, after the hideout lesson)

- **Opening the map threw an error in `UI.hidePlayerUI` on host and client, and the map never
  showed.** A hideout lesson message meant for the other player is hidden on this machine
  (`PersonalFlavorHud.HideCharacterMessage`), and the hide destroyed the message object. Vanilla
  had already put it on the player's `attachedGameObjects`, which the map, menus and UI walk
  and switch off, so the dead entry broke every map open from then on (and left a destroyed
  object in the UI pool). The message is now switched off at once and retired the next frame
  through vanilla `WaitAndDie.onDeath`, which takes it off that list and returns it to the pool.
- **The next playtest: the host still could not open the map; the hideout oven was left unlit
  and lost its first talk.** Two faults in that retire step. It called `WaitAndDie.onDeath`
  directly, but onDeath takes the message off its owner's list only through the
  `CharacterMessage` that vanilla's `tryToDie` looks up first, so the entry stayed and a message
  that is not pooled was destroyed under it: the same dead entry, the same broken map. And it
  emptied the message's line list, which is the GameEvent's own list (vanilla hands it over:
  `displayMessage(...).texts = texts`), so the event's next fire (the other player's turn of the
  hideout lesson, `Hideout1_tutorial_01`) threw on `texts[0]` (host log) and skipped every step
  after it. The unlit oven most likely comes from those skipped steps (not confirmed: the
  event's steps live in scene data); the next playtest shows it. The step now lets go of the list (a new
  empty one) and ends the message through `tryToDie`, vanilla's own end. A one-time report names
  what `UI.hidePlayerUI` trips on if it ever throws again (`HidePlayerUiDiagnosticPatch`).
- Host log wording: a peer leaving logged "disconnected mid-night" at any time of day; it now
  reads "disconnected — night deaths (...)".

### Log file losing its last lines on quit

- **The console showed lines that never reached `LogOutput.log`, always at the end.** BepInEx's
  disk log buffers lines and writes them out every 2 seconds; the file is only closed by a
  finalizer, which Unity's shutdown never runs. So the last seconds before quitting, and
  everything logged during the teardown, were lost (on both installs). A listener after the disk
  log (`Logging/DiskLogFlush.cs`) now writes errors out at once, and every line once the game
  starts quitting; the plugin's teardown writes out the rest.

### Test pilot (unattended dual-box runs)

- Debug-only driver for automated runs, off unless the game is started with the environment
  variable `DWMP_PILOT` (`host:N` hosts LAN and loads profile N; `join` joins 127.0.0.1, puts
  the world copy in profile `DWMP_PILOT_SLOT` and enters). In the world it runs commands
  appended to `pilot/cmd.txt` beside the game's data folder and writes results to
  `pilot/out.txt` and the log (`[Pilot]`): status, tp, tpp, god, hurt, die, time, endnight,
  doors/door, chars, kill, flag, find/goto, lightshare, desync, shot, wait, say, quit. It
  also records each distinct Unity error with its stack. `Domains/Diagnostics/TestPilot.cs`.
- Desync check: logs a check that takes over 25 ms, and the five-minute summary gives the
  slowest; near radii rescaled to world units (doors, traps and items 400, creatures 600,
  containers 200; the first numbers were a hundred times too small). First pilot runs: 41
  checks idle, doors opened from either side, night set by the host: no desync, about
  2.5 ms per check.

### Desync checker (new diagnostic)

- **Playtests now find drift on their own.** Every 15 s (`Debug.DesyncCheckIntervalSec`) the
  host sends each settled client a fingerprint of the state they should share
  (`DesyncDigest`): a hash per world section (story flags, NPC deaths and standing, player
  drops, trader stock, journal, tonight's scenario and events, workbench and weather) and the
  entries themselves for the sections around that player (players' health, alive and skills;
  doors, creatures, traps, generators, ground pickups, opened containers and fires within
  12-30 units). The client builds the same sections from its own world, asks for the
  entries of any hashed section that differs (`DesyncDetailRequest` / `DesyncDetail`) and
  diffs key by key. A difference seen in two checks in a row is logged once as
  `[Desync] DESYNC <section> <key>: host=... client=...` and again as `resolved` when it
  goes away; the host log gets the same lines as `[Desync pN]` (`DesyncReport`). Clock and
  chapter are compared too (10 game minutes of slack).
- Read-only: it never changes the world and mints no ids. Skipped during dreams, joins,
  world shares and migrations, and for a third player still joining. Containers are compared
  only after the client opened them (loot is rolled on the host); objects at the edge of the
  near radius are not counted as missing. Near objects come from a wake-time registry
  (`DesyncRegistry`, patches on `Item.Awake`, `Inventory.Start`, `Trigger.Awake`,
  `Burn.Start`, `NPC.Awake`) so no check does a scene-wide search.
- New config `Debug.DesyncCheck` (default on; the host's setting decides) and
  `Debug.DesyncCheckIntervalSec` (default 15, 5-300). Files: `Domains/Diagnostics/DesyncCheck*.cs`,
  `Core/DesyncEntries.cs` (pure, unit tested), `Networking/Messages/DesyncMessages.cs`.
- Untuned: the first playtest shows which sections are noisy for legitimate reasons; those
  get their rule fixed from the logs.

### Menus and Nightmare deaths

- **Players in the level-up menu or a dialogue were chased and hit.** Vanilla pauses
  the game in both, so nothing can reach the player there. Co-op keeps the world
  running for the others, so a player at the oven or talking (trading included) stood
  helpless. While a player is in one of those menus, creatures now ignore it and
  nothing hurts it (`MenuShield`: vanilla `ignoreMe`, which the host learns through
  PlayerEffectSync, plus `invulnerable`). A creature already chasing that player loses
  it, and the host's "closer enemy" switch skips it.
- **Nightmare (and hard on the last life): one life, and only a full party wipe ends
  the run.** Vanilla ends a one-life run at the first death, day or night. In co-op a
  night death already kept the player down until morning, and the run ended only if
  the whole party was down. A one-life death by day, though, got the player up again at
  home, so in daytime it was not one life at all. It now keeps the player down
  (spectating) until the next morning at any hour. If everyone is down at once, day or
  night, the run ends for the party (the vanilla game-over screen, the save gone).

### Bear traps and other traps

- **A player freed from a bear trap by a teammate stayed frozen.** When a teammate
  looted or removed the sprung trap holding you, the mod only cleared the "in bear
  trap" flags. Vanilla's immobilise effect stayed on, so you could not move or act
  until it timed out, mashing no longer shortened it, and your arms and held item
  were not restored. Now the hold ends the vanilla way: the immobilise effect is
  removed, which plays the step-out animation and gives everything back.
- **Which trap holds a player was guessed by distance.** It is now recorded the moment
  the trap catches you, so the rescue and the trapped-player id on `PlayerState` name
  exactly that trap and never a neighbouring one. The unused occupancy helpers that
  guessed by position are gone. (`LocalBearTrap`.)
- **Clients sprang traps on their copies of enemies.** A client's copy of a host enemy
  that walked over a trap fired it locally and told the host, which then spent its own
  trap with nobody in it while the real enemy walked on. Clients now leave enemy
  triggers to the host; the catch reaches them through trap and entity state.
  (`TrapCollisionCoopPatch`.)
- **Traps another player placed were not saved by the host.** Vanilla registers a placed
  trap for saving and for the world grid; the copies made on the other peers skipped
  that, so a client's trap vanished from the host's save and missed chunk handling.
  Peers now register it as the placing peer does, and hear its placing sound.
- **Traps that vanish when sprung were not synced.** A trap that does not stay after
  firing is removed by vanilla without the sprite switch the mod listened to, so the
  other players kept it armed. The host now sends those too, and peers play the spring
  and remove their copy.
- **Friendly fire now covers traps.** With friendly fire off, a trap placed by another
  player no longer catches you; your own traps still do, as in vanilla. Enemies are
  caught by everyone's traps. The placer travels on `ItemSpawn` (`PlacerId`, stamped by
  the host). Traps loaded from a save have no placer and catch everyone, as vanilla.

### The hideout night

- **Night monsters came only when the host was home.** Vanilla spawns the night's
  monsters around the player and only while that player stands in the hideout. With
  the host out, a client defending the hideout got no monsters at all (a free night),
  and with the host home, half the spawns were sent to players out in the forest,
  where vanilla never sends them. Now every living player at home counts: spawns
  pick their spot around one of the players at home, and with only clients home the
  same spawn step runs around a client there. Players out in the forest get the
  worm, as in vanilla. Night monsters at the hideout keep the hideout's patrol
  waypoints again. (`NightBaseBodies`, `NightSpawnFlagPatch`.)
- **The host's lit hideout scared off monsters hunting other players.** Vanilla makes
  every monster that fears the hideout run and despawn whenever the player carries the
  hideout's ward. With the host home and a client in the forest, that freed the client
  of everything chasing it. The ward now counts for the player the monster is after
  (its target, else the nearest player). A monster set to always attack the player now
  goes for the nearest player instead of the host. (`HostWardScopePatch`.)
- **Night events went to the host's house rather than the hideout.** With the host
  in some other world location (an abandoned house) and a client at home, the night's
  hideout events fired around the host. They now go to the hideout a player is in.
- **A client's light protection did not stop shadows.** Vanilla shadows never strike a
  player standing in a light area or holding a shadow-protecting item. For clients only
  the lit ground was checked; the client's own protection now counts too.
- **Sheltering in a village house could leave the worm ward on forever.** If a session
  backup was restored while sheltering, the ward came back without the mod knowing it
  gave it. The village ward is now recognised by its own duration.

### Enemies and other players

- **Skills and wards reached the host up to 2 s late.** Ninja, wards and the forest
  skills were sent every 2 s, so host monsters kept seeing a ninja client, or hunted a
  warded one. They now go out the moment they change (keepalive every 2 s).
- **Going invisible did not shake off attackers.** Vanilla makes everything attacking
  the player stop when it turns invisible; for a client nothing stopped on the host.
  Now it does, and other players see the ninja at 30% opacity as the caster does.
- **A ninja client's footsteps still alerted enemies.** They no longer do (vanilla).
- **The scary-face skill did nothing when a client used it.** It only touched the
  client's copies of enemies. The host now makes every non-NPC character within 500
  run from the client, and the other players see the effect. (`PlayerScare` gains the
  skill flag and caster; protocol 33.)
- **Enemies chasing a client counted as attacking the host.** The host heard combat
  music for fights it was not in, and when the host died those enemies calmed down
  and stopped chasing the client. Removed; vanilla only lists enemies after the player.
- **Dead, invisible or ignored clients could still be smelled out.** Dogs and wolves
  re-targeted a downed client by smell. They no longer sense one.
- **Enemy moves aimed at the host.** The teleport-next-to-player animation landed
  next to the host while hunting a client, a stalker fled from the host's position,
  and a banshee counted as unseen when only a client was looking at it. All three now
  use the player the enemy is after (or any player's sight, for the banshee).
  (`HostBodyRedirectPatches.cs`.)
- **Client gunshots were heard twice and woke culled enemies.** A fallback after the
  vanilla area alert repeated the hearing and switched on characters the world had
  culled. Removed; the vanilla alert alone runs, as for the host's shots.
- **A client opening or kicking a door was silent to enemies.** Vanilla alerts the area
  when a player swings a door; the applied copy had no player. The host now alerts at
  the door's open, run-kick and close distances.

### Lights on other players

Vanilla's lantern is no held object: on the hotbar it widens the player's own light dot.
The mod's lantern copy follows that; these were the real faults around it.

- **The lantern glow vanished while the player held a flashlight.** Both are on at once
  in vanilla; the glow now stays.
- **The lantern looked white and could turn near-black.** It was drawn with a fixed
  white, and while a flashlight was held, with the flashlight's dark tint. It now copies
  the colour, intensity and shadow layer of the light dot itself (every peer has it),
  so it looks like the owner's.
- **A flashlight cone briefly took the lantern's size.** With both on, the radius sent
  was the lantern's; the cone now keeps its streamed size.
- **A peer's flashlight lit the ground only inside the local player's beam.** Vanilla
  lights path nodes for any light named "Flashlight" within the local player's beam box,
  so shadows and shadow armour ignored a peer's beam (or all of it while the local
  flashlight was off). Each peer's beam now lights its own area.
  (`RemoteFlashlightNodesPatch`.)
- **A torch stayed lit on a player who lay down, dove or played dead.** Vanilla drops the
  flame there without an item switch, which the mod listened for; the change now goes out.
- **Lights were lost on a rebuilt player body.** After a host change or a body rebuild,
  the next identical light message was skipped as "already shown", leaving the new body
  dark until the player toggled something. The last light state is now re-applied.
- **A peer's torch light swung with the hand.** Vanilla keeps the torch light at the body
  centre and moves only the flame; peers now do the same.

### Items and barricades

- **Dropping a stack onto an occupied chest slot lost or copied items.** Vanilla merges a
  same-type stack (whole or part), merges durability, or swaps, and a swap puts the
  slot's item back where the cursor stack came from. None of it reached the host: a
  merge into a chest vanished from the shared world, a swap duplicated. Every container
  involved is now diffed before and after and the changes sent.
  (`ContainerStackOrSwapPatch`.)
- **Dropping an item from a chest with a controller copied it.** It landed on the ground
  but the chest kept it for everyone else. The removal is now sent.
- **Another player's barricade could hijack your own building.** Vanilla's barricade
  code reads the local player's build menu: if you were in dismantle mode when a peer's
  board went up, you got a free wood-and-nails refund and their board was torn down; if
  you were hammering, your own build ended early and was lost. Remote boards are now
  applied with your build state set aside.
- **Joiners saw door barricades at full health.** The door's real barricade and door
  health from the join snapshot are now kept.

### Explosions and fire

- **Explosions set off by a client hit everything twice.** The client ran the blast
  itself (its hits on enemies went to the host as attacks) and the host ran it again on
  request, so enemies took double damage and the client took its own damage twice. A
  client's blast is now look and sound only; the host's blast deals damage, effects and
  world hits for everyone.
- **Explosion effects never reached other players.** A remote player caught in a blast
  took its damage but not its effect (burning, stun). The effect now goes with the
  damage, with vanilla's flat-distance falloff.
- **Every extra pellet into a barrel replayed the boom.** Only a barrel's first
  activation is now sent.
- **Fire and debris from a client's molotov or bomb never showed on clients.** Every
  copy of a throw is muted, and the host did not send the secondaries of a peer's throw.
  It now does, and the thrower no longer filters them out.
- **Burning doors, windows and crates burned up to N times faster.** Each peer ran the
  fire damage tick and sent the result; now only the host ticks (your own burning player
  still ticks on your machine).
- **A burning or struck item could be mistaken for the nearest crate.** Fire and melee
  hits on items matched anything destructible within 25 m; they now match the item's own
  spot.

### Quests, death, the clock and the hideout

- **A client's quest step could be lost.** When a client finished a conversation while
  the host's dialogue window was busy (the host trading, or talking to that same NPC),
  the outcome (quest flags, story events, shared reputation) waited at most 15 s, then
  was thrown away, after the client had already handed over the item. Outcomes now wait
  in order until the window is free and are only cleared when the session ends.
- **A key or quest item the host's dialogue took stayed with everyone else.** Vanilla
  removes it from the journal; the host's own conversations never told the peers. They
  now do, as a client's conversation replayed by the host already did.
- **Dream keys, notes and quest items stayed after the dream on other players.**
  Picked up by a peer during the shared dream, they were not marked as dream items, so
  vanilla's end-of-dream cleanup missed them.
- **Hard and Nightmare were Normal for clients.** A client's co-op save was created on
  Normal, so its deaths never counted toward the permadeath party wipe and it had no
  lives. The host's difficulty now travels with the world (`WorldSaveBegin.Difficulty`).
- **The host's daytime death reset every enemy in the world for everyone.** Vanilla
  respawns all enemies when the player dies; with others alive it wiped their fights and
  chases. It now runs only when nobody is left alive.
- **A client's daytime respawn changed only its own world.** Vanilla clears the home
  area on respawn (enemies sent back, infection and armed traps near the bed removed);
  on a client that split its world from the host's. The client now only teleports home
  and the host clears the shared home area.
- **A waking client could move everyone's clock.** The host adopted the client's day
  and time whenever it woke up (respawn, dream, prologue). Vanilla waking never moves
  the clock (there is no sleeping through time in Darkwood), so the host now re-sends its own.
- **A joiner's world load could put out the party's lit oven.** Vanilla switches unlit
  ovens off as the world loads, and the mod sent each of those as a player action, so the
  lit hideout oven (and its shadow ward) could go dark for everyone. Only a real change
  is now sent.
- **A client's generator switch overwrote the host's fuel.** The host now keeps its own
  fuel and takes only the switch; clients get the fuel level every unit instead of every
  10, so pouring stops at a full tank.
- **Two players upgrading the workbench at once lost a level.** Both paid; the host now
  counts both.

### Dropped items and dragging

- **A dropped item landed in different places.** Vanilla throws a dropped item forward;
  only the dropper's copy moved, the others stayed at the feet, and a later pickup by
  position could miss and lose the item. The throw now travels with the drop. Items
  dropped through vanilla's second drop path in water also use the right prefab.
- **Joiners got duplicate dropped items.** A joiner's world comes from the host's save,
  which already holds the items lying on the ground; the join snapshot then sent them
  again. The saved copy is now adopted instead of spawning a second one.
- **A denied pickup could take back the wrong weapon.** The refund now prefers the copy
  with the durability and ammo that was picked up.
- **A teddy bear counted as a trap.** Trap checks on pickups read display names ("bear",
  "animal"); they now use the object's own trap flags only.
- **Losing a race for a scaled stack kept the loot-share bonus.** The refund now takes
  the bonus back with the stack.
- **Dragging one of two identical objects stopped the other player's drag.** Drags were
  matched by name only (every "chair" in a hideout), so a peer grabbing another chair
  force-stopped yours, and your stop could go unsent. They are now matched by the
  dragged object's position as well.

### More players, fewer surprises

- **Joiners never saw a player already on fire, and curse burns showed full flames.**
  Burning now rides on the player's effect state too (joiners and missed messages
  converge), and vanilla's curse burn (`burnSpecial`) shows no flames or sound, as on
  the owner.
- **Story triggers could go by a stale "who holds what".** The host's view of the
  clients' items only followed pickups and removals; drags, chest moves, drops and death
  left it stale, so a "player has item" trigger could fire with nobody holding the item,
  or never. Clients now compare their bag once a second and send every change, zeros
  included.
- **A client could skip the shared opening movie for itself.** It woke up early in the
  unprepared start while the others still watched. The host's movie now ends for
  everyone when the host skips it; a client's skip key waits.
- **No map marker for a peer's death inside a location.** Vanilla marks the location's
  entrance on the map; peers now get the marker too, and it goes with the bag.
- **A warded client did not scare off the monsters after it.** Vanilla's monsters run
  from a player in the lit hideout (or with the forest-spirit ward); for a client they
  did so only when their detection happened to pick that client. Now a monster after a
  warded client runs, as from the host.
- **A client's molotov could set off the barrel next to it.** An explosion request now
  matches the object by name at that spot, on the host and on the peers' visual side
  (where it could destroy the neighbouring barrel's copy).
- **A thrown axe or spear came back as a fresh one.** Vanilla puts the thrown weapon
  itself in the thrown object; other players' copies were new. They now carry its wear
  and upgrades.

### Flares, redesigned on vanilla's own clock

Vanilla's flare is a fixed clock from the moment it is lit in the hand: the glow rises
for 2 s and settles over 6 s, it flickers, and after its burn time it fades out over 2 s.
The mod used to own each flare's death over the network instead (the host timed every
flare and broadcast a despawn by throw id; peers faded on command), which caused most of
the faults below. Now every copy of a flare (a peer's held flare, a thrown flare, a
joiner's view of one on the ground) runs vanilla's flare itself, started at the flare's
age, so all machines see the same glow and the same burn-out with nothing to send
(`FlareClock`). The despawn message, throw ids and the host's flare expiry are gone.

- **Every other player's held flare threw an error every frame.** Its copy lost its
  physics body, which vanilla's flare reads unguarded; it now keeps a kinematic one (the
  error also left the flare's glare sprite turning with the body).
- **Two players' flares could put each other out.** Throw ids were counted on each
  machine and collided; the first burn-out then killed the other player's flare early,
  and the second could burn forever on a peer.
- **A burn-out could switch off the wrong light.** With no id match, the despawn grabbed
  any object with a light within 3 units, the thrower's own lantern and flashlight
  included.
- **Flares never went out on clients when a despawn was missed.** Clients had no clock of
  their own for them.
- **A flare that burned out in the hand lit up again when thrown.** Others saw a fresh
  flare; now they see the spent one.
- **Burn-out looked different on every machine.** The fade fought the flare's own
  flicker, a peer's held flare dimmed on a guessed threshold and then popped out, and
  every peer copy started with a fresh ignition pulse. All of that is vanilla's own fade
  and phase now.
- **A held flare looked wrong on other players.** It used a different rotation from
  vanilla's held throwable and showed the stick sprite vanilla hides while aiming.
- **A just-thrown flare could flash back into the hand.** A movement packet sent before
  the throw but arriving after it re-lit the held copy; such packets are now ignored.
- **Flare lights were listed twice for path-node lighting.** The light registers itself;
  the mod added it a second time.

### Night events and shadows (checked against the wiki's night event list)

- **A client's lantern could stay dead for the rest of the session after shadows.**
  Vanilla keeps natural lights (torch, lantern) unlightable while a shadow wave runs and
  clears that when its shadows are gone. The shadows die on the host, so clients never
  cleared it: after any wave, theirs stayed blocked. The host now sends the wave's end.
  A wave is also only that player's curse: other players' lights are no longer blocked
  by it (`ShadowEvent` gains `End` and `OwnerId`).
- **Night events picked their spot by the host's position.** Hideout events that land
  "closest to the player" (where a glare forms, what a poltergeist pulls, which door)
  used the host even when only a client was in that location. They now use the player
  in that location.

- **Night scenes (knocking, a voice, a visitor) played in the wrong hideout.** Vanilla
  plays a night location event where the one player stands. The mod played it in the
  host's location only (or the first peer's hideout found while the host was away), and
  every client replayed it in its own location: a client in another hideout heard the
  knocking and nothing came, and a second peer's hideout got nothing. The host now plays
  the scene in every world location a living player stands in, each with that player as
  the scene's owner, and tells clients where (`ScenarioEventFired` gains `Anchors`). A
  client replays it only there. The scene copy's own fire is no longer broadcast as a
  GameEvents fire (clients could never find it and kept searching).
- **A client's night events could start late, at a wrong time, or not at all.** A
  client waited for its own next frequency check to start the host's event, which never
  came while it stood outside a location or its previous event had not ended, and the
  event still went through the client's own random chance roll. A client now starts the
  host's event the moment it is told; its own check only ends events on the shared
  clock.

### Scripted events (triggers and their steps)

- **Scripted events lost track of who set them off.** Vanilla trigger and event steps
  wait at least a frame before they run. Every guard the mod put around starting them
  (who the action belongs to, "the host is applying a peer's action", "this is a replay
  from the host", "this reward is not yours") had already ended when the steps ran. So a
  client walking into a volume, using or examining something credited it to the host:
  recipes, items, a teleport or a location transport landed on the host, and the client
  got nothing. A replayed flag or door change on a client was echoed back to the host.
  The scope that was active when the steps were started is now carried into each step
  (`EventCoroutineScope`).
- **The host now leaves personal steps to the player they belong to.** Even with the
  right player recorded, the host ran a peer's personal steps on its own body. It now
  skips them, and that peer's own replay runs them.
- **Counters and shared reputation were added twice on clients.** Event steps that add
  to a world flag or to an NPC's shared reputation ran on the host, which sends the new
  value, and then again in the client's replay. Clients drifted to double values and
  sent the wrong value back. Replays now skip steps that add.
- **Events that spawn a creature made a second one on clients.** A client's replay
  spawned its own copy at its own random spot, beside the host's real one. That copy
  never matched the host's, which left a phantom and an orphan. Replays now skip spawn
  and replace steps; the host's creature arrives as usual.
- **Events that only run on an active object were lost on far clients.** On a client
  whose copy of the event was culled or inactive, the replay failed to start and the
  event still counted as fired. The host ran it, so the replay now runs it too.
- **Story functions that act on "the player" ran on everyone.** Petting the dog,
  getting out of bed, taking the coat off or putting it on, the flamethrower handover
  (a teleport plus a weapon), Maciek placed beside "the player", and the table leg
  breaking all ran on every peer. They now run only for the player the scene is
  about. A scripted "open this NPC's dialogue" step also opens it only for that
  player.
- **Scripted camera and input steps hit players elsewhere in the world.** Black
  screens, camera pans, shakes, input locks, a hidden HUD and perspective switches
  dragged a peer's camera across the map or locked its inputs. These steps now run for
  the player the scene belongs to and for peers in the same location (a scene out in
  the open world still plays for everyone).
- **Repeatable events set off by a use or an examine reached nobody.** Repeatable
  event sets are not broadcast, because each peer runs its own ambient and area copies.
  A client's use and examine are sent to the host and never run locally, so their
  repeatable events ran on the host alone: the user saw nothing. Uses and examines now
  broadcast them, including the host's own.
- **Trigger volumes kept stale occupants.** When a volume was switched off, or a
  player's stand-in was removed while inside one (the player left, or the stand-in was
  rebuilt), the volume kept counting that player. A later entry was ignored as
  "already inside", and the area's exit never fired for anyone. A switched-off volume
  now drops its occupants, and a removed stand-in counts as walking out.
- **A location's on-enter events credited the host when a client entered.** The host
  opens a location for a client who enters it. Its on-enter events now belong to that
  client. A client who enters a location the host is already in now also gets the
  entry's one-shots that never fired, for example because the first visitor did not
  meet their requirements.
- **Event requirements read the host's body for a client's trigger.** "Health at
  least", "darkness", "fewer than N enemies attacking" and "has skill" checked the
  host when a client set the trigger off. They now check that client. Each player
  sends its health, darkness and skill list with its effect sync (`PlayerEffectSync`
  gains them). Attackers are counted per body, because the host's own list also held
  creatures attacking other players.
- **Scripted hits and clock tweens ran twice on clients.** A client replaying a
  scripted hit on a creature ran vanilla's own death on its copy: loot was rolled again
  and death events fired beside the host's real ones. A clock tween fought every host
  time sync. Replays now leave both to the host, whose results arrive as usual.
- **One-time hints reached only the first player past.** A hint (a message, perhaps
  with a sound) latched for whoever triggered it first. It is now shown once to each
  player.

- **Repeatable one-shots and enter events, second pass.** A per-player one-shot that
  another player's fire latched meanwhile stayed latched for the next player; the latch
  is now lifted only in that case. Entering a location the host is in fires only the
  entry events still pending for that player, not every entry event again. Location
  state requirements are checked for the player the trigger belongs to. "In sight of
  the player" triggers credit the player who saw it. Delayed steps saved in the host's
  world now run on a joining client. Client replays no longer spawn a scripted
  creature or item object of their own. The late-join list of fired events is sent in
  parts, so a long campaign no longer overflows one message.

### Dreams, a full pass

- **A dead player was pulled into the dream, and the dream could never end.** A
  player waiting for morning after a night death (or for a day respawn) was put on the
  dream roster. A dead player can't act or die again, so "everyone in the dream is
  dead" never came: if the dreamers then died, the dream never ended and the clock
  stayed frozen. Waking from the dream also revived that player early. Dead players
  now sit the dream out, on both sides.
- **A client's later level-up dreams all failed after its level 2 dream.** The
  level 2 bunker dream sets the dream name, and vanilla clears it once the dream is
  prepared. The client path never cleared it, so every later level-up asked the host
  for the finished bunker dream again. The host refused, and that level's dream was
  gone for the party. The client now clears it.
- **A refused dream is no longer lost.** If the host is dead, or another dream is
  starting or running, the level-up dream waits. Its entry movie plays again once the
  host can take it, the same way vanilla keeps a dream wanted until it happens. Level
  flags are now merged only when the host takes the request, so a refusal no longer
  uses up that level's dream for the party.
- **A refused request now releases everyone at once.** Every peer had played the
  requester's entry movie and frozen for it. They each sat in the black until a 20 s
  watchdog let go. The host now cancels the entry on all peers (a new `CutsceneSync`
  action) and lets its own world run.
- **Quitting mid-dream saved the dream's kit as the player's own.** The exit backup
  read the live bag, health, effects and clock, which in a dream belong to the dream.
  The next join restored them. It now saves the real ones that vanilla puts aside
  for wake-up.
- **Peers could be killed during the host's entry movie.** The host's world stopped
  only once the pad was up. Through the movie, the prepare wait and the save, its
  creatures kept attacking players who were locked in the movie. The host now freezes
  at the start of the entry (movie or prepare). The clock it saves is then the
  pre-dream one, not the dream's. If no dream follows, the world is released.
- **A world saved mid-dream left a joiner on the loading screen.** Vanilla resumes a
  dream saved in progress, and only that dream lifts the loading screen. A client
  never runs a dream of its own, so the joiner sat on the loading screen. The client
  now drops the stale resume and finishes loading normally.
- **Players outside the dream saw the dream's clock.** A peer that sits a dream out,
  or whose pad failed to load, now shows the overworld time (`TimeSync` gains
  `OverworldTime`).
- **A dream death could break the wake-up.** When a peer died in a dream that has no
  death outcome, the reward downgrade set the outcome to nothing. Vanilla then threw
  during wake-up: no heal, inputs left locked. It now wakes the player like the rest
  of the party, without the rewards.
- **A failed pad load left a pad behind.** When a dream finished while a client was
  still loading its pad, the pad stayed in the scene, and the client stayed marked as
  "about to dream". That blocked its saves and kept it out of the open world. It also
  kept the entry's input locks. All of that is now cleared.
- **The hard dream exit didn't match vanilla.** It now drops whatever is in hand and
  any action in progress. The tutorial wakes at its fixed hour, and an outcome with its
  own wake time keeps that time instead of being overwritten.
- **The tutorial's ending sent the player to spectate.** In the tutorial dream, the
  creature's heavy hit is vanilla's scripted ending, not a death. In co-op it put the
  player into spectate while the others walked on. It now ends the tutorial for the
  whole party, like any story ending.
- **A join could slip into the host's dream entry.** Joins are refused during a
  dream. They are now also refused between the start of the host's entry movie and
  the start of the dream.
- **A refused or abandoned dream entry left players black and muted.** Undoing an
  entry movie (a refused request, a stuck movie, a start blocked at the last step,
  the host's entry that led nowhere) left both black layers on and the game's sound
  faded out until the next dream. All of these now bring back the picture, the sound
  and the cursor. They also leave the clock alone: it ran through the movie.
- **Dead players still played and froze for the entry movie.** They now skip it, as
  they skip the dream.
- **Creatures appearing during the host's entry movie weren't frozen.** Only
  creatures alive when the freeze started were held. Night spawns and waking ground
  in the 20 seconds before the pad attacked players locked in the movie. They are
  now held too; the dream's own creatures stay free.
- **Stuck-movie watchdogs from an old movie ended a newer one.** Each movie now has
  its own.
- **A waiting level dream replayed its movie every few seconds during someone else's
  dream.** It now waits until the host's dream ends. If another player's dream for
  the same level happened in between, the party has had it, and the waiting one is
  dropped (before, it gave a second dream for that level).
- **A dream start blocked at the last step kept the host frozen.** When the party had
  already finished that dream, the pad stayed, the dream stayed "prepared" and the
  world stayed frozen. All of that is now undone.
- **A dream reward could be lost on the pad.** In the hard wake path, a reward that
  didn't fit in the bag was dropped where the player stood, on the dream pad, before
  the move home. Rewards now come after the move, as in vanilla.
- **A dead dreamer came back to life when the dream chained into its next part.** The
  chain runs the wake-up, which heals and revives. A player dead in the dream now
  stays down until the dream really ends.
- **Taking from a dream chest deleted a ground item for the others.** A redundant
  dream pickup message destroyed a same-type item near the chest on other players.
  The container sync already carries the take, so the extra message is no longer
  sent.
- **Joining a dream in progress gave a fresh pad.** The bunker's dialogue door was
  shut and earlier doors closed. The pad's fired events and door states are now
  replayed when a player arrives on it.

- **A dialogue's dream was lost when the host could not start it.** A peer's dialogue
  that chooses a dream is applied on the host, which starts the dream. When the host was
  dead, or another dream was starting or running, the start was refused, the dialogue
  already spent, and the dream never happened. It now waits on the host and starts once
  the host can, as vanilla's wanted dream does.
- **Checked against the game's own dream data:** no dream outcome fires a GameEvents
  prefab, and the outcome items (shovel edge, mutated cockroach, mushrooms, shiny rock,
  the flashlight) are ordinary rewards each dreamer gets, as in vanilla.

- **Level-up dreams are per player, and a played dream is never repeated.** Vanilla
  gives each player one dream at levels 2, 3, 5, 6 and 7. The first player to reach one
  of those levels now brings its dream to everyone who is there, and every player who
  was in it has that level's dream counted as had (`hadDreamAtLvl*` are each player's
  own). A player who was in it just levels up when they reach that level. A player who
  was not (a late joiner, or someone sitting it out dead) still gets a dream for that
  level, one the party has not played yet; none left means just the level-up. The
  bunker (level 2) is played once per world: a player who missed it gets a random
  dream for that slot instead. Before, the party shared one set of level slots, so a
  late joiner skipped every level the party had dreamed. A client whose dream was
  refused while another dream ran also leaked its slot to the host, and its owed dream
  was dropped for good; a peer that becomes the host now still starts its owed dream.
  The dream messages now carry the level slot(s) the dream is for (`LvlFlags`), not a
  party union. Each player's slots are saved in their character backup
  (`DreamLvlFlags`; older backups count every dream level already passed as had). The
  played dreams are saved in the world's co-op sidecar (`CompletedDreams`), so a host
  restart no longer forgets them (an older world counts the bunker as played when the
  host had its level-2 dream).

### Story and unique content (chapter 1 and 2 NPCs and places)

- **The porter's "bring my stash" could destroy it.** Vanilla empties the other
  hideout's containers and delivers them to the hideout the player stands in. Every
  peer that replayed it did that against its own position. With the host out in the
  forest, the stash was emptied and never delivered; with the host in another
  hideout, it went there; and every peer made its own package. The host now runs it
  once and delivers to the hideout the porter is standing at. Peers mirror it: their
  source containers are emptied and the package is placed, and its contents come
  from the host when opened (new `PorterTransport` message). Replaying a client's
  "which hideout" question on a host out in the forest also no longer throws and
  drops that board.
- **The Wolfman's arena checked the wrong bag and could lock up.** "Did you come
  armed" checked the host's bag when a client walked in, and each client checked
  its own and fired its own branch too. The host now decides, using the bag of the
  player who walked in. A client dying in the arena left it locked, because its
  reset event was a blocked one-shot. It now goes through the host. A host dying
  anywhere else no longer resets a fight a client is still in: only a death inside
  the arena resets it.
- **The Wolfman's workbench raid differed on every machine.** Each peer drew its own
  random ten items from its own copy, and a peer without his container loaded lost
  them. The host draws once and sends both containers. A client streaming out of any
  location also cleared the shared "wolf in the hideout" flag, which cost the host
  the next morning's wolf. The wolf's despawn is now the host's.
- **Story steps that place things "around the player" used each peer's own body.**
  The act 2 doctor copies (hidden when far from the scene), Maciek next to the player
  taking the flamethrower, and the Wolfman taking his sister (from the location the
  scene is in) now use the body of the player the scene belongs to, the same on
  every peer.
- **Killing the night trader blacked out the host, not the killer.** The trader dies
  on the host, so vanilla's blackout and lie-down ran on the host's body. The killer
  now gets them (new `PlayerSpecial` message).
- **A player moving home put out the others' oven.** Vanilla puts out your previous
  oven when you light a new one, and that took the home and the shadow ward from
  whoever still lived there. A peer's lit oven also became every other player's own
  home and respawn point. Each player now keeps their own home, carried in the effect
  sync. An oven goes out only when nobody calls it home.
- **The permadeath "start over" reset only the host.** The chapter reload resets
  every character, but clients restored their pre-wipe levels, skills and bags from
  their character snapshots. The reset world's containers then duplicated those
  items. A start-over now discards every snapshot on every machine and skips the
  exit snapshot (`ChapterTransition` gains `StartOver`). Snapshots also record their
  chapter, and a pose from another chapter's map is not restored.
- **One-shot moves carried only the first player.** Volumes and uses that carry "the
  player" somewhere (the road home from the radio tower or the tree village, the
  border gates, the cottage, the elephants) latched after the first player. The next
  player got nothing and could be stranded. An event made only of such moves, plus
  its screen, sound and message steps, now carries each player once. Item rewards
  stay one-shot, since items are one shared world.
- **Location flags of one player were set for everyone.** "At the doctor's house"
  hides talk options there, and "entering the road from the radio tower" picks the
  entry spawn. Both were set on every peer by the replay. These flags, like the
  hideout `player_in*` ones, now belong to the player they describe and are not
  synced.
- **Cutscenes played for players who were elsewhere.** A cutscene inside a location
  hid, froze and input-locked every peer, wherever they stood. When the host's
  manager wasn't found, the cutscene fell back to any manager at all. A cutscene now
  plays for the players in its location (an open-world one still plays for everyone),
  and only the host's manager plays.
- **Scripted slow motion and grid switches hit players elsewhere.** A slow-motion
  step slowed the whole host simulation for everyone. A walk-grid switch moved
  players onto a grid for ground they weren't standing on. Both now follow the
  scene's location, like the camera steps.
- **Host dialogue offered "give X" on a teammate's bag.** Dialogue choices checked
  the whole party's bags, so the host could pick an option it couldn't pay. The
  outcome then found nothing to take and granted the reward anyway. Choices now
  check only the speaker's bag.

- **One death in the Wolfman's arena reset the fight for everyone.** The arena fight
  reset as soon as any trapped player died, also while another player was still
  fighting in it. It now resets only when nobody living is left in the arena.

### Trading, crafting and homes

- **Trading with a trader whose stock hadn't arrived could wipe it.** After a trade,
  a client sent its whole copy of the trader's stock and the host took it as the
  truth. A stale or empty copy wiped the real stock for everyone. A copy the host
  refused left the client with what it bought while the stock reverted, which
  duplicated those items. The client now sends what it bought and sold (new
  `TradeCommit` message). The host checks the purchases against its own stock and
  applies the trade. If the stock lacks them, the host refuses, and the client gives
  the purchases back and gets its goods back. The trading client no longer gets its
  own trade echoed back, which used to rebuild the stock under an open trade window.
- **A craft finished even after a teammate took the ingredients.** Vanilla checks a
  craft's, repair's or upgrade's materials when the bar starts, not when it fills.
  With the workbench pile shared, a teammate could take the planks meanwhile, and a
  crafter who didn't carry any got the product free. Materials are now checked
  again when the bar fills. If two players reach for the same pile item at once and
  the host refuses one, that craft is undone (the product taken back, the repair or
  upgrade reverted).
- **A rejoining client lived in the wrong hideout.** Two causes:
  - A client loads the host's world, so it took the host's home oven.
  - The join's oven list made every lit oven "home" in turn.
  
  Both moved the client's respawn point. The character snapshot now keeps each
  player's home oven, the join list no longer changes it, and a home that the world
  load put out is lit again.
- **Only one hideout got a morning.** The morning (end-of-night effect, lights,
  trader, the night's creatures cleared, the trader's reputation) ran for the host's
  hideout, or for one peer's when the host was away. Every hideout a living player
  greets the morning in now gets it, and each player is rewarded by the trader there.
  The Wolfman, being one man, visits one hideout.
- **A new day could restock a trader under an open trade window.** The restock now
  waits until nobody is talking or trading with that trader.
- **Map pins were matched by name only.** A discovery now carries the pin's position,
  so the right one of two same-named pins is revealed (`MapElementDiscovered` gains
  it). The Navigator skill's meat marker belongs to its player and is no longer
  broadcast; peers kept rescanning for it for five minutes.

### Creatures and combat

- **Creature copies on clients made their own decisions.** A dog's howl ending made
  each client's copy summon two local dogs. Waking up, a cut or the banshee's brood
  did the same kind of thing. Copies also ran their own teleport, despawn and
  "stuck" timers, world events, and functions from animation frames. The host's
  creature makes these decisions and sends the results; copies now only present
  them.
- **A creature hit by a client rolled to flee; hit by the host, it went for whoever
  was nearest.** Vanilla chases the player who hit it. A client's hit came from a
  stand-in, which vanilla doesn't recognise as a player. The host's hit was redirected
  to the nearest body. The player who hit it is now chased.
- **Creatures chasing a client never re-growled, and growled on every re-acquire.**
  The proxy growl skipped vanilla's throttle and repeat. Both are back.
- **The banshee screamed at the nearest player, not the one looking at it.** It now
  picks the nearest player who has it in sight. A dead player's body no longer
  "sees" anything. On clients, the banshee copy's own sight reactions no longer cut
  the scream and overlay the host sent.
- **A Friend of the Forest client was never chased up close.** The host was, as in
  vanilla. Both are now chased.
- **A human-spider spawning by a client settled into the host's location.** It now
  settles into the location of the nearest player.
- **Clients never got combat music.** The host now tells each client when creatures
  start and stop chasing them.
- **Fire never hurt another player's body with friendly fire off.** Vanilla counts
  every flame as a player's hit, so a burning barrel's flames and your own fire bomb
  spared clients. Fires now remember who started them. Friendly fire off spares you
  only from someone else's fire.
- **A client saw no enemy health bar for its own hits.** It now does, and the bar
  follows the host's health numbers.
- **A creature's damaging aura didn't shake a client's screen.** The host now sends
  the shake and the noise vanilla gives the player in range.
- **A shooter hurt only the player nearest to it.** Vanilla hurts "the player"
  whenever the shooter can see them. It now hurts every player it can see.
- **A client's flamethrower was harmless.** Its flames' hits were muted on the
  client, because flames copied from the host's fire bombs are. A player's own
  flamethrower fire now counts as their attack, like a gun. Creature hits go to the
  host with the flame's burn, and door and crate hits are reported.

### Doors

- **A client opening or kicking a door was silent to creatures.** Vanilla alerts
  creatures only when the opener is a player, and the host opened it with no opener.
  The host now raises the same alert (a kick carries further).
- **A story event forcing a locked door open unlocked it on clients only.** The open
  message also cleared the door's locks and its "blocked" state everywhere but on the
  host. Locks travel on their own messages, so a plain open now leaves them alone.
- **A door could end up barricaded and open.** An open that crossed a teammate's
  finished barricade was applied anyway. A barricaded or broken door is no longer
  opened.
- **Client hits on doors and windows looked and counted wrong.** The host fired the
  generic "attacked" trigger instead of "attacked by the player", so scene triggers
  waiting for the player to hit the object never fired for clients. A metal door
  sounded like wood on the client. Both now match vanilla. The client also sees the
  door's health bar on any hit.
- **Client fire overwrote door, window and crate health.** A client's copy of a
  fire bomb's flames also hit them and sent its own health value, which replaced
  the host's. The copy now only sets them alight; the damage is the host's.
- **A peer coming back kept doors open that others had closed.** Joins now carry
  every door's state, closed ones included.

### New game, chapter change and the ending

- **A party wipe's "start over" deleted the other players' worlds.** The wipe marks
  every player's profile dead, and only the player who pressed "start over" got it
  cleared. The profile menu deletes a dead profile's world on its next visit. The
  start-over now clears the mark on every machine.
- **Friends were locked out of the new game's opening.** The tutorial dream starts
  while the world is being made. Joins are refused during a dream, so a friend who
  waited at the title screen couldn't reconnect after loading the shared world.
  Joins are now let into the tutorial dream and pulled into it.
- **The opening movie could play over the live world on a client.** A client
  catching the opening schedules the movie 7 seconds in. An "intro over" arriving
  first (the host skipped) didn't cancel it. It now does. After the movie, clients
  also wake up from sleep as vanilla does.
- **Clients played on through the chapter change.** Vanilla blacks out and locks the
  player while the next chapter is made, but on a client that step is the host's.
  Clients now go black, locked and unhurt until the new chapter loads, and are handed
  back if the change fails.
- **The fastest reader ended the epilogue for everyone.** Credits now start when
  every player in the ending has finished its pages, or after two minutes.
- **A host leaving mid-movie or mid-cutscene left clients locked.** The movie, the
  black screen and the input lock (or the cutscene's freeze) are now released when
  the host is gone.

### Locations and hideout defenses

- **Walking out of a location never told the others.** The "left the location" check
  read flags that the return to the map had already reset, so only deaths announced
  an exit. The host kept the leaver inside, so the location never shut down (its
  creatures kept running), its exit events never fired, and a return wasn't a fresh
  enter. Leaving now announces the exit.
- **A location's entry events still credited the host.** The location wakes over a
  few frames before its entry events fire, and the "who entered" scope was gone by
  then. It now rides along.
- **Going straight from one location to another could leave the first one running.**
  When the second location wasn't made yet, the first was never checked for
  emptiness. It now is, once the player is placed in the second.
- **Later visitors missed a location's entry events.** The host walking into a
  location a client had opened got none of its unfired entry one-shots. Entry moves
  and hints now also reach each player once, as elsewhere.
- **The forest behind a client stayed awake while the host was in a location.**
  Ground woken around a client never went back to sleep, so creatures kept running
  and chasing from far behind. It now sleeps once no player is near.
- **A bad location name from a peer was spawned anyway.** It left an empty marker and
  used up a location slot, again at every heartbeat. Names without a location scene
  are now refused.
- **The join's oven snapshot replayed every frame while any oven was missing.** It
  undid ovens changed since, and broadcast that. Only the ovens not found yet now
  stay pending, applied as the host's state, at most once a second.
- **Lit ovens of other players were silent.** Their hum is back.
- **A teammate's drag could move your own same-named furniture.** A drag update now
  only matches your dragged object if it is at the reported spot.
- **Identical furniture standing close together could swap.** The object lookup
  now takes the nearest object of that name, not the last one it used.
- **The Electrician skill only worked for the host.** Generators burn fuel on the
  host. They now run at the best rate any player's skill gives.

### Items and traps for late joiners

- **A client picking up a ground item lost it for everyone.** The pickup sent both
  the claim and a container removal. The removal made the host destroy its copy
  first, so the claim found nothing and was refused, and the client gave the item
  back. A whole pickup now goes by its claim alone.
- **A generator update could create a stray second generator.** A client using a
  generator in a house the host hadn't loaded made the host spawn a new, unsaved
  one there. The real generator later stood on top of it, and the stray took its
  updates. The update now waits for the real generator. The generator lookup also
  stopped checking after 32 generators.
- **A controller drop out of a chest could duplicate the item.** When the host
  refused the take (a teammate got it first), the item was both on the ground and
  in the chest. The drop is now taken back.
- **Traps placed or removed since the last save were wrong for anyone joining
  later.** A joiner loads the last save, so a newer trap didn't exist for them, and
  one disarmed or picked up since came back armed and could be taken again. The host
  now keeps both lists until the next save and sends them to joiners.
- **Picking up in a location the host was still loading lost the item.** The host
  couldn't find it yet and refused the claim, and the client's copy was already gone.
  Such claims now wait (up to 20 seconds) for the location to load. A failed claim
  also no longer marks the item as taken.
- **A player arriving in a location later saw items others had taken there.** Each
  machine builds a location fresh. Its first visit now carries the pickups already
  taken there.

## 0.8.132 — Shared clock: time stops only when everyone is inside

Branch `dev-entity-sync-remaster`, on top of 0.8.131. **Protocol 31 → 32.** Product
**0.8.131 → 0.8.132**. Built and unit-tested; **runtime is not playtested**. Found by
a code audit, not by a report.

### The clock

- **Time froze for everyone whenever the host was inside a location.** Vanilla stops
  the clock while the player is inside an outside location (village, bunker, basement).
  The host is the only clock, so the host in the village gave the players out in the
  forest endless day, and a client in the village watched time run whenever the host
  was outside. The clock now runs while anyone is in the open world and stops only
  when nobody is. Each player reports it on `PlayerState` (`InOpenWorld`: not inside
  a location, not dreaming, not loading, past the opening movie), so a joiner still
  loading or watching the prologue does not run it. A promoted host keeps the rule.
  Vanilla's own freezes (morning, death, events) still stop it.
  (`HostSharedClockPatch`, `CoopTimePolicy.SharedClockRuns`.)

### Night and morning away from the host

The clock now runs while the host is inside a location, so night and morning can
come while the host is away from home. Several host-only paths broke there:

- **No morning unless the host was home.** Vanilla `startAfterNight` runs on the
  hideout the local player stands in and does nothing elsewhere. With the host out
  at dawn, nobody got a morning: no trader, no freeze, no rewards, and the chapter-1
  wolf visit (a story beat that never retries) was lost. The morning now runs on the
  hideout a living peer stands in. The host gets no reward and no screen effect
  (it was not home), the trader is despawned there when the morning ends, and the
  freeze is the clock alone. (`HostAwayMorning`.)
- **The end-of-night effect showed for clients who were not home.** Every client got
  the morning screen effect and freeze, wherever it stood. Now only a client at home
  at dawn gets it, and walking out of the hideout clears it for that player (the
  shared morning still ends when the hideout is empty).
- **The night type came from where the host stood at dusk.** Inside a location there
  is no biome, so it fell back to the easiest night and sent that to everyone. With
  the host not home, the night is now picked at a peer's hideout, or at a peer in the
  open world when the host is inside a location. (`HostScenarioAnchorPatch`.)
- **Night events fired into location pads.** The host's night events run in the
  location the host stands in, and location events are parented under it: inside a
  village, hideout-defence events landed in the village. With the host not in a world
  location, they now go to the hideout a peer is in, and are dropped inside a pad
  when nobody is home. Clients no longer replay a location event inside a pad
  either. (`NightEventAnchorPatches.cs`.)
- **Rain inside locations, and none outside while the host was in a bunker.** Rain
  could start in full view inside a village. Inside an underground pad vanilla can
  neither start nor stop rain, so the players outside got none while the host was in
  a bunker (a client in a bunker never got the host's rain either). While a player is
  inside a pad, rain now keeps its schedule and state but stays hidden, and shows on
  return. Lightning flashes are not shown inside a pad. (`PadWeather`,
  `RainHostInPadPatch`.)
- **Redirected night spawns aimed at players inside locations.** The host's night
  event spawns (forest spirit, event creatures) were moved next to a far peer even
  inside a location pad, where the events themselves no longer fire. They now pick
  only players in the open world. The hard-night worm is different: vanilla sends it
  after the player anywhere, inside a location too, so it still hunts players in
  pads (a peer still loading is skipped).
- **Clients never got the night warnings.** "Night is coming", "light the oven" and
  the end-of-night sound come from vanilla `refreshTime`, which never runs on a
  client. Clients now get them from the host's clock.

A player inside a location at night gets what vanilla gives one who walks in at
night: the location goes dark with the clock (underground ones stay black), the
worm hunts them there, and they miss the morning rewards if not home. The game
has no NPC day/night routines; the village gets one (below).

### The village at night (new, co-op only)

Vanilla never had night in the chapter-1 village while you were in it; now it does.
(`NightVillage`, `VillageNightPolicy`; village pad `outside_village_ch1_01`.)

- **The villagers go home for the night.** The friendly villagers are away from the
  "night is coming" warning (about two hours before night) until morning. The
  Musician and the crazy / infected villagers stay. Arriving near night finds them
  already gone. With players inside, they leave (and come back at dawn) all at once,
  and only while nobody sees any of them: each player reports it on `PlayerState`
  (`SeesVillager`, sampled 10 times a second) with vanilla's own "in sight or within
  1000" test; with several players inside, all of them must have lost sight. Someone
  who has just walked in counts as seeing for 3 seconds. The host decides and sends
  it on `TimeSync` (`VillagersAway`), so everyone and late joiners get the same
  village. Only the GameObject is switched off: the villagers' saved state is not
  touched, so a save never loses one and a villager a story event removed stays
  removed. Culling, location activation and the entity sync wake paths leave an away
  villager off. Not saved: after a load the host settles it again by the same rule.
- **Village houses shelter from the worm.** Standing indoors in the village (an
  indoor floor under the player, vanilla's own test) gives the player vanilla's
  shadow ward, the same effect a lit hideout gives: the worm and the immortal
  shadows leave them alone. It goes when they step outside or leave the village.
- **Playtest check:** the host log prints `[NightVillage] N friendly villagers in
  'outside_village_ch1_01'` the first time the village is used. The villagers are
  picked by their vanilla faction (`villagerNeutral`); if N is 0, or counts the
  wrong people, the faction guess is wrong.

### Hunger

- **Clients got hungry every evening; the host never did.** The game has no hunger
  mechanic: vanilla's hunger effect is never triggered. The mod triggered it on
  clients at dusk. Removed, along with the client `fedToday` reset nothing reads.

---

## 0.8.131 — Creature sounds, location traversal and dream pass

Branch `dev-entity-sync-remaster`, on top of 0.8.130. **Protocol 30 → 31.** Product
**0.8.130 → 0.8.131**. Built and unit-tested; **runtime is not playtested**. Found by
a code audit (creature sounds, location traversal, dreams), not by a report.

### Creature sounds

- **A client's hits and kills were silent for everyone else.** Every inbound message
  is applied inside the network apply guard, and the host's creature-sound sends
  skipped anything played inside it. A creature hit or killed by a client (the hit
  sound, its pain growl, its death scream) was never sent: other clients heard
  nothing, and the attacker only heard its own predicted melee hit. Host creature
  sounds are now sent whatever triggered them.
- **Only 7 creature sounds were synced.** The host mapped growl, attack 1/2, death,
  curious, aggressive, defensive and the flee stingers to an enum; every other
  `CharacterSounds.play` / `playSingleInstance` (idle barks, animation-event sounds,
  custom event sounds) was dropped on both ends. `EntitySound` now carries the audio
  id and how it was played (`Play`, `Single`, `Attached`, `GetHit`, `Death`), and the
  client plays it on its copy of the creature as vanilla's call would.
- **Creature loops went silent or played forever.** Breathing, buzzing, growl beds
  and sleeping loops were start/stop events: a client too far away when a creature
  woke, a late joiner, or a body the client's WorldGrid toggled never heard the loop,
  and a missed stop left it running on the corpse. The loop is now state: each entity
  snapshot carries the host's current loop (a slot of the creature's own loop
  fields), and the client keeps its copy's loop equal to it, with vanilla's fades
  (`Audio/EntityLoopSync.cs`). The client's own `playIdleLoop` / `destroySounds` on a
  synced copy are blocked (its frozen AI picked calm loops for chasing creatures).
- **Enemy footsteps, shots and sniffs played flat.** They were forwarded as a fixed
  point with forced 3D settings and an 80 ms limiter per sound id (three wolves on
  grass dropped each other's steps). They now go attached to the creature, so they
  move with it and get the game's indoor reverb and wall muffling; footsteps go
  unreliable (a late step is worse than a lost one).
- **Creature sounds on a client had no indoor reverb.** `CharBase.isInside` only
  refreshes in `checkGround`, which the frozen copy never ran; the client now
  refreshes it before playing.
- **The hit echo was a 0.35 s timer.** A client's predicted melee hit muted the host's
  hit sound for that creature for 0.35 s: another player's hit inside the window was
  lost, and over 350 ms of latency the attacker heard it twice. The host now stamps
  the attacker on `GetHit`; the attacker skips only the echo of a hit it showed.
- **An old corpse screamed when it came into view.** The death line also played
  from the snapshot's alive-to-dead change, so a client walking up to a body (or
  joining late) heard it die. It now plays only from the host's death sound.
- `EntitySound` is `[HostOnly]` (it was marked forwardable).

### Banshee

- **The scream never reached the client it was staring at.** Vanilla gives the
  player a banshee sees a scream loop on their own body, a camera shake and the
  red overlay. With a client as the target, the host shook and played the scream
  for itself, and forwarded a loop that peers drop. New `BansheeAgitation` (149):
  every peer turns the banshee's sight light on and off, the victim gets the scream,
  shake and overlay, and loses them when the banshee loses sight
  (`HostAIPatches.Targeting.cs`, `WorldFxNetHandlers.HandleBansheeAgitation`).

### Location traversal

- **AI in a bunker reset every second while a client was inside.** The ~1 Hz
  `LocationEnter` heartbeat called `Location.enter(force: true)` on every receipt,
  which re-runs the whole activation: every creature in the pad is sent back to
  its waypoint and the pad's on-enter events fire again. A pad is now entered once
  (`LocationEnterExitNetHandlers.EnsureEntered`).
- **On-enter events fired twice on the host** for a client's first visit: the
  activation fires them, and the host fired them again. The second call is gone.
- **A peer in the same pad hitched once a second.** The heartbeat also re-placed the
  remote player's stand-in, a hard snap to an idle pose. It is placed on its first
  enter, a deferred resolve, or a missing proxy only.
- **A bunker stayed loaded after the last client left it.** The host tried to leave
  the pad before moving that client's stand-in off it, and the keep-pad-for-remote
  patch saw the stand-in still inside. The stand-in moves first now.
- **A pad the host spawned for a client was half-built.** When a client entered a
  cellar / bunker / house the host had never visited, the host spawned it with
  vanilla `createLocation`, a world-gen helper: its props and doors registered on the
  World culling grid, `WorldGrid.currentGrid` was left on the pad (the host's own
  forest stopped streaming and its next location trip saved a wrong return point),
  the pad's navigation graph was never scanned (its AI had no paths), and its doors,
  game events and characters skipped their init pass. It now spawns like vanilla
  `prepareLocation` minus the host's own transport (`Domains/World/RemotePadSpawn.cs`).
- **A client pressing a location entrance was never moved.** `LocationTransport` ran
  `createLocation` on the client (spawn only, and a duplicate-key throw for a pad it
  already had). It now runs `prepareLocation`: black screen, spawn if needed, transport.
- **Pad to pad left the first pad running.** Going straight from one pad to another
  sends no exit; the host now leaves the pad the player came from once it is empty.
- **Clients activated pads they were not in** (every other player's pad, never left
  again). A client now activates only its own pad; the host keeps every occupied one.
- **A building the host walked out of with a client inside never got its exit
  events.** The leave was skipped for the remote and never retried; the host now
  leaves it once the last remote is out. The keep-for-remote check also looks at
  membership first, so a remote whose state is briefly late keeps its pad.
- **Forced grid refresh near remotes.** Every host grid refresh force-showed every
  object in every node near a remote (`SetActive`, `enableComponents`); a plain enter
  now shows only nodes a remote just reached.
- A client quitting inside a pad backed up no position (every pad slot is past the
  dream-pad bound); it now backs up the world point vanilla keeps for the return trip.
- Session-scoped location heartbeat state is reset per session (a stale
  `LocationExit` snapped a world-map player's stand-in after a chapter change), the
  settle hook no longer claims a pad that does not exist, and an abandoned chapter
  transition (back to the title) no longer auto-reconnects on a later chapter load.

### Dreams

- **Clients ran every dream after the first with the first dream's preset.** The
  peer pad load set `Dreams.preset` only when it was null: music, health, starting
  items, time and outcomes came from the first dream, and the exit tore down the old
  pad's grid and nav graph (the new ones leaked, its camera effects stayed). The
  preset is set for every dream now, from the same list vanilla uses.
- **A client's dream pad never initialized its doors, game events and characters.**
  The pad loads with `loading` set (so cullables register on the pad grid), which
  queues those inits; vanilla runs the queue in `onSpawnedLocation`, which the peer
  load never reached. The pass now runs after `startDreaming`. Likely behind much of
  the broken-dream behaviour on clients.
- **A player who died in the dream stayed spectating after a story end.** Death state
  was cleared on `DreamEnded` receipt, before the exit video, so nothing left spectate
  at wake-up; the body was then dragged onto a teammate and an F4 exit restored the
  dream-pad position (stranded in the abyss). The same early clear gave dead players
  the full story rewards. The death is now kept until wake-up, which leaves spectate
  and restores the body.
- **Clients stayed input-locked after an all-dead dream end** (and after a disconnect,
  reject or host loss in a dream that pins inputs). The hard cleanup never cleared
  what the exit transition clears; it now does.
- **No wake-up animation on clients' hard cleanup**: it always passed `dontLieDown`;
  it now uses the outcome's value as vanilla does.
- **A client's random-dream request skipped the host's roll hooks** (pool refill,
  session begin, early pick to clients): it ran inside the apply guard. The roll now
  runs next frame. A named request for a preset the game does not have is rejected
  before the session begins (it used to hold the session in Starting for 60 s).
- **A client's story end reached the other players late.** The host's fan-out stood
  down inside the apply guard, so peers waited for the host's whole exit. The host
  now fans out when it accepts the request.
- **Entry video on clients for dialogue / event dreams.** Those start with a black
  fade only, but clients played the skill-dream video. `DreamStarted` now says
  whether the entry had a video.
- **Chained dreams on clients**: the client's own `wantToSwitchDream` destroyed the
  current pocket and prepared the next one itself (second pad, local save, bogus
  start request, or the host's freshly loaded pocket destroyed under the player). The
  host's chain message owns the next pocket; the client keeps only the player reset.
- **Skipping a dream video**: a peer's replayed entry video ran vanilla
  `onFinishedVideo` on skip (a random untracked dream on the host, a bogus request on
  a client), and a skipped exit video ran `endDreaming` inside the apply guard, which
  left the dream flagged active. The replayed copy now just ends; a real transition is
  skipped next frame.
- A dream ending during a client's entry video left the overlay and black screen up;
  an all-dead end left the dialogue-door and forest-spirit state for the next dream;
  a client could spawn its own unsynced dream forest spirit; a client entering a dream
  from a cellar left the cellar running. All fixed.
- Apply flags across dreams, cutscenes, the prologue, locks, examine and chapter
  progression were set and then hard-cleared instead of restored (the same clobber
  fixed for sounds in 0.8.130); all restore now.

### Host migration in a dream (was parked)

- **Losing the host mid-dream now migrates instead of disconnecting everyone.** Every
  peer has its own copy of the world and of the dream pad, so the dream carries over
  (`Domains/Dream/DreamSyncManager.Migration.cs`):
  - The elected survivor takes the session: it runs the pad's AI, owns the story end
    and the all-dead end. Those wait up to 25 s for the other survivors to rejoin, so
    they get the same end; a story end it had already asked the old host for runs then.
  - The other survivors keep their dream bookkeeping (moved from the old host's id to
    the new host's), reconnect while staying on their pad, and confirm with
    `DreamEntered` (the new host freezes their stand-in until then). A death or a
    deferred story end the old host never answered is sent again.
  - The new host lets migration survivors through its mid-dream join refusal (LAN and
    Steam); new players are still refused.
  - A peer that was not inside the dream yet (entry video, pad loading) leaves it with
    no reward, and the migration goes on. A rejoined survivor whose new host has no
    such session leaves too.
  - A dead local player no longer reads as "everyone dead" while the survivors are
    still reconnecting.
- **Peers now build the dream pad's navigation graph** as vanilla does: the peer load
  copied only the scene, so a promoted host's dream enemies had no paths (and every
  client's dream exit logged "grid graph not found").
- **No character-backup restore while dreaming**: a reconnect mid-dream swapped the
  dream inventory for the backup's.
- Not changed: a dream left without being finished (reject, disconnect, host lost
  before entry, story-end timeout) gives no reward, as before.

---

## 0.8.130 — Sound pass: one owner per sound

Branch `dev-entity-sync-remaster`, on top of 0.8.129. **Protocol 29 → 30.** Product
**0.8.129 → 0.8.130**. Built and unit-tested; **runtime is not playtested**. Found by
a code audit of the sound sync, not by a report.

### Doubled and endless world sounds on peers

- **Every synced object sound played twice on peers.** The host (and each client)
  forwarded every world-object sound as `PlayerAudio`, and the peer also re-ran the
  same vanilla method from synced state, which plays its own sound. Doubled: door
  open and close (`Door.openSound` is randomized per peer, so two different clips),
  barricade and door hits and breaks, lamp and switch clicks, light and generator
  start/stop, and game event sounds. Now a sound played inside a vanilla method that
  peers re-run (`Door.open/close/getHit/destroyBarricade/destroyDoor`, every
  `ItemSounds` play method, the `GameEvent.fire` body) is owned by that replay and
  never forwarded (`Audio/ReplayOwnedSound.cs`). Host-only spirit FX game events
  (`def_glow` / `def_shadow`, never fanned out) keep the forward.
- **Forwarded loops never stopped.** A forwarded object loop (fire, generator hum,
  radio, stove, a host's crate scrape) played on peers as a bare positional sound,
  and nothing forwards a stop. No looping sound is forwarded any more; each peer's own
  copy of the object plays and stops its loops.
- **Game event replays echoed back.** A replayed event's sound steps run after its
  delay, outside the apply guard, so the peer forwarded them back to the sender. The
  replay scope now covers those steps.
- **Barricade replay now plays vanilla's sounds.** A barricade break played the break
  sound and then a hit sound; a door break played its break sound twice; a metal door
  hit played the wood hit instead of the metal clang. The replay now plays exactly what
  `Door.getHit` played on the sender.
- **A silent forwarded play stopped the sound everywhere.** A world play at volume 0
  arrived as `AudioController.Stop(id)`, killing every instance of that id on the peer.
  Silent plays are no longer sent and are ignored on arrival.

### Forwarded world and creature sounds on clients

- **World sounds landed on the host's body.** Rules meant for a player's own sounds
  were applied to every forwarded sound: a door's `door_hit_metal` (also a player
  blocked-hit sound) and any id containing `activate` / `switch`+`light` were moved to
  the host's stand-in, and ids containing `_get` / `_hide` played 2D in the listener's
  head. They now apply only to the sender's own player sounds (`StickToSender`).
- **Per-player hear gate flipped by world sounds.** World sounds from the host shared
  the host body's sticky range gate; they now use the stateless range band.

- **Lamp replay clicked on every state change.** Peers played the switch click for
  every lamp toggle, including power restores, scripted toggles and the late-join bulk
  (a click from every lit lamp on joining), and added an end sound vanilla never plays
  for an unpowered lamp. `LightState` now carries `Switched` (set inside
  `Item.switchMe`); the click plays only then, and the rest is the item's own
  `turnOn` / `turnOff` sounds.

### Hearing range: each sound's own

- **Every sound past 690 was cut while connected.** The host keeps areas around
  remote players awake, so a cull is needed, but a fixed 690 (XZ) was shorter than
  many vanilla sounds carry (gunshots, explosions, screams), on the host's own world
  too. A sound is now culled only beyond its own range: a 3D sound as far as the game
  lets it carry (the item's override, else its AudioObject prefab's `maxDistance`),
  never less than the 650 peer range; a 2D sound at the peer range
  (`LocalAudioService.AudibleRange`). The same range gates peer sounds on arrival, peer
  gunshots, explosions and dream sounds, and sets the falloff of a forwarded world sound
  (it used the item override only, else 650).
- **A loop started out of range stayed silent.** The cull ran once at play, so a fire,
  generator or creature idle loop that started far away never became audible when the
  listener walked up. 3D loops are no longer culled; their own falloff silences them
  far away, as in vanilla.
- **Apply flag clobbered.** Several replays (peer sounds, creature sounds, lamps, blood,
  object spawn and destroy, trap and door state, generators, liquid fire) cleared the
  network-apply flag when done, even when an outer scope had set it. They now save and
  restore it.

### Scrape (drag / push) sounds on peers

- **Scrape loop rebuilt on the game's audio path.** `MovingObjectSoundService` played
  a raw `AudioSource`: always the first clip, outside the Sound volume slider and the
  game's fades, its own occlusion filter, and always `movingSound`. It now plays exactly
  vanilla `ItemSounds.Update`: `AudioController.Play(id, object, volumeModifier)`, the
  grass scrape off a Ground, stop with the 0.5 s fade.
- **Drag release stopped every scrape in the world.** The release stop killed every
  playing `AudioObject` with that scrape id, including other crates being pushed by
  other players. It now stops only loops on the released object.
- **Drag release could hit the wrong crate.** The stop looked the object up with
  `GameObject.Find(name)` (first same-named object anywhere, e.g. a dream-pad twin)
  and zeroed its velocity. It now resolves the object nearest the local player.
- Removed the `PlayerAudio` stop-signal and object-name fields and their receive
  branches; nothing has sent them since 0.7.76.

### Dream audio

- **Creature sounds doubled in dreams.** Host dream audio forwarding also sent
  `CharacterSounds` that `EntitySound` already plays, and the host's own player sounds
  that `PlayerAudio` already carries. Both are skipped now, as are replay-owned sounds
  and loops.
- **Dream sounds bypassed the Sound slider.** Clients played them on raw
  `AudioSource`s with a hand-rolled clip lookup (first clip only, many ids unresolved).
  They now play through `AudioController`. The clip lookup cache is gone.

### Voice

- **Proximity voice only worked when touching.** `VoiceRangeFull` / `VoiceRangeMax`
  defaulted to 8 / 28 labelled metres, but the game measures in units where a body is
  about 40 across and peer sounds carry 650. New keys `VoiceFullVolumeDistance` (150)
  and `VoiceMaxDistance` (650); the old keys are ignored (config key change). Voice is
  now heard from the listen position (the followed player while spectating).

### Smaller

- AudioController forward prefixes return before any component lookup when no
  session is live (they ran on every parented sound in single player).
- `ItemSounds.Update` suppression and drag stop use a cached field accessor instead
  of a `Traverse` per call.

---

## 0.8.129 — Entity sync remaster: the player being hit decides the hit

Branch `dev-entity-sync-remaster`. The host still runs every enemy. What changed is
who judges an enemy's hit on a client, and how clients show host enemies.
**Protocol 28 → 29.** Product **0.8.128 → 0.8.129**. Built and unit-tested;
**runtime is not playtested** (first checks in `PLAYTEST.md` section 0).

### Enemy hits on clients ("defender decides")

- **Dodges that looked clean still hit.** The host judged an enemy swing against its
  copy of the client, which trails the real client by about a round trip plus two
  snapshot steps. Now the host sends each attack frame (`EnemyAttack` 147: enemy id,
  sensor or projectile, final damage, host time, attack clip and frame). Each client
  re-creates the attack on the enemy as it sees it, and only that client's own player
  can be hit by the copy. The host original still hits the host player, other enemies,
  barricades and doors, and skips remote stand-ins. Covers melee (`Character.melee`),
  ranged `SensorType` shots (`rangedAttack`) and activity projectiles (`spawnProjectile`),
  both bullets and thrown items. A strike older than 0.6 s on arrival is dropped, not
  landed late.
- **Client copies of enemies fired their own attacks.** A client playing a host attack
  clip ran the clip's attack frame (event 997): a second, unsynced melee sensor that
  could hit the client on top of the host's `DamagePlayer`, damage local doors and
  enemies, and (banshee) spawn a scream that the client then fanned back to the host.
  Clients now skip event 997 on host-driven enemies, and any enemy-type melee sensor
  on a client that is not a re-created copy deals nothing.
- **Repeated swings were invisible.** The snapshot stream only replays a clip when its
  name changes, so a second `Attack1` right after the first never played on clients.
  The attack frame now replays the clip at the host's frame.
- **Enemy projectiles were not shown on clients at all.** They are now re-created as
  above. A thrown copy only flies and judges a direct hit on the local player, then
  goes away; the host original keeps the landed object, the blast and any spawn.
- **Blood and sound for other players.** The hit client reports it (`EnemyHitConfirm`
  148, damage already applied there); the host plays the hit sound and blood on that
  player's stand-in and fans the blood to the other clients.
- **Stays host-decided (not dodge-timed):** the `damagesAroundMe` aura, flier dive,
  `Shooter` turrets, explosions (radius damage), night shadows, traps and fire.

### Enemy presentation on clients

- **Host clock on snapshots.** Every `EntityState` batch carries the host time. Clients
  keep a short pose history per enemy and render it 75 ms (near) / 150 ms (far) behind
  the estimated host clock: smooth motion at the host's real speed, a 50 ms coast
  at most when a packet is late, then hold. Replaces "lerp 100 ms from arrival".
- **20 Hz near players.** Enemies within 800 of a remote player are sent every 50 ms,
  the rest at 10 Hz (was 10 Hz for all). A body that stops gets one more send so
  clients pin the stop pose instead of coasting past it.
- **Smaller snapshots.** Name and prefab path (most of each entry) travel only on an
  id's first three sends and on the 1 s resync; clients cache them per id. A joiner
  waits at most 1 s for an enemy's name.
- **Recycled ids kept the old name on the host.** The name/prefab cache was keyed by id
  only, so an id handed to a different body after a wrap sent the old body's name.
  It is now tied to the body that owns the id.

### Shadow sensor fixes (host-decided path)

- **Shadow hits on clients took armor and interrupted.** Vanilla routes a shadow
  sensor through `getHitByShadow` (flat, no armor, no interrupt); the host relay sent a
  normal hit. `DamagePlayer` now carries `ShadowHit` and the client uses
  `getHitByShadow`.
- **An enemy sensor that hit a client could not reach the host player.** The host relay
  consumed the sensor on a stand-in hit; vanilla does not consume an enemy sensor on a
  player hit. Only player weapon sensors are consumed now.

### Key files

`Domains/Combat/Patches/DefenderAttackPatches.cs` (host send, capture, client 997
skip, client sensor / bullet / throw filters), `Domains/Combat/EnemyAttackNetHandlers.cs`,
`Core/EntityTimeline.cs` (host clock + pose history, unit-tested),
`EntityStateBroadcastService`, `ClientEntityInterpolationService.{cs,Snapshot,Tick}`,
`HostCombatPatches`, `ProxyDamagePatch`, `ExplosionFriendlyFirePatch` (blast depth),
`BulletFXSyncPatch` (enemy impacts not forwarded), messages in `CombatMessages` /
`WorldMessages` / `PlayerMessages`.

---

## 0.8.128 — Code-health audit: dupes, relay, saves, patching, structure

Six parallel audits (networking core, session/saves, Harmony patches, world/items,
combat/story, architecture/tests) followed by a fix pass over every finding.
**Protocol 27 → 28.** Product **0.8.127 → 0.8.128**. Built against stand-in
references only (no game DLLs in the build environment): every change was compile-
checked for new errors on both loaders and the test suite passes, but **runtime is not
playtested**. First things to run: two players looting the same container slot,
a saw convert while the other player takes wood, host migration and reconnect, a
manual-save load, and a Steam client that drops and rejoins (`PLAYTEST.md` section 0).

### Item duplication and loss

- **Container take/place validation never ran for clients.** Every host check was
  gated on `!IsApplyingRemoteState`, which is true for every inbound message, so two
  players looting the same slot both kept the item and place amounts were unbounded.
  Validation now keys on "came from a client"; denies refund exactly and stop the relay.
- The host no longer spawns items from a client's physics snapshot (a picked-up item
  could come back from a client's 5 s resync).
- Remote pickup destroy matches exactly (normalized name / item type, 2 m, dropped
  items only) instead of fuzzy substrings over 8–12 m that could delete a nearby
  wardrobe or pile; the host only grants a world-pickup claim it could actually remove.
- Saw stock: clients send convert/fuel deltas, the host validates against its own stock
  and broadcasts the absolute result (was last-writer-wins absolute stock).
- Pending-take bookkeeping is per container and timestamped; a deny refunds what was
  actually granted. Workbench level only goes up and is not double-sent.
- Loading a manual save no longer re-applies the pre-load character on top of the slot.

### Relay and transport

- A deferred saw update set the "do not relay" flag from the per-frame tick and it
  swallowed the next client message's relay. Per-message state (relay flag, sender id,
  delivery method) is now reset around every inbound message; the sender id is -1
  outside dispatch.
- The host drops traffic from refused peers, from peers that have not completed the
  handshake, from unknown peers, and `[HostOnly]` message types sent by a client
  (forwarded-player wrappers, bulk syncs, world share, roster, dream start/chain, …).
  Client-originated cutscene starts and dropped-item deny/remove are rejected and not
  relayed; every handler reject path suppresses the relay.
- Relays keep the inbound delivery method: 30 Hz drag/voice streams stay unreliable
  instead of queueing every reliable event behind them.
- Steam: a stale connection closing no longer tears down a player who already
  reconnected; the reliable send backlog is capped (a stalled peer is dropped); packets
  are copied once.

### Saves and sessions

- A promoted host (after migration) never auto-saves: not on exit, not on a client's
  SaveSync request.
- The exit checkpoint save skips chapter transitions, dreams, held night deaths and
  game quit.
- "Resend world" no longer permanently mutes clients already in the world.
- World-save swaps are journaled and recovered at startup after a crash; the inflate of
  a host package is size-capped; the campaign identity file is written atomically, never
  re-minted on a parse error, and the host's own campaign is no longer labelled a co-op
  copy. Manual save slots are per profile.
- Player-id rebind on reconnect is limited to ids the host reserved, and moves every
  per-player record. The host derives a client's backup identity from the connection,
  not the uploaded JSON.

### Patching

- The MelonLoader build applied every patch twice (MelonLoader's own PatchAll plus
  ours).
- Patches are applied class by class; a failed class is logged by name and, unless it is
  cosmetic, Host/Join refuse to start instead of running half-patched.
- AI targeting is vanilla while hosting alone; one `Core.AddPrefab` detour per overload
  replaces seven (no `object[]` boxing); one `displayNextBoard` patch replaces four;
  per-frame reflection and component lookups in hot patches are cached; the grid lookup
  tests cached bounds first; the client clock override is restored once on session end.

### Combat and story

- A night-dead host whose last living peer leaves now resolves the morning instead of
  spectating forever; a client death just after dawn is no longer recorded as a night
  death.
- Client attacks are range-checked, rate-limited per peer, and rejected from dead or
  dreaming attackers; BulletImpact only spawns allow-listed FX prefabs; shadow armor
  damage is applied by the host; NPC dialogue locks carry the world bit so a renewal
  never replays the enter-dialogue triggers; host-side queued flag deltas are flushed.
- Host migration repopulates the character tracker (NPCs no longer vanish for
  reconnecting clients); trap ids minted by a promoted host cannot collide.

### Performance

- Map discovery no longer rescans the whole scene per pending name per frame;
  pad resyncs on first enter are limited to the pad instead of the whole map;
  phantom re-matching is throttled; trap/door resend caches are pruned by age.
- Legacy logging builds no strings when it is off (interpolated-string handler); the
  perf probe is opt-in (`Debug.PerfProbe` or the Dev/Trace preset).

### Structure

- Inbound dispatch is one handler table (`On<T>`/`OnRaw`) instead of five switch
  partials and 134 copies of the deserialize call.
- Eight pass-through handler facades are gone; one network-manager locator
  (`ModRuntime.Network`); `NetGuard` replaces 70 copies of the session check; no
  underscore fields reached from outside `LanNetworkManager`; shadow tracking has its
  own registry.
- Wire messages read exactly what they write; the dead "older peer" trailer branches are
  removed (the handshake already refuses other protocols).
- Session statics found leaking are reset (pending light queue times, dream story-end
  deferral, unused final-dream death pass, client pad-slot owners, proxy registry, voice
  walkie/linger flags, among others), and a structural test over every runtime folder
  requires each static to be reset on network stop or marked process-scoped.
- Tests: a reflection round-trip over all 136 message types, carried-state round trips,
  whole-tree Harmony rules (Finalizer for Prefix/Postfix flags, coroutine prefixes,
  narrow exception swallowing) replace the source-text greps; the 500-line file cap is
  gone. CI now runs on `dev`. The product version has one source (PluginInfo).
- Transport seam: LAN and Steam peers sit behind one `IPeerTable` (count, route,
  rebind, drop, roster address); the fan-out and session code no longer branch on the
  backend, and a LAN receive resolves its sender in O(1) instead of a linear scan.
- Per-peer bookkeeping (handshake, refused, loading, late-join bulk, sequence
  high-water marks) is one `LinkState` replaced whenever the transport stops; it sits in
  a `SessionState` replaced on network stop. The heavy late-join retry counter leaked
  across sessions before this.
- `WorldPhysicsSyncService` keeps its ~40 session collections in one session object
  that a reset swaps out (thrown lights survive a host promotion on purpose).
- New `docs/ARCHITECTURE.md`: source layout and the rules a change has to follow.
- Comments no longer narrate version/batch history; 0.8.99 and earlier moved to
  `CHANGELOG-ARCHIVE.md`.

## 0.8.127 — Full-mod audit pass (transport, patches, dupes, lifecycle)

Five parallel code audits (0.8.126 diff, networking, Harmony patches vs the
decompile, gameplay lifecycle, product/docs) followed by a full fix pass.
Nothing was deferred. **Protocol 26 → 27** (new message 146, handshake
password, throwable land flag, long-string framing). Product
**0.8.126 → 0.8.127**. **Runtime: not playtested** — see
`DarkwoodMP.Mod/docs/PLAYTEST.md` for the 0.8.127 checklist; the first things
to run are a busy night on LAN (entity chunking), a client walking through a
container deny, and chapter 1→2.

### Blockers

- **LAN entity/physics sync died in busy scenes.** `EntityState` (up to 256
  entities) and `PhysicsState` went out as one `Unreliable` packet; LiteNetLib
  throws `TooBigPacketException` above the single-packet size (~1 KB) and
  nothing caught it, so `Update()` aborted every tick and clients stopped
  seeing enemies. Snapshots are now chunked per peer limit, each chunk with
  its own sequence; oversize sends on a non-fragmentable method are promoted
  to `ReliableOrdered`; `PollEvents` and per-peer `Send` are guarded with
  throttled logging (`EntityStateBroadcastService`, new
  `LanNetworkManager.PhysicsSend.cs`).
- **Stacked `[HarmonyPatch]` attributes only patched the last target.**
  HarmonyX merges class-level attributes. `ClientAIDisablePatches` left 19 of
  22 client AI methods unpatched (clients ran vanilla AI beside host
  snapshots); `NoWorldPausePatch` left Map/Journal/Padlock/Dialogue/skill
  menus pausing the world. All converted to `TargetMethods()` with a
  logging helper (`Harmony/PatchTargets.cs`); pause/unpause symmetry
  re-checked per UI (dialogue now hooks `onTweenClose`, where vanilla
  unpauses). **Playtest:** client enemy animation — the AI skip has never
  actually run before.
- **Pickup completion never fired.** `PlayerPickupDroppedItemPatch` and
  `ThrownItemCombatDespawnSyncPatch` tested `__instance != null` after
  vanilla `Object.Destroy` (deferred, so always non-null). Success is now
  read from state (slot emptied / vanilla `DestroyMe` recorded), so peers no
  longer keep ghost drops and knives leaving the world send
  `WorldObjectRemoved`.

### Items / dupes

- Deny refunds scanned only `Inventory.slots`; vanilla also stacks into
  `Hotbar` → dupe. Both are scanned; removal goes through `removeAmount`.
- Denied drag-from-container left the item on the cursor
  (`Controller.pickedUpItem`) → cursor item is cancelled first.
- Lost GUID pickup race refunded twice (Remove + ClaimDeny fallback) →
  fallback removed; `recipeFor` still travels with the pending claim.
- `ContainerPlaceItemPatch` snapshotted `Player.currentItem` instead of
  `pickedUpItem` → phantom held weapon sent to peers. Both place patches now
  mirror vanilla's guard and check `__runOriginal`.
- Deny snapshot hid the denied slot (`_pendingContainerRemoves` not cleared).
- `_pendingHideoutUpgrades` never cleared across sessions.
- Culled drops unregistered on `OnDisable` → missing from late-join; now
  `OnDestroy`, registry prunes instead of wiping.
- Drag-proxy item leaked on the host when the dragger disconnected.
- Workbench pile sync depth had no finalizer → one throw disabled it.
- Embedded `PlayerId` trusted over the transport peer in DragSync, trade
  presence, vault state, map markers → host uses `CurrentReceivePlayerId`
  and rewrites forwarded DragSync bodies.

### 3+ players

- Forwarded client `PhysicsState` kept the client's sequence under the host's
  id slot → third player rejected doors/traps/generators as stale. Host
  re-stamps from its own counter; door/trap/generator events go
  `ReliableOrdered`.
- Chat relay forwarded raw client bytes (sender spoof) → host re-serialises.
- Host dropped a peer's `PlayerState` relay while it had no proxy for them
  (location load) → relay before proxy lookup.
- Late joiner never got other clients' light state / anim library → host
  caches and replays per client.

### Host migration / reconnect

- Survivors kept `_lastSnapshotSequence` → entities frozen for ~the old
  host's uptime. `StopTransportOnly` resets inbound sequence state.
- A failed in-world (re)connect (`ConnectionFailed`/`Rejected`) triggered
  migration → client promoted itself to a second host. Migration now requires
  a completed handshake and a real loss; link failures do a bounded
  reconnect (3 × 5 s) or stop with a visible status.
- Steam migrate-connect failure set `Offline` while retry needed `Client`.
- Trap `NetId` counter rewound to 1 while traps kept old ids → never rewinds.
- Promote cleared thrown-flare tracking (flares burned forever) →
  `ResetForPromote()` keeps it.
- Destroyed-on-fire GameEvent list recorded only on the host → clients record
  it too.
- `CaptureForResume` bailed when the link was already down → captures as
  client from config / last Steam lobby.

### Night / death / spectator

- Every spectator exit called `DeathStateTracker.Reset()`, wiping *remote*
  death bookkeeping (dead peer revived, no dawn release, all-dead never
  resolved). Split into `ResetLocal()` (spectator exits) and the full reset.
- `PreventSpectator` was set then cleared by that same reset.
- `NightDeathSavePatch` skipped the save but `SaveSyncPatch.Postfix` still
  sent `SaveSync` → peers persisted the first night death.
- `ForceExit` never unmuted local audio; F4 had no state guard (now mirrors
  save-menu guards + chat input lock).
- Shared permadeath: `PartyWipeDeclared` stuck when `Begin` bailed.
- Night participant counter → id set; clients drop a leaver's night-dead
  mark; late joiners get the night-dead snapshot.
- Client-owned night shadow froze when its owner left; a late `Unreliable`
  alive update revived a dead shadow.
- Remote day-death saves coalesced (one per 15 s).
- Last peer dropping no longer blocks the morning resolve.

### Dreams / dialogue

- `DreamWantToSwitchPatch` chained the preset twice → `MarkCompleted` → host
  stuck. Removed; `SetChainedPreset` ignores a repeat.
- `IsDeadInDream` never cleared for a peer who died → their story end in a
  later dream rejected. Cleared for all peers on dream start/end.
- Client dialogue outcome rebound `dw.npc` and silent-closed the host's own
  conversation with another NPC. Host-in-this-talk is decided by NPC instance
  before any rebind; outcomes arriving mid-talk are queued; drain-abort keyed
  by NPC+owner; apply guards scoped to the synchronous apply (no 8 s window).
- Stale `_preDreamPosition` teleported the client on any disconnect.
- Dream entry coroutines survived disconnect/reject → generation + session
  id checked after every yield.
- Last remote peer leaving ended the host's dream as a death; host now
  continues solo.
- `_hostOrderedDreamEnd` and `IsDreamActive` leaked across a chain.
- Pull-in closes dialogue/trade/container/journal; dialogue lock lease
  renews while open; `Starting` watchdog (60 s).
- Clone trap: NPC locks keyed by (name, dream/overworld); strict pad-only
  lookups; leave-door replay limited to the door NPC.

### World

- Explosions resolved barrels by bare `GameObject.Find` → position first,
  name within 8 u, same world. Rigidbody resolve capped at 50 u and scoped
  to the dream pad. Body-push sound likewise.
- Destructible match 25 m → 2 m; door same-name fallback 20 m → 2 m.
- Pending-barricade flush ran every frame with up to 64 overlap scans →
  1 s gate, 30 s expiry.
- `GameEventsBulk` aborted on the first throwing event → per-item.
- Pad-slot wait is capped at 30 s with vanilla fallback; host answers
  "unavailable" instead of silence.
- `LocationEnterExit` pending maps and `_outboundRemoveDebounce` reset on
  session end.

### Saves / chapter handshake

- `TryCommitPermanentSlot` overwrote savs/sav/savch file by file; a mid-way
  failure left a mixed-chapter slot. Now validate → stage `.dwmp_tmp` → move
  originals to `.dwmp_bak` → rename in → roll back on failure
  (`WorldSaveShareService.SlotCommit.cs`).
- Client committed chapter 2 to its slot at ack time, then sat in chapter 1
  for up to 300 s → commit moved to `ChapterLoadGo`; abort discards the
  buffered package.
- Chapter broadcast requested during a running share was silently skipped →
  re-run to everyone after the running share; mid-broadcast joiners get a
  per-peer share.
- Host deadline now per peer and moved by progress acks
  (`StatusReceiving`); explicit `ChapterLoadGo(Proceed=false)` on refusal;
  host cap 270 s < client 300 s.
- Stale host-share coroutine could clear the new share's flag → generation
  token.

### Singleplayer parity (mod loaded, no session)

Gated on `IsConnected`: personal flavor HUD hiding, NightShadows perk wave
(vanilla never calls `tryToSpawnShadow`), vault collider toggling, random
event gate, scenario-event null returns (now a `GameEvent.fire` skip),
`LureRemoveHealthPatch` global RNG reseed (state saved/restored), trap
harvest interrupt, cursor confine guard, offline campaign-id minting,
inventory open sound, auto-heal, unique-object keep, map marker patches,
swallowing finalizers (NRE-only, dream-prepare-only).

### Other patch fixes

- `HitscanImpactSyncPatch` postfixed global `Physics.Raycast`; the host's
  `ProxyAggroCheck` matched → predators "shot" proxies. Scoped to
  `Player.spawnBullet`.
- Client melee sensor sent a range-600 sound on every trigger and skipped
  vanilla gates/durability → mirrors vanilla.
- Melee weapon status effects (bleed/poison/stun…) now travel with
  `PlayerAttack`, `FriendlyFire` and `DamagePlayer` as a trailing
  `SensorEffectWire` (≤ 8 effects) and are applied through vanilla
  `CharacterEffects.activate` on the receiving side; friendly-fire effects
  follow the same gate as damage (`SensorEffectCodec.cs`).
- `FastProjectile` sweep state stale on pool reuse; despawn destroyed a
  pooled instance.
- Explosion friendly-fire stash was static (nested explosions) → `__state`;
  FF-off sets `affectsPlayer=false` for the call instead of rolling health.
- Host AI: banshee scream target validated; attacking-flee no longer
  permanently clears `runAwayAfterAttacking`; melee sensor and sniffer gates
  restored; flame keeps burn visuals on clients.
- Well heal no longer re-synced/duplicated; client `GasIgnite` stops
  `waitToBurnNeighbors`; gas trail queue flushes on a timer.
- `[HarmonyPriority]` on classes is ignored by HarmonyX → moved to methods
  (all 43). Static prefix→postfix stashes converted to `__state`.
- `DayDeathTransportHomeGridPatch` checks `__runOriginal`; `BirdAreaPresence`
  resettable; `SoundAreaVolumePatch` keeps loudest-wins.

### Networking misc

- `ThrowableSpawnMessage.HasLandTarget` now on the wire (grounded flares
  were spawned in flight).
- `TrapBulk` chunked at 512; reader throws instead of zeroing.
- Late-join heavy phases retry ×3 then log the phase.
- `ClientStateBackup` / `DialogTreeState` use int-prefixed long strings
  (65535 overflow).
- Steam: connection handle closed on peer loss; lobby password no longer
  published — sent in the handshake and verified by the host.
- `ItemSpawn` / `WorldObjectRemoved` `ReliableOrdered`.
- `AutoRecycle`; throttled logs with suppressed counts (`NetLogThrottle`,
  `ModLog.WarnRate`); handler exceptions no longer relay except for a
  sender-authoritative allow-list.
- `Slice()` always copies; `Reset()` dead guard fixed.

### Settings sync (new message 146 `SessionSettings`)

Friendly fire, loot-share mode, double items and the party multiplier were
per-peer config; a client with FF on saw hits the host dropped, and the
multiplier was ×2 on clients / ×3 on the host. The host now sends
`SessionSettings` after handshake and whenever the roster or its own settings
change (checked on the roster tick, so a host config edit reaches peers
within a few seconds); all gameplay readers go through
`Core/SessionSettings.cs`.

### UI / config

- Defaults: `FreeCursorForDualBox` **false** (Linux dual-box testers set it
  true), `VerboseEntitySync` **false**, `LogRedactIPs` **true**. Port
  clamped 1..65535. Config save guarded.
- Chat is real: `ChatEnabled` (default true), Ctrl+C / Enter / Esc, with a
  proper input lock (`UI/UiInputLock.cs`, drives `Core.forbidInputs`) also
  used by F2, F3 and the slot picker. Walkie TX and PTT gated on it.
- F2 writes only changed fields on focus loss/close/apply (no more
  per-keystroke saves, lobby id no longer overwritten).
- HOST/JOIN failures show a 4 s label. Invite/launch-lobby joins use the
  join state machine; join statics clear on Offline.
- F3: client save refused; host success only if `sav.dat` changed; Load
  confirms and is refused in-session; restore waits for `!loadingGame`.
- Voice: Steam availability caches only success; leaver's speaker removed
  on disconnect; clip-miss negative cache.
- Bootstrap: resets registered before `PatchAll`; `Stop()` destroys the
  runtime object.

### Product / docs / tests

- `DisplayVersion` → "YokWare Branch 0.8.127 / Path B".
- New `docs/CONFIG.md` (every key), `PLAYTEST.md` rewritten for 0.8.127,
  `TODO.md` reduced to the parked list, `PATH_B_FEATURE_INVENTORY.md`
  deleted (historical), `COOP_COVERAGE.md` baseline updated, README paths
  for Linux and Windows, EntitySpawner (F5) mentioned.
- `pack-release.ps1` rewritten + `pack-release.sh`; CI pins .NET 8;
  `build-loaders.sh` uses `UNITY_DOTNET_SDK`.
- Removed `debug.log`, four unused `ref_*.png`, ~25 dead methods,
  `FreezeTracker` (inert).
- Tests: release consistency, message round-trips (140–146), dispatch
  coverage, config docs, pad-slot allocation logic; hubs split under 500
  lines.

### Post-pass review fixes

A full regression review of this pass found and fixed:

- Non-GUID world pickup still had the blind "claim deny fallback" refund
  (same double-refund as the GUID path) → removed.
- Chapter share re-run: a stale Committed ack from the superseded pass could
  satisfy the host wait, the client then got Go with no package. Begin and
  `ChapterShareAck` now carry a share pass number (trailing field) and old
  passes are ignored; the client drops its go-wait on supersede; the chapter
  callback runs after the re-run, not before; title clients' NotInWorld acks
  survive the re-run.
- Client shadows (now that `ShadowCreature.Start/die` really are skipped on
  clients) were never destroyed after dying → removed after the death clip.
- Steam password: unauthenticated lobby members got the world session, chat,
  roster and entity stream, and held a player slot. They now get only the
  handshake until the password verifies, and are dropped after 20 s.
- A night-dead client that soft-reconnected was never released at dawn → the
  host always broadcasts the (idempotent) release.
- Dialogue: drain now stops if the host opens its own conversation; deferred
  outcomes use the original sender, expire after 15 s or once the host talks
  to that NPC, and hold back that NPC's close; all dialogue apply state resets
  on session end; a drain that finished inside `StartCoroutine` no longer
  leaves a phantom "drain active".
- `UiInputLock`: overlays do not open while vanilla holds input (cutscene,
  dialogue, dream, loading), and release does not clear a lock vanilla raised
  meanwhile; asserted after the vanilla Update pass too.
- Connect-failure reasons only skip migration before the handshake; a
  `PeerNotFound` from a crashed host now migrates. Migration retry interval
  1 s → 6 s (was shorter than the connect timeout).
- Pad slots, inventory dream-prepare guards and the NPC lock lease use the
  network role instead of `IsConnected` (false during a reconnect blip / for a
  lonely host).
- Map marker and LocationEnter/Exit relays carry the host-stamped sender.
- Shift-click take with full bags (vanilla moves nothing) or a partial merge no
  longer tells the host the whole stack left; refunds remove from the bag
  before the hotbar.
- Steam soft-reconnect failure now fully stops the network; per-peer sequence
  marks cleared on leave; any socket exception in a send is contained;
  `PartyWipeDeclared` cleared if the outcome coroutine dies; bird area reset
  keeps the local player's presence; chat relay survives a HUD exception;
  `DialogHostSilentClose` no longer unlocks input under the host's own
  dialogue; multi-target patch groups skip instead of aborting `PatchAll`
  when every target is missing; per-frame `Object.name` allocation in the
  client AI gate cached.

### Parked (untouched, by prior user choice)

- Mid-dream host migration.
- Morning reward needs the host in its hideout (vanilla `startAfterNight`
  behaviour, not a sync gap).

### Notes

- A hard crash in the middle of the slot swap leaves `.dwmp_bak` files
  next to the slot for manual recovery; every software failure path rolls
  back automatically.
- The host validates peer-supplied sensor effects for well-formedness only;
  it cannot verify they match the attacker's weapon (trusted-LAN model, same
  as the existing FlagSync stance).

---

## 0.8.126 — Campaign-blocker pass (chapter 2, pads, night release)

Full-mod review against the decompile found four ways a real campaign could
stop. All four are fixed in code. **Protocol 25 → 26** (new messages 140–145,
new trailing fields). Both installs need the same DLL. Product
**0.8.125 → 0.8.126**. **Runtime: not playtested** — dual-box ch1→ch2 and a
night with one dead peer are the first things to run.

### Chapter 1 → 2 transition

- **Steam sessions could not resume after chapter 2 loaded.** Chapter resume
  always rehosted/reconnected over LAN. `CaptureForResume` now records the
  backend and lobby; a Steam host keeps its lobby through the scene load and
  re-listens on it, Steam clients rejoin the same lobby
  (`ChapterSessionResume`, `HostConnect`, `Steam/*`).
- **Host tore the network down 0.4 s after queueing the world share.** Slow
  peers could miss the chapter save, then fall back to their own stale local
  chapter after ~36 s. New handshake: client sends `ChapterShareAck` (140)
  after committing the shared world; host re-sends to failed clients (×2),
  waits up to 90 s, then sends `ChapterLoadGo` (141) to each client. The stale
  fallback is gone — a client that never got the world shows an error and
  leaves cleanly (`ChapterCommitHandshake.cs`, `ChapterProgressionPatches`,
  `WorldSaveShareService.ClientApply`).
- **"Already in world" trusted any loaded profile.** Handshake now carries
  campaign id + chapter id (LAN and Steam). Wrong campaign → refused as WRONG
  SAVE; same campaign, other chapter → host sends ChapterTransition and
  re-shares (`LanNetworkManager.WorldIdentity.cs`, `SessionHandlers`).
- **PlayerIds were reassigned on every chapter reconnect.** Host snapshots
  stable-key → PlayerId before teardown and rebinds each peer to its old id.
- **Chapter-jump GameEvent skipped when a client walked the trigger.**
  `transportPlayerToObject` + `activeModifier` (vanilla `generateChapter`) was
  classed as a personal teleport. It is now a world event: host always runs
  it, connected clients never self-fire it (`GameEventPersonalActorPatch`).
  Client no longer shows the vanilla fade before the host's transition
  arrives (presentation only).

### Outside-location pads

- **Same location at different coordinates on different peers.** Vanilla
  places a new pad at `locationPositions[actualSpawnedLocationsCount]` with a
  random yaw, so pads depended on each machine's first-enter order, while
  in-pad sync is keyed by absolute position. Host now owns a
  name → (slot, yaw) map; clients request a slot (`LocationPadSlotRequest`
  142) and wait before the marker spawns; host broadcasts assignments and
  sends the map on late join (`LocationPadSlotSync` 143). Pads that already
  diverged in an old session cannot be moved; they are logged
  (`OutsidePadSlots*.cs`, `OutsidePadSlotPatches.cs`).

### Night / death

- **Night-dead peer spectated forever if anyone survived to dawn.** Release
  only ran on the all-dead path. Host `startDay` now broadcasts
  `NightDeathRelease` (144); dead peers leave spectate and go home, host
  clears remote night-death state. Night-death window now mirrors vanilla.
- **Clients never got the morning survival reward.** Host sends
  `MorningReward` (145) per surviving peer in the hideout: their own
  night-trader reputation, saturation +10, trader help — once per morning.
- **Permadeath asymmetry (hard/nightmare).** Host now uses the same shared
  death rewrite as clients when peers are connected. A night party wipe ends
  the run for everyone only if every death was permadeath-eligible
  (`SharedPermadeathDeath.cs`, `PermadeathPolicy`).

### Robustness

- **Container take dupe.** Host rejected nothing when a take claimed more
  than the slot held. Now denied; refunds remove the granted copy (durability
  / ammo / recipe match) instead of the last slot of that type.
- **One handler exception broke 3+ player forwarding.** Dispatch caught only
  malformed packets; anything else escaped the poll loop and skipped the host
  relay, and the suppress-forward flag could stick. Catch-all with
  rate-limited log; flags reset in `finally`; a message whose local apply
  threw is still relayed.

### Parked (untouched)

- Denied `grabItem` take (item in cursor) still has nothing to refund.
- Morning reward needs the host in its hideout (vanilla `startAfterNight`).
- Failed chapter client must rejoin manually (host re-shares on mismatch).
- Denied dropped-recipe pickup when the pending claim is already gone (deny
  fallback has only the wire `ItemType`) — needs a wire field.

### Post-merge review fixes

- All-dead night granted clients the morning reward; dead peers are now
  marked before the respawn clears them.
- Client chapter go-timeout now outlasts the host's retry-extended ack wait.
- Morning release was dropped for everyone if **any** peer was dreaming at
  dawn. Now gated only on the local peer's own dream
  (`DeathStateTracker.Release.cs`).
- Shared permadeath body counter and party-wipe flag leaked when a death
  coroutine was killed by a scene load → next wipe hung. Reset on network
  reset and Single scene loads; wipe waits at most 30 s.
- `_localNightDeathDay` survived world reload → host skipped one morning
  reward after start-over. New `DeathStateTracker.ResetSession()`.
- Denied pickup of a dropped recipe was not refunded (dupe); `recipeFor`
  now travels with the pending claim.
- Host dying on nightmare/last-life while a peer was still loading could play
  the permadeath video twice; one shared-death decision now drives both
  paths.

---

## 0.8.125 — Save-safety + story-softlock pass closed

Code-proven save-brick and story-softlock holes for the whole class. Protocol
**25** unchanged. Product **0.8.124 → 0.8.125**. Not 1.0.

### Save brick — fixed this pass

- **Connected client world Save.** Vanilla `SaveManager.Save` writes
  `Flags.SaveState` (story flags, NPC dialogue/rep/dead) into DynamicSave. A
  connected client's local Save could persist client-only mutations over the
  host-shared copy. Clients now block disk Save except during host-coordinated
  SaveSync (`_isRemoteSaveInProgress`). Personal bag/skills still go through
  ClientStateBackup; Postfix still requests host SaveSync.
- **Permadeath difficulty restore.** Shared-death rewrite arms the real
  difficulty and restores it before Save (0.8.80). The yield loop now restores
  in `finally` so a suppressed Save or mid-death throw cannot leave nightmare
  forced to normal for a later Save. Host never rewrites difficulty.

### Save brick — already covered

| Row | Status |
|-----|--------|
| Client nightmare / last-life → shared death; Save sees real difficulty | covered (+ finally harden) |
| Client `generateChapter` only reloads host's current chapter | covered |
| Dream failure AbortStarting — no MarkCompleted / no default reward | covered (+ End/OnRemoteDreamEnded harden) |
| UniqueObjects dream remap stash + removeObject keep overworld twin | covered |
| Host-coordinated SaveSync client checkpoint still allowed | covered (exception to block) |
| NPC portrait/anim/reputation/dead — host Flags only, no second format | covered |

### Story softlock — fixed this pass

- **Dream End MarkCompleted on failure names.** `DreamSession.End` and remote
  `MarkDreamCompleted` skip failure cleanup outcomes (`rejected:*`, disconnect,
  prepare fail, storyEndTimeout) so a stray path cannot party-lock the preset.

### Story softlock — already covered

| Row | Status |
|-----|--------|
| GameEventsFired one-shot host-auth; ActorPlayerId personal vs world | covered |
| Padlock/Locked: failed open AttemptOnly; unlock clears both sides + first-enter | covered |
| Dream bunker leave-door GE under pad; UniqueObjects pad prefer | covered |
| Skill party-once + second peer still picks skills | covered |
| All-dead grace 25s; chain keeps death roster | covered |
| Epilogue pan/credits only for peers in ending | covered |
| Dialogue lock release on speaker disconnect | covered |
| World/GUID pickup claim deny refund | covered |

Parked (untouched): multipleFire local; mid-dream host migration disconnect;
workbench exclusive lock off; N-peer handoff; waitToSpawnShadow; night-trader
rep per-player.

---

## 0.8.124 — Night + combat pipelines closed

Code-proven holes across the whole night and the whole combat pipeline.
Protocol **25** unchanged. Product **0.8.123 → 0.8.124**. Not 1.0.

### Night — fixed this pass

- **Client aura double-hit.** Presentation characters still start
  `waitToDamageAroundMe` on clients; that stacked with host `DamagePlayer`
  when both bodies shared an around-me aura. Connected clients now skip the
  local aura; host still hits every living body in vanilla falloff.
- **Proxy night shadows vs host death.** Vanilla `ShadowCreature.Start` hooks
  `Player.Instance.onPlayerDeathDelegate`, so a client-owned perk wave died
  when the host died. Proxy shadows unhook host death, die when their owner
  is dead / night-dead, and still die on day / ward (immortal).
- **Hard-night worms skip night-dead bodies.** Party worm pick already skipped
  ward / `!alive`; it now also skips `LocalNightDeath` / `IsRemoteNightDead`.

### Combat — fixed this pass

- **Client Flame CharBase contact.** Host `ExplosionSpawnObject` Flame
  secondaries on clients were still burning / damaging local bodies on top of
  host proxy relay. Client Flame skips CharBase; world Item/Door/Window burn
  visuals stay.
- **Client Shooter.shoot belt.** `Shooter.Update` is already suppressed on
  clients; `shoot()` now Prefix-skips on connected clients too so an animation
  path cannot stack with host damage.

### Night checklist (covered vs fixed)

| Row | Status |
|-----|--------|
| Clock / TimeSync absolute / no client day-chain | covered |
| useTimeSkip host-adopted; lie-down no time advance | covered |
| Hunger `tryToActivateHunger` live-step; no false-hunger on jumps; `fedToday` clear | covered |
| Hard-night worms 1/5s random living unwarded body; no `waitToSpawnShadow` | covered (+ night-dead skip fixed) |
| Client shadows move (cruise before zero); immortal+ward dies; proxy targets | covered (+ host-death unhook fixed) |
| Fliers dive actual target / damage that body | covered |
| Area damage every body in falloff | covered (+ client double-hit fixed) |
| Perception remotes (vision, hearing, furniture scrape, barricade hammer) | covered |
| Morning: host `endAfterNight` / trader despawn mirrored; no client `startDay` | covered |
| Both dead → morning; one dead does not end night | covered |
| Client nightmare / last-life → shared respawn (0.8.80) | covered |
| Night trader reputation per-player; stock shared | covered (parked: keep per-player) |

### Combat checklist (covered vs fixed)

| Row | Status |
|-----|--------|
| Melee / shooter / thrown (stick despawn no peer FX loot) | covered (+ shooter client belt fixed) |
| Explosions (proxies + FF-off host rollback) | covered |
| Traps / flame contact / ground DoT | covered (+ Flame CharBase mute fixed) |
| Client smash destructible → BarricadeEvent (0.8.117) | covered |
| Gas pour / ignite host (0.8.103–104); fire spread peers | covered |
| Hit story trigger fan (0.8.88); examine with world-only guard (0.8.118) | covered |

Parked (untouched): multipleFire local; mid-dream host migration; workbench
exclusive lock off; N-peer handoff; waitToSpawnShadow; night-trader rep
per-player; hunger method is `tryToActivateHunger`.

---

## 0.8.123 — Dream + ending systems closed (grace, chain, epilogue gate)

Code-proven holes across the whole dream session and the ending crawl/credits.
Protocol **25** unchanged. Product **0.8.122 → 0.8.123**. Not 1.0.

### Dreams

- **All-dead grace.** Host `UnfreezeProxiesAfterDelay` (10s) was calling
  `ClearRemoteInDream`, which dropped peers still inside their 25s entry grace.
  All-dead could end the pad while a joiner or slow loader was still entering.
  Unfreeze now only clears proxy freeze; `IsRemoteInDream` owns the deadline.
- **Chain death roster.** Host `NotifyPeersStoryEndBeginning` called
  `DreamSession.End` before `transferToDream` / `wantToSwitchDream`, so the
  follow-up `SetChainedPreset` hit Idle→`TryBegin` and wiped the death roster
  via `OnDreamStarted`. Chain outcomes keep the session Active; clients skip
  `End` the same way. Latch clears on `SetChainedPreset`.
- **`allDead` default reward.** Remote cleanup for unrecognized outcomes fell
  through to the preset `default` reward. Non-reward sentinels (`allDead`,
  `playerDeath`, rejects, disconnect) no longer grant default; `allDead` maps
  to `playerDeath` effects when present.
- **Live preset name.** `UpdateActivePreset` now also notes `_localDreamPreset`
  so `ResolveActivePresetName` stays on the live pocket after a random roll /
  chain swap.
- Already covered this pass (no code change): enter host/client/second/late-join,
  skill party-once + second peer still picks skills, dream bunker leave-door GE
  under pad (not overworld twin / UniqueObjects first-wins), failure cleanup
  abort without MarkCompleted, exit clears `dreaming` before UniqueObject
  restore, personal GE rewards via ActorPlayerId (0.8.99), client dream
  one-shots via host proxy EventTriggers + GameEventsFired apply.

### Ending

- **Epilogue death fan-out.** Host/client `onDeath` in epilogue still broadcast
  `PlayerDied` and ran `DeathStateTracker`, which could pull living peers into
  day/night death paths. Epilogue deaths are vanilla crawl/cam only.
- **Living peer gate.** Credits `SceneLoad` and `epilogue_cameraPanOverBurningForest`
  apply only when the local peer is in the ending (`inEpilogue` / epilogue pad /
  epilog session). A peer still in the forest is not dragged because someone
  else finished the crawl. Client crawl still asks the host to fire the pan;
  client/host `goToCredits` still pulls the party when they are in.
- Permadeath / nightmare client death shared respawn path verified unchanged
  (0.8.80).

### World-grid houses (checked after dreams+ending)

Vanilla `WorldGrid.Cullable.hide` / `show` use `SetActive` (and `Location.leave`
/`enter` for large location cullables). World houses are **not destroyed** on
cull — same class as OutsideLocations. No virgin-prefab replay invented.

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.122 — First-enter pad resync covers remaining virgin-prefab state

0.8.119–0.8.121 replayed barricades, opened doors, destroyed items, NPC
visuals, padlocks, fired one-shot GEs (`ActorPlayerId=0`), and key Locked
unlocks when a client first-spawned an outside pad. Sticky lamp/generator
state already re-pushed via `ResyncWorldLightsForPeer` on the same trigger.
Still missing (late-join pending FIFO/age-out before virgin `createLocation`):
InteractiveItem `isOn` (msg 62 — GE `switchItemOnOff` toggles `Item` only, not
levers), constructed props, traps (30s pending age), world Burn, chains,
ShadowArmor on pad props, and saw/feeder/lure stations on the pad. Protocol
**25** unchanged. Product **0.8.121 → 0.8.122**.

- Host `ResyncOutsideLocationPadForPeer`: also pad-scoped InteractiveItemSwitch
  (isOn set-state, not toggle), ConstructibleConstruction, TrapBulk,
  WorldBurnState, ChainState, ShadowArmorState, Saw/Feeder/Lure — dream path
  still skipped. Client settle invalidates matching scene scans and flushes
  pending for those types.
- Containers stay on-open `ContainerStateRequest` (not first-enter bulk). Gas
  trails are network-spawned at coords (not virgin prefab fields).

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.121 — First-enter pad also replays key Locked unlocks

0.8.120 replayed unlocked Padlocks on host first-enter pad resync. Key
`Locked` (not Padlock) had the same hole: late-join `LockedUnlock` shares the
FIFO-capped pending list (64 with padlocks/interactives), and DoorState opened
replay calls `Door.open` without clearing `Locked.locked` (DoorOpen apply does;
first-enter uses DoorState). Unlocked-but-closed doors and non-door Locked
(chests) were never covered by door-open replay. Protocol **25** unchanged.
Product **0.8.120 → 0.8.121**.

- Host `ResyncOutsideLocationPadForPeer`: also send unlocked `LockedUnlock`
  near the pad (host `!locked` only; pad-scoped like padlocks; dreams still
  skip this path). Client apply stays idempotent (`wasLocked` gates host
  `onActivate` synth — directed SendToPlayer, client Role does not re-fire).

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.120 — First-enter pad also replays padlocks and fired GEs

0.8.119 replayed barricades, opened doors, destroyed items, and NPC visuals
when a client first-spawned an outside pad the host had already changed.
Padlocks and fired GameEvents that mutated pad geometry (setActive / remove /
moves) were still missing: late-join `PadlockUnlock` pending is FIFO-capped
at 64, and `GameEventsBulk` pending ages out at 60s — often long before the
client's first `spawnLocation`. DoorState opened replay does not clear
`Padlock.locked`. Cache invalidate alone does not re-send expired events.
Protocol **25** unchanged. Product **0.8.119 → 0.8.120**.

- Host `ResyncOutsideLocationPadForPeer`: also send unlocked `PadlockUnlock`
  and pad-scoped `GameEventsBulk` (fired `!multipleFire`, `ActorPlayerId=0`
  world geometry only; destroyOnFire latch near the pad; dream-named skipped
  unless dreaming; dream SoftMatch still pad-root filtered). Dreams still
  skip this path (dream load owns the pad).
- Client outside-location settle: invalidate Padlock / Locked / GameEvents
  scene scans and flush pending locks/GEs so SoftMatch sees virgin-pad
  children when the resync arrives.

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.119 — Fresh outside-pad spawn gets host world state

A client's first `OutsideLocations.prepareLocation` /
`createLocation` for a pad not yet in `spawnedLocations` instantiates a
virgin prefab. The host may already have opened doors, torn boards,
smashed crates, and changed NPC portraits/anims. Late-join bulk often
ran while that pad did not exist (find-miss / pending cap). Lights
already re-pushed on first remote enter (`ResyncWorldLightsForPeer`);
doors / barricades / destroyed items / NPC visuals did not. Protocol
**25** unchanged. Product **0.8.118 → 0.8.119**.

- Host `HandleLocationEnter` (first enter / soft-reconnect place): after
  light resync, `ResyncOutsideLocationPadForPeer` re-sends existing
  barricade door/window/item snapshots, opened `DoorState` near the pad,
  and `ReputationBulkSync` (portrait/anim trailers). Idempotent apply —
  already-destroyed crates stay destroyed; no container bulk (open still
  uses `ContainerStateRequest`). Dreams skipped (pad owned by dream load).
- Client barricade pending queue now covers destructible `Item` (IsWindow=2)
  the same as doors/windows, so late-join Destroyed crates can flush after
  the pad wakes even before enter resync.

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.118 — Client examine fans examined flags

Host applied a client `ExamineObject` ActionRequest via
`DialogHostApplyGuard.RunHostWorldFanout(examine)`, so triggers and story GE
ran, but `ExaminableExaminePatch` Postfix returned early on
`IsApplyingRemoteState` and never Broadcast `ActionState`. Peers kept
`examined` / `displayedDescriptionPool` unset (re-examine / pool one-shots
desync). Same class as pre-0.8.117 ItemGetHit swallow. Protocol **25**
unchanged. Product **0.8.117 → 0.8.118**.

- `ExaminableExaminePatch.Postfix`: allow fan when `DialogHostApplyGuard.Active`
  (match DoorOpen / GameEventsFired / Journal). Prefix still blocks Request
  re-send under apply. ActionState apply sets fields only; host ignores
  inbound ActionState.
- Sweep of other `IsApplyingRemoteState` / `NetworkApplyGuard` Prefix/Postfix
  send gates: Door/Window getHit already fan; ItemGetHit stays on
  `_processingBarricadeEvent` only (0.8.117); remaining matches are loop
  stops, client host-sim blocks, or visual-only.

### Parked (unchanged)

- multipleFire local; mid-dream host migration; workbench exclusive lock stub;
  N-peer handoff; no waitToSpawnShadow; night-trader rep per-player; hunger is
  tryToActivateHunger.

---

## 0.8.117 — Client smash fans destructible Item mesh

Host applied a client `MeleeWorldHit` on a destructible `Item` (wardrobe /
furniture) inside the inbound receive guard, so `getHit` / `die` ran locally
but `ItemGetHitPatch` returned early on `IsApplyingRemoteState` and never
sent `BarricadeEvent`. Peers kept the intact mesh. Door and Window getHit
patches already fan under that guard; item was the odd one out. Protocol
**25** unchanged. Product **0.8.116 → 0.8.117**.

- `ItemGetHitPatch`: drop the `IsApplyingRemoteState` swallow. Loop stop stays
  `_processingBarricadeEvent` (BarricadeEvent apply). Host local smash still
  fans once (`die` has no second send). Position in the existing message
  still names the pad twin when relevant.
- Workbench pile snapshot depth (0.8.116) and window/radio paths untouched.

---

## 0.8.116 — Workbench pile choke no longer double-sends

0.8.114 added a choke on `removeItemAmountFromPlayer` /
`removeItemDurabilityFromPlayer` so GameEvent pile drains sync. Craft / repair /
upgrade / construct / HammerWork already wrap the same pile with
`WorkbenchSharedPileSync`, so a real pile consume nested the choke and
`SendFullDiff` ran twice with the same before-snapshot — duplicate
`ContainerItem` RemoveItem. On a client craft that second remove hits an empty
slot on the host → deny + take-refund / "Already taken…". Protocol **25**
unchanged. Product **0.8.115 → 0.8.116**. Late-join portrait/anim bulk (0.8.115)
untouched.

- `WorkbenchSharedPileSync`: outermost snapshot depth — nested choke Prefix is
  a no-op; only the outer Postfix diffs once (still covers GE-only drains with
  no outer wrap).
- Bag-only / workbench-closed / `includeAdditionalInventory: false` still send
  nothing (`SendFullDiff` is a true slot diff; Prefix gates unchanged).

---

## 0.8.115 — Late join gets NPC portrait and body sprites

Live `ReputationSync` already carried GameEvent portrait (0.8.96) and
anim-library (0.8.97) trailers for peers already in the session. Late-join
`ReputationBulkSync` still stopped at `attackedID` / `deadID`, so a joiner (or
a peer whose chunk woke after the live packet) kept the vanilla face/body
while the host had the story result — especially when GameEventsBulk soft-
match missed the local copy. Protocol **25** unchanged. Product
**0.8.114 → 0.8.115**.

- `ReputationBulkSync` AvailableBytes trailers: sparse portrait + anim-library
  (same fields as live), filled from host NPC bodies on send.
- Clients apply via existing `ApplyPortrait` / `ApplyAnimLibrary`; queue until
  `NPC.OnEnable` when the body is not loaded yet (late join / other room).
- No new `NetMessageType`. Night-trader standing stay per-player.

### Candidates checked (already in bulk — no ship)

- Generators on/off + fuel: `SyncExistingGeneratorsTo`
- Barricades / constructed sites / dropped items / map / journal / world burn:
  late-join light + heavy phases already cover them
- Reputation standing + dead/attackedID: ReputationBulk since 0.8.93–0.8.95
- Doors: physics door snapshot scan

---

## 0.8.114 — GameEvent pile drain reaches the host

0.8.110–0.8.113 covered craft / repair / upgrade / construct / HammerWork
finish. Vanilla `GameEvent.addOrRemoveInvItem` (and durability drains) still
call `removeItemAmountFromPlayer` /
`removeItemDurabilityFromPlayer(..., includeAdditionalInventory: true)` —
the open workbench pile can be consumed. On the 0.8.99 actor path the remove
runs after `WaitForSeconds` (NetworkApplyGuard already gone) while the
workbench can still be open (e.g. host-replayed container story triggers), so
the client drained a local pile view with no `ContainerItem` diff. Protocol
**25** unchanged. Product **0.8.113 → 0.8.114**.

- Choke-point snapshot/diff on
  `Inventory.removeItemAmountFromPlayer` /
  `removeItemDurabilityFromPlayer` when `includeAdditionalInventory` is true,
  reusing `WorkbenchSharedPileSync` / `SendFullDiff`. Existing entry patches
  kept (no regression).
- No-op when the workbench pile is not open (bag-only). Workbench exclusive
  lock stays off. Hammer alert (0.8.109) untouched.

### Sibling check (rejected — no ship)

- `ExperienceMachine.tryToCook` / LevelingMenu — opens the personal leveling
  UI against the player's own bag only; no shared oven inventory in the
  decompile (generator fuel stays FuelDelta).

---

## 0.8.113 — Barricade finish drains the shared pile on the host

0.8.112 synced construction-place pile drains. Vanilla
`Player.checkFrameTrigger("HammerWork")` when `doneBuilding` loops
`currentConstruction.requirements` through
`removeItemAmountFromPlayer(..., includeAdditionalInventory: true)` against
`openedItemInventory2` — same hole, not covered by construct or
grab/transfer/place patches. Barricade plank world state already fans via
`BarricadeEvent`; 0.8.109 remote-hammer AI alert stays on that path (this
patch does not alert). Protocol **25** unchanged. Product **0.8.112 → 0.8.113**.

- Snapshot/diff the workbench pile on HammerWork finish only
  (`doneBuilding`), reusing `WorkbenchSharedPileSync` /
  `ContainerSnapshotHelper.SendFullDiff` (no second diff path; mid-swing
  hammers send nothing).
- Personal-only plank spend still sends nothing. Host local path fans like
  craft (Broadcast; no self-apply double). Workbench exclusive lock stays off.

### Sibling check (rejected — no ship)

- GameEvent `addOrRemoveInvItem` → `removeItemAmountFromPlayer(...,
  includeAdditionalInventory: true)` — can touch the pile if a workbench is
  open, but unproven that any GE drains the pile (not the bag) with no
  container diff; 0.8.99 actor routing for bag items stays. Left named.

---

## 0.8.112 — Construction place drains the shared pile on the host

0.8.110–0.8.111 synced craft/repair/upgrade pile drains.
`Constructible.construct(manual: true)` still calls
`ConstructionRequirement.removeIngredients`, which uses
`removeItemAmountFromPlayer` / durability drains with
`includeAdditionalInventory: true` against `openedItemInventory2` — invisible
to grab/transfer/place patches. Protocol **25** unchanged. Product
**0.8.111 → 0.8.112**.

- Snapshot/diff the workbench pile on `Constructible.construct`, reusing
  `WorkbenchSharedPileSync` / `ContainerSnapshotHelper.SendFullDiff` (no second
  diff path; one snapshot for the whole requirement loop).
- Placed prop was already synced via `ConstructibleConstructPatch` →
  `ConstructibleConstruction` (remote apply uses `manual: false`, no local
  drain). Only the ingredient consume was missing.
- Personal-only material spend still sends nothing. Host local path fans like
  craft (Broadcast; no self-apply double). Workbench exclusive lock stays off.

### Sibling check (rejected — no ship)

- `Player` HammerWork barricade finish drain — shipped in **0.8.113**.
- GameEvent `removeItemAmountFromPlayer(..., includeAdditionalInventory:
  true)` — personal GE path is 0.8.99 actor-routed; unproven that a GE drains
  the workbench pile (not the bag) with no container diff. Left named.

---

## 0.8.111 — Workbench repair/upgrade drain the shared pile on the host

0.8.110 synced craft pile drains via `CraftSharedPileSyncPatch`. Repair and
upgrade use other entry points that still call
`removeItemAmountFromPlayer` / durability drains with
`includeAdditionalInventory: true` against `openedItemInventory2`, invisible
to grab/transfer/place patches. Protocol **25** unchanged. Product
**0.8.110 → 0.8.111**.

- Snapshot/diff the workbench pile on `InvItemClass.repair` and
  `ItemUpgrade.removeIngredients`, reusing the same
  `ContainerSnapshotHelper.SendFullDiff` helper as craft (no second diff
  implementation).
- Repaired/upgraded item stays on the crafter's personal inventory (vanilla).
  Personal-only material spend still sends nothing. Host local path fans like
  craft (Broadcast to peers; no self-apply double). Workbench exclusive lock
  stays off.

### Sibling check (rejected — no ship)

- ConstructionRequirement / Constructible place drain — shipped in **0.8.112**.
- GameEvent `removeItemAmountFromPlayer(..., includeAdditionalInventory:
  true)` — GE fan-out path, not workbench UI (still parked; see 0.8.112).
- Repair-kit world-object `InputScript` → `repair()` without an open workbench
  pile — gate skips (no `openedItemInventory2` container).

---

## 0.8.110 — Workbench craft consumes the shared pile on the host

Vanilla `CraftingRecipes.doCraft` pulls ingredients via
`removeItemAmountFromPlayer(..., includeAdditionalInventory: true)`, which
can drain the open workbench storage pile (`openedItemInventory2`). That path
uses `Inventory.removeItemAmount` / `InvItemClass.removeAmount`, not the
grab/transfer/place hooks container sync already patches — so a client craft
could keep materials on the host pile (or leave a peer's pile stale). Protocol
**25** unchanged. Product **0.8.109 → 0.8.110**.

- On `doCraft`, snapshot the workbench pile when `openedItemInventory.isWorkbench`.
- After craft, fan existing `ContainerItem` Remove/Place diffs so the host
  applies the consume; product still goes to the crafter's personal inventory
  unless vanilla stacked it into the pile (then PlaceItem carries it).
- Personal-only craft (no pile touch) sends nothing. Workbench exclusive lock
  stays a stub.

### Sibling check (rejected — no ship)

- Rideable bicycle: vanilla has only `porterWhistle` / bike-bell audio (0.8.102);
  no mount/ride path in the decompile.
- Repair/upgrade ingredient drain from the same pile — shipped in **0.8.111**.

---

## 0.8.109 — Client barricade hammer alerts host AI

Vanilla `Player.checkFrameTrigger("HammerWork")` calls
`Character.alertInArea(playerPos, 500f)` each swing while boarding or
dismantling. Co-op `BarricadeEvent` apply only sets plank state on the host, so
remote hammering was silent to host enemies. Protocol **25** unchanged. Product
**0.8.108 → 0.8.109**.

- Host: on remote `Built` (`PlayerBarricade`) or dismantle `Destroyed`
  (`DamageAmount < 0`), fire the same `alertInArea` at 500f — prefer the sender
  proxy position (vanilla uses the player), else the door/window.
- Host local hammer still plays the anim; host does not re-apply its own
  `BarricadeEvent`, so no double alert.
- Dream: no dream-specific barricade block found; if construction runs on a pad,
  alert uses the live object/proxy position (not a stale overworld twin).

### Sibling check (rejected — no ship)

- Other `checkFrameTrigger` noise: footsteps and gunshots already forwarded;
  no repair/chop/shovel `alertInArea` in vanilla anim events.
- Furniture scrape host alert shipped in **0.8.108** — left alone.
- Melee barricade hit `MeleeSensor` 600f is a separate combat path — not this
  pass.

---

## 0.8.108 — Client furniture scrape alerts host AI

Vanilla `ItemSounds.alertCharactersInArea` only fires when `Player.Instance` is
touching or dragging the object. Co-op remote scrape uses MOS and suppresses
`ItemSounds.Update`, so `checkIfMoving` never runs — client push/drag noise was
silent to host enemies. Protocol **25** unchanged. Product **0.8.107 → 0.8.108**.

- Host: while a remote scrape is active, fire `Character.alertInArea` on the
  vanilla 0.5s cadence using `movingAlertDistance` / `movingAlertVolume`.
- Host: widen `ItemSounds.alertCharactersInArea` so remote scrape / remote drag
  names count like a local player body.

### Checked this pass (already covered / no ship)

- Sight acquire: `HostCanSeeEnemyPatch`, `HostCheckForCloserEnemyPatch`,
  `HostSnifferUpdatePatch`, `HostAttackPlayerNearestPatch`.
- Hearing already forwarded: footsteps (`HandleProxyFootstep`), gunshots
  (`ClientFireWeaponSoundPatch`), melee hit noise (`ClientCombatPatches`),
  aim-scare (`ClientAimScarePatch`).
- Banshee / InSightOfPlayer / Shooter retarget / growl / shadow ward / flee
  (`HostDetectionGapPatches`, `HostAIPatches.Targeting`).
- `constantlyAttackPlayer`: field only in DLL; no prefab/asset hit — left alone.
- `AIPath.rotateTowardsPlayerWhenPlayingCustomAni`: faces host during custom
  ani only — not acquisition.

---

## 0.8.107 — Enemy around-me aura hits every body in range

Vanilla `Character.waitToDamageAroundMe` only calls `Player.Instance.getHit`.
The co-op patch retargeted to the *nearest* living body, so when host and a
client were both inside the falloff radius only one took damage. Protocol
**25** unchanged. Product **0.8.106 → 0.8.107**.

- Host applies the vanilla falloff formula to the host body (plus shake/noise)
  and to every living remote proxy in range; proxy `CharBase.getHit` still
  relays via `DamagePlayer` / `ProxyDamagePatch`.

### Checked this pass (no ship)

- `Player.Instance.getHit` sites: `Flier.Update` (`HostFlierDivePatch`),
  `Shooter.shoot` (`HostShooterShootPatch`), `InputScript` cheat kill (skip).
- `Character.attackPlayer` → `HostAttackPlayerNearestPatch` (BirdArea,
  CharacterSpawner, GameEvent, Sniffer, ShadowArmor, self-calls).
- `Explodes.explode` → `ExplosionFriendlyFirePatch` (area + FF-off host restore).
- Traps (`Trigger.checkCollision`): damages the entering `Player` on that peer;
  client stomp already `TrapTriggered` + local getHit.
- `MeleeSensor` → `HostMeleeSensorPatch` / `ProxyDamagePatch`; shadows via
  `ProxyShadowController` / owner skip.
- `Flame` contact uses `CharBase.getHit` → proxy relay; ground DoT is local
  `Player` Update per body.
- Hard-night worm aim / sniffer retarget already shipped.

---

## 0.8.106 — Host combat throw despawn clears peer FX loot

Thrown knives / rocks / flares already fan `ThrowableSpawn` (host combat copy,
client/peer `MuteThrownCombat` FX). When the host combat copy sticks into a
character (or otherwise `DestroyMe`s on land), peer FX copies stayed pickable
while the real item lived in the NPC inventory — `DestroyObjectByPos` cannot
find it, so a world-pickup claim still granted a second copy. Protocol **25**
unchanged. Product **0.8.105 → 0.8.106**.

- `MuteThrownCombat` marks FX copies and clears `stickOnCollide` / land-spawn
  prefab so FX never invents a peer-only stick-into-char grant.
- Host combat `ThrownItem.onCollide` that leaves the world fans
  `WorldObjectRemoved` (consume + ModeRemove) so peer FX ghosts clear or refund
  pending claims. Ground land still uses the existing FX→claim path.

### Checked this pass (no ship)

- Shared world / GUID pickup claim-grant-deny — host-auth solid (optimistic +
  deny/remove refund; Prefix blocks already-consumed). No dual-keep path.
- Window/door barricade place-remove — already `BarricadeEvent`.
- Prologue cutscene catch-up — already `PrologueSync` / `CutsceneSync`.

---

## 0.8.105 — Client burning infection clears it on the host world

A client swinging a flaming torch / melee into an infection splat runs vanilla
`Infection.disappear` locally (`MeleeSensor` non-Character hits are not
redirected), but the disappear Postfix only broadcast when Role was Host — so
the client's splat faded and the host world kept it. Protocol **25** unchanged
(reuses `WorldObjectRemoved`). Product **0.8.104 → 0.8.105**.

- Either peer's `Infection.disappear` sends `WorldObjectRemoved` (`infection_splat`);
  host destroy + N-peer fan-out already existed on that message.

---

## 0.8.104 — Client lighting gasoline reaches the host world

After 0.8.103 pour sync, a client swinging a flaming torch / melee into a puddle
(or a Burn trigger touching Liquid) still called `Liquid.startBurning` only
locally — and that path was Prefix-dropped so dual fire sims would not fight.
Nothing told the host, so the puddles never burned for anyone. Protocol **25**
unchanged (reuses `GasIgnite`). Product **0.8.103 → 0.8.104**.

- Client ignite sends `GasIgnite` and lights a local visual; host validates near
  the actor's proxy, ignites on the host world, Forwardable fans peers.
- Host torch / molotov / neighbor-spread ignite Postfix path unchanged.

### Checked this pass (no ship)

- Dropped items / barricade place-remove / throwable spawn / map discovery /
  journal notes / prologue — already wired (coverage rows stale).
- Digging — no Darkwood dig mechanic in decompile.
- Skill-tree / personal heal/feed/recipes — personal by design.
- **`oxygentank_full` world-pick fan — rejected as silent-skip hole.** Vanilla
  world pick is `Item.getDroppedItem` → already host-auth
  `FinishWorldPickupClaim` / `WorldObjectRemoved` (and GUID
  `DroppedItemPickup`). Empty-tank peer copy stays `OxygenTankStash`;
  `haveItem` softlock is `PeerItemPresence`, not a missing destroy fan.
- **Silent client Prefix sweep (ClientMustNotMutateWorld / Role.Client return
  false / "client skipped"):** remaining NOSEND skips are host-owned worldgen
  spawners, night/shadow AI, bird volumes (host sees proxy), cutscene init
  (host `CutsceneSync` Begin), trader randomize, infection spread, GameEvents
  one-shots (player paths already defer via ActivateCursorAction / Examine /
  DoorOpen / DialogOutcome), gas Object-path molotov scatter (host owns). No
  proven player-action vanish sibling left for 0.8.105.

---

## 0.8.103 — Client gasoline pour reaches the host world

Client ground pour (`Player.waitToSpillLiquid` → `Items/GasolineTrail`) was
Prefix-skipped so clients never invented wild dual scatter, but nothing told
the host to place trails either — the can drained and no puddles appeared for
anyone. Protocol **25** unchanged (reuses `GasTrailSpawn`). Product
**0.8.102 → 0.8.103**.

- Client pour sends `GasTrailSpawn` and places a local visual; host validates
  near the pourer's proxy, spawns on the host world, Forwardable fans peers.
- Generator / saw pour via `FuelDelta` unchanged. Molotov / Explodes trail
  scatter stays host-owned (Object AddPrefab Prefix).

### Checked this pass (no ship)

- Lie-down / `onEndSleep` — vanity wake anim only; does not set `CurrentTime`
  or call `startDay` / `endAfterNight` / `skipDay`. Host and client both end
  sleep without advancing the shared clock. Night→morning is natural time /
  death `skipDay` / `useTimeSkip` (0.8.91). `SleepEndRequest` only
  forward-adopts a clock the sleep anim never changed — not a morning-chain
  hole; no speculative sleep change.
- Window/door barricade place/destroy — already `BarricadeEvent`.
- Hitscan destroying doors/items — already `MeleeWorldHit` redirect.
- Map item `markLocationsOnMap` → `showElement` — already `MapElementDiscovered`.
- Recipe learn / personal heals — intentional local.

---

## 0.8.102 — Client bike bell summons Porter on the host world

Using the bike bell (`porterWhistle`) calls `Location.spawnPorter`, which places
`Events/porterSpawner`. Clients already Prefix-skip `PorterSpawner.Start` /
`waitToSpawn` (host owns the NPC), so a client ring consumed the item and never
placed a spawner the host would run — no Porter for anyone. Protocol **25**
unchanged (reuses `ItemSpawn` with type sentinel `porterWhistle`). Product
**0.8.101 → 0.8.102**.

- Client `Location.spawnPorter` defers to host via existing `ItemSpawn` (not a
  trap prefab path); local “something happened” toast kept.
- Host applies `Events/porterSpawner` at the matching hideout pad (day /
  `porter_inTransit` / `porter_killed` / already-present gates). Peers see
  Porter via entity snapshots — no ItemSpawn fan-out.

### Checked this pass (no ship)

- Eat/drink/medicine/bandage/`InvItemClass.use` personal effects (health,
  `fedToday`, upgrades, recipes, maps) — personal; intentional local.
- `placeOnUse` — code path exists; no shipped asset with the flag enabled.
- Trap `canBePlaced` — already `ItemSpawn` via `TrapPlacementPatch`.
- Host ringing the bell — already host-local `spawnPorter`; no hole.

---

## 0.8.101 — Client padlock / key unlock story events reach every player

A client who cracked a padlock or unlocked a keyed lock already synced the
unlocked state, but the host’s story-trigger replay ran inside the inbound
`NetworkApplyGuard`. That swallowed `GameEventsFired`, so peers never got the
one-shot, and any personal grant hit host `Player.Instance`. Protocol **25**
unchanged. Product **0.8.100 → 0.8.101**.

- Host synth of `onTryToOpenLocked` / `onUnlockPadlock` (padlock) and
  `onActivate` (Locked) now uses `RunHostWorldFanout` (fan-out + actor stamp).
- Does not open the padlock UI on the host (`unlock(false)` unchanged).

### Checked this pass (no ship)

- `onSelectObject` — `Player.selectObject*` only fires it when the target has
  an `OnSelected` marker; that component is registered in the assembly but not
  attached to any shipped scene/prefab/resources asset. Dead path; no message.
- Examine-object story triggers — already host-apply via `ExamineObject` +
  `RunHostWorldFanout` (0.8.x).
- Barricade place/remove — no `EventTrigger` on barricade build/destroy.
- Eat/consume (`InvItemClass.use`) — personal effects only; no `sendTriggerInfo`.
- Projectile impact on world objects — `Bullet`/`ThrownItem` hit `CharBase`
  only; door/item melee already fans via `MeleeWorldHit` + `RunHostWorldFanout`.

---

## 0.8.100 — Client lever / switch story events reach every player

Vanilla `InteractiveItem.switchOn` / `switchOff` force-fire their `EventTriggers`
(area) into `GameEvents`. A client flipping a lever ran that only locally, where
one-shot GameEvents are blocked. The host applied the toggle inside the network
receive, which swallowed the broadcast, so the player who flipped it never got
the story result. Protocol **25** unchanged. Product **0.8.99 → 0.8.100**.

- Host apply of a remote InteractiveItem toggle now fans the GameEvent out the
  same way door / trap / activate fixes do (`RunHostWorldFanout`).
- Does not open the InteractiveItem UI on the host (`switchOn`/`switchOff` only).

### Checked this pass (no ship)

- `onPlayAttackAnimation` — NPC `chooseAttack` on host sim only; client does not cause it.
- `onWakeup` — enum only; `Character.wakeup()` never calls `sendTriggerInfo`.
- `onSpawned` / `onGameObjectActivated` — `EventTriggers` Start/OnEnable lifecycle, not a client-action receive path.
- `onDeath` (Character/Item) — killing blow from client hits already inside getHit `RunHostWorldFanout` (0.8.88).
- `onBurn` — `Burn.Start` schedules a delayed Invoke; `onBurn` runs after `NetworkApplyGuard` ends, so host fire already broadcasts. Client one-shots stay blocked until that fan-out.
- `onSelectObject` — marker + select path exists, but no existing message to host-apply without a new NetMessageType; skipped.
- Barricade place/remove, map discovery, skills confirm, hunger — no matching EventTrigger swallow hole proven this pass (barricade has no triggers; map/skills already have their own sync).

---

---

Older releases (0.8.99 and earlier): [CHANGELOG-ARCHIVE.md](CHANGELOG-ARCHIVE.md).
