# Darkwood Game API Reference (verified)

Verified against `Assembly-CSharp.dll` of the installed game
(`X:\SteamLibrary\steamapps\common\Darkwood`) via MetadataLoadContext dump.
These are the real types/members the mod's Harmony patches target.
Darkwood is a **2D Toolkit (tk2d) game** — no SkinnedMeshRenderers, sprites via `tk2dSprite`.

## Player
`Player : CharBase` (MonoBehaviour) — the local player. Exactly one instance in the scene.

- `void setHealth(float _value)` — patched for health sync
- `void getHit(float damage)` / `getHit(float, Transform, bool×7)` — damage entry points
- `void die()`
- `void updateHealthBar()`
- inherited from `CharBase`: `float Health`, `float maxHealth`, `OnDieDelegate onDie`

## CharBase
Base class of `Player`, `Character`, `NPC`.
- `float Health`, `float maxHealth` (public fields)
- `void getHit(float damage)` + big overload
- `void updateHealthBar()`

## Character
`Character : CharBase` — enemies/creatures (AI). Found via `FindObjectsOfType(Character)`.
- `void attack(GameObject)`, `void attackPlayer()`, `void teleport(Vector3)`, `void die()` …
- Freezing an enemy (host-authoritative sync): disable the `Character` behaviour,
  disable its A* movement (`CharBase.AIpath` field, type `AIPath`, a separate Behaviour)
  and set `CharBase._rigidBody` (private, on `MonoBehaviourExt`) to kinematic.
- `NPC : MonoBehaviour` is NOT a Character subclass — traders/dialogue NPCs are
  unaffected by Character sweeps.

## Item (world objects / draggable furniture)
`Item : MonoBehaviour` — everything lying in the world, incl. pushable wardrobes.
- `void startDragging()` / `void stopDragging(bool force)` — patched for furniture sync
- fields: `bool draggable`, `bool beingDragged`, `RigidBodyPresetType rigidBodyPresetType`
- `Player.startDragging()/stopDragging()`, field `Player.itemBeingDragged`

## Door
`Door : MonoBehaviour`
- `void open(Vector3 openerPosition, Transform openerTransform, float OpenForce)` — patched
- `void close(Transform openerTransform)` — patched
- `void openClose(Transform openerTransform)` (+ overload with int force)
- fields: `bool opened`, `int openForce`, `string name`, `bool barricaded`, `bool destroyed`, `bool blocked`
- also: `lockMe(string keyType)`, `unlock()`, `barricade(bool byPlayer)`, `destroyBarricade(bool)`

## Controller
`Controller : Singleton<Controller>` — global game controller incl. day/night cycle.
- fields: `int day`, `int currentTime`, `float gameTime`, `float dayTime`, `float nightTime`, `bool doUpdateTime`, `bool isHardNight`
- `void startNightMode()` / `void endNightMode()` — patched (day/night sync)
- `void startDay()`, `void setDay(int dayId)`, `void skipDay()`
- delegates: `onNewDay`, `onDayStart`, `onNightStart`, `onEndNight`, `onUpdateTime`
- `Singleton<T>` base has static field `_instance`; safer lookup: `FindObjectOfType(Controller)`

## Player — animation & items
- `tk2dSpriteAnimator torsoAnimator` / `legsAnimator` — torso clips encode held
  weapon, aiming and attack swings; legs clips encode movement. Syncing clip
  names (`animator.CurrentClip.name` → `animator.Play(name)`) shows held items
  and attacks on remote players. Player clones keep working animator fields.
- `InvItemClass currentItem` — held item; `Inventory Hotbar`
- `Transform spawnDroppedInvItemm(bool droppingPickedUpItem, string itemType, int itemAmount)` — patched (drop sync)
- `Transform spawnDroppedInvItem(InvItemClass _item)` — patched (drop sync)
- `Item.getDroppedItem()` — player picks up a dropped world item — patched
- `InvItemClass` fields: `string type`, `int amount`
- tk2d types are compile-time usable (Assembly-CSharp is referenced) — no reflection needed

## Inventory
- `InvItemClass addItemTypeToPlayer(string type, int amount, bool dropIfNoRoom)` — patched (pickup notify)
- `InvItemClass addItemType(string type, int amount)`
- `int removeItemAmountFromPlayer(string type, int removeAmount, bool includeAdditionalInventory)`

## InteractiveItem
`InteractiveItem : MonoBehaviour` — levers, generators, switches.
- `void switchOn()` / `void switchOff()` — patched
- `void switchMe()`, `void open()`, `void close()`
- fields: `bool isOn`, `EventTriggers onTrigger`, `EventTriggers offTrigger`

## Time types
- `TimeAndDay` (plain object): `int day`, `int time`, `addMinutes(int)`, `toMinutes()`
- `TimeOfDay` (condition object): `Daytime daytime` (enum: `none`, `day`, `night`)
- `OnTimeOfDay : MonoBehaviour` — trigger that fires at a time of day

## WorldGenerator (procedural world)
`WorldGenerator : MonoBehaviour` — generates the world on NEW GAME (not on load).
- `void generateWorld()` — entry point; phases: `createNextWorldChunk`,
  `populateNextWorldChunk`, `spawnMiscObjects`, `getLandmarkPos`,
  `getRandomPlayerSpawn`, `assignMustSpawnLocations`, coroutines for
  landmarks/grid objects/roads.
- All layout decisions use `UnityEngine.Random.Range` (verified in IL) —
  re-seeding `Random.InitState(seed + stepCounter)` at each phase makes
  generation deterministic (see WorldGenSeed_Patch). Per-step anchors make it
  immune to frame-timing/interleaving differences.

## Dreams & story state
- `Dreams : Singleton<Dreams>` — `IEnumerator prepareDream(string presetName, int dreamId)`
  is the single entry point for every dream (called from DialogueWindow.close,
  DreamTransition.onFinishedVideo, GameEvent.fire, dream switching). Patching it
  with a prefix that rewrites the args redirects which dream plays.
  Other members: `startDreaming()`, `endDreaming()`, `getOutcome()`, fields
  `dreaming`, `outcome`, `presetDict`.
- `Flags : Singleton<Flags>` — all story decisions go through
  `setFlag(string, bool)` / `setFlag(string, int)`; read via `hasFlag`/`getFlagInt`.
- Karma: `Flags.karmaPoints` has exactly ONE writer — `Controller.startBeforeDay`
  derives it from flags (verified via IL field-writer scan). Syncing flags is
  sufficient for karma.

## Item pickup / disarm path (beartraps, mushrooms)
- `Player.itemDefaultAction` → `Item.activate()` → `Item.attemptToDisarm()` → `Item.disarm()`
- `Item.disarm()`: adds `Item.invItem.type` × `invItemAmount` via
  `Inventory.addItemTypeToPlayer`, and ONLY on success calls
  `Item.switchTriggerState()` — which either destroys the GameObject or calls
  `Trigger.switchToTriggered()` (if `Trigger.staysAfterDisarming`).
  → switchTriggerState is both the "pickup succeeded" signal (send side) and
  the correct remote replay (receive side).

## Generator / switchable world items
- `Generator : MonoBehaviour` — fields `fuel`, `maxFuel`, `isOn`, `drainDuringNight`;
  methods `turnOn()`, `turnOff()`, `addFuel(float)`, `powerDown()`.
- Verified call chain: `Item.turnOn` → `Generator.turnOn`;
  `Player.<waitToSpillLiquid>` → `Generator.addFuel` (refueling).
- Lamps/standing torches are `Item` with `switchable=true`, toggled via
  `Item.switchMe()`/`turnOn()`/`turnOff()` (patched for sync).

## Lights & thrown items
- `InvItem` (item definition, via `ItemsDatabase.getItem(type, false)`):
  `lightEmitter` (prefab!), `isFlashlight`, `isNaturalLight`, `lightRadius`.
- `InvItemClass.switchActive(bool destActive, bool isDeselect)` — the single
  toggle point for active items (torch lit, flashlight on); called from
  Player.activateItem/switchToItem/deselect and durability burn-out.
- `ThrownItem : MonoBehaviour` — `thrown`, `onGround`, `landTarget`,
  `prefabToSpawnOnLand`, `flaming` (prefab-baked, no runtime writers);
  `FixedUpdate` → `checkIfWantToLand()` fires `onCollide` within 10 units of
  `landTarget` → setting position≈landTarget + `thrown=true, onGround=false`
  replays a remote throw incl. landing effects; `onCollide` runs
  `Explodes.onActivate(flaming)` (molotov), ignites flammable `Liquid` pools,
  spawns `prefabToSpawnOnLand`, damages `Core.GetCharacterAt(pos)`.
- IMPORTANT: `Player.throwItem` does NOT spawn a drop — it detaches the
  already-instantiated `Player.heldItem` and flings it. Patches relying on
  the spawnDroppedInvItem path never fire for throws; capture `heldItem` in
  a prefix instead.

## Item placement & construction (player-built)
- `Player.progressBarCompleted`, `placingItem` branch (verified IL):
  `Core.AddPrefab(currentItem.baseClass.item, proxyItem.pos, proxyItem.rot,
  null, false)` → `Trigger.setByPlayer = true` → `currentItem.removeAmount(1)`
  → `Core.addToSaveable(go, true, true)` →
  `WorldGrid.Instance.registerToNode(go, 0)`.
- `Constructible.construct(bool manual, int forceOption)` — `manual=true`
  consumes ingredients via the open ConstructionMenu; `manual=false` is the
  save-load path (no consumption) → use for remote replay. `forceOption=-1`
  uses `chosenOption`. The component destroys itself after construct unless
  `dontDestroyMeWhenConstructed`.
- `Flare : MonoBehaviourExt`, `ItemLight : MonoBehaviourExt` (Light2D wrappers).

## Damage dispatch (verified virtual)
- `CharBase.getHit(float)` and the 9-arg overload are VIRTUAL with overrides on
  `Player` and `Character` — patching those two types catches all damage
  sources: `MeleeSensor.OnTriggerEnter`, `Bullet.onCollide`, `Player.spawnBullet`
  (hitscan), `Burn`, `Explodes.explode`, `Flame.onCollideWith`, `ThrownItem.onCollide`.
- 9-arg parameter names: `damage, attackerTransform, CanCutInHalf, byPlayer,
  canInterrupt, normalHit, showRedScreen, force, dontShowHealthBar`.
  `byPlayer` distinguishes player attacks from environmental damage.
- Melee/bullets DO hit remote-player clones (colliders + CharBase survive the
  clone's script-disable sweep) — basis of friendly fire.

## Daily random spawns (crate desync root cause)
- `Controller.startDay` → `WorldGenerator.spawnRandomObjects()` (unseeded RNG,
  once per in-game day) — also called during world gen from
  `onSpawnedGridObjects`. Seeded per-day by WorldGenSeed_Patch.

## Enemy sync authority model (v0.3, mod-side)
- Symmetric distance authority: each machine broadcasts Characters within 50m
  of its own player; non-hosts yield when any other player's clone is <60m;
  the host ignores incoming claims for enemies <55m from its player. Claimed
  enemies are frozen (Character + AIPath disabled, rigidbody kinematic) and
  mirrored: position/rotation lerp, Health field + updateHealthBar(), and the
  tk2d clip name carried in EnemyUpdatePacket.State.
- Enemy ids on the wire: "ownerPlayerId:instanceId" (instance ids are
  per-process - the prefix prevents collisions and routes "enemyhit").
- `Character.attack(GameObject)` is used to nudge owner-side enemies onto the
  closest player clone (AI otherwise only targets Player.Instance).

## Combat target resolution (why clones needed custom hit detection)
- `MeleeSensor.OnTriggerEnter` and `Player.spawnBullet` resolve targets via
  `GetComponent<Character>()` — a remote-player clone (Player component) is
  NEVER a valid target for the game's own combat code. PvP hits must be
  detected by the mod (sensor-contact postfix, bullet re-cast along
  `transform.up`). Only the shadow-sensor branch touches `Player`.
- `Trigger.checkCollision(Collider other)` checks `other.GetComponent<Player>()`
  but applies effects to **`Player.Instance`** (the singleton!) — a clone
  tripping a poison mushroom poisons the OBSERVER. Clones must be filtered out
  of trigger collisions entirely.
- `Trigger`: `triggered`, `switchToTriggered()`, `isBearTrap`, `setByPlayer`.

## Barricades
- `Door.barricade(bool byPlayer)` / `Door.destroyBarricade(bool silent)`,
  fields `barricaded`, `barricadeHealth`, `maxBarricadeHealth`.
- `Window.barricade(int destHealth, bool byPlayer)` /
  `Window.destroyBarricade(bool silent)`, `Window.getHit(int, Transform, bool)`
  (enemy damage), fields `barricaded`, `barricadeHealth`.
- Plank refunds run through `setBarricadeState` → `addItemTypeToPlayer`
  (suppressed during RemoteApply via Inventory_Patch prefix).

## Gasoline & fire
- Pouring: `Player.waitToSpillLiquid` spawns segments via
  `Core.AddPrefab("Items/GasolineTrail", pos, rot, null, ...)` (string overload).
- `Liquid`: `flammable`, `burning`, `startBurning()`; burning spreads to
  neighbors locally (`waitToBurnNeighbors`), so igniting the matching puddle
  remotely is enough — idempotent via the `burning` check.

## Enemy spawning (v0.4, all IL-verified)
- `CharacterSpawner.spawnCharacterAround(GameObject destGO, Vector3 offset,
  float distance, string type, bool nocturnal, bool attackPlayer,
  bool relentlessPursuit, bool canSpawnInside)` → THE choke point for all
  dynamic spawns. Spawns `Core.AddPrefab("Characters/" + type, ...)` +
  `Core.addToSaveable`, sets waypoints from `Player.Instance.bigLocation`.
  Callers: `spawnNightChar` (night scenarios, around Player.Instance),
  `RandomEvent.fire` (hardcoded types like "Redneck02"), `GameEvent.fire`.
- `CharacterSpawnPoint.actuallySpawn` rolls `Random.Range` (spawnChance) then
  AddPrefab at its own (fixed) localPosition — seedable per position.
- `WorldChunk.spawnFreeRoamingCharacters` → coroutine `spawnChars` (roamers;
  left local, covered by EnemySync mirroring).
- Placement RNG: `Core.RandomOnUnitCircle2` gated by `Core.randomGeneration`.

## Scripted event system (v0.4, IL-verified call graph)
- `EventTriggers.OnTriggerEnter` compares `other.gameObject ==
  Player.Instance.gameObject` — clones can NEVER fire area triggers.
- `EventTriggers.fireEventTrigger(Type, string, bool)` → `EventTrigger.fire`
  coroutine → fires its `gameEvents` (a `GameEvents` reference).
- `GameEvents.fire()` head: `if (fired && !multipleFire) return; fired=true;
  timeFired=Controller.gameTime;` → iterates `GameEvent.fire(gameObject)`
  coroutines. Public fields `fired`, `multipleFire`, `timeFired` — setting
  `fired=true` remotely suppresses one-shot re-fires exactly.
- `RandomEvent.fire(bool checkIfRequirementsMet, bool force)`: scheduled
  daytime rolls come from `onUpdateTime` as `fire(true, false)`; night path is
  `CustomEvent.fire` → `theEvent.fire(false, force)`. The FIRST arg cleanly
  separates "scheduled roll" (host-only in MP) from "scripted/night fire".
- `Events.getEvent(name)` matches `RandomEvent.type.ToString()`.

## Locks (v0.4, IL-verified)
- `Locked.unlock()` = `locked = false;` only. Keys (`tryToOpenWithKey`) and
  lockpicks (`tryToLockpick`) both call it.
- `Door.unlock()` writes `Locked.locked = false` DIRECTLY (no Locked.unlock
  call); `Door.lockMe(keyType)` adds a `Locked` component + sets keyType.
- `Padlock.unlock(bool manually)`: manually=true → success message +
  `Core.sendTriggerInfo` ×2 (fires the padlock's event triggers = opens the
  guarded object) + `deactivate()`; manually=false → only `locked = false`.

## Trader / stations (v0.4)
- `NPC.randomizeTraderInv` → `Inventory.clear()` + `InventoryRandom.randomize`
  (unseeded RNG; called from init + onNewDay). `NPC.inventory` is a regular
  `Inventory` — the container reconciliation works on it.
- `DialogueWindow.acceptTrade` commits a trade; `DialogueWindow.npc` is set.
- `Saw`: `convert()` (woodLog → wood inside its private `inventory` field),
  `addFuel(float)` (called from Player.waitToSpillLiquid like the generator),
  public `fuel`/`maxFuel`/`refresh()`.
- `Feeder.activate()` = `makeInactive()` + `CharacterEffects.activate(effect)`
  on Player.Instance + sound. `Lure.removeHealth(int, Character)` null-checks
  the eating character; internal Random.Range for gore drops.
- `Location.spawnTrader/despawnTrader/spawnPorter/despawnPorter/spawnWolf/
  despawnWolf` — visiting-NPC lifecycle, driven by (host-only) events.

## Effects & weather (v0.4)
- `MeleeSensor.effects : List<InvItemEffect>` — carried by poison bites etc.
- `InvItemEffect`/`CharacterEffect` fields: `type` (CharacterEffectType),
  `duration`, `modifier`, `interval`. Apply via
  `CharacterEffects.activate(type, duration, modifier, interval, timeElapsed)`.
- `Rain.rainToday` writers: `Rain.onNewDay`, `Rain.setUpNextRain` (+ save
  load) — day-seeding those two makes weather identical.
- `Player.onEndSleep` restores player state after sleeping; the clock lives in
  `Controller.gameTime` (public float).
- `Window.getHit(int, Transform, bool)` / `Door.getHit(int, Transform, bool,
  bool)` — enemy barricade chip damage; destroyBarricade fires internally at 0.

## Types that do NOT exist (previous patch targets — all silently no-oped)
`PlayerController`, `DoorController`, `EnemyManager`, `Damageable`, `Weapon`

## Re-dumping the API
A dump tool lives in the scratchpad of the dev session; recreate quickly:
.NET 8 console app + `System.Reflection.MetadataLoadContext`,
`PathAssemblyResolver` over all DLLs in `Darkwood_Data/Managed`, core assembly `mscorlib`.
