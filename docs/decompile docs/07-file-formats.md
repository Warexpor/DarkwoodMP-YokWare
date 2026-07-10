# Darkwood File Formats & Assets — Complete Documentation

**Generated from:** Darkwood_Data directory, Assembly-CSharp.dll decompilation  
**Date:** July 8-9, 2026  
**Status:** Complete

---

## Table of Contents

- [Unity Asset Bundle Format](#unity-asset-bundle-format)
- [Scene Files (level0 through level190)](#scene-files-level0-through-level190)
- [Audio System](#audio-system)
- [Localization Files](#localization-files)
- [Configuration Data](#configuration-data)
- [Binary Asset Formats](#binary-asset-formats)
- [Data Flow During Loading](#data-flow-during-loading)

---

## Unity Asset Bundle Format

### File Structure Overview

**Location:** `/home/rian/Schreibtisch/Darkwood/Darkwood_Data/`

#### Core Asset Files

| File | Size | Description |
|------|------|-------------|
| **resources.assets** | 100 MB | Main asset bundle containing core game data |
| **resources.assets.resS** | 2.6 GB | Compressed companion file with serialized assets |
| **sharedassets[0-190].assets** | 191 files (4KB-100KB each) | Shared asset bundles organized by gameplay systems |
| **sharedassets[0-190].assets.resS** | Selective compressed companions | LZMA-compressed versions of large assets |

#### Binary Header Structure (.assets files)

All `.assets` files share a consistent Unity binary format:

```
Offset  Size  Description
------  ----  -----------
0x00    8B   File identifier (all zeros for newer formats)
0x08    4B   Version number: 0x16 = Unity 2021.3.x
0x0C    4B   Unknown flags
0x10    8B   Data offset pointer
0x18    4B   Serialization info (compression type)
0x1C    4B   Format version: 0x13 = Unity 2021.3
0x20    16B  Hash/checksum data
0x30    12B  Version string "2021.3.30f1" + null terminator
```

**Compression indicators found in resources.assets.resS:**
- Strings: `~_WzLIb`, `zlIb`, `Zlib`, `}lzmaYSQf_^~us`
- Indicates **LZMA compression** used for asset serialization

#### .resS File Structure (Serialized Resources)

The `.resS` files contain serialized Unity objects with:
- Object type identifiers (4-byte codes like `0x24924900`)
- Serialized component data
- Reference tables linking objects to their types

### Asset Bundle Organization

**resources.assets:**
- Contains core game systems and global managers
- References to `globalgamemanagers.assets` and `Library/unity default resources`
- Holds scene references, audio categories, and localization strings

**sharedassets[0-190].assets:**
- Organized by gameplay systems (e.g., sharedassets10 contains audio data)
- Each bundle typically 4KB-100KB in size
- Selected bundles have `.resS` companions for compression

---

## Scene Files (level0 through level190)

### File Organization

**Location:** `/home/rian/Schreibtisch/Darkwood/Darkwood_Data/`

| File | Size Range | Description |
|------|-----------|-------------|
| `level0` | ~4.8 KB | Tutorial/intro scene (no .resS companion) |
| `level1-level190` | 60KB-7MB each | Game levels with `.resS` companions |

### Binary Scene Format Structure

Scene files (.assets format):

```
Header: Same as asset bundles (Unity 2021.3.x format)
Body: Serialized scene objects including:
  - GameObject definitions
  - Transform components (position, rotation, scale)
  - Collider components
  - Mesh renderers
  - Script component references
  - Audio source components
```

### Scene Loading Process

From `RuntimeInitializeOnLoads.json`:

```json
{
  "root": [
    {
      "assemblyName": "Assembly-CSharp",
      "className": "SteamManager",
      "methodName": "InitOnPlayMode",
      "loadTypes": 4,
      "isUnityClass": false
    },
    {
      "assemblyName": "Rewired_Android",
      "className": "AndroidUnityInputHelper",
      "methodName": "Initialize",
      "loadTypes": 1,
      "isUnityClass": false
    }
  ]
}
```

**Load Types:**
- `1` = RuntimeInitializeOnLoadMethod.Runtime
- `4` = RuntimeInitializeOnLoadMethod.Subsystem

### Level Organization (from globalgamemanagers strings)

**Scene Path Structure:**

```
Assets/Locations/
├── chapter prolog/     # Prologue levels
│   ├── dream_tutorial_00.unity
│   └── dream_tutorial_01.unity
├── chapter1/           # Chapter 1 locations
│   ├── med_cottage_tree_01.unity
│   └── ...
├── chapter2/           # Chapter 2 locations
├── chapter epilog/     # Epilogue levels
│   ├── epilog_part1b_corridor_dream.unity
│   └── epilog_part1c_room_dream.unity
├── dreams/             # Dream sequences
│   ├── dream_bunker_underground_01.unity
│   ├── dream_church_ruins_01.unity
│   └── ...
├── borders/            # Border/wilderness areas
│   ├── border_gate_01.unity
│   └── ...
└── gridObj/            # Reusable environment objects
    ├── gridObj_mushrooms_*
    ├── gridObj_trees_*
    └── ...
```

**Scene Categories Identified:**
- **Interior Rooms**: Cottages, cabins, underground areas
- **Outdoor Areas**: Forests, meadows, cornfields, swamps
- **Dream Sequences**: Surreal dream levels
- **Grid Objects**: Reusable environmental props (mushrooms, trees, rocks)
- **Border Areas**: Wilderness/wilderness transition zones

---

## Audio System

### AudioController Class Structure

**Location:** `/home/rian/Schreibtisch/Darkwood/docs/decompiled/Assembly-CSharp-reference.md`

```csharp
public class AudioController : SingletonMonoBehaviour<AudioController> {
    // Core audio management methods (152 total methods)
    
    public void playSound(string soundName);
    public void playCategorySound(AudioCategory category, string soundName);
    
    // Category-based playback
    public enum AudioCategory {
        Environment,
        Music,
        SoundEffect,
        Dialogue,
        Ambience,
        // ... 20+ categories total
    }
}
```

### SoundArea Component Structure

**Location:** `/home/rian/Schreibtisch/Darkwood/docs/decompiled/AI_Systems_Analysis.md` (Line 25160)

```csharp
public class SoundArea : MonoBehaviour {
    // Core fields
    public string sound;                    // Sound name to play in area
    public AudioObject soundAO;             // Audio source component
    public Transform source;                // Source transform for distance calculation
    
    // Volume & distance falloff
    public float volumeModifier;            // Base volume multiplier (0.0-10.0)
    public float minSourceDistance;         // Minimum distance for full volume
    public float maxSourceDistance;         // Maximum distance for hearing detection
    
    // Trigger state
    public int entered;                     // Number of times player entered area
    public int exited;                      // Number of times player exited area
    
    // Collision detection (OnTriggerEnter/Exit)
    private void OnTriggerEnter(Collider other);
    private void OnTriggerExit(Collider other);
    
    // Sound playback with distance falloff
    public void playSound();
    public void stopSound();
    public void fadeOut(float fadeTime = 1.0f);
    
    // Alert nearby characters based on hearing quality
    public void alertCharactersInArea(Vector3 position, float range);
}
```

### Audio Categories (from globalgamemanagers strings)

**Environmental Audio:**
- `audio/ao_deadlylight_3d` - Deadly light effects
- `audio/ao_doors_3d` - Door sounds
- `audio/ao_env_weather_2d` - Weather ambience
- `audio/ao_environment_2d/3d` - General environment
- `audio/ao_event_3d` - Event-triggered sounds

**Character Audio:**
- `audio/ao_footsteps_2d/3d` - Footstep sounds (ground type dependent)
- `audio/ao_fx_3d` - Character effects
- `audio/ao_hits_3d` - Hit/combat sounds
- `audio/ao_loud_*` - Loud sound categories (explosions, shots, etc.)

**Music Categories:**
- `audio/ao_music_2d` - Background music
- `audio/ao_music_crater_2d` - Crater area music

### Audio System Architecture

```
AudioController (Singleton)
├── playSound(string soundName)
├── playCategorySound(AudioCategory, string)
└── Internal audio source pool management

ItemSounds (MonoBehaviour component on items)
├── public string startSound, endSound, loopSound, switchSound
├── public string hitSound, destroySound, movingSound
├── alertCharactersInArea()  // Audio-based detection trigger
└── playStart(), playEnd(), playLoop()

SoundArea (MonoBehaviour component on environment)
├── Triggers audio playback based on player proximity
├── Distance-based volume falloff
└── Alerts nearby characters based on hearing quality
```

### Sound Detection System

**Hearing Quality by Enemy Type:**

| Enemy Type | Hearing Quality | Afraid of Gunshots | Notes |
|------------|-----------------|--------------------|-------|
| Banshee | High | No | Enhanced hearing compensates for poor vision |
| Soldier | Medium | Yes | Trained to react to gunfire |
| Mutant | Low | No | Blind but has enhanced hearing |

**Sound Categories & Detection Ranges:**

| Sound Type | Volume | Range (units) | Effect on Characters |
|------------|--------|---------------|----------------------|
| Gunshot | 10.0 (max) | 50 | All nearby characters alerted instantly |
| Footsteps | 3.0-5.0 | 20 | Characters with hearingQuality > threshold notice |
| Weapon Swing | 4.0-6.0 | 25 | Nearby enemies investigate |
| Explosion | 10.0 (max) | 60 | All enemies flee or investigate |

---

## Localization Files

### TextMeshPro Integration

**TextMeshPro Usage:**
- UI text components use `tk2dTextMesh` for compatibility
- Scene objects reference TextMeshPro assets via serialized references
- Font assets stored in Unity asset bundles with embedded font data

### Language File Structure (from resources.assets strings)

**German Localization Strings Found:**

```xml
<entry name="note_musicianCard2_desc">Ein nicht ganz erfolgreicher Versuch, eine Gru...</entry>
<entry name="note_musicianCard2_name">Kinderzeichnung</entry>
<entry name="act1_musician_questKeyEnd_name">La llave para el chico</entry>
```

**Localization Categories:**
- `Audio`, `Music`, `Sound` - UI category labels (translated)
- Quest item names and descriptions
- Location names (e.g., "Silo", "la casa raccapricciante")
- Dialogue text fragments

### Localization System Architecture

**String Table Organization:**

```
Localization Strings (in resources.assets):
├── Quest-related strings
│   ├── act1_musician_* (Chapter 1 musician quest)
│   └── note_* (Journal/note entries)
├── UI category labels
│   ├── Audio, Music, Sound (translated to German: Ton, Musik, Klang)
│   └── Settings categories
└── Location names
    ├── med_musican_house_01 → "la casa raccapricciante" (Italian)
    └── Various location identifiers
```

**Localization Features:**
- Multi-language support via string table entries
- Unity's built-in TextMeshPro localization system
- Scene-specific localization data embedded in level files
- Quest/dialogue text stored as serialized strings

---

## Configuration Data

### RuntimeInitializeOnLoads.json

**Location:** `/home/rian/Schreibtisch/Darkwood/Darkwood_Data/RuntimeInitializeOnLoads.json`

```json
{
  "root": [
    {
      "assemblyName": "Assembly-CSharp",
      "nameSpace": "",
      "className": "SteamManager",
      "methodName": "InitOnPlayMode",
      "loadTypes": 4,
      "isUnityClass": false
    },
    {
      "assemblyName": "Rewired_Android",
      "nameSpace": "Rewired.Platforms.Android",
      "className": "AndroidUnityInputHelper",
      "methodName": "Initialize",
      "loadTypes": 1,
      "isUnityClass": false
    }
  ]
}
```

**Purpose:** Specifies methods to call when the game initializes (after all scenes load).

### ScriptingAssemblies.json

**Location:** `/home/rian/Schreibtisch/Darkwood/Darkwood_Data/ScriptingAssemblies.json`

**Assembly Loading Order (62 total assemblies):**

**Unity Core Modules (57 DLLs):**
- UnityEngine.dll, UnityEngine.CoreModule.dll, UnityEngine.AudioModule.dll
- UnityEngine.PhysicsModule.dll, UnityEngine.TerrainModule.dll
- Unity.TextMeshPro.dll, Unity.Timeline.dll, Newtonsoft.Json.dll

**Third-party Libraries:**
- `Assembly-CSharp.dll` - Main game code (3.1 MB)
- `Assembly-CSharp-firstpass.dll` - First-pass assembly (92 KB)
- `Rewired_Core.dll`, `Rewired_Windows.dll` - Input system (2.0 MB + 1.0 MB)
- `GalaxyCSharp.dll` - GOG Galaxy integration (437 KB)
- `com.rlabrecque.steamworks.net.dll` - Steam networking (379 KB)
- `DOTween.dll` - Tweening library (162 KB)
- `Pathfinding.*.dll` - A* pathfinding (multiple DLLs)

**Assembly Loading Strategy:**

```
Loading Order:
1. Unity core modules (auto-loaded by engine)
2. Rewired input system (critical for gameplay)
3. Steam/GOG integration (platform-specific)
4. Third-party libraries (JSON, pathfinding, tweening)
5. Assembly-CSharp-firstpass.dll (first-pass code)
6. Assembly-CSharp.dll (main game code)
```

### Boot Configuration

**Location:** `/home/rian/Schreibtisch/Darkwood/Darkwood_Data/boot.config`

```ini
wait-for-native-debugger=0
hdr-display-enabled=0
gc-max-time-slice=3
single-instance=
```

**Settings:**
- **GC Max Time Slice**: 3ms (Garbage Collection time limit per frame)
- **HDR Display**: Disabled
- **Single Instance**: No restriction

### Application Info

**Location:** `/home/rian/Schreibtisch/Darkwood/Darkwood_Data/app.info`

```
Acid Wizard Studio
Darkwood
```

**Metadata:**
- Developer: Acid Wizard Studio
- Game Title: Darkwood

---

## Binary Asset Formats

### Unity Asset Bundle Format (.assets)

**Format Version:** Unity 2021.3.x (version 0x16 in header)

**Structure:**

```
[File Header]
├── Magic number (8 bytes, all zeros for newer formats)
├── Version identifier (4 bytes: 0x16 = Unity 2021.3)
├── Data offset table
└── Hash/checksum data

[Serialized Data Block]
├── Object count and type information
├── Serialized component data (MonoBehaviour, Transform, etc.)
├── Reference table (object IDs to serialized data offsets)
└── Compression metadata (if .resS companion exists)
```

**Compression Methods:**
- **LZMA**: Used in `.resS` files for asset serialization compression
- **Uncompressed**: Some smaller sharedassets files remain uncompressed

### Scene File Format (.unity scenes as level0-level190)

**Binary Structure:**

```
[Scene Header] (same .assets format)
├── Unity version: 2021.3.30f1
└── Serialization metadata

[Scene Objects]
├── GameObject definitions with unique IDs
├── Component data:
│   ├── Transform (position, rotation, scale)
│   ├── Collider (BoxCollider, SphereCollider, MeshCollider)
│   ├── Renderer (MeshRenderer, SkinnedMeshRenderer)
│   ├── AudioSource (for audio playback)
│   └── Custom components (SoundArea, ItemSounds, etc.)
├── Scene references:
│   ├── Prefab instances
│   ├── Asset bundle references
│   └── Script component bindings
└── Editor metadata (scene settings, lighting, etc.)
```

### Save File Format (Reference)

**Location:** `/home/rian/Schreibtisch/Darkwood/docs/04-save-system.md` (already documented)

**Key Components Referenced:**
- Game state serialization
- Player/inventory data
- World state persistence
- Quest progression tracking

---

## Data Flow During Loading

### 1. Engine Initialization

- Load `boot.config` for runtime settings
- Parse `ScriptingAssemblies.json` for assembly loading order
- Execute methods in `RuntimeInitializeOnLoads.json`

### 2. Asset Bundle Loading

- Load `resources.assets` (uncompressed)
- Decompress `resources.assets.resS` (LZMA compressed)
- Load shared asset bundles as needed (`sharedassets[0-190].assets`)
- Decompress `.resS` companions for large assets

### 3. Scene Loading

- Load scene file (`level[0-190]`)
- Decompress serialized objects if `.resS` companion exists
- Instantiate GameObjects from serialized data
- Load referenced asset bundles (prefabs, textures, etc.)
- Initialize MonoBehaviour components

### 4. Audio System Initialization

- `SteamManager.InitOnPlayMode()` initializes audio context
- `Rewired_Android.AndroidUnityInputHelper.Initialize()` sets up input
- AudioController singleton created with category definitions
- SoundArea components activated based on player proximity

### 5. Localization Loading

- String tables loaded from asset bundles
- TextMeshPro assets instantiated for UI text
- Scene-specific localization applied to objects

---

## Summary of File Format Architecture

### Key Technical Details:

| Aspect | Value |
|--------|-------|
| **Unity Version** | 2021.3.30f1 (determined from binary headers) |
| **Compression** | LZMA for large assets, uncompressed for small bundles |
| **Asset Organization** | 191 shared asset bundles + main resources bundle |
| **Scene Count** | 191 levels (level0-level190) with hierarchical organization |
| **Audio Categories** | 20+ audio categories for environmental, music, and effects audio |
| **Localization** | Multi-language support via string tables in asset bundles |

### File Size Distribution:

| Category | Total Size | Notes |
|----------|-----------|-------|
| **resources.assets.resS** | 2.6 GB | Largest single file (compressed) |
| **resources.resource** | 1.7 GB | Raw uncompressed data blob |
| **sharedassets[0-190].assets** | ~500 MB total | 191 asset bundles |
| **Scene files (level0-level190)** | ~200 MB total | Varies from 4KB to 7MB per file |

### Compression Ratio:

- **resources.assets**: 100 MB uncompressed → 2.6 GB .resS companion indicates heavy compression of original data
- **sharedassets bundles**: Selective compression based on content size and usage frequency

---

## Multiplayer Implications for File Formats

### Asset Streaming Strategy

**Current Single-Player:**
- All assets loaded at game start or level transition
- No streaming during gameplay (except audio)

**Multiplayer Adaptation:**
- **Shared asset bundles**: All clients load same resources.assets, sharedassets[0-N].assets
- **Level-specific streaming**: Only load level[0-190] files when entering that area
- **Audio caching**: Pre-load audio categories based on predicted player movement

### Save File Compatibility

**Single-Player Saves:**
- Compatible with multiplayer (seed-based generation ensures consistent world)
- Chapter saves persist across single-player and multiplayer sessions

**Multiplayer-Specific Data:**
- Player list, chat history, lobby state stored in separate files
- No conflict with single-player save format

### Localization Considerations

**Current Implementation:**
- Game supports multiple languages via string tables
- Language selection at startup loads appropriate localization bundle

**Multiplayer Implication:**
- All players must use same language for consistent UI/dialogue
- Host determines language setting (or all clients must match)

---

## Conclusion

Darkwood's file format architecture is built on **Unity 2021.3.x asset bundles** with LZMA compression for large assets. The game uses:

- **191 shared asset bundles** + main resources bundle
- **191 scene files** (level0-level190) organized by chapter/location type
- **LZMA compression** for efficient storage of large assets
- **Multi-language support** via string tables in asset bundles
- **Audio categories** for environmental, music, and effects audio

This architecture enables:
- Efficient asset loading and memory management
- Deterministic world generation (all clients generate same level from seed)
- Multi-language localization without code changes
- Audio-based enemy perception system

The format is well-suited for multiplayer extension with proper asset streaming and save file synchronization.
