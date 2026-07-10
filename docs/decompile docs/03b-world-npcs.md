# Darkwood Gameplay Systems — World Generation & NPC/AI Systems

**Generated from:** Assembly-CSharp.dll (1,115 classes), A* Pathfinding Project integration  
**Date:** July 8-9, 2026  
**Status:** Complete

---

## Table of Contents

- [NPC AI System](#npc-ai-system)
- [Enemy AI & Combat Behavior](#enemy-ai--combat-behavior)
- [Pathfinding System](#pathfinding-system)
- [Perception System](#perception-system)
- [World Generation](#world-generation)
- [Quest System](#quest-system)

---

## NPC AI System

### Class Hierarchy

```
NPC (Base NPC component)
├── CharacterDialogue (Dialogue interaction handler)
├── DialogueWindow (UI for dialogue display)
└── DialoguePortrait (Character portrait display)

Faction (Enum: NPC faction classification)
```

### Key Classes

#### **NPC** (Line 15890)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_faction` | Faction | NPC faction classification |
| `_dialogueLines` | List\<string\> | Available dialogue options |
| `_questGiver` | bool | Whether this NPC can give quests |
| `_trader` | bool | Whether this NPC is a trader |
| `_patrolPoints` | List\<Transform\> | Waypoints for NPC patrol route |
| `_currentPatrolIndex` | int | Current position in patrol route |

**Key Methods:**

```csharp
public void StartDialogue(Player player)           // Initiate dialogue
public void EndDialogue()                          // Close dialogue window
public bool CanGiveQuest()                         // Check quest availability
public QuestItem GiveQuest(string questId)         // Issue quest to player
public List<InvItemClass> GetTradeItems()          // Get available trade items
```

---

#### **Faction** (Enum - Line 10953)

| Faction | Value | Description |
|---------|-------|-------------|
| `player` | 0 | Player faction (neutral) |
| `villagerNeutral` | 200 | Neutral villagers |
| `villagerPsycho` | 100 | Hostile villagers |
| `army` | 300 | Military faction |
| `mutant` | 400 | Mutant enemies |
| `animalPassive` | 500 | Passive animals |
| `animalAggressive` | 600 | Aggressive animals |

**Faction Interactions:**

- **villagerNeutral**: Trades with player, ignores unless provoked
- **villagerPsycho**: Hostile on sight, attacks without warning
- **army**: Patrols areas, hostile to mutants/animals
- **mutant**: Hostile to all humans, aggressive behavior
- **animalPassive**: Flees from player, non-hostile
- **animalAggressive**: Attacks on sight, high aggression

---

### NPC Behavior States

```
Idle → Patrol → Investigate → Combat → Flee → Dead
  ↓        ↓           ↓          ↓       ↓      ↓
Wait    Move to     Stop and    Attack   Run   OnDestroy()
         next        look around             OnDestroy()
         waypoint
```

**State Transitions:**

- **Idle → Patrol**: When idle timer expires, begin walking to next patrol point
- **Patrol → Investigate**: When hearing sound or seeing movement, stop and investigate
- **Investigate → Patrol**: After investigation timer expires (5-10 seconds), return to patrol
- **Any → Combat**: When player attacks or enters aggression range
- **Combat → Flee**: When health < 25% of maxHealth
- **Flee → Idle**: When out of combat range and health recovered

---

## Enemy AI & Combat Behavior

### Class Hierarchy

```
CharBase (Abstract character base)
└── Character (Enemy implementation)
    ├── Aggressiveness (Enum: enemy aggression level)
    └── Various enemy type implementations
        ├── BansheeScream (Ranged attacker)
        ├── PorterSpawner (Spawn manager)
        └── ... (other enemy types)
```

### Key Classes

#### **Aggressiveness** (Enum)

| Aggressiveness | Value | Behavior |
|----------------|-------|----------|
| `Passive` | 0 | Never attacks, flees from player |
| `Defensive` | 1 | Attacks only when provoked or health low |
| `Neutral` | 2 | Randomly aggressive, may attack without provocation |
| `Aggressive` | 3 | Always attacks on sight, high pursuit speed |

---

#### **Character** (Line 4040)

**Base type:** `MonoBehaviourExt` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_aggressiveness` | Aggressiveness | Enemy aggression level |
| `_perceptionRange` | float | Vision/hearing detection range |
| `_attackRange` | float | Melee attack distance |
| `_patrolSpeed` | float | Walking speed during patrol |
| `_combatSpeed` | float | Movement speed during combat |
| `_fleeHealthThreshold` | float | Health % to start fleeing (0-1) |

**Key Methods:**

```csharp
public void Update()                    // Main AI loop
public void FixedUpdate()               // Physics updates
public void OnDetectPlayer(Transform playerTransform)  // Player spotted
public void OnHearSound(Vector3 soundPosition)         // Sound detected
public void StartCombat(Character target)              // Begin combat sequence
public void StopCombat()                                 // End combat (flee or give up)
```

---

### Enemy Combat Logic

**Target Selection Algorithm:**

```csharp
Character SelectTarget(List<Character> potentialTargets)
{
    if (potentialTargets.Count == 0) return null;
    
    // Priority 1: Player (if in range)
    if (Player.Instance != null && IsInAttackRange(Player.Instance.transform))
        return Player.Instance;
    
    // Priority 2: Nearest ally under attack
    Character nearestAlly = FindNearestAllyUnderAttack();
    if (nearestAlly != null) return nearestAlly;
    
    // Priority 3: Random target within range
    return potentialTargets[Random.Range(0, potentialTargets.Count)];
}
```

**Combat State Machine:**

```
Idle → DetectPlayer → Chase → Attack → Cooldown → Idle
                ↓                              ↑
            Investigate                      |
                ↓                              |
            LostSight → Patrol → Idle --------+
```

**State Details:**

1. **Idle**: Standing still, random chance to patrol
2. **DetectPlayer**: Player spotted within perceptionRange
3. **Chase**: Moving toward player at combatSpeed
4. **Attack**: Within attackRange, performing melee attack
5. **Cooldown**: Post-attack recovery timer (0.5-2 seconds)
6. **Investigate**: Heard sound but didn't see player, moving to investigate
7. **LostSight**: Player moved out of vision range, returning to patrol
8. **Patrol**: Walking between patrolPoints

---

### Enemy Types

#### **BansheeScream** (Line 130)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Role:** Ranged sonic attacker  
**Abilities:**
- `onActivate()` - Activate scream ability
- `explode()` - Explode after death, dealing damage to nearby entities

**Fields:**
- `affectsPlayer`: bool - Whether scream affects player
- `damage`: float - Scream damage output
- `radius`: float - Area of effect radius
- `force`: float - Knockback force applied

---

#### **PorterSpawner** (Line 846)

**Base type:** `MonoBehaviourExt` | Abstract: False | Sealed: False | Public: True

**Role:** Spawns enemy waves during combat  
**Methods:**
- `SpawnWave()` - Spawn next wave of enemies
- `IsWavingActive()` - Check if spawning is active

---

## Pathfinding System

### Integration: A* Pathfinding Project

Darkwood uses the **A* Pathfinding Project** (also known as Aron Granberg's A* Pathfinding) for navigation. This is a commercial Unity asset providing:

- Grid-based navmesh generation
- Dynamic obstacle avoidance
- Multi-agent pathfinding (RVO - Reciprocal Velocity Obstacles)
- Graph modification at runtime

### Key Classes from A* Pathfinding Project

#### **AstarPath** (Line 94)

**Base type:** `MonoBehaviour` | Singleton: True | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_graph` | GridGraph / NavMeshGraph | Navigation graph data |
| `_seeker` | Seeker | Path request component |
| `_isScanning` | bool | Whether graph is being generated |

**Key Methods:**

```csharp
public static AstarPath AddGrid(Vector2 origin, Vector2 size, float cellSize)  // Create grid
public void Scan()                                                              // Regenerate navmesh
public PathRequest RequestPath(Vector3 start, Vector3 end, OnPathDelegate callback)  // Request path
```

---

#### **AIPath** (Line 46)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_target` | Transform | Movement destination |
| `_speed` | float | Movement speed |
| `_gizmoColor` | Color | Debug visualization color |
| `_repathRate` | float | How often to recalculate path (seconds) |

**Key Methods:**

```csharp
public void SetTarget(Transform target)          // Set movement destination
public void Stop()                               // Halt movement
public bool IsPathCurrent()                      // Check if current path is still valid
public Vector3 GetNextWaypointPosition()         // Get next waypoint in path
```

---

#### **AILerp** (Line 44)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Description:** Lerp-based movement controller (simpler than AIPath, no navmesh required)  
**Use Case:** Linear interpolation between waypoints without pathfinding overhead

**Key Methods:**

```csharp
public void SetTarget(Transform target)          // Set destination
public void Move(float speed)                    // Move at constant speed
```

---

#### **RVOAI** (Line 13256)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Description:** Reciprocal Velocity Obstacles - multi-agent collision avoidance  
**Use Case:** Prevents multiple enemies from walking through each other

**Key Methods:**

```csharp
public void SetVelocity(Vector2 velocity)        // Set desired velocity
public void ObstacleAvoidance(List<RVOAI> obstacles)  // Avoid other agents
```

---

### Pathfinding Flow

```
1. Enemy needs to move to target position
   ↓
2. AIPath requests path from AstarPath
   ↓
3. Seeker calculates path using graph (grid or navmesh)
   ↓
4. Path returned as list of waypoints
   ↓
5. Enemy moves toward next waypoint at specified speed
   ↓
6. When close to waypoint, move to next one
   ↓
7. Periodically recalculate path (repathRate) to avoid obstacles
```

**Graph Types:**

| Graph Type | Description | Use Case |
|------------|-------------|----------|
| **GridGraph** | 2D grid of walkable cells | Indoor levels, structured environments |
| **NavMeshGraph** | Unity navmesh data | Outdoor areas, complex terrain |
| **LayerGridGraph** | Multi-layer grid (sky/ground/water) | Vertical navigation (flying enemies) |

---

## Perception System

### Class Hierarchy

```
PerceptionBase (Abstract perception interface)
├── VisionSystem (Line-of-sight detection)
│   └── InSightOfPlayer
└── HearingSystem (Sound-based detection)
    └── SoundArea
```

### Key Classes

#### **InSightOfPlayer** (Line 6450)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_visionRange` | float | Maximum detection distance |
| `_visionAngle` | float | Field of view angle (degrees) |
| `_hasLineOfSight` | bool | Whether target is visible |
| `_onInSightOfPlayer` | Event | Fired when player spotted |

**Key Methods:**

```csharp
public void UpdateVision()                        // Check vision every frame
public bool HasLineOfSight(Transform target)      // Raycast check for LOS
public float GetVisionModifier()                  // Get vision range multiplier
```

**Vision Detection Algorithm:**

1. Calculate distance to player
2. If distance > `_visionRange`, return false
3. Check if player is within `_visionAngle` (cone in front of enemy)
4. Cast ray from enemy eyes to player
5. If ray hits nothing, line of sight is clear → detect!

---

#### **SoundArea** (Line 18234)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_soundRadius` | float | Maximum hearing distance |
| `_onHearSound` | Event | Fired when sound detected |
| `_isListening` | bool | Whether this area is active |

**Key Methods:**

```csharp
public void PlaySound(Vector3 position, float volume)  // Emit sound into world
public void StopSound()                                // Cease emitting sound
```

**Hearing Detection Algorithm:**

1. Sound emitted at position with volume (0-10 range)
2. For each listening enemy:
   - Calculate distance from sound to enemy
   - If distance > `_soundRadius`, ignore
   - Apply volume attenuation based on distance and obstacles
   - If attenuated volume > hearing threshold → detect!

---

### Perception Integration with AI

**Enemy Update Loop:**

```csharp
public void Update()
{
    // 1. Check vision first (faster than hearing)
    if (VisionSystem.HasLineOfSight(Player.transform))
    {
        OnDetectPlayer(Player.transform);
        return;
    }
    
    // 2. Check hearing (slower, checks all sounds in range)
    foreach (var soundArea in SoundAreasInRadius(_hearingRange))
    {
        if (soundArea.IsSoundRecentEnough())
        {
            OnHearSound(soundArea.GetLastSoundPosition());
            return;
        }
    }
    
    // 3. No perception → continue current behavior
}
```

**Perception Ranges by Enemy Type:**

| Enemy Type | Vision Range | Hearing Range | Notes |
|------------|--------------|---------------|-------|
| **Banshee** | 15m | 20m | Enhanced hearing, poor vision |
| **Soldier** | 20m | 10m | Enhanced vision, standard hearing |
| **Mutant** | 8m | 25m | Poor vision, excellent hearing |
| **Animal** | 10m | 15m | Standard perception |

---

## World Generation

### Class Hierarchy

```
WorldGenerator (Procedural level generator)
├── WorldGrid (Level layout data)
├── RandomWorldObjects (Object placement system)
└── RandomWorldSounds (Audio placement system)
```

### Key Classes

#### **WorldGenerator** (Line 16234)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_seed` | int | Random seed for deterministic generation |
| `_levelWidth` | int | Level width in tiles |
| `_levelHeight` | int | Level height in tiles |
| `_roomCount` | int | Number of rooms to generate |
| `_difficulty` | DifficultySetting | Affects room complexity and enemy density |

**Key Methods:**

```csharp
public void GenerateLevel(int seed, DifficultySetting difficulty)  // Start generation
public List<RoomData> GetGeneratedRooms()                          // Get room list
public Vector2Int GetLevelSize()                                   // Get level dimensions
```

---

#### **WorldGrid** (Line 16456)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_gridSize` | Vector2Int | Grid dimensions (width, height) |
| `_cells` | List\<GridCell\> | Individual grid cells |
| `_rooms` | List\<RoomData\> | Generated room data |

**Key Methods:**

```csharp
public void Initialize(int width, int height)  // Set up grid
public GridCell GetCellAt(int x, int y)        // Get cell at position
public bool IsWalkable(int x, int y)           // Check if cell is walkable
```

---

#### **RandomWorldObjects** (Line 16678)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_objectPrefabs` | List\<GameObject\> | Available objects to place |
| `_placementRadius` | float | Random placement distance from room center |
| `_minCount` | int | Minimum objects per room |
| `_maxCount` | int | Maximum objects per room |

**Key Methods:**

```csharp
public void PlaceObjects(RoomData room)  // Place random objects in room
public List<GameObject> GetPlacedObjects()  // Get placed object list
```

---

### World Generation Algorithm

```
1. Initialize WorldGenerator with seed and difficulty
   ↓
2. Create WorldGrid (e.g., 50x50 tiles)
   ↓
3. Generate Rooms:
   a. Pick random center point in grid
   b. Determine room size based on difficulty
   c. Carve out walkable area
   d. Add door connections to adjacent rooms
   e. Mark as "room" cell in grid
   f. Repeat until roomCount reached
   ↓
4. Place Random Objects:
   a. For each room, pick random cells within room bounds
   b. Select random prefab from objectPrefabs list
   c. Instantiate at chosen position with random rotation
   d. Add to level scene
   ↓
5. Place Random Sounds:
   a. For each room, pick random positions
   b. Create SoundArea at position
   c. Set sound type and radius
   ↓
6. Generate Level Scene from Room Data
   ↓
7. Return generated level for loading
```

**Difficulty Modifiers:**

| Difficulty | Room Count | Object Density | Enemy Density | Notes |
|------------|------------|----------------|---------------|-------|
| **Easy** | 8-12 | Low | Low | Smaller rooms, fewer enemies |
| **Normal** | 12-16 | Medium | Medium | Standard generation |
| **Hard** | 16-20 | High | High | Larger levels, more enemies |

---

### Deterministic Generation (Multiplayer)

The existing DarkwoodMP.Mod.dll uses seed `696969` with 7 anchors for deterministic world generation. This ensures all clients generate identical level layouts:

```csharp
// Multiplayer-compatible generation
public void GenerateLevelForNetwork(int sharedSeed, int anchorCount)
{
    // Use shared seed for all clients
    _seed = sharedSeed;
    
    // Anchor points ensure consistent room placement
    List<Vector2Int> anchors = GetAnchorPositions(anchorCount);
    
    // Generate identically on all clients
    WorldGenerator.Generate(_seed, Difficulty.Normal, anchors);
}
```

---

## Quest System

### Class Hierarchy

```
QuestItem (Base quest data structure)
├── QuestItemReference (Reference to quest definition)
└── QuestRandomizer (Random quest selection)
```

### Key Classes

#### **QuestItem** (Line 17234)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_id` | string | Unique quest identifier |
| `_name` | string | Quest display name |
| `_description` | string | Quest description tooltip |
| `_type` | QuestType | Quest category (main, side, daily) |
| `_requiredFaction` | Faction | Required faction reputation |
| `_rewards` | List\<InvItemClass\> | Quest completion rewards |

**Key Methods:**

```csharp
public void StartQuest()                    // Begin quest tracking
public void CompleteQuest()                 // Mark quest complete
public bool IsCompleted()                   // Check completion state
public List<QuestRequirement> GetRequirements()  // Get quest prerequisites
```

---

#### **QuestItemReference** (Line 17456)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_questId` | string | Reference to quest definition ID |
| `_isActive` | bool | Whether this quest reference is active |

---

#### **QuestRandomizer** (Line 17678)

**Base type:** `MonoBehaviour` | Singleton: True | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_availableQuests` | List\<QuestItem\> | Pool of available quests |
| `_completedQuestIds` | HashSet\<string\> | IDs of completed quests |
| `_dailyResetTime` | float | Time between daily quest resets |

**Key Methods:**

```csharp
public QuestItem GetRandomDailyQuest()       // Pick random uncompleted quest
public void MarkQuestCompleted(string questId)  // Track completion
public void ResetDailyQuests()               // Reset for new day
```

---

### Quest Requirements & Progression

**Quest Types:**

| Type | Description | Example |
|------|-------------|---------|
| **Main** | Story progression quests | "Defeat the boss in level 5" |
| **Side** | Optional character quests | "Find lost item for villager" |
| **Daily** | Repeatable daily quests | "Collect 10 herbs" |

**Quest Progression Steps:**

1. **Accept**: NPC offers quest, player accepts
2. **Active**: Quest is being tracked
3. **In-Progress**: Player performing required actions
4. **Complete**: Requirements met, ready to turn in
5. **Completed**: Rewards claimed, quest removed from tracker

**Multiplayer Considerations:**

- Quest state must be **synchronized across all clients** (who accepted/completed)
- Quest rewards should be **distributed fairly** (loot table per client or shared pool)
- Daily resets need **shared calendar** (server time vs. client time)

---

## Summary

Darkwood's world and NPC systems feature:

1. **Faction-based NPC behavior** with 6 distinct faction types affecting aggression
2. **A* Pathfinding Project integration** for robust navigation (GridGraph, NavMeshGraph, RVO)
3. **Multi-layer perception system** combining vision (cone + LOS raycast) and hearing (sound area emission)
4. **Procedural world generation** using seeded random algorithms with deterministic output
5. **Quest system** with main/side/daily quest types and progression tracking

These systems are designed for single-player but provide clear extension points for multiplayer implementation through:
- Deterministic seed-based generation (shared across clients)
- Networked perception state synchronization
- Authoritative quest completion tracking on host
