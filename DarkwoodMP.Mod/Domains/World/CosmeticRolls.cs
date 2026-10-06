using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DWMPHorde.Logging;
using UnityEngine;
using Random = UnityEngine.Random;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Cosmetic randomness every machine rolls the same way: <c>SpriteRandomizer</c> tints, flips,
    /// rotations, heights, sprite and clip picks (about 70,000 in the shipped scenes),
    /// <c>AnimationPlay</c> clip, start frame and timing picks, parallax layer drift, and
    /// <c>VineSpawner</c> vine rotations.
    /// <para>
    /// Vanilla rolls them from the global random stream, so every machine got its own look, and
    /// even one machine changed its look between a fresh world and the same world loaded (about
    /// 85% of randomizers re-roll on every load; the rest fall back to the prefab look). A roll
    /// here runs on a seed made from the object's identity and puts the global stream back
    /// afterwards, so the same object rolls the same look on every machine.
    /// </para>
    /// <para>
    /// A roll's live seed comes from what every machine spawning the object live has bit for bit:
    /// inside a location, the location's authored name, its root's coarse placement and the
    /// authored path (names and local offsets) down to the object; outside one, the name and world
    /// position (<see cref="PlaceKey"/>). Positions pick up float noise through each save and
    /// load, so a seed is not made again from them: every save writes the seed of each roll under
    /// a saved object (<see cref="KeyStoreFileName"/>, keyed by that object's save id and the
    /// names below it), the world share sends that file along, and a load rolls on the stored seed.
    /// </para>
    /// <para>
    /// A roll runs when vanilla's would: in <c>Awake</c> for a live spawn (a location loaded live
    /// still stands at its authored spot then, on every machine alike), after world generation or
    /// a save load for the rest. A save load rolls every randomizer (vanilla skips the 15% not
    /// marked <c>randomizeOnLoad</c>) and keeps a saved object's saved rotation and height instead
    /// of rolling them again (vanilla stacked another height offset on every load).
    /// </para>
    /// <para>
    /// Things that move (creatures, physics props) keep the key of where they first rolled
    /// (<see cref="AnchorRec"/>). A copy a machine gets later at another spot (a phantom, a prop
    /// spawned after the save) takes the host's key and rolls again: creatures with their entity
    /// descriptor (<see cref="KeyOfCharacter"/> / <see cref="ApplyCharacterKey"/>), props in the
    /// late-join bulk (<see cref="SendMovedTo"/>).
    /// </para>
    /// Runs with or without a session: a host's world from before it hosted must already look the
    /// way its clients will roll it.
    /// </summary>
    internal static partial class CosmeticRolls
    {
        // ---- identity ---------------------------------------------------------------------------

        /// <summary>
        /// The key of a place in the world, made from what every machine spawning it live has bit
        /// for bit. <paramref name="withRootPlacement"/>: include where the location root stands, so
        /// two copies of one location prefab look different. Callers whose roll may run before the
        /// root is placed leave it out.
        /// </summary>
        internal static int PlaceKey(Transform t, bool withRootPlacement)
        {
            Transform loc = OutermostLocation(t);
            if (loc != null)
            {
                uint h = CosmeticKey.Add(CosmeticKey.Start(), CosmeticKey.RootName(loc.name));
                if (withRootPlacement)
                {
                    Vector3 p = loc.position;
                    h = CosmeticKey.Add(h, CosmeticKey.Cell(p.x, CosmeticKey.RootCell));
                    h = CosmeticKey.Add(h, CosmeticKey.Cell(p.z, CosmeticKey.RootCell));
                }
                return CosmeticKey.Finish(Chain(h, t, loc));
            }
            Vector3 w = t.position;
            uint g = CosmeticKey.Add(CosmeticKey.Start(), CleanName(t.name));
            g = CosmeticKey.Add(g, CosmeticKey.Cell(w.x, CosmeticKey.LocalCell));
            g = CosmeticKey.Add(g, CosmeticKey.Cell(w.z, CosmeticKey.LocalCell));
            return CosmeticKey.Finish(g);
        }

        internal const int SaltSprite = 1;
        internal const int SaltAnim = 2;
        internal const int SaltParallax = 3;
        internal const int SaltAnchor = 4;
        internal const int SaltSchedule = 5;
        internal const int SaltTk2dRandom = 6;

        /// <summary>
        /// The seed a roll of kind <paramref name="salt"/> on <paramref name="t"/> runs on: the seed
        /// the loaded save stored for it, else <paramref name="liveSeed"/>. Either way it is kept for
        /// the next save.
        /// </summary>
        internal static int SeedFor(Transform t, int salt, int liveSeed)
        {
            int seed = liveSeed;
            if (_storedSeeds.Count > 0 && TryStoreKey(t, salt, out long storeKey)
                && _storedSeeds.TryGetValue(storeKey, out int stored))
                seed = stored;
            Mark(t.gameObject, salt, seed);
            return seed;
        }

        /// <summary>Names and authored x/z offsets from <paramref name="from"/> up to (not including) <paramref name="stop"/>.</summary>
        private static uint Chain(uint h, Transform from, Transform stop)
        {
            for (Transform n = from; n != null && n != stop; n = n.parent)
            {
                h = CosmeticKey.Add(h, CleanName(n.name));
                Vector3 lp = n.localPosition;
                h = CosmeticKey.Add(h, CosmeticKey.Cell(lp.x, CosmeticKey.LocalCell));
                h = CosmeticKey.Add(h, CosmeticKey.Cell(lp.z, CosmeticKey.LocalCell));
            }
            return h;
        }

        /// <summary>
        /// A pooled instance is named "<c>prefab(Clone)001</c>" with a per-machine spawn count;
        /// the key uses the prefab name.
        /// </summary>
        private static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return clone >= 0 ? name.Substring(0, clone) : name;
        }

        private static Transform OutermostLocation(Transform t)
        {
            Transform found = null;
            for (Transform n = t; n != null; n = n.parent)
            {
                if (n.GetComponent<Location>() != null)
                    found = n;
            }
            return found;
        }

        /// <summary>A creature, or a free physics body: something that moves away from where it rolled.</summary>
        private static Transform MoverAnchor(Transform t, out bool isCharacter)
        {
            Character c = t.GetComponentInParent<Character>(true);
            if (c != null)
            {
                isCharacter = true;
                return c.transform;
            }
            isCharacter = false;
            Rigidbody rb = t.GetComponentInParent<Rigidbody>(true);
            return rb != null && !rb.isKinematic ? rb.transform : null;
        }

        /// <summary>A transform the save restored (its rotation and height are the saved ones).</summary>
        private static bool IsRestored(Transform t)
        {
            SaveableObject so = t != null ? t.GetComponent<SaveableObject>() : null;
            return so != null && so.assigned && !so.dontSave;
        }

        // ---- movers -------------------------------------------------------------------------------

        private struct Params
        {
            public bool RotationRandomize, FullRotationRange, HeightRandomize, AnimRandomize,
                AnimRandomizeStartFrame, MirrorRandomize, SpriteRandomize, ColorRandomize, AlphaRandomize;
            public float HeightRandom, ColorRandom, LightnessRandom, AlphaRandom;
            public List<string> Anims;

            public static Params From(SpriteRandomizer sr) => new Params
            {
                RotationRandomize = sr.rotationRandomize,
                FullRotationRange = sr.fullRotationRange,
                HeightRandomize = sr.heightRandomize,
                HeightRandom = sr.heightRandom,
                AnimRandomize = sr.animRandomize,
                AnimRandomizeStartFrame = sr.animRandomizeStartFrame,
                MirrorRandomize = sr.mirrorRandomize,
                SpriteRandomize = sr.spriteRandomize,
                ColorRandomize = sr.colorRandomize,
                ColorRandom = sr.colorRandom,
                LightnessRandom = sr.lightnessRandom,
                AlphaRandomize = sr.alphaRandomize,
                AlphaRandom = sr.alphaRandom,
                Anims = sr.anims
            };
        }

        /// <summary>One randomizer under a mover, with what it needs to roll again.</summary>
        private sealed class RollRec
        {
            public Params P;
            public uint Chain;
            /// <summary>The randomizer's object (its seed is stored under it).</summary>
            public GameObject Owner;
            public tk2dBaseSprite Sprite;
            public tk2dSpriteAnimator Animator;
            public Color BaseColor;
            public bool BaseFlipX, BaseFlipY;
            public tk2dSpriteCollectionData BaseCollection;
            public int BaseSpriteId;
        }

        /// <summary>A mover and the key its randomizers rolled with.</summary>
        private sealed class AnchorRec
        {
            public GameObject Go;
            public bool IsCharacter;
            public int Key;
            public readonly List<RollRec> Rolls = new List<RollRec>(1);
        }

        private static readonly Dictionary<int, AnchorRec> _anchors = new Dictionary<int, AnchorRec>(256); // process-scoped: tracks live objects with or without a session; dead entries pruned
        private static int _pruneAt = 512; // process-scoped: prune threshold for _anchors

        private static AnchorRec GetOrAddAnchor(Transform anchor, bool isCharacter)
        {
            int id = anchor.gameObject.GetInstanceID();
            if (_anchors.TryGetValue(id, out AnchorRec rec) && rec.Go == anchor.gameObject)
                return rec;
            if (_anchors.Count >= _pruneAt)
                PruneAnchors();
            rec = new AnchorRec
            {
                Go = anchor.gameObject,
                IsCharacter = isCharacter,
                // Where it was born, or the birth key the save stored for it.
                Key = SeedFor(anchor, SaltAnchor, PlaceKey(anchor, withRootPlacement: true))
            };
            _anchors[id] = rec;
            return rec;
        }

        private static readonly List<int> _deadScratch = new List<int>(64); // process-scoped: scratch buffer

        private static void PruneAnchors()
        {
            _deadScratch.Clear();
            foreach (KeyValuePair<int, AnchorRec> kv in _anchors)
            {
                if (kv.Value.Go == null)
                    _deadScratch.Add(kv.Key);
            }
            for (int i = 0; i < _deadScratch.Count; i++)
                _anchors.Remove(_deadScratch[i]);
            _deadScratch.Clear();
            _pruneAt = Math.Max(512, _anchors.Count * 2);
        }

        // ---- the roll -----------------------------------------------------------------------------

        /// <summary>
        /// Vanilla <c>SpriteRandomizer.init</c> step for step (same draws in the same order), on
        /// <paramref name="seed"/>. <paramref name="applyTransform"/> / <paramref name="applyAnim"/>
        /// off: the draw is still made (later draws stay in step) but its result is not applied.
        /// </summary>
        private static void Roll(ref Params p, Transform tf, tk2dBaseSprite sprite, tk2dSpriteAnimator animator,
            int seed, bool applyTransform, bool applyAnim)
        {
            Random.State outer = Random.state;
            Random.InitState(seed);
            try
            {
                if (p.RotationRandomize)
                {
                    float yaw = p.FullRotationRange ? Random.Range(0, 360) : Core.getRandomHalfRotation();
                    if (applyTransform)
                        tf.eulerAngles = new Vector3(90f, yaw, 0f);
                }
                if (p.HeightRandomize)
                {
                    float y = Random.Range(0f - p.HeightRandom, p.HeightRandom);
                    if (applyTransform)
                        tf.position += new Vector3(0f, y, 0f);
                }
                if (animator != null)
                {
                    if (p.AnimRandomize)
                    {
                        string clip = p.Anims[Random.Range(0, p.Anims.Count)];
                        if (applyAnim)
                        {
                            animator.setClip(clip);
                            animator.SetFrame(0);
                        }
                    }
                    if (p.AnimRandomizeStartFrame && animator.CurrentOrDefaultClip != null)
                    {
                        tk2dSpriteAnimationClip current = animator.CurrentOrDefaultClip;
                        int frame = Random.Range(0, current.frames.Length);
                        if (applyAnim)
                            animator.Play(current, frame, 0f);
                    }
                }
                if (sprite != null)
                {
                    if (p.MirrorRandomize)
                    {
                        if (Random.Range(0, 100) > 50)
                            sprite.FlipX = true;
                        if (Random.Range(0, 100) > 50)
                            sprite.FlipY = true;
                    }
                    if (p.SpriteRandomize)
                        sprite.SetSprite(p.Anims[Random.Range(0, p.Anims.Count)]);
                    if (p.ColorRandomize)
                    {
                        float lightness = Random.Range(0f, p.LightnessRandom);
                        Color c = sprite.color;
                        float alpha = c.a;
                        if (p.AlphaRandomize)
                            alpha -= Random.Range(0f, p.AlphaRandom);
                        float r = c.r - Random.Range(0f, p.ColorRandom) - lightness;
                        float g = c.g - Random.Range(0f, p.ColorRandom) - lightness;
                        float b = c.b - Random.Range(0f, p.ColorRandom) - lightness;
                        sprite.color = new Color(r, g, b, alpha);
                    }
                }
            }
            finally
            {
                Random.state = outer;
            }
        }

        /// <summary>Replaces vanilla <c>SpriteRandomizer.init</c> (same guards, same end: the component is destroyed).</summary>
        internal static void RollSprite(SpriteRandomizer sr)
        {
            if (sr == null || sr.initialized)
                return;
            sr.initialized = true;
            if (sr.sprite == null)
                sr.sprite = sr.GetComponentInChildren<tk2dBaseSprite>();
            if (sr.animator == null)
                sr.animator = sr.GetComponentInChildren<tk2dSpriteAnimator>();
            if (sr.transformOverride == null)
                sr.transformOverride = sr.transform;

            bool restored = TakeLoadInit(sr) && IsRestored(sr.transformOverride);
            // A mover's rolls build on the key of where it was born; anything else on its own place.
            Transform mover = MoverAnchor(sr.transform, out bool isCharacter);
            AnchorRec arec = null;
            uint chain = 0;
            int liveSeed;
            if (mover != null)
            {
                arec = GetOrAddAnchor(mover, isCharacter);
                chain = Chain(CosmeticKey.Start(), sr.transform, mover);
                liveSeed = CosmeticKey.Mix(arec.Key, chain);
            }
            else
            {
                liveSeed = PlaceKey(sr.transform, withRootPlacement: true);
            }
            int seed = SeedFor(sr.transform, SaltSprite, liveSeed);

            Params p = Params.From(sr);
            RollRec rec = null;
            if (arec != null)
            {
                tk2dBaseSprite s = sr.sprite;
                rec = new RollRec
                {
                    P = p,
                    Chain = chain,
                    Owner = sr.gameObject,
                    Sprite = s,
                    Animator = sr.animator,
                    BaseColor = s != null ? s.color : Color.white,
                    BaseFlipX = s != null && s.FlipX,
                    BaseFlipY = s != null && s.FlipY,
                    BaseCollection = s != null ? s.Collection : null,
                    BaseSpriteId = s != null ? s.spriteId : 0
                };
            }

            Roll(ref p, sr.transformOverride, sr.sprite, sr.animator, seed, applyTransform: !restored, applyAnim: true);
            if (rec != null)
                arec.Rolls.Add(rec);
            UnityEngine.Object.Destroy(sr);
        }

        /// <summary>
        /// Roll a mover's randomizers again on <paramref name="key"/> (the host's). The transform is
        /// left alone (the save or the host's sync owns it); a creature's clip too (its animation is
        /// the host's).
        /// </summary>
        private static void Reroll(AnchorRec arec, int key)
        {
            if (arec.Key == key || arec.Go == null)
                return;
            arec.Key = key;
            Mark(arec.Go, SaltAnchor, key);
            for (int i = 0; i < arec.Rolls.Count; i++)
            {
                RollRec r = arec.Rolls[i];
                int seed = CosmeticKey.Mix(key, r.Chain);
                if (r.Owner != null)
                    Mark(r.Owner, SaltSprite, seed);
                if (r.Sprite != null)
                {
                    r.Sprite.color = r.BaseColor;
                    r.Sprite.FlipX = r.BaseFlipX;
                    r.Sprite.FlipY = r.BaseFlipY;
                    if (r.P.SpriteRandomize && r.BaseCollection != null)
                        r.Sprite.SetSprite(r.BaseCollection, r.BaseSpriteId);
                }
                Params p = r.P;
                Roll(ref p, null, r.Sprite, r.Animator, seed, applyTransform: false, applyAnim: !arec.IsCharacter);
            }
        }

        // ---- save loads ---------------------------------------------------------------------------

        private static readonly ConditionalWeakTable<SpriteRandomizer, object> _loadInits = new ConditionalWeakTable<SpriteRandomizer, object>(); // process-scoped: weak, entries die with their randomizer
        private static readonly object LoadMark = new object(); // process-scoped: immutable marker

        /// <summary>
        /// <c>SpriteRandomizer.Awake</c> while a save loads: every randomizer rolls (on vanilla's own
        /// load queue), and the roll knows it is a load.
        /// </summary>
        internal static void NoteAwake(SpriteRandomizer sr)
        {
            if (sr == null || !Core.loadingGame)
                return;
            _loadInits.Remove(sr);
            _loadInits.Add(sr, LoadMark);
            if (!sr.randomizeOnLoad)
                sr.randomizeOnLoad = true;
        }

        private static bool TakeLoadInit(SpriteRandomizer sr)
        {
            if (!_loadInits.TryGetValue(sr, out _))
                return false;
            _loadInits.Remove(sr);
            return true;
        }

        // ---- holding a roll for its save id ---------------------------------------------------------

        /// <summary>
        /// A roll during <c>SaveManager.loadObj</c> (a parallax set up in <c>Awake</c>, in a world
        /// that is not generated) runs before the object has its save id, so it could not find its
        /// stored seed. It waits until <c>loadObj</c> gave the id (<see cref="ExitLoadObj"/>).
        /// Everything else rolls when vanilla does.
        /// </summary>
        private static int _loadObjDepth; // process-scoped: call-scoped, unwound by the loadObj Finalizer
        private static readonly List<UnityEngine.Object> _loadObjHeld = new List<UnityEngine.Object>(8); // process-scoped: call-scoped buffer
        private static readonly List<UnityEngine.Object> _readyScratch = new List<UnityEngine.Object>(8); // process-scoped: scratch buffer

        /// <summary>A held <c>Parallax.init</c> running now: its layer rolls run at once (it reads their result).</summary>
        private static int _parallaxRunDepth; // process-scoped: call-scoped, unwound by the Parallax.init Finalizer

        private static bool Hold(UnityEngine.Object target)
        {
            if (_loadObjDepth <= 0 || _parallaxRunDepth > 0)
                return false;
            _loadObjHeld.Add(target);
            return true;
        }

        /// <summary><c>SpriteRandomizer.init</c> prefix: false (the roll is taken over, now or after its save id).</summary>
        internal static bool InitPrefix(SpriteRandomizer sr)
        {
            if (sr == null || sr.initialized)
                return true;
            if (!Hold(sr))
                RollSprite(sr);
            return false;
        }

        /// <summary>
        /// <c>Parallax.init</c> prefix. Vanilla's setup rolls each layer's randomizer and reads its
        /// height right after, so a held parallax is held whole (false: held).
        /// </summary>
        internal static bool ParallaxInitPrefix(Parallax parallax, out bool running)
        {
            running = false;
            if (parallax == null)
                return true;
            if (Hold(parallax))
                return false;
            _parallaxRunDepth++;
            running = true;
            return true;
        }

        internal static void ParallaxInitFinalizer(bool running)
        {
            if (running && _parallaxRunDepth > 0)
                _parallaxRunDepth--;
        }

        /// <summary>
        /// Each parallax layer's ease factor is rolled too (vanilla: from the global stream, so each
        /// machine drifted its layers differently).
        /// </summary>
        internal static StreamScope EnterParallaxLayer(Transform layer)
        {
            if (layer == null)
                return default;
            var scope = new StreamScope { Active = true, Outer = Random.state };
            Random.InitState(SeedFor(layer, SaltParallax, CosmeticKey.Salt(PlaceKey(layer, withRootPlacement: true), "Parallax")));
            return scope;
        }

        internal static void ExitParallaxLayer(StreamScope scope)
        {
            if (scope.Active)
                Random.state = scope.Outer;
        }

        /// <summary><c>SaveManager.loadObj</c> starts: rolls inside it wait for the save id.</summary>
        internal static void EnterLoadObj() => _loadObjDepth++;

        /// <summary><c>SaveManager.loadObj</c> gave the object its save id: roll what it held.</summary>
        internal static void ExitLoadObj()
        {
            if (_loadObjDepth <= 0)
                return;
            _loadObjDepth--;
            if (_loadObjDepth > 0 || _loadObjHeld.Count == 0)
                return;
            _readyScratch.Clear();
            _readyScratch.AddRange(_loadObjHeld);
            _loadObjHeld.Clear();
            for (int i = 0; i < _readyScratch.Count; i++)
            {
                if (_readyScratch[i] != null)
                    RunAsAwake(_readyScratch[i]);
            }
            _readyScratch.Clear();
        }

        /// <summary>
        /// A held roll belongs to the object's <c>Awake</c>, where Unity logs an exception and goes
        /// on with the load; a held roll that throws does the same.
        /// </summary>
        private static void RunAsAwake(UnityEngine.Object target)
        {
            try
            {
                if (target is SpriteRandomizer sr)
                {
                    if (!sr.initialized)
                        RollSprite(sr);
                }
                else if (target is Parallax parallax)
                {
                    parallax.init();
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, target);
            }
        }
    }
}
