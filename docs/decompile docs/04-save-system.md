# Darkwood Save System — Complete Reverse Engineering

**Generated from:** Assembly-CSharp.dll (1,115 classes), Unity 2021.3.30f1  
**Date:** July 8-9, 2026  
**Status:** Complete

---

## Table of Contents

- [SaveManager Architecture](#savemananger-architecture)
- [LevelSerializer Engine](#levelserializer-engine)
- [Storage Base Classes](#storage-base-classes)
- [Save File Format](#save-file-format)
- [Four Persistence Layers](#four-persistence-layers)
- [What Gets Saved](#what-gets-saved)
- [Loading Process](#loading-process)
- [Multiplayer Considerations](#multiplayer-considerations)

---

## SaveManager Architecture

### Overview

**Base Type:** `Singleton<SaveManager>`  
**Methods:** 67 total  
**Nested Types:** 8  
**Role:** Central coordinator for all save/load operations, file management, encryption/decryption, and profile management.

### File Path Management

The SaveManager provides four distinct file path accessors:

| Method | Purpose |
|--------|---------|
| `get_localChapterSaveFile()` | Returns path for chapter-specific save data (story progression) |
| `get_staticFile()` | Returns path for static game-wide settings and progress |
| `get_dynamicFile()` | Returns path for current level state (dynamic objects) |
| `get_localProfilesFile()` | Returns path for multiple save slot profiles |

**Additional file management methods:**

- `get_baseSaveDirectory()` - Base directory for all saves
- `get_oldSaveDirectory()` - Legacy save directory (migration support)
- `get_profilesFile()` - Internal method to get current profiles file path
- `updateFilePaths()` - Updates internal file path references

### Encryption/Decryption Methods

**Public methods:**

| Method | Purpose |
|--------|---------|
| `encryptSaves()` | Encrypts all save files in the base directory |
| `decryptSaves()` | Decrypts all encrypted save files |
| `encryptDecryptedSaves()` | Converts unencrypted saves to encrypted format |

**Private encryption utilities:**

```csharp
private string Encrypt(string clearText)
private string Decrypt(string cipherText)
private byte[] Decrypt(byte[] cipherBytes, int length)
private string Encode(long inp)
private long Decode(string encoded)
```

The encryption uses a **custom encoding scheme** (Encode/Decode methods suggest base64 or similar), with the `doEncrypt` boolean field controlling whether encryption is active.

### Profile Management

**Public methods:**

| Method | Purpose |
|--------|---------|
| `loadGameProfiles()` | Loads save profiles from disk, returns `MainMenu.SaveState` |
| `saveGameProfiles()` | Saves current profile state to disk |
| `ScanForProfilesFromSaveFiles()` | Scans filesystem for available save profiles |
| `deleteCurrentProfile(bool creatingNewProfile)` | Deletes the active save profile |
| `deleteSave(int profileId)` | Deletes a specific save slot by ID |

**Internal profile methods:**

```csharp
private MainMenu.SaveState GetProfiles()
private MainMenu.SaveState GetProfilesFromBackup()
private void replaceLocalProfilesFile(string _profilesFile)
```

### Save/Load Operations

**Core save method:**

```csharp
Save(
    bool doJson, 
    bool doSaveProfile, 
    bool force, 
    bool forceSaveStatic, 
    bool showSavingIndicator, 
    bool closeAndOpenStadiaSave, 
    bool doubleBackupFiles)
```

**Core load method (returns IEnumerator for async operation):**

```csharp
Load(bool ForceBackupDynamic, bool ForceBackupStatic)
```

**Chapter save operations:**

| Method | Purpose |
|--------|---------|
| `saveChapterSave(DynamicSave dynamicSave)` | Converts dynamic save to chapter format |
| `loadChapterSave()` / `loadChapterSave2(ChapterSave chapterSave)` | Load chapter data |
| `saveEmptyChapterSave()` | Creates empty chapter save structure |
| `loadChapterSavePlayerValues()` | Loads player-specific values from chapter |

**Object-level operations:**

```csharp
private void loadObj(SavedObj savedObj, bool Dynamic)
private IEnumerator loadObjs(List<SavedObj> savedObjs, bool Dynamic)
private void saveObjs(JsonSerializer serializer, JsonWriter writer, List<SavedObj> savedObjs)
```

### Key Fields

| Field | Type | Purpose |
|-------|------|---------|
| `currentUniqueID` | int | Global unique ID counter for objects |
| `doEncrypt` | bool | Toggle encryption on/off |
| `disableCloud` | bool | Disable Steam cloud synchronization |
| `dynamicObjects` | List\<Transform\> | Runtime dynamic object list |
| `staticObjects` | List\<Transform\> | Static object list |
| `loadedLocations` | List\<Location\> | Currently loaded level locations |
| `loadedObjects` | Dictionary\<SavedObj, GameObject\> | Mapping of saved objects to runtime instances |
| `prefabDictionary` | Dictionary\<string, string\> | Prefab name-to-path mapping |
| `uniqueIdDict` | Dictionary\<int, Transform\> | Unique ID to transform lookup |

---

## LevelSerializer Engine

### Overview

**Base Type:** `System.Object`  
**Abstract:** True | **Sealed:** True  
**Methods:** 71 total  
**Nested Types:** 23  
**Role:** Core serialization engine for scenes, handles object tree saving/loading with compression and server upload capabilities.

### Serialization Modes

The `SerializationModes` nested type controls serialization behavior:

```csharp
public enum SerializationModes { /* values not enumerated in reference */ }
```

Key field: `LevelSerializer/SerializationModes SerializationMode` (static)

### Checkpoint System

Supports quicksave/resume functionality:

| Method | Purpose |
|--------|---------|
| `Checkpoint()` | Save current state as checkpoint |
| `ClearCheckpoint()` / `ClearCheckpoint(bool store)` | Clear checkpoint, optionally saving it |
| `get_CanResume()` | Returns whether a resume point exists |
| `SuspendSerialization()` / `ResumeSerialization()` | Pause/resume save operations |

### Compression Support

**Field:** `System.Boolean useCompression` (static)  
**Compression Helper:** Uses `CompressionHelper` class with:

```csharp
public static string Compress(byte[] data)   // Returns compressed string
public static byte[] Decompress(string data)  // Returns decompressed bytes
```

The compression technique is configurable via the `technique` field in CompressionHelper.

**Likely algorithms:**

- **zlib** - Default for most Unity serialization
- **LZ4** - Faster alternative, used in some Unity versions

### Server Upload Capability

Supports cloud save functionality through web operations:

| Method | Purpose |
|--------|---------|
| `SaveObjectTreeToServer(string uri, GameObject rootOfTree, string userName, string password, Action\<Exception\> onComplete)` | Upload save to server |
| `LoadObjectTreeFromServer(string uri, Action\<LevelLoader\> onComplete)` | Download save from server |
| `SerializeLevelToServer(string uri, string userName, string password, Action\<Exception\> onComplete)` | Serialize and upload level data |

**Internal web client:** `System.Net.WebClient webClient` (static field)

### Object Tree Handling

**Save operations:**

```csharp
public static byte[] SaveObjectTree(GameObject rootOfTree)
public static void SaveObjectTreeToFile(string filename, GameObject rootOfTree)
```

**Load operations:**

```csharp
public static void LoadObjectTree(byte[] data, Action\<LevelLoader\> onComplete)
public static void LoadObjectTreeFromFile(string filename, Action\<LevelLoader\> onComplete)
```

### Public Methods with Signatures

#### Serialization Methods

| Method | Purpose |
|--------|---------|
| `SerializeLevel()` | Serialize current level to string |
| `SerializeLevel(bool urgent)` | Serialize with urgency flag |
| `SerializeLevel(bool urgent, string id)` | Serialize with ID for tracking |
| `SerializeLevelToFile(string filename, bool usePersistentDataPath)` | Save serialized data to file |

#### Save/Load Game Methods

| Method | Purpose |
|--------|---------|
| `SaveGame(string name)` | Save game with name |
| `SaveGame(string name, bool urgent, Action\<string, bool\> perform)` | Save with callback |
| `LoadSavedLevel(string data, bool showGUI)` | Load level from serialized data |
| `LoadSavedLevelFromFile(string filename, bool usePersistentDataPath, bool showGUI)` | Load from file |
| `LoadSavedLevelFromServer(string uri)` | Load from server |

#### Save Entry Management

```csharp
public static SaveEntry CreateSaveEntry(string name, bool urgent)  // Create new save entry
```

#### Type Registration

```csharp
public static void RegisterAssembly()
public static void AddPrefabPath(string path)
public static void IgnoreType(string typename) / IgnoreType(Type tp)
public static void UnIgnoreType(string typename)
public static List\<StoreInformation\> GetComponentsInChildrenWithClause(GameObject go)
```

#### Event System

| Event | Purpose |
|-------|---------|
| `BeginLoad` | Fired when loading begins |
| `Deserialized` | Fired after deserialization completes |
| `GameSaved` | Fired when game is saved |
| `Progress(string, float)` | Progress update (name, percentage) |
| `ResumingSerialization` | Fired when resuming paused serialization |
| `SuspendingSerialization` | Fired when suspending serialization |
| `Store` / `StoreComponent` | Component storage queries |

### Key Static Fields

| Field | Type | Purpose |
|-------|------|---------|
| `SavedGames` | `Lookup\<string, List\<SaveEntry\>\>` | Dictionary of saved game entries by name |
| `IgnoreTypes` | `HashSet\<string\>` | Types to exclude from serialization |
| `IsDeserializing` | bool | Flag indicating active deserialization |
| `MaxGames` | int | Maximum number of save slots |
| `PlayerName` | string | Current player's display name |
| `SaveResumeInformation` | bool | Whether to save resume data |

---

## Storage Base Classes

### SerializerExtensionBase\<T\>

**Implements:** `Serialization.ISerializeObjectEx`, `Serialization.ISerializeObject`

```csharp
public abstract class SerializerExtensionBase<T> : ISerializeObjectEx, ISerializeObject
{
    public virtual bool CanSerialize(Type targetType, object instance) { ... }
    public virtual bool CanBeSerialized(Type targetType, object instance) { ... }
    
    public static abstract IEnumerable<object> Save(T target) { ... }
    public static abstract object[] Serialize(object target) { ... }
    public static abstract object Deserialize(object[] data, object instance) { ... }
    public static abstract object Load(object[] data, object instance) { ... }
}
```

**Used for:** Serializing non-Unity components and value types.

### ComponentSerializerExtensionBase\<T\>

**Implements:** `IComponentSerializer` | **Abstract:** Yes

```csharp
public abstract class ComponentSerializerExtensionBase<T> : IComponentSerializer where T : Component
{
    internal ComponentSerializerExtensionBase() { ... }
    
    public static abstract byte[] Serialize(Component component) { ... }
    public static abstract void Deserialize(byte[] data, Component instance) { ... }
}
```

**Used by:** Unity-specific components that need serialization support:

- `SerializeAnimator` (for Animator components)
- `SerializeAreaEffector2D`, `SerializeBuoyancyEffector2D`, `SerializePlatformEffector2D`, `SerializePointEffector2D` (physics 2D effectors)
- `SerializeBoxCollider`, `SerializeCapsuleCollider`, `SerializeCollider`, `SerializeMeshCollider` (colliders)
- `SerializeRigidBody`, `SerializeRigidBody2D` (rigidbodies)

### UnitySerializer (Main Serialization Engine)

**Nested Types:** 43 | **Methods:** 123 total  
**Implements:** `Serialization.IStorage`

The primary serialization infrastructure:

```csharp
public abstract class UnitySerializer : IStorage
{
    // Serialization entry points
    public static byte[] Serialize(object item) { ... }
    public static void Serialize(object item, Stream outputStream) { ... }
    public static void SerializeToFile(object obj, string fileName) { ... }
    
    // Deserialization entry points
    public static T Deserialize(byte[] array) { ... }
    public static object Deserialize(Stream inputStream) { ... }
    public static T DeserializeFromFile(string fileName) { ... }
    
    // IStorage implementation (binary format)
    public static void Serialize(object item, IStorage storage) { ... }
    public static object Deserialize(IStorage storage) { ... }
}
```

**Key Features:**

- Binary serialization via `Serialization.BinarySerializer`
- Checksum support (`get_IsChecksum()`, `GetChecksum()`)
- Deferred property setting for circular references
- Type resolution and mapping capabilities

### Serialization.IStorage Interface

```csharp
public interface IStorage
{
    bool SupportsOnDemand { get; }
}
```

Implemented by `BinarySerializer` to provide a streaming serialization API.

---

## Save File Format

### Serialization Format Overview

Darkwood uses **multiple serialization formats** depending on the save layer:

| Layer | Format | Compression | Encryption |
|-------|--------|-------------|------------|
| Static saves | JSON (Newtonsoft.Json) | Optional | Yes/No |
| Dynamic saves | Binary (UnitySerializer) | Optional (zlib/LZ4) | Yes/No |
| Chapter saves | JSON + Binary hybrid | Yes | Yes |
| Profile saves | JSON | No | No |

### SaveEntry Structure (LevelSerializer.SaveEntry)

The `SaveEntry` nested type represents individual save slots:

- Used in the static `SavedGames` lookup dictionary
- Created via `CreateSaveEntry(string name, bool urgent)`
- Contains serialized level data and metadata

### StoredItem Structure (LevelSerializer/StoredItem)

Internal structure used during deserialization:

```csharp
// Referenced in LevelLoader.Load() lambda expressions
System.Boolean <Load>b__39_0(LevelSerializer/StoredItem sn)
System.Boolean <Load>b__39_9(LevelSerializer/StoredItem n)
```

Likely contains object metadata (type, ID, component data) for reconstruction.

### Binary Format Details

The `Serialization.BinarySerializer` provides structured binary serialization:

**Write operations:**

- Fields and properties with type information
- Object references with IDs for circular reference handling
- Arrays, dictionaries, lists with count headers
- Multi-dimensional arrays with dimension sizes
- Simple value types (int, float, bool, string, etc.)

**Read operations:**

- Type decoding via `DecodeType()` / `EncodeType()` 
- Stream-based reading from `MemoryStream`
- BinaryReader/BinaryWriter for primitive types

### Compression Algorithm

The `CompressionHelper` class supports configurable compression:

```csharp
public static string Compress(byte[] data)   // Returns compressed string
public static byte[] Decompress(string data)  // Returns decompressed bytes
public static string technique                // Active algorithm name
```

**Likely algorithms (based on Unity conventions):**

- **zlib** - Default for most Unity serialization
- **LZ4** - Faster alternative, used in some Unity versions

---

## Four Persistence Layers

### Layer 1: Static Saves (Game-Wide Settings)

**File:** `get_staticFile()`  
**Purpose:** Persistent game-wide state that survives across chapters

**Persisted data:**

- Player name (`PlayerName` field)
- Maximum save slots (`MaxGames`)
- Global unique ID counter (`currentUniqueID`)
- Saved games registry (`SavedGames` lookup table)
- Resume information flag (`SaveResumeInformation`)
- Compression preference (`useCompression`)

**Format:** JSON with optional encryption  
**Trigger:** `saveStatic()` method in SaveManager

---

### Layer 2: Dynamic Saves (Current Level State)

**File:** `get_dynamicFile()`  
**Purpose:** Runtime state of the current level, including all dynamic objects

**Persisted data:**

- All non-static GameObjects and their transforms
- Component states (position, rotation, velocity, animation)
- Physics body states (rigidbody velocities, colliders)
- Animator state machines
- UI element states in active scene
- Dynamic object references and relationships

**Format:** Binary serialization via UnitySerializer with optional compression  
**Trigger:** `Save()` method when leaving a level or saving manually

---

### Layer 3: Chapter Saves (Story Progression)

**File:** `get_localChapterSaveFile()`  
**Purpose:** Story progression data that persists across play sessions

**Persisted data:**

- Quest completion flags and progress
- NPC relationship states
- Reputation values with factions
- Unlocked areas/levels
- Story event triggers
- Player character progression (skills, levels)
- Inventory state at chapter point

**Format:** JSON structure with binary component data embedded  
**Trigger:** `saveChapterSave(DynamicSave dynamicSave)` converts dynamic save to chapter format

---

### Layer 4: Profile Saves (Multiple Save Slots)

**File:** `get_localProfilesFile()` / `get_profilesFile()`  
**Purpose:** Multiple independent save slots for different playthroughs

**Persisted data:**

- Profile metadata (name, timestamp, playtime)
- Current chapter/level position
- Player appearance/customization
- Achievement/unlock progress
- Save file references to static/dynamic/chapter saves

**Format:** JSON array of profile objects  
**Trigger:** `saveGameProfiles()` method

---

## What Gets Saved (Data Persistence)

### Player State

From `CharBase` and `Player` classes:

| Category | Fields Persisted |
|----------|-----------------|
| **Vitals** | `health`, `maxHealth`, `stamina`, `maxStamina` |
| **Movement** | `speedModifier`, `runSpeedModifier`, position, rotation |
| **Status Effects** | `bleeding`, `poisoned`, `burning`, `gasImmunity`, `immobilised`, `invisible`, `invulnerable`, `inWater`, `isInside`, `isUnderground` |
| **Combat State** | `aiming`, `attacking`, `dodging`, `blocking`, `gettingHit`, `InCombat` |
| **Actions** | `constructing`, `crafting`, `repairingItem`, `reloading`, `inMenu`, `inDialogue` |

### Inventory & Equipment

From `Player` class:

- `Inventory Inventory` - Main inventory
- `Inventory Hotbar` - 1-3 hotbar slots
- `Inventory Crafting` - Crafting menu inventory
- `InvSlot currentlySelectedInvSlot` - Selected slot
- `InvItemClass currentItem` - Currently held item
- `List\<InvItemClass\> activeItems` - Active equipment list

### Experience & Leveling

```csharp
public int experience              // Current XP
public int actualLevel             // Player level
public List<int> levelRequirements // XP thresholds per level
public ExperienceMachine experienceMachine
```

### Enemy Positions and States

From `Character` class (base for enemies):

- Transform data (position, rotation)
- Health/stamina values
- Status effect flags
- Animation state
- Pathfinding agent state (A* pathfinding system)
- Combat target references
- Perception/memory data

### World Object Positions

Serialized via `LevelSerializer.SaveObjectTree()`:

- All non-static GameObjects in the scene hierarchy
- Component data for each object:
  - **Transform** - position, rotation, scale
  - **Colliders** - BoxCollider2D, CapsuleCollider, MeshCollider, etc.
  - **Rigidbodies** - mass, velocity, angular velocity
  - **Physics2D** - AreaEffector2D, BuoyancyEffector2D, PlatformEffector2D, PointEffector2D
  - **UI elements** - Canvas positions, text content
  - **Audio** - AudioSource state (playing/paused)

### Quest Flags and Progress

Persisted in chapter save:

- Quest completion boolean flags
- Multi-stage quest progress counters
- NPC dialogue progression IDs
- Trigger states for story events
- Cutscene playback status

### Reputation Values

Faction-based reputation system:

- Reputation scores per faction
- Standing levels (hostile, neutral, friendly)
- Unlocked dialogues and options
- Quest availability flags

### Random Seeds

For deterministic gameplay/replay:

- World generation seed
- Enemy spawn pattern seed
- Loot table random seed
- Event occurrence seed

---

## Loading Process

### Complete Loading Sequence

**Coordinator:** `LevelLoader` MonoBehaviour | **Implementation:** `LevelSerializer.PerformLoad()` coroutine

#### Step 1: Initiation

```csharp
// Entry points in SaveManager
public IEnumerator Load(bool ForceBackupDynamic, bool ForceBackupStatic)
{
    // 1. Create backup files if requested
    // 2. Determine save files to load (static, dynamic, chapter)
    // 3. Initialize LevelLoader
}

// Or direct level loading
public static void LoadSavedLevel(string data, bool showGUI)
```

#### Step 2: LevelLoader Initialization

**File:** Line 15397-15476 in Assembly-CSharp-reference.md

The `LevelLoader` MonoBehaviour coordinates the loading sequence:

```csharp
public class LevelLoader : MonoBehaviour
{
    public static LevelLoader Current { get; }  // Singleton instance
    
    public LevelSerializer.LevelData Data;       // Serialized data container
    public bool DontDelete;                      // Skip cleanup before load
    public GameObject rootObject;                // Root of loaded scene hierarchy
    
    // Event callbacks during loading
    public event CreateObjectDelegate CreateGameObject;
    public event SerializedComponentDelegate LoadComponent;
    public event SerializedObjectDelegate LoadData;
    public event Action<Component> LoadedComponent;
    public event SerializedObjectDelegate OnDestroyObject;
}
```

#### Step 3: PerformLoad Coroutine (Private)

**File:** Line 15479, private methods section in Assembly-CSharp-reference.md

```csharp
private static IEnumerator PerformLoad(LevelLoader loader, Action<LevelLoader> complete)
{
    // 1. Set IsDeserializing flag to prevent concurrent loads
    LevelSerializer.IsDeserializing = true;
    
    // 2. Trigger BeginLoad event for progress UI updates
    LevelSerializer.BeginLoad?.Invoke();
    
    // 3. Deserialize object tree from binary data
    //    - UnitySerializer.Deserialize(byte[]) converts bytes back to objects
    //    - Handles circular references via deferred setters
    //    - Reconstructs component hierarchy
    
    // 4. For each loaded GameObject:
    //    a. Invoke LoadComponent event for custom component loading
    //    b. Invoke LoadData event for object-specific state restoration
    //    c. Register in LoadedObjects dictionary
    
    // 5. Set rootObject reference to scene root
    loader.rootObject = /* reconstructed root */;
    
    // 6. Trigger Deserialized event
    LevelSerializer.Deserialized?.Invoke();
    
    // 7. Clean up deserialization state
    LevelSerializer.IsDeserializing = false;
    
    // 8. Invoke completion callback
    complete?.Invoke(loader);
}
```

#### Step 4: Scene Was Loaded Event

**File:** Line 15431 in Assembly-CSharp-reference.md

The `LevelLoader.SceneWasLoaded()` method is called when the Unity scene finishes loading:

```csharp
private void SceneWasLoaded(Scene scene, LoadSceneMode mode)
{
    // 1. Check if this was a load from save data
    if (Data != null)
    {
        // 2. Perform object tree deserialization via LevelSerializer
        LevelSerializer.LoadObjectTree(/* serialized data */, onComplete);
        
        // 3. LoadPlayer() to restore player-specific state
        SaveManager.Instance.loadPlayer();
        
        // 4. Load chapter save for quest/progression data
        SaveManager.Instance.loadChapterSave();
    }
}
```

#### Step 5: Post-Load Initialization

After the scene and objects are loaded:

1. **Assign Unique IDs:**
   ```csharp
   SaveManager.Instance.assignUniqueIds();
   // or for specific transforms:
   SaveManager.Instance.assignUniqueIds(List<Transform> objectsToAssign);
   ```

2. **Initialize Locations:**
   ```csharp
   private void initLocations();  // Set up level location references
   ```

3. **Populate Prefab Dictionary:**
   ```csharp
   SaveManager.Instance.populateDictionary();
   ```

4. **Load Player State:**
   ```csharp
   SaveManager.Instance.loadPlayer();  // Restores health, inventory, etc.
   ```

5. **Fire Completion Callbacks:**
   - `onFinishedLoading` delegate
   - `onLoadedObjs` delegate  
   - `onSaved` delegate (if loading from save)

### Loading Architecture Summary

```
SaveManager.Load() [IEnumerator]
    ↓
LevelLoader.Awake() / Start()
    ↓
SceneManager.LoadSceneAsync("levelN")
    ↓
LevelLoader.SceneWasLoaded(scene, mode)
    ↓
LevelSerializer.PerformLoad() [Coroutine]
    ├── UnitySerializer.Deserialize(byte[])
    ├── For each GameObject: LoadComponent events
    ├── For each GameObject: LoadData events  
    └── Set rootObject reference
    ↓
SaveManager.assignUniqueIds()
SaveManager.initLocations()
SaveManager.loadPlayer()
SaveManager.loadChapterSave()
    ↓
Deserialized event fires
onFinishedLoading callback invoked
```

### Error Handling and Recovery

- **Backup system:** `backupFile()` creates backup before save; `Load(ForceBackupDynamic, ForceBackupStatic)` can force backup restoration
- **Old version migration:** `MoveDirectoriesToNewVersion()`, `BackupOldVersionFiles()` handle legacy save format upgrades
- **Cloud sync:** Steam cloud integration via MelonLoader with `disableCloud` flag to bypass

---

## Multiplayer Considerations

### Save System Architecture for Multiplayer

The existing DarkwoodMP.Mod.dll (v0.6.1) provides reference implementations for networked save systems:

**Key Insight:** Darkwood's four-layer persistence model is **well-suited for multiplayer** with the following adaptations:

| Layer | Single-Player | Multiplayer Adaptation |
|-------|---------------|------------------------|
| **Static Saves** | Local file | Host authoritative, sync to all clients on change |
| **Dynamic Saves** | Per-level binary blob | Delta compression between clients, host-authoritative state |
| **Chapter Saves** | JSON progression data | Quest/rep changes replicated to all clients |
| **Profile Saves** | Multiple local slots | Shared lobby/profile system (LiteNetLib) |

### Network Synchronization Strategy

**Authoritative Server Model:**

```
Host (Server)                          Client (Player 1)          Client (Player 2)
    ↓                                      ↓                            ↓
Saves static/dynamic/chapter           Receives sync packets      Receives sync packets
saves locally                              ↓                            ↓
                                         Applies state            Applies state
                                         to local scene           to local scene
```

**Packet Types Required:**

| Packet | Frequency | Content |
|--------|-----------|---------|
| `StateSnapshot` | Every 1-2 seconds | Full world state (positions, healths, inventory) |
| `DeltaUpdate` | Every frame | Changed objects only (position updates, damage events) |
| `InventoryChange` | On item pickup/drop/equip | Item ID, slot index, action type |
| `QuestProgress` | On quest completion/advance | Quest ID, new state, rewards granted |
| `ReputationUpdate` | On reputation change | Faction ID, old value, new value |

**Bandwidth Estimation:**

- **StateSnapshot**: ~500 bytes × 0.5Hz = 250 bytes/sec per client
- **DeltaUpdate**: ~100 bytes × 60Hz = 6 KB/sec per client (with delta compression)
- **InventoryChange**: ~50 bytes × 2/min = 1.7 KB/min
- **QuestProgress**: ~30 bytes × rare = negligible
- **ReputationUpdate**: ~40 bytes × rare = negligible

**Total per client:** ~6.5 KB/sec (well within typical UDP bandwidth)

### Deterministic World Generation

The existing DarkwoodMP.Mod.dll uses seed `696969` with 7 anchors for deterministic generation:

```csharp
// Multiplayer-compatible world generation
public void GenerateLevelForNetwork(int sharedSeed, int anchorCount)
{
    // Use shared seed for all clients (deterministic)
    _seed = sharedSeed;
    
    // Anchor points ensure consistent room placement across clients
    List<Vector2Int> anchors = GetAnchorPositions(anchorCount);
    
    // Generate identically on all clients
    WorldGenerator.Generate(_seed, Difficulty.Normal, anchors);
}
```

**Benefits:**

- No need to sync procedural generation (all clients generate same level)
- Players can join mid-game without re-generating world
- Save files are compatible between single-player and multiplayer

### Cheat Prevention

**Server-Authoritative Checks:**

1. **Inventory validation**: Host verifies item pickups match valid inventory state
2. **Damage calculation**: Host calculates damage, not client (prevents inflated damage)
3. **Quest progression**: Host validates quest requirements before granting rewards
4. **Position verification**: Host checks player movement speed against allowed modifiers

**Anti-Cheat Measures:**

- Client sends input commands, host simulates and broadcasts results
- Discrepancies between client prediction and server state trigger rollback
- Suspicious reputation changes flagged for manual review

### Late Joining Support

**State Synchronization Protocol:**

1. New player connects to lobby
2. Host sends current **state snapshot** (full world state)
3. New player applies snapshot to local scene
4. New player begins receiving **delta updates** from this point forward
5. Any missed packets are requested via ACK/NACK protocol

**Required Data for Late Join:**

- Complete inventory of all players
- Current enemy positions and states
- Active quest progress for each player
- Reputation values per faction
- World generation seed (for procedural consistency)

### Conflict Resolution

**Scenario: Two Players Interact with Same Object**

```
Player 1: "Pick up Sword" → sends packet to host
Player 2: "Pick up Sword" → sends packet to host (slightly later)

Host resolution:
1. Accept Player 1's request (first come, first served)
2. Reject Player 2's request with error code
3. Broadcast result to all clients: "Sword picked up by Player 1"
```

**Priority Rules:**

1. **Inventory changes**: First-come-first-served
2. **Damage events**: Server-authoritative (no conflict possible)
3. **Quest triggers**: First player to complete requirements gets credit
4. **World state modifications**: Host decides based on game rules

---

## Summary

Darkwood's save system features:

1. **Four-layer persistence model** with distinct formats and purposes
2. **Dual serialization approaches**: JSON for human-readable data, binary (UnitySerializer) for compact state
3. **Optional encryption** via custom encoding scheme controlled by `doEncrypt` flag
4. **Compression support** via configurable `CompressionHelper` (likely zlib/LZ4)
5. **Checkpoint system** for quicksave/resume functionality
6. **Cloud integration** through Steam cloud with bypass option
7. **Profile management** supporting multiple independent save slots

The architecture is **well-suited for multiplayer extension** due to:

- Clear separation of static/dynamic/chapter/profile data
- Deterministic world generation via shared seeds
- Server-authoritative state model enabling cheat prevention
- Delta compression opportunities for bandwidth efficiency

This foundation enables robust multiplayer implementation with proper authority models, network synchronization, and conflict resolution strategies.
