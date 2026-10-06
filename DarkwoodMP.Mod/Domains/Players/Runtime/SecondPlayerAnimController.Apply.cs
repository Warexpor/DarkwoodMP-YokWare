using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Drives torso and leg animations for the second player (local co-op or remote proxy) based on network snapshots.
    /// </summary>
    public sealed partial class SecondPlayerAnimController : MonoBehaviour
    {

        /// <summary>
        /// Applies a full animation snapshot from the network, updating torso, legs, facing, and flip.
        /// </summary>
        public void ApplyNetworkSnapshot(
            LocomotionState state,
            bool flipX,
            short legFacingY,
            bool reverseLegs,
            short torsoFacingY,
            string torsoClip,
            string legsClip,
            short currentFrame = -1)
        {
            _networkLegFacingY = legFacingY;
            _networkReverseLegs = reverseLegs;
            _hasNetworkLegFacing = true;

            if (flipX != _flipX)
                SetFlipX(flipX);

            bool wasMoving = _state == LocomotionState.Walk || _state == LocomotionState.Run;
            _state = state;
            bool isMoving = state == LocomotionState.Walk || state == LocomotionState.Run;

            // Vanilla Player.setLegsFPS: walk clips run at 10fps while dragging, 17fps otherwise.
            // Detect drag from torso Pushing* clips (same as local ProcessAnims).
            bool dragging = !string.IsNullOrEmpty(torsoClip)
                && (torsoClip.IndexOf("Pushing", System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (!dragging && _torsoAnimator != null && _torsoAnimator.CurrentClip != null)
            {
                string cur = _torsoAnimator.CurrentClip.name;
                if (!string.IsNullOrEmpty(cur)
                    && cur.IndexOf("Pushing", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    dragging = true;
            }
            SetLegsWalkFps(dragging);

            bool hideLegs = ShouldHideLegsForTorso(torsoClip);

            if (!string.IsNullOrEmpty(torsoClip))
            {
                PlayTorso(torsoClip);
                // Sync animation frame if provided
                if (currentFrame >= 0 && _torsoAnimator != null && _torsoAnimator.CurrentClip != null && currentFrame < _torsoAnimator.CurrentClip.frames.Length)
                    _torsoAnimator.SetFrame(currentFrame);
            }
            else if (state == LocomotionState.Idle)
            {
                // Don't interrupt transient non-looping clips (e.g. Hit1/Hit2
                // from PlayerAnimationMessage); let them play to completion.
                bool transientPlaying = _torsoAnimator != null && _torsoAnimator.Playing
                    && _torsoAnimator.CurrentClip != null
                    && _torsoAnimator.CurrentClip.wrapMode == tk2dSpriteAnimationClip.WrapMode.Once;
                if (!transientPlaying)
                    PlayTorso("Idle");
                else if (_torsoAnimator.CurrentClip != null)
                    hideLegs = ShouldHideLegsForTorso(_torsoAnimator.CurrentClip.name);
            }
            else
                ApplyLocomotion(state, Vector3.zero, legFacingY, reverseLegs);

            // Vanilla ProcessAnims: when jumping / beartrap / dodge / etc., legs
            // renderer is disabled and walk clips must not keep playing on the proxy.
            if (hideLegs)
            {
                SetLegsHidden(true);
            }
            else if (!string.IsNullOrEmpty(legsClip))
            {
                SetLegsHidden(false);
                PlayLegs(legsClip);
            }
            else if (state == LocomotionState.Walk)
            {
                SetLegsHidden(false);
                PlayLegs(reverseLegs ? "LegsWalkReverse" : "LegsWalk");
            }
            else if (state == LocomotionState.Run)
            {
                SetLegsHidden(false);
                PlayLegs("LegsRun");
            }
            else if (wasMoving && !isMoving && _legsAnimator != null && _legsAnimator.Playing)
            {
                // Walk clips reach FeetNeutral naturally and are stopped by
                // OnLegsAnimationEvent.  Run clips have no FeetNeutral event
                // so we must stop them immediately.
                if (_legsAnimator.CurrentClip != null &&
                    _legsAnimator.CurrentClip.name.IndexOf("Run") >= 0)
                {
                    StopLegsAtNeutral();
                    _feetNeutralReached = true;
                    if (_legsAnimator != null)
                        _legsAnimator.transform.rotation = transform.rotation;
                }
            }

            if (state == LocomotionState.Walk)
            {
                _feetNeutralReached = false;
                AlignLegsToFacing(legFacingY, snapRunToBody: false);
            }
            else if (state == LocomotionState.Run)
            {
                _feetNeutralReached = false;
                AlignLegsToFacing(legFacingY, snapRunToBody: true);
            }
        }

        public void ResetDeathState()
        {
            _deathClipPlayed = false;
        }

        /// <summary>
        /// Immediate death pose on PlayerDied (before next PlayerState tick).
        /// </summary>
        public void PlayDeathClip(string clipName = "Death1")
        {
            if (string.IsNullOrEmpty(clipName))
                clipName = "Death1";
            // Allow one play even if a prior death clip finished this session.
            if (clipName == "Death1" || clipName == "Death2")
                _deathClipPlayed = false;
            PlayTorso(clipName);
        }

        private void ApplyLocomotion(
            LocomotionState state,
            Vector3 velocity,
            short legFacingY,
            bool reverseLegs)
        {
            _state = state;

            if (Mathf.Abs(velocity.x) > 0.01f)
                SetFlipX(velocity.x < 0f);

            switch (state)
            {
                case LocomotionState.Run:
                    PlayTorso("Run");
                    PlayLegs("LegsRun");
                    AlignLegsToFacing(legFacingY, snapRunToBody: true);
                    break;

                case LocomotionState.Walk:
                    PlayTorso("Idle");
                    PlayLegs(reverseLegs ? "LegsWalkReverse" : "LegsWalk");
                    AlignLegsToFacing(legFacingY, snapRunToBody: false);
                    break;

                default:
                    PlayTorso("Idle");
                    break;
            }
        }

        /// <summary>
        /// Smoothly rotates legs back to align with the body rotation when idling.
        /// </summary>
        public void ResetLegsToStanding()
        {
            if (_legsAnimator == null)
                return;

            Quaternion target = Quaternion.Euler(90f, transform.eulerAngles.y, 0f);
            _legsAnimator.transform.rotation = Quaternion.Slerp(
                _legsAnimator.transform.rotation,
                target,
                Time.deltaTime * LegBlendSpeed);
        }

        private void AlignLegsToFacing(short legFacingY, bool snapRunToBody)
        {
            if (_legsAnimator == null)
                return;

            if (snapRunToBody)
            {
                _legsAnimator.transform.rotation = transform.rotation;
                return;
            }

            float y = _hasNetworkLegFacing ? legFacingY : transform.eulerAngles.y;
            _legsAnimator.transform.rotation = Quaternion.Euler(90f, y, 0f);
        }

        internal void PlayTorso(string clipName)
        {
            if (_torsoAnimator == null || string.IsNullOrEmpty(clipName))
                return;

            if (_torsoAnimator.GetClipByName(clipName) == null)
            {
                // Fall back to the "None" animation library if the clone doesn't have the clip
                if (_noneAnimsLib != null && _noneAnimsLib.GetClipByName(clipName) != null)
                {
                    _torsoAnimator.Library = _noneAnimsLib;
                }
                else
                {
                    return;
                }
            }

            // Only skip if the animator is already actively playing this clip.
            // Allows replay when a non-looping clip finishes and restarts
            // (for example, double-barrel reload loop; the same clip plays twice).
            if (_torsoAnimator.Playing && _torsoAnimator.CurrentClip?.name == clipName)
                return;

            // Prevent replay of death clips once played to completion;
            // the host sends "Death1"/"Death2" every 30ms in PlayerStateMessage,
            // but the non-looping clip finishes and restarts endlessly.
            if ((clipName == "Death1" || clipName == "Death2") && _deathClipPlayed)
                return;

            _torsoAnimator.Play(clipName);
            UpdateLegVisibility(clipName);

            if (clipName == "Death1" || clipName == "Death2")
                _deathClipPlayed = true;
        }

        /// <summary>
        /// Vanilla Player.ProcessAnims (non-locomotion branch) disables legs renderer
        /// for vault, beartrap, dodge, crawl, death, etc. Mirror that on the proxy.
        /// </summary>
        internal static bool ShouldHideLegsForTorso(string torsoClipName)
        {
            if (string.IsNullOrEmpty(torsoClipName)) return false;
            if (torsoClipName == "Idle" || torsoClipName == "Run") return false;
            if (torsoClipName.IndexOf("Pushing", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (torsoClipName.IndexOf("Walk", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            // Explicit vanilla specials + prefixes
            if (torsoClipName == "JumpWindow"
                || torsoClipName == "Dodge"
                || torsoClipName == "Sleep"
                || torsoClipName == "Fight"
                || torsoClipName == "Death1"
                || torsoClipName == "Death2"
                || torsoClipName == "DeathFake"
                || torsoClipName == "WeaponStuck"
                || torsoClipName == "PetDog"
                || torsoClipName == "DiveIn"
                || torsoClipName == "DiveOut"
                || torsoClipName == "GetUpFromBed"
                || torsoClipName == "BeartrapStart"
                || torsoClipName == "BeartrapLoop"
                || torsoClipName == "BeartrapStop"
                || torsoClipName.StartsWith("Crawl", System.StringComparison.Ordinal)
                || torsoClipName.StartsWith("Inventory", System.StringComparison.Ordinal)
                || torsoClipName.StartsWith("Beartrap", System.StringComparison.Ordinal))
                return true;

            return false;
        }

        private void UpdateLegVisibility(string torsoClipName)
        {
            SetLegsHidden(ShouldHideLegsForTorso(torsoClipName));
        }

        private void SetLegsHidden(bool hide)
        {
            if (_legsRenderer != null)
                _legsRenderer.enabled = !hide;
            if (hide && _legsAnimator != null && _legsAnimator.Playing)
            {
                StopLegsAtNeutral();
                _feetNeutralReached = true;
            }
        }

        /// <summary>
        /// Stop the legs on their standing frame (the walk clip's FeetNeutral), not wherever the
        /// step was: a stop mid-step (caught in a bear trap while walking, a vault, a run ending)
        /// showed the proxy standing with one leg forward until the player walked again. Vanilla
        /// keeps the hidden legs cycling until FeetNeutral, so they come back standing.
        /// </summary>
        private void StopLegsAtNeutral()
        {
            tk2dSpriteAnimationClip clip = _legsAnimator.CurrentClip;
            int frame = NeutralFrame(clip);
            if (frame < 0)
            {
                clip = _legsAnimator.GetClipByName(_networkReverseLegs ? "LegsWalkReverse" : "LegsWalk")
                    ?? _legsAnimator.GetClipByName("LegsWalk");
                frame = NeutralFrame(clip);
            }
            if (frame >= 0)
                _legsAnimator.PlayFromFrame(clip, frame);
            _legsAnimator.Stop();
        }

        private static int NeutralFrame(tk2dSpriteAnimationClip clip)
        {
            if (clip == null || clip.frames == null)
                return -1;
            for (int i = 0; i < clip.frames.Length; i++)
            {
                if (clip.frames[i] != null && clip.frames[i].eventInfo == "FeetNeutral")
                    return i;
            }
            return -1;
        }

        internal void PlayLegs(string clipName)
        {
            if (_legsAnimator == null || string.IsNullOrEmpty(clipName))
                return;

            if (_legsAnimator.GetClipByName(clipName) == null)
            {
                // Fall back to normal walk animation if reverse walk clip is missing
                if (clipName == "LegsWalkReverse" && _legsAnimator.GetClipByName("LegsWalk") != null)
                    clipName = "LegsWalk";
                else
                    return;
            }

            // Always call Play: tk2d's already-playing path refreshes clipFps from
            // the library without restarting the clip. An early return left the
            // instance stuck at the previous rate (often too slow after drag).
            _legsAnimator.Play(clipName);
            ApplyLegsClipFps();
        }

        /// <summary>
        /// Mirrors Player.setLegsFPS rates (10 drag / 17 walk) on the *proxy
        /// animator only*. Do not mutate shared library clip.fps; the local
        /// Player uses the same tk2dSpriteAnimation assets, and mutating them
        /// made one peer's drag rate bleed into the other.
        /// </summary>
        private void SetLegsWalkFps(bool dragging)
        {
            _legsDragFps = dragging;
            ApplyLegsClipFps();
        }

        private void ApplyLegsClipFps()
        {
            if (_legsAnimator == null) return;
            float fps = _legsDragFps ? 10f : 17f;
            // ClipFps only applies when a clip is current; PlayLegs re-calls after Play.
            if (_legsAnimator.CurrentClip == null) return;
            string n = _legsAnimator.CurrentClip.name;
            if (n == "LegsWalk" || n == "LegsWalkReverse")
                _legsAnimator.ClipFps = fps;
        }

        private void StopTorso()
        {
            if (_torsoAnimator == null)
                return;
            _torsoAnimator.Stop();
        }

        private void SetFlipX(bool flip)
        {
            _flipX = flip;
            if (_torsoSprite != null)
                _torsoSprite.FlipX = flip;
        }
    }
}
