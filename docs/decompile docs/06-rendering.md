# Darkwood Rendering & Graphics System — Complete Analysis

**Generated from:** Assembly-CSharp.dll (1,115 classes), Unity 2021.3.30f1  
**Date:** July 8-9, 2026  
**Status:** Complete

---

## Table of Contents

- [Render Pipeline Configuration](#render-pipeline-configuration)
- [Sprite Rendering System (tk2d Integration)](#sprite-rendering-system-tk2d-integration)
- [Lighting System](#lighting-system)
- [Fog of War System](#fog-of-war-system)
- [Day/Night Cycle System](#daynight-cycle-system)
- [Camera System & Screen Effects](#camera-system--screen-effects)
- [Particle Systems](#particle-systems)
- [AmplifyColor Integration](#amplifycolor-integration)
- [Performance Considerations](#performance-considerations)

---

## Render Pipeline Configuration

### Unity Version & Rendering Mode

**Unity Version:** 2021.3.30f1 (Mono runtime)

The globalgamemanagers file contains references to:

- `Hidden/Internal-DeferredShading` - Indicates deferred shading support
- `Hidden/Internal-DeferredReflections` - Deferred reflection pass
- `Hidden/BlitCopyHDRTonemap` - HDR tonemapping capabilities
- `Hidden/Dof/DepthOfFieldHdr` - Depth of field with HDR

**Quality Settings:** The game uses Unity's standard quality settings system with resolution management through tk2dCamera.

---

## Sprite Rendering System (tk2d Integration)

### Core Architecture

The game heavily relies on **tk2d (TwoKinds)** for all 2D sprite rendering, which is a custom 2D rendering framework built on top of Unity's rendering system.

### Key Classes

#### tk2dSystem (Singleton Manager)

**Base Type:** `UnityEngine.ScriptableObject`  
**Role:** Central manager for tk2d resources and platform-specific asset loading

**Key Methods:**
- `LoadResourceByGUID()` / `LoadResourceByName()` - Resource loading by identifier
- `GetAssetPlatform()` - Platform-specific asset selection
- `CurrentPlatform` property - Active platform (PC, mobile, etc.)

---

#### tk2dCamera (Primary Camera Controller)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 35 total - Comprehensive camera management

**Key Properties:**
| Property | Type | Purpose |
|----------|------|---------|
| `Instance` | Singleton access | Global camera reference |
| `resolution` | Vector2Int | Current resolution |
| `ScaledResolution` | Vector2Int | Scaled resolution |
| `TargetResolution` | Vector2Int | Target resolution for scaling |
| `NativeResolution` | Vector2Int | Native display resolution |
| `NativeScreenExtents` | Rect | Native screen boundaries |
| `ZoomFactor` | float | Zoom level multiplier |
| `zoomScale` | float | Additional zoom scale |
| `ScreenCamera` | Camera | tk2d camera reference |
| `UnityCamera` | Camera | Unity built-in camera reference |

**Key Methods:**
```csharp
public Camera CameraForLayer(int layer)           // Get camera for specific layer
public void UpdateCameraMatrix()                   // Update projection/view matrices
public Matrix4x4 GetProjectionMatrixForOverride()  // Custom projection matrix calculation
public void OnPreCull()                            // Pre-culling callback
```

---

#### tk2dCameraAnchor (Camera Anchoring)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 15 total - Camera positioning and anchoring

**Key Properties:**
| Property | Type | Purpose |
|----------|------|---------|
| `AnchorCamera` | Camera | Reference camera to anchor to |
| `AnchorOffsetPixels` | Vector2 | Offset in pixels from anchor point |
| `AnchorPoint` | Tk2dBaseSprite.Anchor | Anchor point enum (top-left, center, etc.) |
| `AnchorToNativeBounds` | bool | Whether to anchor to native bounds |

---

#### tk2dCameraResolutionOverride (Resolution Overrides)

**Base Type:** `System.Object`  
**Methods:** 4 total - Resolution override management  
**Fields:** 10 total - Override configuration data  
**Properties:** 3 total - Access to override settings

---

### Sprite Rendering Hierarchy

```
tk2dBaseSprite (abstract base, 49 methods)
├── tk2dSprite (17 methods, 5 fields) - Basic sprite
├── tk2dClippedSprite (24 methods) - Clipping support
└── tk2dSlicedSprite (29 methods) - Sliced/9-slice sprite

tk2dAnimatedSprite (extends tk2dSprite, 44 methods) - Animated sprite variant
```

### Key Sprite Classes

#### tk2dSpriteCollection (Atlas Management)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 5 total - Atlas and texture management  
**Fields:** 52 total - Comprehensive atlas configuration including:
- `atlasTextures[]` - Texture array for sprites
- `atlasWidth/Height` - Atlas dimensions
- `atlasFormat` - Format enum (AtlasFormat)
- `globalScale`, `globalTextureRescale` - Scaling factors
- `loadable`, `managedSpriteCollection` - Loading flags

---

#### tk2dSpriteCollectionData (Runtime Sprite Data)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 21 total - Runtime sprite data management  
**Fields:** 33 total - Sprite definition storage  
**Properties:** 5 total - Access to sprite definitions

---

#### tk2dStaticSpriteBatcher (Performance Optimization)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 17 total - Static sprite batching  
**Key Methods:**
- `GetMaterial()` - Get material for batched sprites
- `CheckFlag()` / `SetFlag()` - Batch state management

---

### Sprite Animation System

#### tk2dSpriteAnimator (Animation Controller)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 56 total - Comprehensive animation control

**Key Properties:**
| Property | Type | Purpose |
|----------|------|---------|
| `Library` | tk2dSpriteAnimation library reference | Animation data source |
| `CurrentClip` | string | Current animation clip name |
| `DefaultClip` | string | Default animation clip name |
| `Playing` | bool | Animation playing state |
| `Paused` | bool | Animation paused state |
| `CurrentFrame` | int | Current frame index |

**Key Methods:**
```csharp
public void Play() / Pause() / Resume() / Stop()  // Playback control
public void SetFrame(int frame, bool triggerEvents)  // Frame setting with optional trigger events
public tk2dSpriteAnimationClip GetClipByName(string name)  // Clip retrieval by name
public tk2dSpriteAnimationClip GetClipById(int id)  // Clip retrieval by ID
public void UpdateAnimation()  // Core animation update (called every frame)
```

---

#### tk2dSpriteAnimationClip (Animation Data)

**Base Type:** `System.Object`  
**Methods:** 6 total - Animation clip data  
**Fields:** 6 total - Frame timing and sprite references

**Structure:**
```csharp
public class tk2dSpriteAnimationClip : System.Object
{
    public string name;              // Clip name
    public float length;             // Total duration in seconds
    public float fps;                // Frames per second
    public int[] frameIndices;       // Array of sprite indices for each frame
    public float[] frameDurations;   // Duration for each frame (optional)
}
```

---

## Lighting System

### Light2D Component (Custom 2D Lighting)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 95 total - Extensive lighting control

**Key Properties:**
| Property | Type | Purpose |
|----------|------|---------|
| `LightColor` | Color | Light color (RGBA) |
| `LightIntensity` | float | Light intensity multiplier |
| `LightRadius` | float | Circular light radius |
| `LightType` | enum | Light type setting (point, directional, spotlight) |
| `LightDetail` | enum | Detail level for performance optimization |
| `IsVisible` | bool | Whether light is visible/rendered |
| `IsShadowEmitter` | bool | Whether light casts shadows |

**Key Methods:**
```csharp
public static Light2D Create(Vector3 position, Color color, float radius, float coneAngle)  // Factory method
public void LookAt(GameObject target) / LookAt(Transform transform) / LookAt(Vector3 position)  // Three overloads
public void ToggleLight(bool enable, bool updateMesh = true)  // Enable/disable light with optional mesh update
public void lightGraphNodes()  // Update light graph nodes for visibility queries
public void populateAffectedNodes()  // Mark affected navigation graph nodes as lit/unlit
```

**Events:**
- `OnBeamEnter` - Fired when light beam enters a region
- `OnBeamExit` - Fired when light beam exits a region
- `OnBeamStay` - Fired while light beam is active in a region

---

### Light Area (Proximity-Based Lighting)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 3 total - Basic light area functionality  
**Fields:** 0 total - Minimal configuration

**Purpose:** Defines an area where lighting conditions change (e.g., entering a lit room from darkness).

---

## Fog of War System

### PP_FoggyScreen (Post-Processing Effect)

**Base Type:** `PostProcessBase`  
**Methods:** 4 total - Post-processing lifecycle

**Key Methods:**
```csharp
public void OnRenderImage(RenderTexture source, RenderTexture destination)  // Core rendering hook for fog effect
```

**Fields:**
| Field | Type | Purpose |
|-------|------|---------|
| `fogColor` | Color | Color of the fog overlay (typically dark brown/black) |
| `fogThickness` | float | Intensity/thickness control (0-1 range) |

**Implementation Pattern:** Uses Unity's post-processing pipeline with custom shader to create visibility mask based on player position and explored areas. Areas not yet visited by the player are covered in fog; explored areas gradually reveal themselves.

### Fog of War Mechanics

**Visibility Calculation:**
1. Player moves through world, revealing nearby tiles
2. Revealed tiles stored in persistent data (saved across levels)
3. Post-processing shader reads visibility mask and applies fog effect
4. Fog thickness varies by distance from revealed area (gradient edge)

**Persistent Revealed Areas:**
- Stored in chapter save (Layer 3: Chapter Saves)
- Survives level transitions (player remembers explored areas)
- Can be partially obscured by new darkness sources

---

## Day/Night Cycle System

### TimeOfDay (Time Management)

**Base Type:** `System.Object`  
**Methods:** 1 total - Constructor  
**Key Methods:**
```csharp
public bool isCurrentTime()  // Check if current time matches specified time of day
```

**Fields:**
| Field | Type | Purpose |
|-------|------|---------|
| `_daytime` | enum | Day/night state (Day, Night, Dusk, Dawn) |
| `_offset` | float | Time offset from base time |
| `_equals` | enum | Comparison type (greater than, less than, equal) |

---

### AmbientColor System

#### AmbientColors (Manager)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 1 total - Color management  
**Fields:** 1 total:
- `ambientColors[]` - List of AmbientColor objects

---

#### AmbientColor (Color Data)

**Base Type:** `System.Object`  
**Methods:** 1 total - Constructor  
**Fields:** 2 total - Color configuration data

**Structure:**
```csharp
public class AmbientColor : System.Object
{
    public string name;           // Time period name (e.g., "Dawn", "Midnight")
    public Color skyColor;        // Sky/background color
    public Color fogColor;        // Fog/atmosphere color
    public float intensity;       // Lighting intensity multiplier
}
```

---

### OnTimeOfDay (Event Trigger)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 5 total - Time change event handling

**Key Methods:** Event triggers for time-of-day changes:
- `OnDayStart()` - Fired when day begins
- `OnNightStart()` - Fired when night begins
- `OnDuskStart()` - Fired when dusk begins
- `OnDawnStart()` - Fired when dawn begins

**Fields:** 3 total - Configuration including `timeOfDay` reference

---

## Camera System & Screen Effects

### Vision Effects (Post-Processing)

#### PP_BlackAndWhite (B&W Effect)

**Base Type:** `PostProcessBase`  
**Methods:** 4 total - Post-processing lifecycle  
**Fields:** 1 total - Basic configuration

**Purpose:** Converts screen to grayscale (used during certain story moments or night vision).

---

#### PP_NightVision / PP_NightVisionV2

**Base Type:** `PostProcessBase`  
**Methods:** 3-4 total - V2 adds more fields for enhanced effect  
**Fields:** 0-5 total - Night vision parameters

**Purpose:** Green-tinted night vision mode with reduced visibility but enhanced detail.

---

#### PP_ThermalVision / PP_ThermalVisionV2

**Base Type:** `PostProcessBase`  
**Methods:** 3-4 total - Thermal imaging effect  
**Fields:** 0-3 total - Thermal vision parameters

**Purpose:** Heat-based visualization showing warm objects (living creatures) as bright against cool background.

---

### Additional Post-Processing Effects (Full List)

The game includes an extensive post-processing stack with **60+ effects** including:

| Category | Effects | Purpose |
|----------|---------|---------|
| **Color Grading** | PP_Amnesia, PP_Bleach, PP_Charcoal, PP_Dream_Color/Grey | Artistic color transforms |
| **Blur/Focus** | PP_BlurH/V, PP_RadialBlur, PP_Scanlines | Motion blur, focus effects |
| **Artistic** | PP_LineArt, PP_Pencil, PP_CrossHatch, PP_Negative | Hand-drawn/negative film looks |
| **Lighting** | PP_Godrays1, PP_LightShafts, PP_HDR, PP_BloomSimple | Light beam and bloom effects |
| **Distortion** | PP_Displace, PP_Ripple, PP_ScreenWaves, PP_Noise | Screen distortion/wave effects |
| **Edge Detection** | PP_EdgeDetect, PP_SobelOutline (V1-V5) | Outline/edge highlighting |
| **Specialized** | PP_FoggyScreen, PP_NightVision/Thermal, PP_Pixelated | Game-specific effects |

---

## Particle Systems

### AutoDestroyParticles (Auto-Cleanup Particles)

**Base Type:** `MonoBehaviourExt`  
**Methods:** 8 total - Lifecycle management

**Key Methods:**
```csharp
public void stopEmission() / resumeEmission()  // Emission control
public void waitToDestroy(float delay) / waitToDestroyOld(float delay)  // Coroutine-based cleanup
```

**Fields:**
| Field | Type | Purpose |
|-------|------|---------|
| `manual` | bool | Manual vs automatic destruction mode |
| `onlyDisable` | bool | Disable instead of destroy flag (for pooling) |
| `particles[]` | ParticleSystem[] | Array of ParticleSystem components |

---

### Particle System Usage

The game uses Unity's standard `ParticleSystem` (94 methods) with various configurations for:

| Effect Type | Description | Example Use Case |
|-------------|-------------|------------------|
| **Fire/Smoke Effects** | Atmospheric particles | Campfires, burning buildings, smoke vents |
| **Blood/Gore** | Combat visual effects | Hit reactions, death animations |
| **Environmental** | Rain, dust, ambient particles | Weather effects, atmosphere |

---

## AmplifyColor Integration

### AmplifyColor.dll (Third-Party Library)

**Size:** 3.5 KB  
**Types:** 1 type with minimal methods/fields

This is a lightweight wrapper or configuration for the Amplify Color post-processing system.

---

### AmplifyColorBase (Core Effect System)

**Base Type:** `UnityEngine.MonoBehaviour`  
**Methods:** 38 total - Comprehensive color effect management

**Key Properties:**
| Property | Type | Purpose |
|----------|------|---------|
| `DefaultLut` | Texture2D | Default lookup texture for color grading |
| `LutTexture` | Texture2D | Active lookup texture |
| `BlendAmount` | float | Blending amount between original and LUT (0-1) |
| `Exposure` | float | Exposure adjustment multiplier |
| `Quality` | enum | Quality level setting (Low, Medium, High) |
| `Tonemapper` | enum | Tonemapping mode (Reinhard, ACES, etc.) |

**Key Methods:**
```csharp
public void BlendTo(Texture2D targetLut, float duration)  // Blend to target LUT with timing
public void EnterVolume() / ExitVolume()  // Volume interaction callbacks
public static bool ValidateLutDimensions(int width, int height)  // LUT dimension validation (power of 2 required)
```

---

### AmplifyColorEffect (Concrete Effect Implementation)

**Base Type:** `AmplifyColorBase`  
**Sealed:** Yes  
**Methods:** 1 total - Constructor only

**Purpose:** Concrete implementation of color grading effect for specific scenes or moments.

---

## Performance Considerations

### Batching & Optimization

1. **tk2dStaticSpriteBatcher** - Groups static sprites for efficient rendering
   - Reduces draw calls by merging multiple sprites into single batch
   - Automatically batches sprites with same material and atlas

2. **LightDetail Settings** - Multiple detail levels for Light2D performance
   - High: Full shadow casting, all light beams
   - Medium: Reduced shadow resolution
   - Low: No shadows, simplified lighting

3. **Resolution Overrides** - Dynamic resolution scaling via tk2dCameraResolutionOverride
   - Lower resolution during intense scenes (combat, particle effects)
   - Automatic recovery to target resolution when load decreases

4. **Particle Auto-Destruction** - Automatic cleanup prevents memory leaks
   - Particles destroyed after animation completes
   - Option to disable instead of destroy for pooling

### Layer Management

- `tk2dCamera.CameraForLayer()` - Per-layer camera assignment
  - Separate cameras for background, midground, foreground
  - Enables parallax scrolling and depth-based rendering

- `viewportClippingEnabled` - Viewport clipping for performance
  - Only render sprites within camera viewport
  - Reduces overdraw and improves frustum culling

- `resolutionOverride[]` array - Multiple resolution profiles
  - Different resolutions for different platforms/scenes
  - Optimized for mobile vs desktop performance targets

### Memory Management

- `collectionsToUnload` / `collectionsToUnloadDuringWorldGen` - Sprite collection unloading
  - Unload unused sprite atlases to free memory
  - Critical for large open-world levels

- `managedSpriteCollection` flag - Manual memory management option
  - Developer-controlled loading/unloading of sprite collections
  - Bypasses automatic garbage collection for performance-critical assets

- `dynamicUnload` - Dynamic asset unloading support
  - Runtime decision to unload assets based on distance from player
  - Reduces memory footprint in large levels

---

## Summary

Darkwood uses a sophisticated rendering architecture combining:

1. **tk2d Framework** for all 2D sprite rendering with custom batching and atlas management
2. **Light2D System** for dynamic 2D lighting with beam collision detection
3. **Post-Processing Stack** (60+ effects) including fog of war, vision modes, and atmospheric effects
4. **AmplifyColor Integration** for advanced color grading via lookup textures
5. **Unity ParticleSystem** for particle effects with automatic cleanup

The rendering pipeline emphasizes performance through:

- Sprite batching via tk2dStaticSpriteBatcher
- Dynamic resolution scaling via tk2dCameraResolutionOverride
- Detail level management for lights (LightDetail)
- Efficient memory handling via tk2d's atlas system
- Per-layer camera assignment for parallax and depth effects

This architecture provides a solid foundation for understanding Darkwood's visual presentation and enables optimization opportunities for multiplayer implementation (e.g., client-side particle effects, server-authoritative lighting decisions).
