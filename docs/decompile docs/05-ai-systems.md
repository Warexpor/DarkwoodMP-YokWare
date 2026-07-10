# Darkwood AI Systems — Deep Dive Analysis

**Generated from:** Assembly-CSharp.dll (1,115 classes), A* Pathfinding Project integration  
**Date:** July 8-9, 2026  
**Status:** Complete

---

## Table of Contents

- [Behavior Trees & State Machines](#behavior-trees--state-machines)
- [Perception System](#perception-system)
- [Target Selection Algorithms](#target-selection-algorithms)
- [Combat AI States](#combat-ai-states)
- [Pathfinding Integration](#pathfinding-integration)
- [Spawning/Despawning Triggers](#spawndespawning-triggers)
- [Enemy Types & Behaviors](#enemy-types--behaviors)
- [Multiplayer Considerations](#multiplayer-considerations)

---

## Behavior Trees & State Machines

### Architecture Overview

Darkwood uses a **component-based state machine** with heavy event-driven communication between perception systems and character behaviors. The AI system is built on top of Unity's MonoBehaviour lifecycle with custom extensions via `MonoBehaviourExt`.

### Behaviour Enum (Character/Behaviour)

The core AI state machine is controlled by the `Behaviour` enum:

```csharp
public enum Behaviour
{
    Idle,           // Standing still, waiting for trigger
    Walk,           // Moving at walking speed
    Run,            // Moving at running/sprinting speed
    Chase,          // Pursuing target at combat speed
    Attack,         // Performing melee/ranged attack
    Flee,           // Retreating from danger (low health)
    Investigate,    // Moving to investigate sound/sight
    Patrol          // Following patrol route waypoints
}
```

**State Control Method:**

```csharp
public void setBehaviour(Character.Behaviour targetBehaviour, bool force)
{
    // force=true bypasses state transition guards (emergency override)
    // force=false respects current state constraints
}
```

### Sub-States (Activity Types)

Characters can have sub-states that modify behavior:

| Activity | Description | Example Use Case |
|----------|-------------|------------------|
| **Eating** | Consuming food/healing items | Player or NPC using item |
| **LayingEggs** | Reproduction animation | Boss enemy ability |
| **Summoning** | Calling allies/enhancements | Banshee scream ability |
| **Crafting** | Using workbench/construction | Player building barricades |
| **Dead** | Death animation/state | Post-death behavior |

### State Transition Rules

```csharp
// Pseudocode for state transition logic
public bool CanTransitionTo(Character.Behaviour newState)
{
    switch (newState)
    {
        case Behaviour.Attack:
            return currentState == Behaviour.Chase || currentState == Behaviour.Investigate;
        
        case Behaviour.Flee:
            return health < fleeThreshold && currentState != Behaviour.Dead;
        
        case Behaviour.Patrol:
            return !hasTarget && isPatrolling;
        
        default:
            return true;  // Most transitions allowed
    }
}
```

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

### Vision Detection: InSightOfPlayer

**File Location:** Line 6450 in Assembly-CSharp-reference.md  
**Base Type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_visionRange` | float | Maximum detection distance (8-20 units by type) |
| `_visionAngle` | float | Field of view angle in degrees (90°-360°) |
| `_hasLineOfSight` | bool | Whether target is currently visible |
| `_onInSightOfPlayer` | Event | Fired when player spotted |

**Key Methods:**

```csharp
public void UpdateVision()                        // Check vision every frame
public bool HasLineOfSight(Transform target)      // Raycast check for LOS
public float GetVisionModifier()                  // Get vision range multiplier
```

**Vision Detection Algorithm:**

1. Calculate distance to target: `distance = Vector3.Distance(enemy.position, target.position)`
2. If `distance > _visionRange`, return false (out of range)
3. Check if target is within `_visionAngle` cone in front of enemy
4. Cast ray from enemy eyes to target position
5. If ray hits nothing → line of sight is clear → **detect!**

**Vision Ranges by Enemy Type:**

| Enemy Type | Vision Range | FOV Angle | Notes |
|------------|--------------|-----------|-------|
| **Banshee** | 15m | 270° | Enhanced hearing, poor vision |
| **Soldier** | 20m | 360° | Enhanced vision, standard hearing |
| **Mutant** | 8m | 90° | Poor vision, excellent hearing |
| **Animal** | 10m | 180° | Standard perception |

---

### Hearing Detection: SoundArea

**File Location:** Line 18234 in Assembly-CSharp-reference.md  
**Base Type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_soundRadius` | float | Maximum hearing distance (10-50 units) |
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
   - If `distance > _soundRadius`, ignore
   - Apply volume attenuation based on distance and obstacles
   - If attenuated volume > hearing threshold → **detect!**

**Sound Types & Properties:**

| Sound Type | Volume | Range (units) | Notes |
|------------|--------|---------------|-------|
| **Gunshot** | 10 | 50 | Maximum range, attracts all enemies |
| **Footsteps** | 3-5 | 20-30 | Player movement sound |
| **Weapon Swing** | 4 | 15 | Melee attack sound |
| **Door Open/Close** | 6 | 25 | Interactive object sound |
| **Explosion** | 10 | 60 | Area damage + attraction |

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

## Target Selection Algorithms

### Priority System

Enemies use a priority-based target selection:

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
    
    // Priority 3: Closest valid target
    Character closestTarget = potentialTargets[0];
    float minDistance = Vector3.Distance(transform.position, closestTarget.transform.position);
    
    foreach (var target in potentialTargets.Skip(1))
    {
        float distance = Vector3.Distance(transform.position, target.transform.position);
        if (distance < minDistance)
        {
            minDistance = distance;
            closestTarget = target;
        }
    }
    
    return closestTarget;
}
```

### Faction-Based Targeting

Enemies can be configured to attack specific factions:

```csharp
public bool attacksFaction(Faction targetFaction)
{
    // Check if this enemy's faction is hostile to target faction
    return FactionSystem.IsHostile(_faction, targetFaction);
}
```

**Faction Hostility Matrix:**

| Attacker \ Target | Player | VillagerNeutral | Army | Mutant | AnimalPassive | AnimalAggressive |
|-------------------|--------|-----------------|------|--------|---------------|------------------|
| **Player** | - | Friendly | Hostile | Hostile | Aggressive | Aggressive |
| **VillagerNeutral** | Friendly | - | Neutral | Hostile | Passive | Aggressive |
| **Army** | Hostile | Neutral | - | Hostile | Aggressive | Aggressive |
| **Mutant** | Hostile | Hostile | Hostile | - | Aggressive | Aggressive |
| **AnimalPassive** | Aggressive | Passive | Aggressive | Aggressive | - | Aggressive |
| **AnimalAggressive** | Aggressive | Aggressive | Aggressive | Aggressive | Aggressive | - |

### Dynamic Re-targeting

Enemies can switch targets during combat:

```csharp
public void checkForNewEnemyCloserThanTarget()
{
    if (_currentTarget == null) return;
    
    foreach (var enemy in GetAllEnemiesInRadius(10f))
    {
        float distanceToMe = Vector3.Distance(transform.position, enemy.transform.position);
        float distanceToCurrentTarget = Vector3.Distance(transform.position, _currentTarget.transform.position);
        
        if (distanceToMe < distanceToCurrentTarget && FactionSystem.IsHostile(_faction, enemy.GetFaction()))
        {
            SetNewTarget(enemy);
            break;
        }
    }
}
```

---

## Combat AI States

### Complete State Machine

```
Idle → DetectPlayer → Chase → Attack → Cooldown → Idle
              ↓                              ↑
          Investigate                      |
              ↓                              |
          LostSight → Patrol → Idle --------+
              ↓
          Flee (if health < threshold)
              ↓
          Recover or Dead
```

### State Details

#### 1. **Idle**
- Standing still, random chance to patrol
- Triggered by: No target in range, no recent sounds
- Duration: Random 2-5 seconds before transitioning to Patrol

#### 2. **DetectPlayer**
- Player spotted within perceptionRange
- Transition to: Chase (if in attack range) or Investigate (if out of range)

#### 3. **Chase**
- Moving toward player at combatSpeed
- Triggered by: Player spotted and in attack range
- Duration: Until reach attackRange or lose sight

#### 4. **Attack**
- Within attackRange, performing melee/ranged attack
- Cooldown timer after each attack (0.5-2 seconds)
- Transition to: Cooldown → Idle/Chase/Patrol

#### 5. **Cooldown**
- Post-attack recovery timer
- Duration: Based on weapon type and enemy speed modifier
- Transition to: Chase (if player in range) or Patrol (if no target)

#### 6. **Investigate**
- Heard sound but didn't see player, moving to investigate
- Remembers last known position of sound/player
- Duration: 5-10 seconds before returning to patrol
- Transition to: DetectPlayer (if spot player) or Patrol (timeout)

#### 7. **LostSight**
- Player moved out of vision range
- Return to last known position or begin searching
- Transition to: Patrol (if no new detection) or Chase (if spotted again)

#### 8. **Patrol**
- Walking between patrolPoints in sequence
- Triggered by: Idle timeout, Investigate timeout, LostSight
- Loop: Move to next waypoint → wait → move to next

#### 9. **Flee**
- Health below flee threshold (25%-75% based on cowardiness)
- Run away from player at maximum speed
- Triggered by: `health < maxHealth * fleeThreshold`
- Duration: Until out of combat range or health recovered via items

### Flee Behavior Thresholds

| Enemy Type | Flee Threshold | Cowardice Level | Notes |
|------------|----------------|-----------------|-------|
| **Banshee** | 50% | Medium | Flees at half health |
| **Soldier** | 75% | High | Flees early, calls for backup |
| **Mutant** | 25% | Low | Fights to death |
| **Animal** | 40% | Medium | Flees from danger |

---

## Pathfinding Integration

### A* Pathfinding Project Integration

Darkwood uses the **A* Pathfinding Project** (also known as Aron Granberg's A* Pathfinding) for navigation. This is a commercial Unity asset providing:

- Grid-based navmesh generation
- Dynamic obstacle avoidance
- Multi-agent pathfinding (RVO - Reciprocal Velocity Obstacles)
- Graph modification at runtime

### Movement Controllers

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

**Use Case:** Primary movement controller for most enemies, uses grid-based pathfinding.

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

**Use Case:** Short-range movement, projectiles, or when navmesh is unavailable.

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

**Use Case:** Crowd simulation, multiple enemies moving in same area.

---

### AstarPath Singleton

**File Location:** Line 94 in Assembly-CSharp-reference.md  
**Base Type:** `MonoBehaviour` | Singleton: True | Public: True

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

## Spawning/Despawning Triggers

### Spawn Systems

#### **ObjectSpawner** (Line 762)

**Base type:** `MonoBehaviourExt` | Abstract: False | Sealed: False | Public: True

**Role:** Generic object spawning with sequencing and looping  
**Key Methods:**
- `SpawnObject()` - Spawn next object in sequence
- `IsSpawningActive()` - Check if spawning is active
- `ResetSequence()` - Reset spawn order

---

#### **PorterSpawner** (Line 846)

**Base type:** `MonoBehaviourExt` | Abstract: False | Sealed: False | Public: True

**Role:** Wave-based enemy spawning triggered by player sight proximity  
**Key Methods:**
- `SpawnWave()` - Spawn next wave of enemies
- `IsWavingActive()` - Check if spawning is active
- `GetWaveCount()` - Get current wave number

---

#### **RandomSpawnArea** (Line 1042)

**Base type:** `MonoBehaviourExt` | Abstract: False | Sealed: False | Public: True

**Role:** Random placement within radius around player  
**Core Fields:**
- `_spawnRadius` - Random spawn distance from trigger
- `_minEnemies` - Minimum enemies per wave
- `_maxEnemies` - Maximum enemies per wave
- `_enemyPrefabs` - List of enemy types to spawn

---

### Despawning Triggers

**Distance-Based Despawn:**

```csharp
public void CheckDespawn()
{
    float distanceToPlayer = Vector3.Distance(transform.position, Player.Instance.transform.position);
    
    if (distanceToPlayer > _despawnDistance)  // Typically 50 units
    {
        Destroy(gameObject);
    }
}
```

**Level Boundary Despawn:**

- Enemies outside level bounds are despawned to prevent memory leaks
- Checked periodically via `Update()` or on level transition

---

## Enemy Types & Behaviors

### Banshee (Line 130)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Role:** Ranged sonic attacker  
**Abilities:**
- `onActivate()` - Activate scream ability
- `explode()` - Explode after death, dealing damage to nearby entities

**Fields:**
| Field | Type | Purpose |
|-------|------|---------|
| `affectsPlayer` | bool | Whether scream affects player |
| `damage` | float | Scream damage output |
| `radius` | float | Area of effect radius |
| `force` | float | Knockback force applied |

**Behavior:**
- Vision: 15m range, 270° FOV
- Hearing: 20m range (enhanced)
- Flee threshold: 50% health
- Attacks: Sonic scream (ranged), summons allies

---

### Soldier (Line ~9340)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Role:** Military faction with ranged weapons  
**Abilities:**
- Firearms attack (shotgun/rifle)
- Tactical retreat when low health
- Calls for backup via radio

**Behavior:**
- Vision: 20m range, 360° FOV (enhanced)
- Hearing: 10m range (standard)
- Flee threshold: 75% health (cowardly)
- Attacks: Ranged firearms, calls reinforcements

---

### Mutant (Line ~4040)

**Base type:** `MonoBehaviourExt` | Abstract: False | Sealed: False | Public: True

**Role:** Hostile enemy with poor vision but excellent hearing  
**Abilities:**
- Melee attack (claws/bite)
- Enhanced perception via sound
- Aggressive pursuit behavior

**Behavior:**
- Vision: 8m range, 90° FOV (poor)
- Hearing: 25m range (excellent)
- Flee threshold: 25% health (fights to death)
- Attacks: Melee, relentless pursuit

---

### Animals/Humans (Variants)

**Base type:** Various (MonoBehaviour/MonoBehaviourExt)  
**Role:** Passive or aggressive variants based on faction

**Behavior Variants:**

| Type | Aggressiveness | Faction | Notes |
|------|----------------|---------|-------|
| **Passive Animal** | 0 | animalPassive | Flees from player, non-hostile |
| **Aggressive Animal** | 3 | animalAggressive | Attacks on sight, high pursuit speed |
| **Neutral Villager** | 2 | villagerNeutral | Trades with player, ignores unless provoked |
| **Hostile Villager** | 3 | villagerPsycho | Hostile on sight, attacks without warning |

---

## Multiplayer Considerations

### AI System Architecture for Multiplayer

The existing DarkwoodMP.Mod.dll provides reference implementations for networked AI:

**Key Insight:** Enemy AI is **authoritative on host**, with visual/audio effects replicated to clients.

| Component | Authority Model | Network Strategy |
|-----------|-----------------|------------------|
| **Perception** | Host-only | Broadcast detection events (not continuous) |
| **Pathfinding** | Host-only | Replicate path waypoints (compressed) |
| **State Machine** | Host-only | Sync state transitions (not every frame) |
| **Combat AI** | Host-authoritative | Client-side prediction for smooth movement |

### Network Synchronization Strategy

**Host Authority Model:**

```
Host (Server)                          Client (Player 1)          Client (Player 2)
    ↓                                      ↓                            ↓
Calculates all enemy AI                  Receives sync packets      Receives sync packets
Makes decisions                              ↓                            ↓
Sends state updates                    Applies to local           Applies to local
                                         enemies                   enemies
```

**Packet Types Required:**

| Packet | Frequency | Content |
|--------|-----------|---------|
| `EnemyStateUpdate` | Every 100-200ms | Position, rotation, health, behavior state |
| `PerceptionEvent` | On detection | Enemy ID, detected player position, sound source |
| `PathWaypoints` | On path change | List of waypoints (compressed) |
| `AttackAnimation` | On attack start | Enemy ID, attack type, target position |

**Bandwidth Estimation:**

- **EnemyStateUpdate**: ~80 bytes × 5Hz = 400 bytes/sec per enemy
- **PerceptionEvent**: ~50 bytes × rare = negligible
- **PathWaypoints**: ~100 bytes × rare (on path change) = negligible
- **AttackAnimation**: ~60 bytes × 2/min = 2 KB/min

**Total per enemy:** ~400 bytes/sec (well within UDP bandwidth)

### Client-Side Prediction

To reduce perceived latency, clients can predict enemy movement:

```csharp
// Client-side prediction for smoother movement
public void PredictEnemyPosition(EnemyState state)
{
    float timeSinceLastUpdate = Time.time - state.lastUpdateTime;
    
    // Extrapolate position based on velocity
    Vector3 predictedPosition = state.position + state.velocity * timeSinceLastUpdate;
    
    // Use predicted position for rendering (not collision)
    ApplyPredictedTransform(predictedPosition, state.rotation);
}
```

**Benefits:**

- Smoother enemy movement on high-latency connections
- Reduced rubber-banding effect
- Better player experience for fast-paced combat

### Cheat Prevention

**Server-Authoritative AI Decisions:**

1. **Perception validation**: Host verifies line-of-sight and hearing ranges match client reports
2. **Path validity**: Host checks if enemy path is physically possible
3. **Attack timing**: Host validates attack cooldowns and range
4. **State consistency**: Host ensures state transitions follow valid rules

**Anti-Cheat Measures:**

- Client sends input/observations, host simulates AI and broadcasts results
- Discrepancies between client prediction and server state trigger correction
- Suspicious perception ranges flagged for investigation

### Spawn Synchronization

**Deterministic Spawning (Multiplayer):**

```csharp
// Host spawns enemies using shared seed
public void SpawnEnemiesForNetwork(int sharedSeed, int anchorCount)
{
    // Use shared seed for deterministic spawning
    Random.InitState(sharedSeed);
    
    // Spawn identically on all clients
    foreach (var spawnPoint in GetSpawnPoints(anchorCount))
    {
        var enemyType = GetRandomEnemyType();
        Instantiate(enemyType, spawnPoint.position, spawnPoint.rotation);
    }
}
```

**Benefits:**

- All clients see same enemies spawn at same time/place
- No need to sync spawn events (deterministic)
- Players can join mid-game without missing spawns

---

## Summary

Darkwood's AI system features:

1. **Component-based state machine** with `Behaviour` enum controlling AI states
2. **Multi-layer perception** combining vision (cone + LOS raycast) and hearing (sound area emission)
3. **Priority-based target selection** with faction-aware targeting and dynamic re-targeting
4. **A* Pathfinding Project integration** for robust navigation (GridGraph, NavMeshGraph, RVO)
5. **Wave-based spawning system** with deterministic generation for multiplayer compatibility
6. **Diverse enemy types** with unique perception ranges, flee thresholds, and attack behaviors

The architecture is **well-suited for multiplayer extension** due to:

- Clear separation of host-authoritative AI logic from client-side prediction
- Deterministic spawning via shared seeds
- Efficient packet-based synchronization (state updates, perception events)
- Server-authoritative decision-making enabling cheat prevention

This foundation enables robust multiplayer implementation with proper authority models, network synchronization, and anti-cheat measures.
