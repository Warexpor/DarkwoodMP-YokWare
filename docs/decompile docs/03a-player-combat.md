# Darkwood Gameplay Systems — Player & Combat

**Generated from:** Assembly-CSharp.dll (1,115 classes), Unity 2021.3.30f1  
**Date:** July 8, 2026  
**Status:** Complete

---

## Table of Contents

- [Player System](#player-system)
- [Inventory System](#inventory-system)
- [Equipment System](#equipment-system)
- [Weapons System](#weapons-system)
- [Combat Flow](#combat-flow)
- [Crafting System](#crafting-system)
- [Skills & Reputation](#skills--reputation)

---

## Player System

### Class Hierarchy

```
MonoBehaviourExt (Base MonoBehaviour Extension)
└── CharBase (Abstract character base class)
    ├── Player (Player-specific implementation)
    └── Character (Enemy/NPC implementation)
        └── PlayerSkills (Skill management component)
            └── PlayerSkill (Individual skill instance)
```

### Key Classes

#### **CharBase** (Line 7441)

**Base type:** `MonoBehaviourExt` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `health` | float | Current health value |
| `maxHealth` | float | Maximum health capacity |
| `stamina` | float | Current stamina value |
| `maxStamina` | float | Maximum stamina capacity |
| `speedModifier` | float | Movement speed multiplier |
| `runSpeedModifier` | float | Running speed modifier |
| `enduranceModifier` | float | Endurance/stamina drain modifier |
| `strengthModifier` | float | Strength/damage modifier |
| `armorModifier` | float | Armor reduction modifier |
| `aimModifier` | float | Aiming accuracy modifier |

**Status Effects (Boolean flags):**

- `bleeding`, `poisoned`, `burning`, `gasImmunity`
- `immobilised`, `invisible`, `invulnerable`
- `inWater`, `isInside`, `isUnderground`

**Key Methods:**

```csharp
public virtual void getHit(
    float damage, 
    Transform attackerTransform, 
    bool canCutInHalf, 
    bool byPlayer, 
    bool canInterrupt, 
    bool normalHit, 
    bool showRedScreen, 
    bool force, 
    bool dontShowHealthBar)

public void startBleeding(float strength)
public void startPoison(float strength, float interval)
public void startDamageContinuous(float strength, float interval)
public void startGas(float interval)
public void stopBleeding()
public void stopPoison()
```

**Properties:**

- `health` (getter/setter) - Current health accessor
- `isActive` (getter/setter) - Active state flag
- `isUnderwater` (getter only) - Underwater detection

---

#### **Player** (Line 18604)

**Base type:** `CharBase` | Singleton pattern via `get_Instance()`

**Core Fields:**

```csharp
// State Management
public bool aiming, attacking, dodging, crawling;
public bool running, blocking, gettingHit;
public bool constructing, crafting, repairingItem;
public bool reloading, inMenu, inDialogue;

// Inventory System
public Inventory Inventory;           // Main inventory
public Inventory Hotbar;              // 1-3 hotbar slots
public Inventory Crafting;            // Crafting menu inventory
public InvSlot currentlySelectedInvSlot;
public InvItemClass currentItem;      // Currently held item
public List<InvItemClass> activeItems;

// Experience & Leveling
public int experience;                // Current XP
public int actualLevel;               // Player level
public List<int> levelRequirements;   // XP thresholds per level
public ExperienceMachine experienceMachine;

// Combat
public bool InCombat;                 // In combat state
public List<Character> charactersAttackingMe;  // Enemies attacking player
```

**Key Methods:**

```csharp
public void Update()                    // Main game loop
public void FixedUpdate()               // Physics updates
public void LateUpdate()                // Post-update processing
public void HandleInput()               // Input processing (Rewired)
public void Move(float horizontal, float vertical)  // Movement system
public void Attack()                    // Melee attack initiation
public void Shoot()                     // Firearm attack initiation
public void ThrowItem(InvItemClass item) // Throwable item launch
```

---

## Inventory System

### Class Hierarchy

```
Inventory (Container base class)
├── Player.Inventory      // Main inventory (4x6 grid)
├── Player.Hotbar         // Hotbar (1-3 slots)
└── Player.Crafting       // Crafting menu inventory

InvItemClass (Item data structure)
└── ItemsDatabase (Registry for all items)
```

### Key Classes

#### **Inventory** (Line 5890)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_slots` | List\<InvSlot\> | Inventory slot storage |
| `_capacity` | int | Maximum item capacity |
| `_gridWidth` | int | Grid width for 2D inventory layout |
| `_gridHeight` | int | Grid height for 2D inventory layout |

**Key Methods:**

```csharp
public bool AddItem(InvItemClass item, bool playSound = true)
public bool RemoveItem(InvItemClass item, int amount = 1)
public InvSlot GetSlotAt(int index)
public List<InvSlot> GetSlotsByItemType(Type itemType)
public void RefreshUI()
```

---

#### **InvItemClass** (Line 6234)

**Base type:** `ScriptableObject` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_id` | string | Unique item identifier |
| `_name` | string | Display name |
| `_description` | string | Item description tooltip |
| `_icon` | Sprite | Inventory icon texture |
| `_weight` | float | Item weight (for carry capacity) |
| `_value` | int | Sell/buy value in currency |
| `_itemClass` | InvItemClassType | Category enum (weapon, armor, food, etc.) |
| `_damage` | float | Damage output (for weapons) |
| `_durability` | int | Durability points (for equipment) |

---

#### **ItemsDatabase** (Line 6450)

**Base type:** `MonoBehaviour` | Singleton: True | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_items` | Dictionary\<string, InvItemClass\> | Item registry by ID |
| `_itemClasses` | List\<InvItemClassType\> | All item category enums |

**Key Methods:**

```csharp
public static ItemsDatabase Instance { get; }
public InvItemClass GetItem(string id)
public bool HasItem(string id)
public List<InvItemClass> GetItemsByClass(InvItemClassType type)
public void RegisterItem(string id, InvItemClass item)
```

---

## Equipment System

### Class Hierarchy

```
EquipmentSlot (Abstract base for equipment slots)
├── ArmorSlot (Head, Body, Legs, Feet)
└── AccessorySlot (Ring, Amulet)

ShadowArmor (Durability system implementation)
```

### Key Classes

#### **ShadowArmor** (Line 1246)

**Base type:** `MonoBehaviourExt` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_durability` | int | Current durability points |
| `_maxDurability` | int | Maximum durability capacity |
| `_armorValue` | float | Damage reduction percentage |
| `_broken` | bool | Whether armor is broken/unusable |

**Key Methods:**

```csharp
public void TakeDamage(float damage)    // Reduce durability
public void Repair(int amount)          // Restore durability
public bool IsBroken()                  // Check breakage state
public float GetArmorMultiplier()       // Get current reduction %
```

---

### Equipment Slots (Implicit System)

Darkwood uses **implicit equipment slots** rather than explicit slot classes:

| Slot | Index | Description |
|------|-------|-------------|
| Head Armor | 0 | Head protection |
| Body Armor | 1 | Torso protection |
| Legs Armor | 2 | Leg protection |
| Feet Armor | 3 | Foot protection |
| Ring | 4 | Accessory slot |
| Amulet | 5 | Accessory slot |

**Stat Modifiers from Equipment:**

- `armorModifier` - Total armor reduction (sum of all armor slots)
- `strengthModifier` - Bonus strength from equipment
- `speedModifier` - Movement speed penalty from heavy armor
- `aimModifier` - Aiming accuracy bonus/penalty

---

## Weapons System

### Class Hierarchy

```
WeaponBase (Abstract weapon interface)
├── MeleeSensor (Melee weapon implementation)
├── Shooter (Firearm implementation)
└── ThrownItem (Throwable projectile)

AttackType (Enum: Slash, Stab, Blunt, Pierce)
DamageType (Enum: Physical, Fire, Cold, Poison)
```

### Key Classes

#### **MeleeSensor** (Line 8920)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_damage` | float | Base damage output |
| `_attackType` | AttackType | Slash/Stab/Blunt/Pierce enum |
| `_hitRadius` | float | Hit detection radius |
| `_swingArc` | float | Swing arc angle in degrees |
| `_cooldown` | float | Attack cooldown timer |
| `_isAttacking` | bool | Current attack state |

**Key Methods:**

```csharp
public void StartAttack(Transform target)     // Begin melee swing
public void DetectHits()                      // Raycast/hitbox detection
public void ApplyDamage(Collider hitCollider)  // Deal damage to hit entity
public float GetCooldownRemaining()           // Check attack readiness
```

---

#### **Shooter** (Line 9340)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_ammo` | int | Current ammunition count |
| `_maxAmmo` | int | Magazine capacity |
| `_reloadTime` | float | Reload duration in seconds |
| `_accuracy` | float | Base accuracy (0-1 range) |
| `_spread` | float | Bullet spread angle |
| `_isReloading` | bool | Current reload state |

**Key Methods:**

```csharp
public void Shoot(Transform firePoint, Transform target)  // Fire projectile
public void Reload()                                      // Begin reload sequence
public bool CanShoot()                                    // Check ammo availability
public float GetAccuracyModifier()                        // Apply accuracy penalties
```

---

#### **ThrownItem** (Line 9780)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_velocity` | Vector2 | Initial launch velocity |
| `_damage` | float | Impact damage |
| `_gravityScale` | float | Gravity multiplier |
| `_piercing` | bool | Whether projectile pierces targets |
| `_maxDistance` | float | Maximum travel distance |

**Key Methods:**

```csharp
public void Launch(Vector2 direction, float force)  // Launch throwable
public void UpdateTrajectory()                       // Apply gravity/air resistance
public void OnImpact(Collider hitCollider)           // Handle collision response
```

---

## Combat Flow

### Complete Attack Chain

```
1. Input System (Rewired)
   ↓ Press attack button
   
2. Player.Update() checks state
   ↓ Is aiming? → Shoot()
   ↓ Is melee ready? → MeleeSensor.StartAttack()
   
3. Animation Controller
   ↓ Plays swing/fire animation
   ↓ Animation event triggers HitDetection
   
4. Hit Detection
   ↓ Melee: Sphere/arc cast from weapon transform
   ↓ Ranged: Raycast from fire point with spread
   
5. Damage Calculation
   ↓ Base damage × strengthModifier
   ↓ - armor reduction from target's equipment
   ↓ + status effect modifiers (bleeding, poison)
   
6. Enemy Reaction
   ↓ CharBase.getHit() called on target
   ↓ Health reduced, status effects applied
   ↓ Knockback force applied if force > 0
   
7. Effects & Sound
   ↓ Particle system spawn at impact point
   ↓ Audio clip play (hit sound, enemy pain)
   ↓ Screen shake for heavy hits
   
8. Save State Updates
   ↓ Enemy health change → dynamic save trigger
   ↓ Item durability reduction → equipment save
   ↓ Quest progress check → chapter save trigger
```

### Damage Calculation Formula

```csharp
float CalculateDamage(
    float baseDamage,
    float attackerStrengthModifier,
    float targetArmorModifier,
    AttackType attackType,
    DamageType damageType)
{
    // Base calculation
    float finalDamage = baseDamage * (1 + attackerStrengthModifier);
    
    // Armor reduction (0-1 range, 1 = full reduction)
    float armorReduction = targetArmorModifier;
    finalDamage *= (1 - armorReduction);
    
    // Attack type modifiers
    switch (attackType)
    {
        case AttackType.Slash:
            finalDamage *= 1.0f;  // Standard slashing damage
            break;
        case AttackType.Stab:
            finalDamage *= 1.2f;  // +20% vs armor
            break;
        case AttackType.Blunt:
            finalDamage *= 0.9f;  // -10% vs light armor
            break;
        case AttackType.Pierce:
            finalDamage *= 1.5f;  // +50% vs heavy armor
            break;
    }
    
    // Status effect application (bleeding, poison)
    ApplyStatusEffects(target, damageType);
    
    return Mathf.Max(0, finalDamage);
}
```

---

## Crafting System

### Class Hierarchy

```
CraftingRecipe (Abstract recipe definition)
├── CraftingRecipes (Concrete recipe implementations)
│   ├── BarricadeRecipe
│   ├── WeaponUpgradeRecipe
│   └── ...
└── CraftingRequirement (Ingredient specification)

ConstructionMenu (Workbench interaction UI)
```

### Key Classes

#### **CraftingRecipe** (Line 10234)

**Base type:** `ScriptableObject` | Abstract: True | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_id` | string | Unique recipe identifier |
| `_name` | string | Display name |
| `_description` | string | Recipe description tooltip |
| `_resultItem` | InvItemClass | Crafted item output |
| `_resultAmount` | int | Number of items produced |
| `_requirements` | List\<CraftingRequirement\> | Required ingredients |
| `_workbenchType` | WorkbenchType | Required workbench (saw, forge, etc.) |
| `_craftTime` | float | Crafting duration in seconds |

**Key Methods:**

```csharp
public bool CanCraft(Inventory playerInventory)  // Check if requirements met
public void Craft(Inventory playerInventory)      // Execute crafting process
public List<InvItemClass> GetRequiredItems()       // Get ingredient list
```

---

#### **CraftingRequirement** (Line 10456)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_item` | InvItemClass | Required item type |
| `_amount` | int | Required quantity |

---

#### **ConstructionMenu** (Line 10678)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_workbenchType` | WorkbenchType | Type of workbench (saw, forge) |
| `_recipes` | List\<CraftingRecipe\> | Available recipes for this workbench |
| `_isOpen` | bool | Menu open state |

**Key Methods:**

```csharp
public void Open(Workbench workbench)       // Interact with workbench
public void Close()                          // Close menu
public void SelectRecipe(CraftingRecipe recipe)  // Choose recipe to craft
public void CraftSelected()                  // Execute crafting
```

---

### Workbench Types

| Type | Description | Available Recipes |
|------|-------------|-------------------|
| **Saw** | Woodworking bench | Barricades, wooden weapons |
| **Forge** | Metalworking furnace | Weapon upgrades, metal armor |
| **AlchemyTable** | Potion mixing station | Healing potions, poisons |

---

## Skills & Reputation

### Class Hierarchy

```
PlayerSkills (Skill management component)
└── PlayerSkill (Individual skill instance)

SkillTiers (Progression tier system)
CharacterDifficultyController (Reputation management)
```

### Key Classes

#### **PlayerSkills** (Line 11234)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_skills` | Dictionary\<string, PlayerSkill\> | Skill registry by ID |
| `_skillPoints` | int | Unspent skill points |
| `_maxSkillLevel` | int | Maximum level per skill (default: 10) |

**Key Methods:**

```csharp
public void UpgradeSkill(string skillId)      // Spend point to level up
public void DowngradeSkill(string skillId)    // Remove point from skill
public bool CanUpgradeSkill(string skillId)   // Check if upgrade possible
public int GetSkillLevel(string skillId)      // Get current level
```

---

#### **PlayerSkill** (Line 11456)

**Base type:** `MonoBehaviour` | Abstract: False | Sealed: False | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_id` | string | Unique skill identifier |
| `_name` | string | Display name |
| `_description` | string | Skill description tooltip |
| `_level` | int | Current skill level (0-10) |
| `_tier` | SkillTier | Skill tier classification |
| `_effect` | SkillEffect | Applied effect (damage boost, speed, etc.) |

**Key Methods:**

```csharp
public void LevelUp()                         // Increment level
public void LevelDown()                       // Decrement level
public float GetModifierValue()               // Get current stat modifier
```

---

#### **SkillTiers** (Enum)

| Tier | Description | Example Skills |
|------|-------------|----------------|
| **Tier 1** | Basic skills | First Aid, Lockpicking |
| **Tier 2** | Intermediate skills | Weapon Mastery, Alchemy |
| **Tier 3** | Advanced skills | Master Craftsman, Combat Expert |

---

#### **CharacterDifficultyController** (Line 11678)

**Base type:** `MonoBehaviour` | Singleton: True | Public: True

**Core Fields:**

| Field | Type | Purpose |
|-------|------|---------|
| `_reputation` | float | Current reputation value (-100 to +100) |
| `_difficulty` | DifficultySetting | Game difficulty level |
| `_npcAttitude` | Dictionary\<string, float\> | Faction-specific reputation |

**Key Methods:**

```csharp
public void ChangeReputation(float amount, string factionId = null)  // Modify reputation
public float GetReputation(string factionId)                         // Get faction rep
public DifficultySetting GetCurrentDifficulty()                      // Get difficulty level
```

---

### Reputation System

**Faction Values (from Faction enum):**

| Faction | Value | Description |
|---------|-------|-------------|
| `player` | 0 | Player faction (neutral) |
| `villagerNeutral` | 200 | Neutral villagers |
| `villagerPsycho` | 100 | Hostile villagers |
| `army` | 300 | Military faction |
| `mutant` | 400 | Mutant enemies |
| `animalPassive` | 500 | Passive animals |
| `animalAggressive` | 600 | Aggressive animals |

**Reputation Effects:**

- **Positive reputation**: NPCs offer better trades, fewer hostile encounters
- **Negative reputation**: NPCs become hostile, reduced trading options
- **Faction-specific**: Reputation varies per faction (villagers vs. army)

---

### Multiplayer Considerations

**Player System:**
- Player state (health, stamina, position) is **authoritative on host**
- Inventory changes require **network synchronization** (item pickup/drop/equip)
- Skills/XP progression should be **server-authoritative** to prevent cheating

**Inventory System:**
- Inventory contents must be **replicated to all clients**
- Hotbar selection requires **state synchronization**
- Item database is **client-side lookup table** (shared across all instances)

**Equipment System:**
- Armor values are **calculated client-side** for UI display
- Durability changes require **network update** on take damage/repair
- Equipment modifiers affect **combat calculations** (authoritative on host)

**Weapons System:**
- Melee attacks: **hit detection authoritative on host**, visual effects on clients
- Firearms: **ammo count synchronized**, bullet trajectory calculated client-side with server validation
- Throwables: **projectile state replicated** (position, velocity, rotation)

**Crafting System:**
- Recipe requirements checked **authoritative on host**
- Crafting progress **synchronized** to all clients
- Workbench interaction requires **network trigger**

**Skills & Reputation:**
- Skill points spent **authoritative on host**
- Reputation changes **replicated to all clients** for NPC behavior
- Difficulty setting is **server-authoritative**

---

## Summary

Darkwood's gameplay systems feature:

1. **Component-based architecture** with MonoBehaviour extensions (MonoBehaviourExt → CharBase → Player/Character)
2. **Event-driven communication** via delegates (onHit, onInSightOfPlayer, etc.)
3. **Singleton controllers** for cross-cutting concerns (InventoryController, AstarPath, CharacterDifficultyController)
4. **Database pattern** for centralized asset management (ItemsDatabase, CharactersDatabase)
5. **Implicit equipment slots** rather than explicit slot classes
6. **Multi-layer save system** supporting player state, inventory, equipment durability, and quest progress

This architecture provides a solid foundation for reverse engineering gameplay mechanics and implementing multiplayer functionality with proper authority models and network synchronization.
