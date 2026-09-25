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

        private static void ApplyEntityPresentation(Character c, short entityId, string clip, short clipFrame, bool alive)
        {
            if (c == null) return;

            tk2dSpriteAnimator body = ResolveBodyAnimator(c);

            if (!alive)
            {
                // Always ensure death presentation (covers pending-match path + late packets).
                EnsureDeathAnimation(c, entityId, clip, clipFrame);
                return;
            }

            ApplyClipToAnimator(body, entityId, clip, clipFrame, alive: true, trackDeath: false);

            // Dogs and NPCs link animator.legs to legsAnimator; body Play drives both.
            // Dual-Play(bodyClip) on legs freezes walk cycles (floaty roam until aggro).
            tk2dSpriteAnimator legs = null;
            try { legs = c.legsAnimator; } catch { /* dismantled */ }
            if (legs != null && legs != body)
            {
                bool linked = false;
                try { linked = body != null && body.legs == legs; } catch { /* no .legs */ }
                if (!linked)
                    ApplyClipToAnimator(legs, entityId, clip, clipFrame, alive: true, trackDeath: false);
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

        /// <summary>
        /// True if clip name is a real death / cut-in-half presentation (not idle/walk).
        /// </summary>
        private static bool IsDeathClipName(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return false;
            if (clip.IndexOf("Death", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (clip.Equals("Cut_half", System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (clip.Equals("BeartrapDeath", System.StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static string ResolveDeathClipName(Character c, tk2dSpriteAnimator anim, string hostClip)
        {
            if (anim == null) return null;

            if (IsDeathClipName(hostClip) && anim.GetClipByName(hostClip) != null)
                return hostClip;

            try
            {
                string deathAnim = Traverse.Create(c).Field("deathAnim").GetValue<string>();
                if (string.IsNullOrEmpty(deathAnim))
                {
                    Traverse.Create(c).Method("getDeathAnims").GetValue();
                    deathAnim = Traverse.Create(c).Field("deathAnim").GetValue<string>();
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
        /// </summary>
        private static void EnsureDeathAnimation(Character c, short entityId, string hostClip, short hostFrame)
        {
            if (c == null) return;
            if (_deathAnimationPlayed.Contains(entityId)) return;

            tk2dSpriteAnimator body = ResolveBodyAnimator(c);
            if (body == null) return;

            if (!body.enabled)
                body.enabled = true;

            string deathClip = ResolveDeathClipName(c, body, hostClip);
            if (string.IsNullOrEmpty(deathClip))
            {
                EntitySyncLog.Event(() =>
                    "[ClientDeathAnim] MISSING for " + c.name + "(id=" + entityId
                    + ") hostClip=" + (hostClip ?? ""));
                // Still mark so we don't spam; corpse stays lootable via die() patch.
                _deathAnimationPlayed.Add(entityId);
                return;
            }

            body.Play(deathClip);
            // Mid-death join: snap to host frame. Fresh kill: play from start.
            if (IsDeathClipName(hostClip) && hostFrame > 0 && body.CurrentClip != null)
            {
                int maxFrame = body.CurrentClip.frames.Length - 1;
                if (maxFrame >= 0)
                    body.SetFrame(Mathf.Clamp(hostFrame, 0, maxFrame), false);
            }

            _deathAnimationPlayed.Add(entityId);
            EntitySyncLog.Event(() =>
                "[ClientDeathAnim] Play(" + deathClip + ") on " + c.name + "(id=" + entityId
                + ") hostClip=" + (hostClip ?? "") + " frame=" + hostFrame);
        }

        private static void ApplyClipToAnimator(
            tk2dSpriteAnimator anim, short entityId, string clip, short clipFrame, bool alive, bool trackDeath)
        {
            if (anim == null) return;

            // Dead entities are handled exclusively by EnsureDeathAnimation.
            if (!alive)
                return;

            if (!string.IsNullOrEmpty(clip))
            {
                tk2dSpriteAnimationClip clipDef = anim.GetClipByName(clip);
                bool clipChanged = anim.CurrentClip == null || anim.CurrentClip.name != clip;
                bool looping = clipDef != null && IsLoopingWrap(clipDef.wrapMode);
                // Replay a loop that stopped after the object woke. One-shots
                // (hit, attack) stay on the last frame until the host changes clip.
                if (clipDef != null && (clipChanged || (!anim.Playing && looping)))
                {
                    string prev = anim.CurrentClip != null ? anim.CurrentClip.name : "";
                    bool wasPlaying = anim.Playing;
                    if (!anim.enabled)
                        anim.enabled = true;
                    anim.Play(clip);

                    // Align only at clip boundaries (start of attack/hitreact).
                    if (clipChanged && clipFrame >= 0 && anim.CurrentClip != null)
                    {
                        int maxFrame = anim.CurrentClip.frames.Length - 1;
                        if (maxFrame >= 0)
                            anim.SetFrame(Mathf.Clamp(clipFrame, 0, maxFrame), false);
                    }

                    if (clipChanged)
                    {
                        EntitySyncLog.Anim(entityId.ToString(),
                            "[ClientAnim] id=" + entityId + " clip " + prev + " → " + clip
                            + " frame=" + clipFrame + " wasPlaying=" + wasPlaying, 0.35f);
                        // Hit / attack / react clip names are high-signal for reaction debug.
                        if (IsReactionClipName(clip))
                            EntitySyncLog.Reaction(entityId.ToString(),
                                "[ClientReact] id=" + entityId + " clip=" + clip
                                + " frame=" + clipFrame, 0.25f);
                    }
                }
                // Alive, same clip, and already playing: leave natural playback
                // running.
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
                    EntitySyncLog.Anim(entityId.ToString(),
                        "[ClientAnim] id=" + entityId + " empty host clip → idle=" + idleClip, 2f);
                }
            }
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
        public static void PresentHostDeath(Character c, short entityId, string hostClip, short hostFrame)
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
                EnsureDeathAnimation(c, entityId, hostClip, hostFrame);
                NoteLocalDeathPresentation(c, entityId);
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

        private static bool IsReactionClipName(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return false;
            if (clip.StartsWith("Hit", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (clip.StartsWith("Attack", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (clip.IndexOf("React", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (clip.IndexOf("Stun", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (clip.IndexOf("Flinch", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static void ApplyHostDespawn(short entityId)
        {
            if (entityId == 0) return;

            Character c = CharacterTracker.FindByStableId(entityId);
            // Host removeMe destroyed the object. A local corpse shell is not a
            // reason to keep a ghost the host no longer has.

            _states.Remove(entityId);
            _displayPositions.Remove(entityId);
            _displayRotations.Remove(entityId);
            _hostSyncedIds.Remove(entityId);
            _spawnedPhantomIds.Remove(entityId);
            _everHostSyncedIds.Remove(entityId);
            _audioStoppedIds.Remove(entityId);
            _deathAnimationPlayed.Remove(entityId);
            _localHitEchoIgnoreUntil.Remove(entityId);
            _localDeathSoundPlayed.Remove(entityId);

            if (c != null && c.gameObject != null)
            {
                EntitySyncLog.Event(() =>
                    "[ClientDespawn] id=" + entityId + " " + c.name);
                Object.Destroy(c.gameObject);
            }
        }

        private static void EnsureEntityAwake(Character c)
        {
            if (c == null) return;

            GameObject go = c.gameObject;
            bool isCorpse = !c.alive || c.GetComponent<Item>() != null;
            // Fast path: already fully live; skip repeated GetComponent calls.
            // Corpses keep isActive=false after TickClientCorpseSetup; do not force-revive.
            if (go.activeSelf && c.enabled && (isCorpse || c.isActive))
            {
                tk2dBaseSprite sp = c.sprite;
                if (sp != null)
                {
                    Color col = sp.color;
                    if (col.a > 0f)
                        return;
                    sp.color = new Color(col.r, col.g, col.b, 1f);
                    return;
                }
                // No sprite reference yet; fall through once to wire animation and renderer.
            }

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

                Color col = sprite.color;
                if (col.a <= 0f)
                    sprite.color = new Color(col.r, col.g, col.b, 1f);
            }

            // Rigidbody left non-kinematic so the client player can push entities via physics.
            // Host snapshots drive position via Rigidbody.MovePosition, which respects collisions.
        }

    }
}
