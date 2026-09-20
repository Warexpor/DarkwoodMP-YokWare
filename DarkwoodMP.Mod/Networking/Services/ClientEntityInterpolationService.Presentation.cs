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
                ModRuntime.LegacyInfo($"[Entity] death anim missing for {c.name}(id={entityId}) hostClip={hostClip}");
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
            ModRuntime.LegacyInfo($"[Entity] death anim Play({deathClip}) on {c.name}(id={entityId})");
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
                bool clipChanged = anim.CurrentClip == null || anim.CurrentClip.name != clip;
                // After SetActive(false)→true, CurrentClip name can stick while Playing=false
                // → floaty roam sprites until clip name changes (aggro). Restart if stopped.
                if ((clipChanged || !anim.Playing) && anim.GetClipByName(clip) != null)
                {
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
                }
                // Alive, same clip, and already playing: leave natural playback
                // running.
            }
            else if (!anim.Playing)
            {
                string idleClip = null;
                try
                {
                    Character ch = anim.GetComponent<Character>()
                        ?? anim.GetComponentInParent<Character>();
                    if (ch != null)
                        idleClip = Traverse.Create(ch).Field("idleAni").GetValue<string>();
                }
                catch { /* ignore */ }

                if (!string.IsNullOrEmpty(idleClip) && anim.GetClipByName(idleClip) != null)
                    anim.Play(idleClip);
            }
        }

        public static void ApplyHostDespawn(short entityId)
        {
            if (entityId == 0) return;

            Character c = CharacterTracker.FindByStableId(entityId);
            // Never despawn lootable corpses via removeMe echo.
            if (c != null && (!c.alive || c.GetComponent<Item>() != null))
                return;

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
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Entity] host despawn: {c.name}(id={entityId})");
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
