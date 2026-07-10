# Darkwood Engine Architecture — Core Systems Analysis

**Generated from:** Assembly-CSharp.dll (1,115 classes), MelonLoader v0.7.2, Unity 2021.3.30f1  
**Date:** July 8, 2026  
**Status:** Complete

---

## Table of Contents

- [Startup Sequence](#startup-sequence)
- [Singleton Managers Architecture](#singleton-managers-architecture)
- [Scene Loading Flow](#scene-loading-flow)
- [Save System Entry Points](#save-system-entry-points)
- [Update Loop Execution Order](#update-loop-execution-order)
- [Key Architectural Patterns](#key-architectural-patterns)
- [Third-Party Integration](#third-party-integration)

---

## Startup Sequence

### 1. MelonLoader Bootstrap (Before Unity)

MelonLoader uses a **proxy DLL injection** technique (`version.dll`) to load before Unity's native code initializes:

```
version.dll → MelonLoader.NativeHost.dll → .NET 6 Runtime
    ↓
Loads plugins from /Plugins/ folder
Loads mods from /Mods/ folder (e.g., DarkwoodMP.Mod.dll)
Hooks into assembly loading via Mono.Cecil/AsmResolver
```

**Key Insight:** MelonLoader runs on **.NET 6 runtime**, not the game's Mono runtime. This allows it to intercept and modify assemblies before Unity loads them.

### 2. RuntimeInitializeOnLoad Execution

`RuntimeInitializeOnLoads.json` specifies two initialization methods:

| Method | Assembly | Purpose |
|--------|----------|---------|
| `SteamManager.InitOnPlayMode` | Assembly-CSharp | Steam integration (achievements, cloud saves) |
| `AndroidUnityInputHelper.Initialize` | Rewired_Android | Android input helper (**not present** in PC build) |

### 3. Unity Initialization Sequence

```
1. Unity native code loads
2. MelonLoader hooks into assembly loading
3. RuntimeInitializeOnLoad methods execute (SteamManager)
4. MonoBehaviour.Awake() runs for all components
5. MonoBehaviour.Start() runs for all components
6. SingletonMonoBehaviour classes self-register instances
7. SaveGameManager.Initialize() sets up asset references
8. First scene loads (level0 or main menu)
```

### 4. MonoBehavior Lifecycle Order

Standard Unity lifecycle applied to all game objects:

```
Awake() → OnEnable() → Start() → Update() loop
                    ↓
              OnDestroy() on destruction
```

**Game-specific extensions:**
- `MonoBehaviourExt` adds `startRoutine()` / `stopRoutine()` for coroutine management
- `CharBase` extends `MonoBehaviourExt` with character-specific lifecycle hooks (`OnSpawned()`)

---

## Singleton Managers Architecture

Darkwood uses **multiple singleton patterns** across its codebase:

### 1. Singleton<T> Pattern (Generic Base)

Used by: `SaveManager`, `InputScript`

```csharp
public abstract class Singleton<T> : MonoBehaviour where T : MonoBehaviour
{
    private static T _instance;
    public static T Instance => _instance ??= FindObjectOfType<T>();
    
    protected virtual void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }
}
```

**Key Singletons:**
- **SaveManager** (67 methods): Handles all save/load operations, file encryption/decryption, profile management
- **InputScript** (24 methods): Manages Rewired input system, mouse/keyboard/controller inputs

### 2. SingletonMonoBehaviour<T> Pattern

Used by: `AudioController` (152 methods)

```csharp
public abstract class SingletonMonoBehaviour<T> : MonoBehaviour where T : MonoBehaviour
{
    private static T _instance;
    public static T Instance => _instance ??= FindObjectOfType<T>();
}
```

**Key Singletons:**
- **AudioController**: Manages all audio playback, categories (music, sfx, ambient), playlists
- **tk2dUIManager**: tk2d 2D UI rendering manager

### 3. Static Class Pattern

Used by: `RoomManager` (static class)

```csharp
public static class RoomManager
{
    private static Dictionary<string, Room> _rooms = new();
    private static string _currentRoomName;
    
    public static void LoadRoom(string name, bool showGUI);
    public static void SaveCurrentRoom();
}
```

### 4. Instance Property Pattern

Used by: `SaveGameManager`, `tk2dUIAudioManager`

```csharp
public class SaveGameManager : MonoBehaviour
{
    private static SaveGameManager _instance;
    public static SaveGameManager Instance => _instance;
    
    private void Awake() { _instance = this; }
}
```

---

## Scene Loading Flow

### Level Naming Convention

Darkwood uses **level0 through level190** (192 levels total). Each level has:
- Unity scene file: `levelN` (binary format)
- Serialized data: `levelN.resS` (compressed companion)

### Loading Architecture

```
RoomManager.LoadRoom(string name, bool showGUI)
    ↓
LevelLoader (coordinates loading sequence)
    ↓
LevelSerializer (handles serialization/deserialization)
    ↓
SceneWasLoaded event fires
    ↓
RoomLoader processes loaded scene
    ↓
Game state restored from save data
```

### Key Classes in Scene Loading

| Class | Role | Methods |
|-------|------|---------|
| **LevelLoader** | Coordinates loading with callbacks | 22 methods, handles `SceneWasLoaded` events |
| **RoomManager** | Static room dictionary and state | Static class, manages current room |
| **Room** | MonoBehaviour per level instance | Has `Current` static property |
| **RoomDataSaveGameStorage** | Stores room-specific data | Extends `DontStoreObjectInRoom`, uses Dictionary<string, string> |

### Scene Persistence

- Objects marked with `DontDestroyOnLoad()` persist across scene loads (singletons, managers)
- Each level is a separate Unity scene but shares the same runtime state via singletons

---

## Save System Entry Points

### Primary Save Components

#### 1. SaveManager (`Singleton<SaveManager>`)

**Role:** Central save/load coordinator  
**Methods:** 67 total

| Method Category | Examples |
|-----------------|----------|
| File Management | `get_localChapterSaveFile()`, `get_staticFile()`, `get_dynamicFile()` |
| Encryption/Decryption | `encryptSaves()`, `decryptSaves()` |
| Profile Management | `loadGameProfiles()`, `saveGameProfiles()` |
| Save Operations | `Save()`, `Load()`, `saveChapterSave()`, `loadPlayer()` |

**Data Persistence Layers:**
1. **Static saves**: Game-wide settings and progress (`get_staticFile()`)
2. **Dynamic saves**: Current level state (`get_dynamicFile()`)
3. **Chapter saves**: Story progression data (`get_localChapterSaveFile()`)
4. **Profile saves**: Multiple save slots (`loadGameProfiles()`)

#### 2. LevelSerializer (Abstract Sealed Class)

**Role:** Core serialization engine for scenes  
**Nested Types:** 23 types, 71 methods total

| Method | Purpose |
|--------|---------|
| `SerializeLevel(bool urgent, string id)` | Serialize complete level to string |
| `SaveObjectTree(GameObject rootOfTree)` | Recursively save object hierarchy |
| `LoadObjectTree(string data)` | Deserialize and reconstruct objects |
| `Checkpoint()` / `ClearCheckpoint()` | Save/load checkpoint states |

**Features:**
- Compression support (likely zlib or LZ4)
- Checkpoint system for quicksave functionality
- Server upload capability (for multiplayer mod?)

#### 3. Storage Base Class

Abstract base providing serialization interface:

```csharp
public abstract class SerializerExtensionBase<T>
{
    public abstract string SerializeToString(T obj);
    public abstract T Deserialize(string data);
}
```

**Serialization Extensions for Unity Components:**
- `SerializeCamera`, `SerializeRigidBody`, `SerializeAudioClip`
- All extend `SerializerExtensionBase<T>` or `ComponentSerializerExtensionBase<T>`

---

## Update Loop Execution Order

### Base Class Hierarchy

#### MonoBehaviourExt (Game Object Base)

Extends `UnityEngine.MonoBehaviour` with routine management:

```csharp
public class MonoBehaviourExt : MonoBehaviour
{
    public virtual void Update();  // Virtual for override
    
    public static void startRoutine(Action startAction, Action finishAction, float length, bool looping);
    public static void stopRoutine(string routineType, bool all);
    public static bool isRoutineActive(string routineType);
}
```

**Used by:** CharBase, ItemLight, Liquid, and other game entities

#### CharBase (Character Base)

Extends `MonoBehaviourExt` — base class for **Player** and all characters:

- 44 methods, 68 fields
- Manages health, effects, character state
- Has `OnDestroy()`, `updateGraph()` methods

### Execution Order

Unity's standard execution order applies:

1. **FixedUpdate**: Physics-based updates (characters with Rigidbody)
2. **Update**: General game logic (InputScript, various game objects)
3. **LateUpdate**: Post-update processing (tk2dUIManager text mesh updates, camera adjustments)

**Key Classes with Update Methods:**

| Class | Update Type | Purpose |
|-------|-------------|---------|
| `InputScript` | Update() + LateUpdate() | Input polling and controller mode |
| `tk2dUpdateManager` | LateUpdate() | tk2d text mesh queue commits |
| `Player` (via CharBase) | Inherits from MonoBehaviourExt | Player game logic |

### No Script Execution Order Settings

Darkwood does **not** use Unity's script execution order system. Instead:
- Singleton patterns guarantee initialization order
- Static class methods coordinate manager interactions
- Event-based communication (SceneWasLoaded events) handles scene transitions

---

## Key Architectural Patterns

### Initialization Order

1. MelonLoader loads before Unity via `version.dll` proxy
2. RuntimeInitializeOnLoad runs `SteamManager.InitOnPlayMode`
3. MonoBehaviour.Awake() initializes all components
4. SingletonMonoBehaviour classes self-register instances
5. SaveGameManager.Initialize() sets up asset references and ID mappings

### Scene Management

- **Room-based loading**: Each "level" is a room within the main scene
- **Persistent objects**: `DontDestroyOnLoad` marks persistent game objects (singletons, managers)
- **Serialization**: LevelSerializer handles complete object tree serialization with compression

### Data Persistence Layers

| Layer | Purpose | Example Use Case |
|-------|---------|------------------|
| Static saves | Game-wide settings and progress | Options, unlocked achievements |
| Dynamic saves | Current level state | Enemy positions, loot drops |
| Chapter saves | Story progression data | Quest completion, reputation changes |
| Profile saves | Multiple save slots | Player choice of save file |

### Third-Party Integration

| Library | Purpose | Key Classes |
|---------|---------|-------------|
| **Rewired** | Input system (AndroidUnityInputHelper) | Android input helper (not present in PC build) |
| **tk2d** | 2D rendering and UI | tk2dUIManager, tk2dUpdateManager |
| **DOTween** | Animation system | DG.Tweening classes (in firstpass assembly) |
| **PathologicalGames** | Object pooling | SpawnPool, PrefabPool |
| **A* Pathfinding Project** | Navigation | AstarPath, AIPath, AILerp, RVOAI |

---

## Multiplayer Mod Integration Points

The existing **DarkwoodMP.Mod.dll v0.6.1** by yky provides reference implementations for:

- **Sync Services:** ~30 sync services (NetworkLayer, PacketReceiver, PlayerSync, EnemySync, etc.)
- **Deterministic World Gen:** Uses seed `696969` with 7 anchors
- **Networking Library:** LiteNetLib for packet-based networking

**Key Insight:** The multiplayer mod demonstrates that Darkwood's architecture is **compatible with network synchronization**, particularly:
- Singleton managers can host authoritative game state
- LevelSerializer's serialization can be extended for network replication
- RoomManager's scene loading can be coordinated across clients

---

## Summary

Darkwood's engine architecture features:

1. **MelonLoader-based modding** via proxy DLL injection (.NET 6 runtime)
2. **Multiple singleton patterns** (Singleton<T>, SingletonMonoBehaviour<T>, static classes, instance properties)
3. **Room-based scene loading** with level0-level190 naming convention
4. **Multi-layer save system** (static/dynamic/chapter/profile) using LevelSerializer with compression
5. **Update loop hierarchy** via MonoBehaviourExt → CharBase for characters
6. **Third-party integrations**: Rewired (input), tk2d (UI), DOTween (animations), A* Pathfinding

This architecture provides a solid foundation for reverse engineering gameplay systems and implementing multiplayer functionality.
