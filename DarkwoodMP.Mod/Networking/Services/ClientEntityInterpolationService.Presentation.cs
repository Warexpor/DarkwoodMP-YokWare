using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public static partial class ClientEntityInterpolationService
    {
        private static string PickHitClip(tk2dSpriteAnimator anim)
        {
            if (anim == null || anim.Library == null) return null;
            int n = 0;
            if (anim.GetClipByName("Hit1") != null) n++;
            if (anim.GetClipByName("Hit2") != null) n++;
            if (anim.GetClipByName("Hit3") != null) n++;
            if (n <= 0) return null;
            return "Hit" + UnityEngine.Random.Range(1, n + 1);
        }

        /// <summary>
        /// Show a host clip on a living (or downed) body; a dead one is shown by
        /// <see cref="EnsureDeathAnimation"/>. <paramref name="elapsed"/>: host seconds since the
        /// moment <paramref name="clipFrame"/> was sampled; <paramref name="animating"/>: the
        /// host keeps this clip moving (vanilla replays a finished once clip every frame).
        /// </summary>
        private static void ApplyEntityPresentation(Character c, short entityId, string clip, short clipFrame,
            float elapsed, bool animating)
        {
            if (c == null) return;

            tk2dSpriteAnimator body = ResolveBodyAnimator(c);
            ApplyClipToAnimator(body, entityId, clip, clipFrame, elapsed, animating);

            // Dogs and NPCs link animator.legs to legsAnimator; body Play drives both.
            // Dual-Play(bodyClip) on legs freezes walk cycles (floaty roam until aggro).
            tk2dSpriteAnimator legs = null;
            try { legs = c.legsAnimator; } catch { /* dismantled */ }
            if (legs != null && legs != body)
            {
                bool linked = false;
                try { linked = body != null && body.legs == legs; } catch { /* no .legs */ }
                if (!linked)
                    ApplyClipToAnimator(legs, entityId, clip, clipFrame, elapsed, animating);
            }
        }

        private static tk2dSpriteAnimator ResolveBodyAnimator(Character c)
        {
            if (c == null) return null;
            tk2dSpriteAnimator body = null;
            try
            {
                body = c.animator;
            }
            catch { /* dismantled */ }
            if (body == null)
                body = c.GetComponent<tk2dSpriteAnimator>();
            return body;
        }

        private const byte ClipKindReaction = 1;
        private const byte ClipKindDeath = 2;
        /// <summary>Clip name → reaction / death bits: asked per body per snapshot and per frame, worked out once per name.</summary>
        private static readonly Dictionary<string, byte> _clipKinds = new Dictionary<string, byte>(64); // reset-in: Reset

        private static byte ClipKind(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return 0;
            if (_clipKinds.TryGetValue(clip, out byte kind))
                return kind;
            kind = 0;
            if (clip.StartsWith("Hit", System.StringComparison.OrdinalIgnoreCase)
                || clip.StartsWith("Attack", System.StringComparison.OrdinalIgnoreCase)
                || clip.IndexOf("React", System.StringComparison.OrdinalIgnoreCase) >= 0
                || clip.IndexOf("Stun", System.StringComparison.OrdinalIgnoreCase) >= 0
                || clip.IndexOf("Flinch", System.StringComparison.OrdinalIgnoreCase) >= 0)
                kind |= ClipKindReaction;
            // PreDeath_Start / _Loop / _Hit are the downed phase, not a death.
            if ((clip.IndexOf("Death", System.StringComparison.OrdinalIgnoreCase) >= 0
                    && !clip.StartsWith("PreDeath", System.StringComparison.OrdinalIgnoreCase))
                || clip.Equals("Cut_half", System.StringComparison.OrdinalIgnoreCase)
                || clip.Equals("BeartrapDeath", System.StringComparison.OrdinalIgnoreCase))
                kind |= ClipKindDeath;
            if (_clipKinds.Count >= 512)
                _clipKinds.Clear();
            _clipKinds[clip] = kind;
            return kind;
        }

        /// <summary>
        /// True if clip name is a real death / cut-in-half presentation (not idle/walk).
        /// </summary>
        private static bool IsDeathClipName(string clip) => (ClipKind(clip) & ClipKindDeath) != 0;

        private static readonly AccessTools.FieldRef<Character, string> DeathAnimRef =
            AccessTools.FieldRefAccess<Character, string>("deathAnim");
        private static readonly System.Action<Character> GetDeathAnims =
            AccessTools.MethodDelegate<System.Action<Character>>(AccessTools.Method(typeof(Character), "getDeathAnims"));
        private static readonly System.Action<Character> DestroyComponents =
            AccessTools.MethodDelegate<System.Action<Character>>(AccessTools.Method(typeof(Character), "destroyComponents"));
        private static readonly System.Action<Character> DestroyComponents2 =
            AccessTools.MethodDelegate<System.Action<Character>>(AccessTools.Method(typeof(Character), "destroyComponents2"));

        private static string ResolveDeathClipName(Character c, tk2dSpriteAnimator anim, string hostClip)
        {
            if (anim == null) return null;

            if (IsDeathClipName(hostClip) && anim.GetClipByName(hostClip) != null)
                return hostClip;

            try
            {
                string deathAnim = DeathAnimRef(c);
                if (string.IsNullOrEmpty(deathAnim))
                {
                    GetDeathAnims(c);
                    deathAnim = DeathAnimRef(c);
                }
                if (!string.IsNullOrEmpty(deathAnim) && anim.GetClipByName(deathAnim) != null)
                    return deathAnim;
            }
            catch { /* field/method missing on odd prefabs */ }

            string[] fallbacks = { "Death1", "Death2", "Death3", "Death", "Cut_half", "BeartrapDeath" };
            for (int i = 0; i < fallbacks.Length; i++)
            {
                if (anim.GetClipByName(fallbacks[i]) != null)
                    return fallbacks[i];
            }
            return null;
        }

        /// <summary>
        /// Start the death presentation once. Client AI-skip blocks processAnims which
        /// is what vanilla uses to Play(deathAnim). Host clip is often already empty
        /// by the time Alive=false arrives (animator destroyed post-death on host).
        /// <paramref name="watched"/>: this client showed the body alive (or down), so the death
        /// plays, from the host's frame when the host was in it. Otherwise the body is an old
        /// corpse to this client (late join, back into view, a save corpse) and lies on the
        /// clip's last frame, as vanilla Character.init puts a dead body down.
        /// </summary>
        private static void EnsureDeathAnimation(Character c, short entityId, string hostClip, short hostFrame,
            bool watched, float elapsed)
        {
            if (c == null) return;
            if (_deathAnimationPlayed.Contains(entityId)) return;

            tk2dSpriteAnimator body = ResolveBodyAnimator(c);
            if (body == null) return;

            if (!body.enabled)
                body.enabled = true;

            string deathClip = ResolveDeathClipName(c, body, hostClip);
            tk2dSpriteAnimationClip def = !string.IsNullOrEmpty(deathClip) ? body.GetClipByName(deathClip) : null;
            if (def == null || def.frames == null || def.frames.Length == 0)
            {
                EntitySyncLog.Event(() =>
                    "[ClientDeathAnim] MISSING for " + c.name + "(id=" + entityId
                    + ") hostClip=" + (hostClip ?? ""));
                // Still mark so we don't spam; corpse stays lootable via die() patch.
                _deathAnimationPlayed.Add(entityId);
                return;
            }

            if (watched)
            {
                bool hostInIt = string.Equals(hostClip, deathClip, System.StringComparison.Ordinal);
                PlayAligned(body, def, hostInIt ? hostFrame : (short)0, hostInIt ? elapsed : 0f, hostLoops: false);
            }
            else
            {
                body.PlayFromFrame(def, def.frames.Length - 1);
            }

            _deathAnimationPlayed.Add(entityId);
            EntitySyncLog.Event(() =>
                "[ClientDeathAnim] " + (watched ? "Play(" : "lie on last frame of (") + deathClip + ") on " + c.name
                + "(id=" + entityId + ") hostClip=" + (hostClip ?? "") + " frame=" + hostFrame);
        }

        /// <summary>
        /// Start <paramref name="clip"/> where the host has it: <paramref name="hostFrame"/> (-1:
        /// the start) moved on by <paramref name="elapsed"/> host seconds at the clip's fps.
        /// A looping clip wraps; a once clip the host keeps replaying (<paramref name="hostLoops"/>)
        /// wraps too; any other once clip stops on its last frame. Always restarts the clip (tk2d
        /// Play does nothing for the clip already playing; an attack after the same attack must
        /// show). Random-frame clips are left to tk2d.
        /// </summary>
        private static void PlayAligned(tk2dSpriteAnimator anim, tk2dSpriteAnimationClip clip, short hostFrame,
            float elapsed, bool hostLoops)
        {
            int n = clip.frames != null ? clip.frames.Length : 0;
            if (n == 0 || clip.fps <= 0f
                || clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.Single
                || clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.RandomFrame
                || clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.RandomLoop)
            {
                anim.Play(clip);
                return;
            }
            float frame = (hostFrame > 0 ? Mathf.Min(hostFrame, n - 1) : 0) + Mathf.Max(elapsed, 0f) * clip.fps;
            if (clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.Once)
            {
                if (hostLoops)
                    frame %= n;
                else if (frame > n - 1)
                    frame = n - 1;
            }
            // Same offset as vanilla PlayFromFrame, so the frame index does not round down.
            anim.PlayFrom(clip, (frame + 0.001f) / clip.fps);
        }

        private static void ApplyClipToAnimator(
            tk2dSpriteAnimator anim, short entityId, string clip, short clipFrame, float elapsed, bool animating)
        {
            if (anim == null) return;

            if (!string.IsNullOrEmpty(clip))
            {
                tk2dSpriteAnimationClip clipDef = anim.GetClipByName(clip);
                if (clipDef == null)
                    return;
                // By clip object, not name: an animation library swap (animationLibraryOverride)
                // keeps the names and changes every clip.
                tk2dSpriteAnimationClip current = anim.CurrentClip;
                if (!ReferenceEquals(current, clipDef))
                {
                    if (!anim.enabled)
                        anim.enabled = true;
                    PlayAligned(anim, clipDef, clipFrame, elapsed, animating);
                    if (EntitySyncLog.On)
                    {
                        string prev = current != null ? current.name : "";
                        EntitySyncLog.Anim(entityId.ToString(),
                            "[ClientAnim] id=" + entityId + " clip " + prev + " → " + clip
                            + " frame=" + clipFrame + " +" + elapsed.ToString("F2") + "s anim=" + (animating ? 1 : 0), 0.35f);
                        // Hit / attack / react clip names are high-signal for reaction debug.
                        if (IsReactionClipName(clip))
                            EntitySyncLog.Reaction(entityId.ToString(),
                                "[ClientReact] id=" + entityId + " clip=" + clip
                                + " frame=" + clipFrame, 0.25f);
                    }
                }
                else if (!anim.Playing && animating && (ClipKind(clip) & (ClipKindReaction | ClipKindDeath)) == 0)
                {
                    // Same clip, finished, and the host keeps it going: vanilla's Play restart.
                    anim.Play(clipDef);
                }
            }
            else if (!anim.Playing)
            {
                Character ch = null;
                try
                {
                    ch = anim.GetComponent<Character>()
                        ?? anim.GetComponentInParent<Character>();
                }
                catch { /* dismantled */ }

                if (ch != null
                    && (ch.behaviour == Character.Behaviour.chasingTarget
                        || ch.behaviour == Character.Behaviour.escaping
                        || ch.behaviour == Character.Behaviour.running)
                    && anim.CurrentClip != null)
                    return;

                string idleClip = null;
                try
                {
                    if (ch != null)
                        idleClip = Traverse.Create(ch).Field("idleAni").GetValue<string>();
                }
                catch { /* ignore */ }

                if (!string.IsNullOrEmpty(idleClip) && anim.GetClipByName(idleClip) != null)
                {
                    anim.Play(idleClip);
                    if (EntitySyncLog.On)
                        EntitySyncLog.Anim(entityId.ToString(),
                            "[ClientAnim] id=" + entityId + " empty host clip → idle=" + idleClip, 2f);
                }
            }
        }

        /// <summary>
        /// Per frame: a once clip that finished here while the host keeps it going (Walk, Run,
        /// Idle, DefensiveLoop are once clips vanilla processAnims restarts with its every-frame
        /// Play) starts again, as tk2d Play restarts it there. A clip that ends and holds on the
        /// host (aim, a reaction, death) stays on its last frame.
        /// </summary>
        private static void ReplayFinishedClip(Character c, EntityInterpState state, float renderTime)
        {
            // Living, or down (PreDeath_Loop runs on the host's every-frame Play too).
            if (!state.downed && (!state.alive || !c.alive))
                return;
            tk2dSpriteAnimator body = ResolveBodyAnimator(c);
            if (body == null || !body.enabled || body.Playing)
                return;
            tk2dSpriteAnimationClip current = body.CurrentClip;
            if (current == null)
                return;
            if (!state.Timeline.LatestAt(renderTime, out TimelineSample s) || !s.Animating
                || !string.Equals(s.Clip, current.name, System.StringComparison.Ordinal))
                return;
            if ((ClipKind(current.name) & (ClipKindReaction | ClipKindDeath)) != 0)
                return;
            body.Play(current);
        }

        /// <summary>
        /// The presentation half of vanilla Character.Update, which a creature copy skips with
        /// the rest of its AI: legs under the body, the shadow's rotation and ground spot, and a
        /// flier's altitude (processAnims), scale, in-flight fade and sky shadow. Run after the
        /// shown pose is written, so they follow the body this frame.
        /// </summary>
        private static void PresentCharacterFrame(Character c, Rigidbody rb, float dt)
        {
            // Vanilla Update's own gate (a corpse is inactive).
            if (!c.isActive || c.dummy)
                return;
            Transform tr = c.transform;
            Vector3 bodyPos = rb != null ? rb.position : tr.position;

            tk2dSpriteAnimator legs = c.legsAnimator;
            if (legs != null)
                legs.transform.position = Core.getYPos(bodyPos, PosType.characterLegs, randomize: false) + c.posSeed;

            Transform shadow = c.shadow;
            if (shadow != null)
            {
                if (!c.rotateShadow)
                    shadow.eulerAngles = new Vector3(90f, 0f, 0f);
                Vector3 ground = tr.position + new Vector3(c.shadowOffset.x, 0f, c.shadowOffset.z);
                shadow.position = Core.getYPos(ground, c.dying || !c.alive ? PosType.low1 : PosType.characterShadow,
                    randomize: false) + c.posSeed;
            }

            Flier flier = c.flier;
            if (flier == null)
                return;
            // processAnims (living bodies): climb to 13, or dive to between 5 and 10.
            if (c.alive && flier.inFlight)
            {
                flier.altitude = flier.diving
                    ? Mathf.Clamp(flier.altitude - 30f * dt, 5f, 10f)
                    : Mathf.Clamp(flier.altitude + 10f * dt, 0f, 13f);
            }
            float alt = flier.altitude;
            tk2dBaseSprite sprite = c.sprite;
            if (sprite != null)
            {
                float k = 2f + alt / 10f;
                sprite.scale = new Vector3(k, k, k);
                if (flier.disappearWhenInFlight && alt > 11f)
                {
                    Color col = sprite.color;
                    sprite.color = new Color(col.r, col.g, col.b, col.a - 0.5f * dt);
                }
            }
            if (shadow != null)
            {
                shadow.position = bodyPos + new Vector3(alt * 2f, 5f, -alt * 2f);
                tk2dBaseSprite shadowSprite = shadow.GetComponent<tk2dBaseSprite>();
                if (shadowSprite != null)
                    shadowSprite.color = new Color(1f, 1f, 1f, 1f - alt / 10f);
            }
        }

        /// <summary>
        /// A creature copy's clip finished (Character.OnAniFinish, gated on copies). Vanilla's
        /// handler is mostly AI the host runs and sends (recover after an attack, run away, end
        /// a turn, summon, despawn on Hide, pause on Aim); only its presentation runs here: the
        /// defensive loop's random speed and the body laid down when its death clip ends
        /// (components dropped, corpse set up without vanilla's NPC save).
        /// </summary>
        internal static void OnCopyClipFinished(Character c)
        {
            tk2dSpriteAnimator anim = c.animator;
            tk2dSpriteAnimationClip clip = anim != null ? anim.CurrentClip : null;
            if (clip == null)
                return;
            string name = clip.name;

            if (name == "DefensiveLoop" && c.randomizeDefensiveAnimFPS)
            {
                clip.fps = Random.Range(18, 30);
                return;
            }
            if (name == "Cut_half")
            {
                if (c.dying)
                {
                    c.cutInHalf = true;
                    DestroyComponents2(c);
                }
                c.cuttingInHalf = false;
                return;
            }
            // Alive, or down with health left (pre-death): not a finished kill.
            if (c.alive || (c.hasPreDeath && c.Health > 0f))
                return;
            string deathAnim = DeathAnimRef(c);
            bool deathClip = (!string.IsNullOrEmpty(deathAnim) && (name == deathAnim || name == deathAnim + "_Water"))
                || name == "Death_doctor" || IsDeathClipName(name);
            if (!deathClip)
                return;
            DestroyComponents(c);
            DestroyComponents2(c);
            FinalizeClientCorpse(c);
        }

        /// <summary>
        /// Vanilla setDeathCollider for a creature copy: lootable corpse Item, death drop
        /// inventory, lying collider and ground height, inactive. Its NPC dead flag and save are
        /// the host's.
        /// </summary>
        internal static void FinalizeClientCorpse(Character c)
        {
            if (c == null) return;
            if (c.GetComponent<Item>() == null)
            {
                Item item = c.gameObject.AddComponent<Item>();
                item.name = c.name.ToLower() + "_corpse";
                if (c.searched)
                    item.searched = true;
            }
            if (c.inventory != null)
                c.inventory.invType = Inventory.InvType.deathDrop;
            ApplyDeathPose(c);
            c.isActive = false;
            ClearPendingCorpse(c);
        }

        private static bool IsLoopingWrap(tk2dSpriteAnimationClip.WrapMode wrapMode)
        {
            return wrapMode == tk2dSpriteAnimationClip.WrapMode.Loop
                || wrapMode == tk2dSpriteAnimationClip.WrapMode.LoopSection
                || wrapMode == tk2dSpriteAnimationClip.WrapMode.PingPong
                || wrapMode == tk2dSpriteAnimationClip.WrapMode.RandomLoop;
        }

        /// <summary>
        /// Show the host's kill without running vanilla die/die2.
        /// Those fire death triggers, night-spawner bookkeeping, and the trader ending.
        /// </summary>
        public static void PresentHostDeath(Character c, short entityId, string hostClip, short hostFrame,
            bool watched = true, float elapsed = 0f)
        {
            if (c == null) return;
            if (Player.Instance != null && c.gameObject == Player.Instance.gameObject)
                return;

            bool already = entityId != 0 && !c.alive && _deathAnimationPlayed.Contains(entityId);
            c.dying = true;
            c.alive = false;
            c.Health = 0f;
            c.immobilised = false;
            c.cuttingInHalf = string.Equals(hostClip, "Cut_half", System.StringComparison.OrdinalIgnoreCase);

            if (!c.dontSwitchColliderTriggerOnDeath && c.collider != null)
            {
                c.collider.isTrigger = true;
                c.collider.enabled = true;
            }

            if (c.hitCollider != null)
                UnityEngine.Object.Destroy(c.hitCollider);

            try
            {
                ParticleSystem[] particles = c.GetComponentsInChildren<ParticleSystem>();
                for (int i = 0; i < particles.Length; i++)
                {
                    if (particles[i] != null)
                        particles[i].Stop();
                }
            }
            catch { /* dismantled */ }

            if (c.AIpath != null)
                UnityEngine.Object.Destroy(c.AIpath);

            NoteClientDeathForCorpse(c);
            if (!already)
            {
                EnsureDeathAnimation(c, entityId, hostClip, hostFrame, watched, elapsed);
                EntitySyncLog.Event(() =>
                    "[ClientDeath] present " + c.name + "(id=" + entityId
                    + ") clip=" + (hostClip ?? ""));
            }

            // Shape uses the death clip that just started. Doing this earlier
            // matched the walk clip and left the standing collider.
            ApplyDeathColliderShape(c);
        }

        /// <summary>
        /// First fall for an enemy that gets back up. Vanilla die2 keeps them
        /// downed with health restored, not a finished corpse.
        /// </summary>
        public static void PresentHostDowned(Character c, short entityId, string hostClip)
        {
            if (c == null) return;
            c.alive = false;
            c.dying = true;
            c.startingPreDeath = string.IsNullOrEmpty(hostClip)
                || string.Equals(hostClip, "PreDeath_Start", System.StringComparison.Ordinal);
            if (c.Health <= 0f)
            {
                if (c.preDeathMaxHealth > 0f)
                    c.Health = c.preDeathMaxHealth;
                else
                    c.Health = c.maxHealth > 0.01f ? c.maxHealth * 0.01f : 1f;
            }
            c.isActive = true;
            _deathAnimationPlayed.Remove(entityId);
            _localDeathSoundPlayed.Remove(entityId);
            ClearPendingCorpse(c);
            RemovePresentationCorpseItem(c);
            if (c.gameObject != null && !c.gameObject.activeSelf)
                c.gameObject.SetActive(true);
        }

        /// <summary>
        /// Host says this body is alive again (stood up from pre-death, or a
        /// finished kill the host brought back). Drop the local corpse shell.
        /// </summary>
        public static void PresentHostRevive(Character c, short entityId)
        {
            if (c == null) return;
            c.alive = true;
            c.dying = false;
            c.startingPreDeath = false;
            c.cuttingInHalf = false;
            c.isActive = true;
            if (c.collider != null && !c.dontSwitchColliderTriggerOnDeath)
                c.collider.isTrigger = false;
            _deathAnimationPlayed.Remove(entityId);
            _localDeathSoundPlayed.Remove(entityId);
            ClearPendingCorpse(c);
            RemovePresentationCorpseItem(c);
            if (c.gameObject != null && !c.gameObject.activeSelf)
                c.gameObject.SetActive(true);
        }

        /// <summary>
        /// Lying-down collider and ground height. Loot and the dead flag stay on the host.
        /// Opening the body requests the host inventory.
        /// </summary>
        public static void ApplyDeathPose(Character c)
        {
            if (c == null) return;
            ApplyDeathColliderShape(c);
        }

        private static void ApplyDeathColliderShape(Character c)
        {
            try { Traverse.Create(c).Method("setYPosAfterDeath").GetValue(); }
            catch { /* ignore */ }

            tk2dSpriteAnimator anim = ResolveBodyAnimator(c);
            string clipName = anim != null && anim.CurrentClip != null ? anim.CurrentClip.name : null;
            if (c.capsuleCollider != null && c.deathColliders != null && !string.IsNullOrEmpty(clipName))
            {
                for (int i = 0; i < c.deathColliders.Count; i++)
                {
                    ColliderInfo info = c.deathColliders[i];
                    if (info != null && info.identifier == clipName)
                    {
                        c.capsuleCollider.center = info.center;
                        c.capsuleCollider.radius = info.radius;
                        c.capsuleCollider.height = info.height;
                        break;
                    }
                }
            }

            if (!c.dontSwitchColliderTriggerOnDeath && c.collider != null)
            {
                c.collider.isTrigger = true;
                c.collider.enabled = true;
            }
        }

        private static void RemovePresentationCorpseItem(Character c)
        {
            Item corpse = c.GetComponent<Item>();
            if (corpse == null || corpse.name == null) return;
            if (!corpse.name.EndsWith("_corpse", System.StringComparison.Ordinal))
                return;
            UnityEngine.Object.Destroy(corpse);
        }

        private static bool IsReactionClipName(string clip) => (ClipKind(clip) & ClipKindReaction) != 0;

        public static void ApplyHostDespawn(short entityId)
        {
            if (entityId == 0) return;

            Character c = CharacterTracker.FindByStableId(entityId);
            // Host removeMe destroyed the object. A local corpse shell is not a
            // reason to keep a ghost the host no longer has.

            // Full untrack immediately: ClearId alone left the dying Character in
            // _characters so FindByPositionAndName could claim it for a new nearby
            // same-name spawn (crow/rabbit) while Destroy is still deferred.
            if (c != null)
                CharacterTracker.Remove(c);

            _states.Remove(entityId);
            _displayPositions.Remove(entityId);
            _displayRotations.Remove(entityId);
            _hostSyncedIds.Remove(entityId);
            _spawnedPhantomIds.Remove(entityId);
            _everHostSyncedIds.Remove(entityId);
            _audioStoppedIds.Remove(entityId);
            _deathAnimationPlayed.Remove(entityId);
            _localHitsAwaitingEcho.Remove(entityId);
            _localDeathSoundPlayed.Remove(entityId);
            _recentlyDespawnedUntil[entityId] = Time.unscaledTime + DespawnSnapshotIgnoreSec;
            // A recycled id is a different body; the host re-sends its descriptor.
            ForgetSaveIdOwner(entityId);
            _descriptors.Remove(entityId);

            // Drop pending match rows for this host id (would otherwise claim a twin).
            for (int i = _pendingMatches.Count - 1; i >= 0; i--)
            {
                if (_pendingMatches[i].HostId == entityId)
                    _pendingMatches.RemoveAt(i);
            }

            if (c != null && c.gameObject != null)
            {
                // Its loop is pooled: stop it before the body goes, as vanilla removeMe does.
                Audio.EntityLoopSync.Stop(c);
                EntitySyncLog.Event(() =>
                    "[ClientDespawn] id=" + entityId + " " + c.name);
                Object.Destroy(c.gameObject);
            }
        }

        private static void EnsureEntityAwake(Character c)
        {
            if (c == null || NightVillage.IsHidden(c.gameObject)) return;

            GameObject go = c.gameObject;
            bool isCorpse = !c.alive || c.GetComponent<Item>() != null;
            // Fast path: already fully live; skip repeated GetComponent calls.
            // Corpses keep isActive=false after TickClientCorpseSetup; do not force-revive.
            // A shown body's alpha is its own: a flier that vanishes in flight fades it to 0
            // (vanilla Character.Update), and setting it back each snapshot flashed the bird.
            if (go.activeSelf && c.enabled && (isCorpse || c.isActive) && c.sprite != null)
                return;

            if (!go.activeSelf)
                go.SetActive(true);

            if (!c.enabled)
                c.enabled = true;

            if (!isCorpse && !c.isActive)
                c.isActive = true;

            tk2dSpriteAnimator anim = c.GetComponent<tk2dSpriteAnimator>();
            if (anim != null && !anim.enabled)
                anim.enabled = true;

            tk2dBaseSprite sprite = c.sprite ?? c.GetComponent<tk2dBaseSprite>();
            if (sprite != null)
            {
                Renderer r = sprite.GetComponent<Renderer>();
                if (r != null && !r.enabled)
                    r.enabled = true;

                // A body woken from an inactive node shows, unless it is a flier gone in flight.
                Color col = sprite.color;
                bool goneInFlight = c.flier != null && c.flier.disappearWhenInFlight && c.flier.inFlight;
                if (col.a <= 0f && !goneInFlight)
                    sprite.color = new Color(col.r, col.g, col.b, 1f);
            }

            // Rigidbody left non-kinematic so the client player collides with entities via physics.
            // Host snapshots drive the pose every frame (WriteShownPose: transform + body).
        }

    }
}
