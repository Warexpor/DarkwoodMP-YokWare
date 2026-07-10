# Assembly-CSharp-firstpass.dll — Complete Type Reference

**Assembly:** `Assembly-CSharp-firstpass` | **Version:** v1.0.0.0

- **File size:** 89,0 KB
- **Types:** 38
- **Methods:** 336
- **Fields:** 126

## Namespace: ``

### Classes

| Class | Base Type | Abstract | Sealed | Methods | Fields | Props | Nested |

|-------|-----------|----------|--------|---------|--------|-------|--------|

| `<Module>` | `<object>` |  |  | 0 | 0 | 0 | 0 |

| `BlurEffect` | `UnityEngine.MonoBehaviour` |  |  | 7 | 4 | 1 | 0 |

| `ColorCorrectionEffect` | `ImageEffectBase` |  |  | 2 | 1 | 0 | 0 |

| `ContrastStretchEffect` | `UnityEngine.MonoBehaviour` |  |  | 10 | 13 | 4 | 0 |

| `EdgeDetectEffect` | `ImageEffectBase` |  |  | 2 | 1 | 0 | 0 |

| `GammaCorrectionEffect` | `ImageEffectBase` |  |  | 2 | 3 | 0 | 0 |

| `GlowEffect` | `UnityEngine.MonoBehaviour` |  |  | 10 | 10 | 3 | 0 |

| `GrayscaleEffect` | `ImageEffectBase` |  |  | 2 | 2 | 0 | 0 |

| `ImageEffectBase` | `UnityEngine.MonoBehaviour` |  |  | 4 | 2 | 1 | 0 |

| `ImageEffects` | `System.Object` |  |  | 4 | 0 | 0 | 0 |

| `MotionBlur` | `ImageEffectBase` |  |  | 4 | 3 | 0 | 0 |

| `NoiseEffect` | `UnityEngine.MonoBehaviour` |  |  | 6 | 18 | 1 | 0 |

| `ReadOnlyDictionary`2` | `System.Object` |  |  | 20 | 1 | 6 | 0 |

| `SepiaToneEffect` | `ImageEffectBase` |  |  | 2 | 0 | 0 | 0 |

| `SLayersInfo` | `UnityEngine.MonoBehaviour` |  |  | 1 | 1 | 0 | 1 |

| `SSAOEffect` | `UnityEngine.MonoBehaviour` |  |  | 8 | 11 | 0 | 1 |

| `TwirlEffect` | `ImageEffectBase` |  |  | 2 | 3 | 0 | 0 |

| `VortexEffect` | `ImageEffectBase` |  |  | 2 | 3 | 0 | 0 |

### `<Module>`

**Base type:** `<object>` | **Abstract:** False | **Sealed:** False | **Public:** False

### `BlurEffect`

**Base type:** `UnityEngine.MonoBehaviour` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void BlurEffect..ctor()` — public

#### Methods

**Public methods:**
- `System.Void FourTapCone(UnityEngine.RenderTexture source, UnityEngine.RenderTexture dest, System.Int32 iteration)` — static: False, virtual: False

**Private methods:**
- `System.Void DownSample4x(UnityEngine.RenderTexture source, UnityEngine.RenderTexture dest)` — static: False, virtual: False
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False

#### Fields

**Public fields:**
- `UnityEngine.Shader blurShader` — static: False

- `System.Single blurSpread` — static: False

- `System.Int32 iterations` — static: False

**Private fields:**
- `UnityEngine.Material m_Material` — static: True

#### Properties

- `UnityEngine.Material material` — getter: True, setter: False


### `ColorCorrectionEffect`

**Base type:** `ImageEffectBase` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void ColorCorrectionEffect..ctor()` — public

#### Methods

**Private methods:**
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False

#### Fields

**Public fields:**
- `UnityEngine.Texture textureRamp` — static: False


### `ContrastStretchEffect`

**Base type:** `UnityEngine.MonoBehaviour` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void ContrastStretchEffect..ctor()` — public

#### Methods

**Private methods:**
- `System.Void CalculateAdaptation(UnityEngine.Texture curTexture)` — static: False, virtual: False
- `System.Void OnDisable()` — static: False, virtual: False
- `System.Void OnEnable()` — static: False, virtual: False
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False
- `System.Void Start()` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Single adaptationSpeed` — static: False

- `System.Single limitMaximum` — static: False

- `System.Single limitMinimum` — static: False

- `UnityEngine.Shader shaderAdapt` — static: False

- `UnityEngine.Shader shaderApply` — static: False

- `UnityEngine.Shader shaderLum` — static: False

- `UnityEngine.Shader shaderReduce` — static: False

**Private fields:**
- `UnityEngine.RenderTexture[] adaptRenderTex` — static: False

- `System.Int32 curAdaptIndex` — static: False

- `UnityEngine.Material m_materialAdapt` — static: False

- `UnityEngine.Material m_materialApply` — static: False

- `UnityEngine.Material m_materialLum` — static: False

- `UnityEngine.Material m_materialReduce` — static: False

#### Properties

- `UnityEngine.Material materialAdapt` — getter: True, setter: False

- `UnityEngine.Material materialApply` — getter: True, setter: False

- `UnityEngine.Material materialLum` — getter: True, setter: False

- `UnityEngine.Material materialReduce` — getter: True, setter: False


### `EdgeDetectEffect`

**Base type:** `ImageEffectBase` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void EdgeDetectEffect..ctor()` — public

#### Methods

**Private methods:**
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Single threshold` — static: False


### `GammaCorrectionEffect`

**Base type:** `ImageEffectBase` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void GammaCorrectionEffect..ctor()` — public

#### Methods

**Private methods:**
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Single gamma` — static: False

- `System.Single inBlack` — static: False

- `System.Single inWhite` — static: False


### `GlowEffect`

**Base type:** `UnityEngine.MonoBehaviour` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void GlowEffect..ctor()` — public

#### Methods

**Public methods:**
- `System.Void BlitGlow(UnityEngine.RenderTexture source, UnityEngine.RenderTexture dest)` — static: False, virtual: False
- `System.Void FourTapCone(UnityEngine.RenderTexture source, UnityEngine.RenderTexture dest, System.Int32 iteration)` — static: False, virtual: False

**Private methods:**
- `System.Void DownSample4x(UnityEngine.RenderTexture source, UnityEngine.RenderTexture dest)` — static: False, virtual: False
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Int32 blurIterations` — static: False

- `UnityEngine.Shader blurShader` — static: False

- `System.Single blurSpread` — static: False

- `UnityEngine.Shader compositeShader` — static: False

- `UnityEngine.Shader downsampleShader` — static: False

- `System.Single glowIntensity` — static: False

- `UnityEngine.Color glowTint` — static: False

**Private fields:**
- `UnityEngine.Material m_BlurMaterial` — static: False

- `UnityEngine.Material m_CompositeMaterial` — static: False

- `UnityEngine.Material m_DownsampleMaterial` — static: False

#### Properties

- `UnityEngine.Material blurMaterial` — getter: True, setter: False

- `UnityEngine.Material compositeMaterial` — getter: True, setter: False

- `UnityEngine.Material downsampleMaterial` — getter: True, setter: False


### `GrayscaleEffect`

**Base type:** `ImageEffectBase` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void GrayscaleEffect..ctor()` — public

#### Methods

**Private methods:**
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Single rampOffset` — static: False

- `UnityEngine.Texture textureRamp` — static: False


### `ImageEffectBase`

**Base type:** `UnityEngine.MonoBehaviour` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void ImageEffectBase..ctor()` — public

#### Methods

#### Fields

**Public fields:**
- `UnityEngine.Shader shader` — static: False

**Private fields:**
- `UnityEngine.Material m_Material` — static: False

#### Properties

- `UnityEngine.Material material` — getter: True, setter: False


### `ImageEffects`

**Base type:** `System.Object` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void ImageEffects..ctor()` — public

#### Methods

**Public methods:**
- `System.Void Blit(UnityEngine.RenderTexture source, UnityEngine.RenderTexture dest)` — static: True, virtual: False
- `System.Void BlitWithMaterial(UnityEngine.Material material, UnityEngine.RenderTexture source, UnityEngine.RenderTexture dest)` — static: True, virtual: False
- `System.Void RenderDistortion(UnityEngine.Material material, UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination, System.Single angle, UnityEngine.Vector2 center, UnityEngine.Vector2 radius)` — static: True, virtual: False


### `MotionBlur`

**Base type:** `ImageEffectBase` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void MotionBlur..ctor()` — public

#### Methods

**Private methods:**
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Single blurAmount` — static: False

- `System.Boolean extraBlur` — static: False

**Private fields:**
- `UnityEngine.RenderTexture accumTexture` — static: False


### `NoiseEffect`

**Base type:** `UnityEngine.MonoBehaviour` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void NoiseEffect..ctor()` — public

#### Methods

**Private methods:**
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False
- `System.Void SanitizeParameters()` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Single grainIntensityMax` — static: False

- `System.Single grainIntensityMin` — static: False

- `System.Single grainSize` — static: False

- `UnityEngine.Texture grainTexture` — static: False

- `System.Boolean monochrome` — static: False

- `System.Single scratchFPS` — static: False

- `System.Single scratchIntensityMax` — static: False

- `System.Single scratchIntensityMin` — static: False

- `System.Single scratchJitter` — static: False

- `UnityEngine.Texture scratchTexture` — static: False

- `UnityEngine.Shader shaderRGB` — static: False

- `UnityEngine.Shader shaderYUV` — static: False

**Private fields:**
- `UnityEngine.Material m_MaterialRGB` — static: False

- `UnityEngine.Material m_MaterialYUV` — static: False

- `System.Boolean rgbFallback` — static: False

- `System.Single scratchTimeLeft` — static: False

- `System.Single scratchX` — static: False

- `System.Single scratchY` — static: False

#### Properties

- `UnityEngine.Material material` — getter: True, setter: False


### `ReadOnlyDictionary`2`

**Base type:** `System.Object` | **Abstract:** False | **Sealed:** False | **Public:** True
**Implements:** `System.Collections.Generic.IDictionary`2<TKey,TValue>`, `System.Collections.Generic.ICollection`1<System.Collections.Generic.KeyValuePair`2<TKey,TValue>>`, `System.Collections.Generic.IEnumerable`1<System.Collections.Generic.KeyValuePair`2<TKey,TValue>>`, `System.Collections.IEnumerable`

#### Constructors

- `System.Void ReadOnlyDictionary`2..ctor(System.Collections.Generic.IDictionary`2<TKey,TValue> dictionary)` — public

#### Methods

**Public methods:**
- `System.Boolean Contains(System.Collections.Generic.KeyValuePair`2<TKey,TValue> item)` — static: False, virtual: True
- `System.Boolean ContainsKey(TKey key)` — static: False, virtual: True
- `System.Void CopyTo(System.Collections.Generic.KeyValuePair`2<TKey,TValue>[] array, System.Int32 arrayIndex)` — static: False, virtual: True
- `System.Int32 get_Count()` — static: False, virtual: True
- `System.Boolean get_IsReadOnly()` — static: False, virtual: True
- `TValue get_Item(TKey key)` — static: False, virtual: False
- `System.Collections.Generic.ICollection`1<TKey> get_Keys()` — static: False, virtual: True
- `System.Collections.Generic.ICollection`1<TValue> get_Values()` — static: False, virtual: True
- `System.Collections.Generic.IEnumerator`1<System.Collections.Generic.KeyValuePair`2<TKey,TValue>> GetEnumerator()` — static: False, virtual: True
- `System.Boolean TryGetValue(TKey key, TValue& value)` — static: False, virtual: True

**Private methods:**
- `System.Exception ReadOnlyException()` — static: True, virtual: False
- `System.Void System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey,TValue>>.Add(System.Collections.Generic.KeyValuePair`2<TKey,TValue> item)` — static: False, virtual: True
- `System.Void System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey,TValue>>.Clear()` — static: False, virtual: True
- `System.Boolean System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey,TValue>>.Remove(System.Collections.Generic.KeyValuePair`2<TKey,TValue> item)` — static: False, virtual: True
- `System.Void System.Collections.Generic.IDictionary<TKey,TValue>.Add(TKey key, TValue value)` — static: False, virtual: True
- `TValue System.Collections.Generic.IDictionary<TKey,TValue>.get_Item(TKey key)` — static: False, virtual: True
- `System.Boolean System.Collections.Generic.IDictionary<TKey,TValue>.Remove(TKey key)` — static: False, virtual: True
- `System.Void System.Collections.Generic.IDictionary<TKey,TValue>.set_Item(TKey key, TValue value)` — static: False, virtual: True
- `System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()` — static: False, virtual: True

#### Fields

**Private fields:**
- `System.Collections.Generic.IDictionary`2<TKey,TValue> _dictionary` — static: False

#### Properties

- `System.Int32 Count` — getter: True, setter: False

- `System.Boolean IsReadOnly` — getter: True, setter: False

- `TValue Item` — getter: True, setter: False

- `System.Collections.Generic.ICollection`1<TKey> Keys` — getter: True, setter: False

- `TValue System.Collections.Generic.IDictionary<TKey,TValue>.Item` — getter: True, setter: True

- `System.Collections.Generic.ICollection`1<TValue> Values` — getter: True, setter: False


### `SepiaToneEffect`

**Base type:** `ImageEffectBase` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void SepiaToneEffect..ctor()` — public

#### Methods

**Private methods:**
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False


### `SLayersInfo`

**Base type:** `UnityEngine.MonoBehaviour` | **Abstract:** False | **Sealed:** False | **Public:** True
**Nested types:** 1

#### Constructors

- `System.Void SLayersInfo..ctor()` — public

#### Fields

**Public fields:**
- `System.Collections.Generic.List`1<SLayersInfo/Layer> layers` — static: False


### `SSAOEffect`

**Base type:** `UnityEngine.MonoBehaviour` | **Abstract:** False | **Sealed:** False | **Public:** True
**Nested types:** 1

#### Constructors

- `System.Void SSAOEffect..ctor()` — public

#### Methods

**Private methods:**
- `UnityEngine.Material CreateMaterial(UnityEngine.Shader shader)` — static: True, virtual: False
- `System.Void CreateMaterials()` — static: False, virtual: False
- `System.Void DestroyMaterial(UnityEngine.Material mat)` — static: True, virtual: False
- `System.Void OnDisable()` — static: False, virtual: False
- `System.Void OnEnable()` — static: False, virtual: False
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False
- `System.Void Start()` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Int32 m_Blur` — static: False

- `System.Int32 m_Downsampling` — static: False

- `System.Single m_MinZ` — static: False

- `System.Single m_OcclusionAttenuation` — static: False

- `System.Single m_OcclusionIntensity` — static: False

- `System.Single m_Radius` — static: False

- `UnityEngine.Texture2D m_RandomTexture` — static: False

- `SSAOEffect/SSAOSamples m_SampleCount` — static: False

- `UnityEngine.Shader m_SSAOShader` — static: False

**Private fields:**
- `UnityEngine.Material m_SSAOMaterial` — static: False

- `System.Boolean m_Supported` — static: False


### `TwirlEffect`

**Base type:** `ImageEffectBase` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void TwirlEffect..ctor()` — public

#### Methods

**Private methods:**
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Single angle` — static: False

- `UnityEngine.Vector2 center` — static: False

- `UnityEngine.Vector2 radius` — static: False


### `VortexEffect`

**Base type:** `ImageEffectBase` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void VortexEffect..ctor()` — public

#### Methods

**Private methods:**
- `System.Void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Single angle` — static: False

- `UnityEngine.Vector2 center` — static: False

- `UnityEngine.Vector2 radius` — static: False


## Namespace: `DG.Tweening`

### Classes

| Class | Base Type | Abstract | Sealed | Methods | Fields | Props | Nested |

|-------|-----------|----------|--------|---------|--------|-------|--------|

| `DG.Tweening.DOTweenCYInstruction` | `System.Object` | **Yes** | **Yes** | 0 | 0 | 0 | 6 |

| `DG.Tweening.DOTweenModuleAudio` | `System.Object` | **Yes** | **Yes** | 15 | 0 | 0 | 3 |

| `DG.Tweening.DOTweenModulePhysics` | `System.Object` | **Yes** | **Yes** | 11 | 0 | 0 | 11 |

| `DG.Tweening.DOTweenModuleSprite` | `System.Object` | **Yes** | **Yes** | 4 | 0 | 0 | 3 |

| `DG.Tweening.DOTweenModuleUI` | `System.Object` | **Yes** | **Yes** | 41 | 0 | 0 | 41 |

| `DG.Tweening.DOTweenModuleUnityVersion` | `System.Object` | **Yes** | **Yes** | 16 | 0 | 0 | 8 |

| `DG.Tweening.DOTweenModuleUtils` | `System.Object` | **Yes** | **Yes** | 2 | 1 | 0 | 1 |

### `DG.Tweening.DOTweenCYInstruction`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** True | **Public:** True
**Nested types:** 6


### `DG.Tweening.DOTweenModuleAudio`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** True | **Public:** True
**Nested types:** 3

#### Methods

**Public methods:**
- `System.Int32 DOComplete(UnityEngine.Audio.AudioMixer target, System.Boolean withCallbacks)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<System.Single,System.Single,DG.Tweening.Plugins.Options.FloatOptions> DOFade(UnityEngine.AudioSource target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `System.Int32 DOFlip(UnityEngine.Audio.AudioMixer target)` — static: True, virtual: False
- `System.Int32 DOGoto(UnityEngine.Audio.AudioMixer target, System.Single to, System.Boolean andPlay)` — static: True, virtual: False
- `System.Int32 DOKill(UnityEngine.Audio.AudioMixer target, System.Boolean complete)` — static: True, virtual: False
- `System.Int32 DOPause(UnityEngine.Audio.AudioMixer target)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<System.Single,System.Single,DG.Tweening.Plugins.Options.FloatOptions> DOPitch(UnityEngine.AudioSource target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `System.Int32 DOPlay(UnityEngine.Audio.AudioMixer target)` — static: True, virtual: False
- `System.Int32 DOPlayBackwards(UnityEngine.Audio.AudioMixer target)` — static: True, virtual: False
- `System.Int32 DOPlayForward(UnityEngine.Audio.AudioMixer target)` — static: True, virtual: False
- `System.Int32 DORestart(UnityEngine.Audio.AudioMixer target)` — static: True, virtual: False
- `System.Int32 DORewind(UnityEngine.Audio.AudioMixer target)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<System.Single,System.Single,DG.Tweening.Plugins.Options.FloatOptions> DOSetFloat(UnityEngine.Audio.AudioMixer target, System.String floatName, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `System.Int32 DOSmoothRewind(UnityEngine.Audio.AudioMixer target)` — static: True, virtual: False
- `System.Int32 DOTogglePause(UnityEngine.Audio.AudioMixer target)` — static: True, virtual: False


### `DG.Tweening.DOTweenModulePhysics`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** True | **Public:** True
**Nested types:** 11

#### Methods

**Public methods:**
- `DG.Tweening.Sequence DOJump(UnityEngine.Rigidbody target, UnityEngine.Vector3 endValue, System.Single jumpPower, System.Int32 numJumps, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector3,DG.Tweening.Plugins.Core.PathCore.Path,DG.Tweening.Plugins.Options.PathOptions> DOLocalPath(UnityEngine.Rigidbody target, UnityEngine.Vector3[] path, System.Single duration, DG.Tweening.PathType pathType, DG.Tweening.PathMode pathMode, System.Int32 resolution, System.Nullable`1<UnityEngine.Color> gizmoColor)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Quaternion,UnityEngine.Vector3,DG.Tweening.Plugins.Options.QuaternionOptions> DOLookAt(UnityEngine.Rigidbody target, UnityEngine.Vector3 towards, System.Single duration, DG.Tweening.AxisConstraint axisConstraint, System.Nullable`1<UnityEngine.Vector3> up)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector3,UnityEngine.Vector3,DG.Tweening.Plugins.Options.VectorOptions> DOMove(UnityEngine.Rigidbody target, UnityEngine.Vector3 endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector3,UnityEngine.Vector3,DG.Tweening.Plugins.Options.VectorOptions> DOMoveX(UnityEngine.Rigidbody target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector3,UnityEngine.Vector3,DG.Tweening.Plugins.Options.VectorOptions> DOMoveY(UnityEngine.Rigidbody target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector3,UnityEngine.Vector3,DG.Tweening.Plugins.Options.VectorOptions> DOMoveZ(UnityEngine.Rigidbody target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector3,DG.Tweening.Plugins.Core.PathCore.Path,DG.Tweening.Plugins.Options.PathOptions> DOPath(UnityEngine.Rigidbody target, UnityEngine.Vector3[] path, System.Single duration, DG.Tweening.PathType pathType, DG.Tweening.PathMode pathMode, System.Int32 resolution, System.Nullable`1<UnityEngine.Color> gizmoColor)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Quaternion,UnityEngine.Vector3,DG.Tweening.Plugins.Options.QuaternionOptions> DORotate(UnityEngine.Rigidbody target, UnityEngine.Vector3 endValue, System.Single duration, DG.Tweening.RotateMode mode)` — static: True, virtual: False


### `DG.Tweening.DOTweenModuleSprite`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** True | **Public:** True
**Nested types:** 3

#### Methods

**Public methods:**
- `DG.Tweening.Tweener DOBlendableColor(UnityEngine.SpriteRenderer target, UnityEngine.Color endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Color,UnityEngine.Color,DG.Tweening.Plugins.Options.ColorOptions> DOColor(UnityEngine.SpriteRenderer target, UnityEngine.Color endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Color,UnityEngine.Color,DG.Tweening.Plugins.Options.ColorOptions> DOFade(UnityEngine.SpriteRenderer target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Sequence DOGradientColor(UnityEngine.SpriteRenderer target, UnityEngine.Gradient gradient, System.Single duration)` — static: True, virtual: False


### `DG.Tweening.DOTweenModuleUI`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** True | **Public:** True
**Nested types:** 41

#### Methods

**Public methods:**
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOAnchorMax(UnityEngine.RectTransform target, UnityEngine.Vector2 endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOAnchorMin(UnityEngine.RectTransform target, UnityEngine.Vector2 endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOAnchorPos(UnityEngine.RectTransform target, UnityEngine.Vector2 endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector3,UnityEngine.Vector3,DG.Tweening.Plugins.Options.VectorOptions> DOAnchorPos3D(UnityEngine.RectTransform target, UnityEngine.Vector3 endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector3,UnityEngine.Vector3,DG.Tweening.Plugins.Options.VectorOptions> DOAnchorPos3DX(UnityEngine.RectTransform target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector3,UnityEngine.Vector3,DG.Tweening.Plugins.Options.VectorOptions> DOAnchorPos3DY(UnityEngine.RectTransform target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector3,UnityEngine.Vector3,DG.Tweening.Plugins.Options.VectorOptions> DOAnchorPos3DZ(UnityEngine.RectTransform target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOAnchorPosX(UnityEngine.RectTransform target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOAnchorPosY(UnityEngine.RectTransform target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Tweener DOBlendableColor(UnityEngine.UI.Graphic target, UnityEngine.Color endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Tweener DOBlendableColor(UnityEngine.UI.Image target, UnityEngine.Color endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Tweener DOBlendableColor(UnityEngine.UI.Text target, UnityEngine.Color endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Color,UnityEngine.Color,DG.Tweening.Plugins.Options.ColorOptions> DOColor(UnityEngine.UI.Graphic target, UnityEngine.Color endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Color,UnityEngine.Color,DG.Tweening.Plugins.Options.ColorOptions> DOColor(UnityEngine.UI.Image target, UnityEngine.Color endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Color,UnityEngine.Color,DG.Tweening.Plugins.Options.ColorOptions> DOColor(UnityEngine.UI.Outline target, UnityEngine.Color endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Color,UnityEngine.Color,DG.Tweening.Plugins.Options.ColorOptions> DOColor(UnityEngine.UI.Text target, UnityEngine.Color endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<System.Int32,System.Int32,DG.Tweening.Plugins.Options.NoOptions> DOCounter(UnityEngine.UI.Text target, System.Int32 fromValue, System.Int32 endValue, System.Single duration, System.Boolean addThousandsSeparator, System.Globalization.CultureInfo culture)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<System.Single,System.Single,DG.Tweening.Plugins.Options.FloatOptions> DOFade(UnityEngine.CanvasGroup target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Color,UnityEngine.Color,DG.Tweening.Plugins.Options.ColorOptions> DOFade(UnityEngine.UI.Graphic target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Color,UnityEngine.Color,DG.Tweening.Plugins.Options.ColorOptions> DOFade(UnityEngine.UI.Image target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Color,UnityEngine.Color,DG.Tweening.Plugins.Options.ColorOptions> DOFade(UnityEngine.UI.Outline target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Color,UnityEngine.Color,DG.Tweening.Plugins.Options.ColorOptions> DOFade(UnityEngine.UI.Text target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<System.Single,System.Single,DG.Tweening.Plugins.Options.FloatOptions> DOFillAmount(UnityEngine.UI.Image target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOFlexibleSize(UnityEngine.UI.LayoutElement target, UnityEngine.Vector2 endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Sequence DOGradientColor(UnityEngine.UI.Image target, UnityEngine.Gradient gradient, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Tweener DOHorizontalNormalizedPos(UnityEngine.UI.ScrollRect target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Sequence DOJumpAnchorPos(UnityEngine.RectTransform target, UnityEngine.Vector2 endValue, System.Single jumpPower, System.Int32 numJumps, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOMinSize(UnityEngine.UI.LayoutElement target, UnityEngine.Vector2 endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Tweener DONormalizedPos(UnityEngine.UI.ScrollRect target, UnityEngine.Vector2 endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOPivot(UnityEngine.RectTransform target, UnityEngine.Vector2 endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOPivotX(UnityEngine.RectTransform target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOPivotY(UnityEngine.RectTransform target, System.Single endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOPreferredSize(UnityEngine.UI.LayoutElement target, UnityEngine.Vector2 endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Tweener DOPunchAnchorPos(UnityEngine.RectTransform target, UnityEngine.Vector2 punch, System.Single duration, System.Int32 vibrato, System.Single elasticity, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOScale(UnityEngine.UI.Outline target, UnityEngine.Vector2 endValue, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Tweener DOShakeAnchorPos(UnityEngine.RectTransform target, System.Single duration, System.Single strength, System.Int32 vibrato, System.Single randomness, System.Boolean snapping, System.Boolean fadeOut)` — static: True, virtual: False
- `DG.Tweening.Tweener DOShakeAnchorPos(UnityEngine.RectTransform target, System.Single duration, UnityEngine.Vector2 strength, System.Int32 vibrato, System.Single randomness, System.Boolean snapping, System.Boolean fadeOut)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOSizeDelta(UnityEngine.RectTransform target, UnityEngine.Vector2 endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<System.String,System.String,DG.Tweening.Plugins.Options.StringOptions> DOText(UnityEngine.UI.Text target, System.String endValue, System.Single duration, System.Boolean richTextEnabled, DG.Tweening.ScrambleMode scrambleMode, System.String scrambleChars)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<System.Single,System.Single,DG.Tweening.Plugins.Options.FloatOptions> DOValue(UnityEngine.UI.Slider target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False
- `DG.Tweening.Tweener DOVerticalNormalizedPos(UnityEngine.UI.ScrollRect target, System.Single endValue, System.Single duration, System.Boolean snapping)` — static: True, virtual: False


### `DG.Tweening.DOTweenModuleUnityVersion`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** True | **Public:** True
**Nested types:** 8

#### Methods

**Public methods:**
- `System.Threading.Tasks.Task AsyncWaitForCompletion(DG.Tweening.Tween t)` — static: True, virtual: False
- `System.Threading.Tasks.Task AsyncWaitForElapsedLoops(DG.Tweening.Tween t, System.Int32 elapsedLoops)` — static: True, virtual: False
- `System.Threading.Tasks.Task AsyncWaitForKill(DG.Tweening.Tween t)` — static: True, virtual: False
- `System.Threading.Tasks.Task AsyncWaitForPosition(DG.Tweening.Tween t, System.Single position)` — static: True, virtual: False
- `System.Threading.Tasks.Task AsyncWaitForRewind(DG.Tweening.Tween t)` — static: True, virtual: False
- `System.Threading.Tasks.Task AsyncWaitForStart(DG.Tweening.Tween t)` — static: True, virtual: False
- `DG.Tweening.Sequence DOGradientColor(UnityEngine.Material target, UnityEngine.Gradient gradient, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Sequence DOGradientColor(UnityEngine.Material target, UnityEngine.Gradient gradient, System.String property, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOOffset(UnityEngine.Material target, UnityEngine.Vector2 endValue, System.Int32 propertyID, System.Single duration)` — static: True, virtual: False
- `DG.Tweening.Core.TweenerCore`3<UnityEngine.Vector2,UnityEngine.Vector2,DG.Tweening.Plugins.Options.VectorOptions> DOTiling(UnityEngine.Material target, UnityEngine.Vector2 endValue, System.Int32 propertyID, System.Single duration)` — static: True, virtual: False
- `UnityEngine.CustomYieldInstruction WaitForCompletion(DG.Tweening.Tween t, System.Boolean returnCustomYieldInstruction)` — static: True, virtual: False
- `UnityEngine.CustomYieldInstruction WaitForElapsedLoops(DG.Tweening.Tween t, System.Int32 elapsedLoops, System.Boolean returnCustomYieldInstruction)` — static: True, virtual: False
- `UnityEngine.CustomYieldInstruction WaitForKill(DG.Tweening.Tween t, System.Boolean returnCustomYieldInstruction)` — static: True, virtual: False
- `UnityEngine.CustomYieldInstruction WaitForPosition(DG.Tweening.Tween t, System.Single position, System.Boolean returnCustomYieldInstruction)` — static: True, virtual: False
- `UnityEngine.CustomYieldInstruction WaitForRewind(DG.Tweening.Tween t, System.Boolean returnCustomYieldInstruction)` — static: True, virtual: False
- `UnityEngine.CustomYieldInstruction WaitForStart(DG.Tweening.Tween t, System.Boolean returnCustomYieldInstruction)` — static: True, virtual: False


### `DG.Tweening.DOTweenModuleUtils`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** True | **Public:** True
**Nested types:** 1

#### Methods

**Public methods:**
- `System.Void Init()` — static: True, virtual: False

**Private methods:**
- `System.Void Preserver()` — static: True, virtual: False

#### Fields

**Private fields:**
- `System.Boolean _initialized` — static: True


## Namespace: `OX.Copyable`

### Classes

| Class | Base Type | Abstract | Sealed | Methods | Fields | Props | Nested |

|-------|-----------|----------|--------|---------|--------|-------|--------|

| `OX.Copyable.Copyable` | `System.Object` | **Yes** |  | 2 | 2 | 0 | 0 |

| `OX.Copyable.IInstanceProvider` | `<object>` | **Yes** |  | 0 | 0 | 1 | 0 |

| `OX.Copyable.IInstanceProvider`1` | `<object>` | **Yes** |  | 0 | 0 | 0 | 0 |

| `OX.Copyable.InstanceProvider`1` | `System.Object` | **Yes** |  | 3 | 0 | 1 | 0 |

| `OX.Copyable.ObjectExtensions` | `System.Object` | **Yes** | **Yes** | 10 | 1 | 0 | 1 |

| `OX.Copyable.VisitedGraph` | `System.Collections.Generic.Dictionary`2<System.Object,System.Object>` |  |  | 3 | 0 | 1 | 0 |

### `OX.Copyable.Copyable`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void OX.Copyable.Copyable..ctor(System.Object[] args)` — internal

#### Methods

**Public methods:**
- `System.Object CreateInstanceForCopy()` — static: False, virtual: False

#### Fields

**Private fields:**
- `System.Reflection.ConstructorInfo constructor` — static: False

- `System.Object[] constructorArgs` — static: False


### `OX.Copyable.IInstanceProvider`

**Base type:** `<object>` | **Abstract:** True | **Sealed:** False | **Public:** True
#### Properties

- `System.Type Provided` — getter: True, setter: False


### `OX.Copyable.IInstanceProvider`1`

**Base type:** `<object>` | **Abstract:** True | **Sealed:** False | **Public:** True
**Implements:** `OX.Copyable.IInstanceProvider`


### `OX.Copyable.InstanceProvider`1`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** False | **Public:** True
**Implements:** `OX.Copyable.IInstanceProvider`1<T>`, `OX.Copyable.IInstanceProvider`

#### Constructors

- `System.Void OX.Copyable.InstanceProvider`1..ctor()` — internal

#### Methods

**Public methods:**
- `System.Object CreateCopy(System.Object toBeCopied)` — static: False, virtual: True
- `System.Type get_Provided()` — static: False, virtual: True

#### Properties

- `System.Type Provided` — getter: True, setter: False


### `OX.Copyable.ObjectExtensions`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** True | **Public:** True
**Nested types:** 1

#### Constructors

- `System.Void OX.Copyable.ObjectExtensions..cctor()` — private

#### Methods

**Public methods:**
- `System.Object Copy(System.Object instance)` — static: True, virtual: False
- `System.Object Copy(System.Object instance, System.Object copy)` — static: True, virtual: False

**Private methods:**
- `System.Void AssemblyLoaded(System.Object sender, System.AssemblyLoadEventArgs args)` — static: True, virtual: False
- `System.Object Clone(System.Object instance, OX.Copyable.VisitedGraph visited)` — static: True, virtual: False
- `System.Object Clone(System.Object instance, OX.Copyable.VisitedGraph visited, System.Object copy)` — static: True, virtual: False
- `System.Object DeduceInstance(System.Object instance)` — static: True, virtual: False
- `System.Collections.Generic.IEnumerable`1<OX.Copyable.IInstanceProvider> GetInstanceProviders(System.Reflection.Assembly assembly)` — static: True, virtual: False
- `System.Collections.Generic.List`1<OX.Copyable.IInstanceProvider> IntializeInstanceProviders()` — static: True, virtual: False
- `System.Void UpdateInstanceProviders(System.Reflection.Assembly assembly, System.Collections.Generic.List`1<OX.Copyable.IInstanceProvider> providerList)` — static: True, virtual: False

#### Fields

**Private fields:**
- `System.Collections.Generic.List`1<OX.Copyable.IInstanceProvider> Providers` — static: True


### `OX.Copyable.VisitedGraph`

**Base type:** `System.Collections.Generic.Dictionary`2<System.Object,System.Object>` | **Abstract:** False | **Sealed:** False | **Public:** False
#### Constructors

- `System.Void OX.Copyable.VisitedGraph..ctor()` — public

#### Methods

**Public methods:**
- `System.Boolean ContainsKey(System.Object key)` — static: False, virtual: False
- `System.Object get_Item(System.Object key)` — static: False, virtual: False

#### Properties

- `System.Object Item` — getter: True, setter: False


## Namespace: `PathologicalGames`

### Classes

| Class | Base Type | Abstract | Sealed | Methods | Fields | Props | Nested |

|-------|-----------|----------|--------|---------|--------|-------|--------|

| `PathologicalGames.InstanceHandler` | `System.Object` | **Yes** | **Yes** | 2 | 2 | 0 | 2 |

| `PathologicalGames.PoolManager` | `System.Object` | **Yes** | **Yes** | 1 | 1 | 0 | 0 |

| `PathologicalGames.PrefabPool` | `System.Object` |  |  | 22 | 20 | 5 | 2 |

| `PathologicalGames.PrefabsDict` | `System.Object` |  |  | 24 | 1 | 6 | 0 |

| `PathologicalGames.PreRuntimePoolItem` | `UnityEngine.MonoBehaviour` |  |  | 2 | 4 | 0 | 0 |

| `PathologicalGames.SpawnPool` | `UnityEngine.MonoBehaviour` |  | **Yes** | 59 | 16 | 6 | 7 |

| `PathologicalGames.SpawnPoolsDict` | `System.Object` |  |  | 31 | 2 | 6 | 1 |

### `PathologicalGames.InstanceHandler`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** True | **Public:** True
**Nested types:** 2

#### Methods

#### Fields

**Public fields:**
- `PathologicalGames.InstanceHandler/DestroyDelegate DestroyDelegates` — static: True

- `PathologicalGames.InstanceHandler/InstantiateDelegate InstantiateDelegates` — static: True


### `PathologicalGames.PoolManager`

**Base type:** `System.Object` | **Abstract:** True | **Sealed:** True | **Public:** True
#### Constructors

- `System.Void PathologicalGames.PoolManager..cctor()` — private

#### Fields

**Public fields:**
- `PathologicalGames.SpawnPoolsDict Pools` — static: True


### `PathologicalGames.PrefabPool`

**Base type:** `System.Object` | **Abstract:** False | **Sealed:** False | **Public:** True
**Nested types:** 2

#### Constructors

- `System.Void PathologicalGames.PrefabPool..ctor(UnityEngine.Transform prefab)` — public

- `System.Void PathologicalGames.PrefabPool..ctor()` — public

#### Methods

**Public methods:**
- `System.Boolean Contains(UnityEngine.Transform transform)` — static: False, virtual: False
- `System.Collections.Generic.List`1<UnityEngine.Transform> get_despawned()` — static: False, virtual: False
- `System.Boolean get_logMessages()` — static: False, virtual: False
- `System.Collections.Generic.List`1<UnityEngine.Transform> get_spawned()` — static: False, virtual: False
- `System.Int32 get_totalCount()` — static: False, virtual: False
- `UnityEngine.Transform SpawnNew()` — static: False, virtual: False
- `UnityEngine.Transform SpawnNew(UnityEngine.Vector3 pos, UnityEngine.Quaternion rot)` — static: False, virtual: False

**Private methods:**
- `System.Void nameInstance(UnityEngine.Transform instance)` — static: False, virtual: False
- `System.Collections.IEnumerator PreloadOverTime()` — static: False, virtual: False
- `System.Void set_preloaded(System.Boolean value)` — static: False, virtual: False
- `System.Void SetRecursively(UnityEngine.Transform xform, System.Int32 layer)` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Boolean _logMessages` — static: False

- `System.Int32 cullAbove` — static: False

- `System.Int32 cullDelay` — static: False

- `System.Boolean cullDespawned` — static: False

- `System.Int32 cullMaxPerPass` — static: False

- `System.Int32 limitAmount` — static: False

- `System.Boolean limitFIFO` — static: False

- `System.Boolean limitInstances` — static: False

- `UnityEngine.Transform prefab` — static: False

- `System.Int32 preloadAmount` — static: False

- `System.Single preloadDelay` — static: False

- `System.Int32 preloadFrames` — static: False

- `System.Boolean preloadTime` — static: False

- `PathologicalGames.SpawnPool spawnPool` — static: False

**Private fields:**
- `System.Boolean _preloaded` — static: False

- `System.Boolean cullingActive` — static: False

- `System.Boolean forceLoggingSilent` — static: False

#### Properties

- `System.Collections.Generic.List`1<UnityEngine.Transform> despawned` — getter: True, setter: False

- `System.Boolean logMessages` — getter: True, setter: False

- `System.Boolean preloaded` — getter: True, setter: True

- `System.Collections.Generic.List`1<UnityEngine.Transform> spawned` — getter: True, setter: False

- `System.Int32 totalCount` — getter: True, setter: False


### `PathologicalGames.PrefabsDict`

**Base type:** `System.Object` | **Abstract:** False | **Sealed:** False | **Public:** True
**Implements:** `System.Collections.Generic.IDictionary`2<System.String,UnityEngine.Transform>`, `System.Collections.Generic.ICollection`1<System.Collections.Generic.KeyValuePair`2<System.String,UnityEngine.Transform>>`, `System.Collections.Generic.IEnumerable`1<System.Collections.Generic.KeyValuePair`2<System.String,UnityEngine.Transform>>`, `System.Collections.IEnumerable`

#### Constructors

- `System.Void PathologicalGames.PrefabsDict..ctor()` — public

#### Methods

**Public methods:**
- `System.Void Add(System.String key, UnityEngine.Transform value)` — static: False, virtual: True
- `System.Void Add(System.Collections.Generic.KeyValuePair`2<System.String,UnityEngine.Transform> item)` — static: False, virtual: True
- `System.Void Clear()` — static: False, virtual: True
- `System.Boolean Contains(System.Collections.Generic.KeyValuePair`2<System.String,UnityEngine.Transform> item)` — static: False, virtual: True
- `System.Boolean ContainsKey(System.String prefabName)` — static: False, virtual: True
- `System.Int32 get_Count()` — static: False, virtual: True
- `UnityEngine.Transform get_Item(System.String key)` — static: False, virtual: True
- `System.Collections.Generic.ICollection`1<System.String> get_Keys()` — static: False, virtual: True
- `System.Collections.Generic.ICollection`1<UnityEngine.Transform> get_Values()` — static: False, virtual: True
- `System.Collections.Generic.IEnumerator`1<System.Collections.Generic.KeyValuePair`2<System.String,UnityEngine.Transform>> GetEnumerator()` — static: False, virtual: True
- `System.Boolean Remove(System.String prefabName)` — static: False, virtual: True
- `System.Boolean Remove(System.Collections.Generic.KeyValuePair`2<System.String,UnityEngine.Transform> item)` — static: False, virtual: True
- `System.Void set_Item(System.String key, UnityEngine.Transform value)` — static: False, virtual: True
- `System.String ToString()` — static: False, virtual: True
- `System.Boolean TryGetValue(System.String prefabName, UnityEngine.Transform& prefab)` — static: False, virtual: True

**Private methods:**
- `System.Void CopyTo(System.Collections.Generic.KeyValuePair`2<System.String,UnityEngine.Transform>[] array, System.Int32 arrayIndex)` — static: False, virtual: False
- `System.Boolean get_IsReadOnly()` — static: False, virtual: False
- `System.Void System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<System.String,UnityEngine.Transform>>.CopyTo(System.Collections.Generic.KeyValuePair`2<System.String,UnityEngine.Transform>[] array, System.Int32 arrayIndex)` — static: False, virtual: True
- `System.Boolean System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<System.String,UnityEngine.Transform>>.get_IsReadOnly()` — static: False, virtual: True
- `System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()` — static: False, virtual: True

#### Fields

**Private fields:**
- `System.Collections.Generic.Dictionary`2<System.String,UnityEngine.Transform> _prefabs` — static: False

#### Properties

- `System.Int32 Count` — getter: True, setter: False

- `System.Boolean IsReadOnly` — getter: True, setter: False

- `UnityEngine.Transform Item` — getter: True, setter: True

- `System.Collections.Generic.ICollection`1<System.String> Keys` — getter: True, setter: False

- `System.Boolean System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<System.String,UnityEngine.Transform>>.IsReadOnly` — getter: True, setter: False

- `System.Collections.Generic.ICollection`1<UnityEngine.Transform> Values` — getter: True, setter: False


### `PathologicalGames.PreRuntimePoolItem`

**Base type:** `UnityEngine.MonoBehaviour` | **Abstract:** False | **Sealed:** False | **Public:** True
#### Constructors

- `System.Void PathologicalGames.PreRuntimePoolItem..ctor()` — public

#### Methods

**Private methods:**
- `System.Void Start()` — static: False, virtual: False

#### Fields

**Public fields:**
- `System.Boolean despawnOnStart` — static: False

- `System.Boolean doNotReparent` — static: False

- `System.String poolName` — static: False

- `System.String prefabName` — static: False


### `PathologicalGames.SpawnPool`

**Base type:** `UnityEngine.MonoBehaviour` | **Abstract:** False | **Sealed:** True | **Public:** True
**Implements:** `System.Collections.Generic.IList`1<UnityEngine.Transform>`, `System.Collections.Generic.ICollection`1<UnityEngine.Transform>`, `System.Collections.Generic.IEnumerable`1<UnityEngine.Transform>`, `System.Collections.IEnumerable`

**Nested types:** 7

#### Constructors

- `System.Void PathologicalGames.SpawnPool..ctor()` — public

#### Methods

**Public methods:**
- `System.Void Add(UnityEngine.Transform instance, System.String prefabName, System.Boolean despawn, System.Boolean parent)` — static: False, virtual: False
- `System.Void Add(UnityEngine.Transform item)` — static: False, virtual: True
- `System.Void Clear()` — static: False, virtual: True
- `System.Boolean Contains(UnityEngine.Transform item)` — static: False, virtual: True
- `System.Void CopyTo(UnityEngine.Transform[] array, System.Int32 arrayIndex)` — static: False, virtual: True
- `System.Void CreatePrefabPool(PathologicalGames.PrefabPool prefabPool)` — static: False, virtual: False
- `System.Void Despawn(UnityEngine.Transform instance)` — static: False, virtual: False
- `System.Void Despawn(UnityEngine.Transform instance, UnityEngine.Transform parent)` — static: False, virtual: False
- `System.Void Despawn(UnityEngine.Transform instance, System.Single seconds)` — static: False, virtual: False
- `System.Void Despawn(UnityEngine.Transform instance, System.Single seconds, UnityEngine.Transform parent)` — static: False, virtual: False
- `System.Void DespawnAll()` — static: False, virtual: False
- `System.Int32 get_Count()` — static: False, virtual: True
- `System.Boolean get_dontDestroyOnLoad()` — static: False, virtual: False
- `UnityEngine.Transform get_group()` — static: False, virtual: False
- `System.Boolean get_IsReadOnly()` — static: False, virtual: True
- `UnityEngine.Transform get_Item(System.Int32 index)` — static: False, virtual: True
- `System.Collections.Generic.Dictionary`2<System.String,PathologicalGames.PrefabPool> get_prefabPools()` — static: False, virtual: False
- `System.Collections.Generic.IEnumerator`1<UnityEngine.Transform> GetEnumerator()` — static: False, virtual: True
- `UnityEngine.Transform GetPrefab(UnityEngine.Transform instance)` — static: False, virtual: False
- `UnityEngine.GameObject GetPrefab(UnityEngine.GameObject instance)` — static: False, virtual: False
- `PathologicalGames.PrefabPool GetPrefabPool(UnityEngine.Transform prefab)` — static: False, virtual: False
- `PathologicalGames.PrefabPool GetPrefabPool(UnityEngine.GameObject prefab)` — static: False, virtual: False
- `System.Int32 IndexOf(UnityEngine.Transform item)` — static: False, virtual: True
- `System.Void Insert(System.Int32 index, UnityEngine.Transform item)` — static: False, virtual: True
- `System.Boolean IsSpawned(UnityEngine.Transform instance)` — static: False, virtual: False
- `System.Void Remove(UnityEngine.Transform item)` — static: False, virtual: False
- `System.Void RemoveAt(System.Int32 index)` — static: False, virtual: True
- `System.Void set_dontDestroyOnLoad(System.Boolean value)` — static: False, virtual: False
- `System.Void set_Item(System.Int32 index, UnityEngine.Transform value)` — static: False, virtual: True
- `UnityEngine.Transform Spawn(UnityEngine.Transform prefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, UnityEngine.Transform parent)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(UnityEngine.Transform prefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(UnityEngine.Transform prefab)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(UnityEngine.Transform prefab, UnityEngine.Transform parent)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(UnityEngine.GameObject prefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, UnityEngine.Transform parent)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(UnityEngine.GameObject prefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(UnityEngine.GameObject prefab)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(UnityEngine.GameObject prefab, UnityEngine.Transform parent)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(System.String prefabName)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(System.String prefabName, UnityEngine.Transform parent)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(System.String prefabName, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot)` — static: False, virtual: False
- `UnityEngine.Transform Spawn(System.String prefabName, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, UnityEngine.Transform parent)` — static: False, virtual: False
- `UnityEngine.AudioSource Spawn(UnityEngine.AudioSource prefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot)` — static: False, virtual: False
- `UnityEngine.AudioSource Spawn(UnityEngine.AudioSource prefab)` — static: False, virtual: False
- `UnityEngine.AudioSource Spawn(UnityEngine.AudioSource prefab, UnityEngine.Transform parent)` — static: False, virtual: False
- `UnityEngine.AudioSource Spawn(UnityEngine.AudioSource prefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, UnityEngine.Transform parent)` — static: False, virtual: False
- `UnityEngine.ParticleSystem Spawn(UnityEngine.ParticleSystem prefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot)` — static: False, virtual: False
- `UnityEngine.ParticleSystem Spawn(UnityEngine.ParticleSystem prefab, UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, UnityEngine.Transform parent)` — static: False, virtual: False
- `System.String ToString()` — static: False, virtual: True

**Private methods:**
- `System.Void Awake()` — static: False, virtual: False
- `System.Collections.IEnumerator DoDespawnAfterSeconds(UnityEngine.Transform instance, System.Single seconds, System.Boolean useParent, UnityEngine.Transform parent)` — static: False, virtual: False
- `System.Collections.IEnumerator ListenForEmitDespawn(UnityEngine.ParticleSystem emitter)` — static: False, virtual: False
- `System.Collections.IEnumerator ListForAudioStop(UnityEngine.AudioSource src)` — static: False, virtual: False
- `System.Void OnDestroy()` — static: False, virtual: False
- `System.Void set_group(UnityEngine.Transform value)` — static: False, virtual: False
- `System.Boolean System.Collections.Generic.ICollection<UnityEngine.Transform>.Remove(UnityEngine.Transform item)` — static: False, virtual: True
- `System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()` — static: False, virtual: True

#### Fields

**Public fields:**
- `System.Boolean _dontDestroyOnLoad` — static: False

- `System.Collections.Generic.Dictionary`2<System.Object,System.Boolean> _editorListItemStates` — static: False

- `System.Collections.Generic.List`1<PathologicalGames.PrefabPool> _perPrefabPoolOptions` — static: False

- `PathologicalGames.SpawnPool/DestroyDelegate destroyDelegates` — static: False

- `System.Boolean dontReparent` — static: False

- `PathologicalGames.SpawnPool/InstantiateDelegate instantiateDelegates` — static: False

- `System.Boolean logMessages` — static: False

- `System.Boolean matchPoolLayer` — static: False

- `System.Boolean matchPoolScale` — static: False

- `System.Single maxParticleDespawnTime` — static: False

- `System.String poolName` — static: False

- `PathologicalGames.PrefabsDict prefabs` — static: False

- `System.Collections.Generic.Dictionary`2<System.Object,System.Boolean> prefabsFoldOutStates` — static: False

**Private fields:**
- `System.Collections.Generic.List`1<PathologicalGames.PrefabPool> _prefabPools` — static: False

- `UnityEngine.Transform <group>k__BackingField` — static: False

#### Properties

- `System.Int32 Count` — getter: True, setter: False

- `System.Boolean dontDestroyOnLoad` — getter: True, setter: True

- `UnityEngine.Transform group` — getter: True, setter: True

- `System.Boolean IsReadOnly` — getter: True, setter: False

- `UnityEngine.Transform Item` — getter: True, setter: True

- `System.Collections.Generic.Dictionary`2<System.String,PathologicalGames.PrefabPool> prefabPools` — getter: True, setter: False


### `PathologicalGames.SpawnPoolsDict`

**Base type:** `System.Object` | **Abstract:** False | **Sealed:** False | **Public:** True
**Implements:** `System.Collections.Generic.IDictionary`2<System.String,PathologicalGames.SpawnPool>`, `System.Collections.Generic.ICollection`1<System.Collections.Generic.KeyValuePair`2<System.String,PathologicalGames.SpawnPool>>`, `System.Collections.Generic.IEnumerable`1<System.Collections.Generic.KeyValuePair`2<System.String,PathologicalGames.SpawnPool>>`, `System.Collections.IEnumerable`

**Nested types:** 1

#### Constructors

- `System.Void PathologicalGames.SpawnPoolsDict..ctor()` — public

#### Methods

**Public methods:**
- `System.Void Add(System.String key, PathologicalGames.SpawnPool value)` — static: False, virtual: True
- `System.Void Add(System.Collections.Generic.KeyValuePair`2<System.String,PathologicalGames.SpawnPool> item)` — static: False, virtual: True
- `System.Void AddOnCreatedDelegate(System.String poolName, PathologicalGames.SpawnPoolsDict/OnCreatedDelegate createdDelegate)` — static: False, virtual: False
- `System.Void Clear()` — static: False, virtual: True
- `System.Boolean Contains(System.Collections.Generic.KeyValuePair`2<System.String,PathologicalGames.SpawnPool> item)` — static: False, virtual: True
- `System.Boolean ContainsKey(System.String poolName)` — static: False, virtual: True
- `System.Boolean ContainsValue(PathologicalGames.SpawnPool pool)` — static: False, virtual: False
- `PathologicalGames.SpawnPool Create(System.String poolName)` — static: False, virtual: False
- `PathologicalGames.SpawnPool Create(System.String poolName, UnityEngine.GameObject owner)` — static: False, virtual: False
- `System.Boolean Destroy(System.String poolName)` — static: False, virtual: False
- `System.Void DestroyAll()` — static: False, virtual: False
- `System.Int32 get_Count()` — static: False, virtual: True
- `PathologicalGames.SpawnPool get_Item(System.String key)` — static: False, virtual: True
- `System.Collections.Generic.ICollection`1<System.String> get_Keys()` — static: False, virtual: True
- `System.Collections.Generic.ICollection`1<PathologicalGames.SpawnPool> get_Values()` — static: False, virtual: True
- `System.Collections.Generic.IEnumerator`1<System.Collections.Generic.KeyValuePair`2<System.String,PathologicalGames.SpawnPool>> GetEnumerator()` — static: False, virtual: True
- `System.Boolean Remove(System.String poolName)` — static: False, virtual: True
- `System.Boolean Remove(System.Collections.Generic.KeyValuePair`2<System.String,PathologicalGames.SpawnPool> item)` — static: False, virtual: True
- `System.Void RemoveOnCreatedDelegate(System.String poolName, PathologicalGames.SpawnPoolsDict/OnCreatedDelegate createdDelegate)` — static: False, virtual: False
- `System.Void set_Item(System.String key, PathologicalGames.SpawnPool value)` — static: False, virtual: True
- `System.String ToString()` — static: False, virtual: True
- `System.Boolean TryGetValue(System.String poolName, PathologicalGames.SpawnPool& spawnPool)` — static: False, virtual: True

**Private methods:**
- `System.Boolean assertValidPoolName(System.String poolName)` — static: False, virtual: False
- `System.Void CopyTo(System.Collections.Generic.KeyValuePair`2<System.String,PathologicalGames.SpawnPool>[] array, System.Int32 arrayIndex)` — static: False, virtual: False
- `System.Boolean get_IsReadOnly()` — static: False, virtual: False
- `System.Void System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<System.String,PathologicalGames.SpawnPool>>.CopyTo(System.Collections.Generic.KeyValuePair`2<System.String,PathologicalGames.SpawnPool>[] array, System.Int32 arrayIndex)` — static: False, virtual: True
- `System.Boolean System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<System.String,PathologicalGames.SpawnPool>>.get_IsReadOnly()` — static: False, virtual: True
- `System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()` — static: False, virtual: True

#### Fields

**Private fields:**
- `System.Collections.Generic.Dictionary`2<System.String,PathologicalGames.SpawnPool> _pools` — static: False

#### Properties

- `System.Int32 Count` — getter: True, setter: False

- `System.Boolean IsReadOnly` — getter: True, setter: False

- `PathologicalGames.SpawnPool Item` — getter: True, setter: True

- `System.Collections.Generic.ICollection`1<System.String> Keys` — getter: True, setter: False

- `System.Boolean System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<System.String,PathologicalGames.SpawnPool>>.IsReadOnly` — getter: True, setter: False

- `System.Collections.Generic.ICollection`1<PathologicalGames.SpawnPool> Values` — getter: True, setter: False


## Referenced By

- `Assembly-CSharp.dll`

