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
            SetLegHeading(legFacingY);
            _networkReverseLegs = reverseLegs;

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
                if (IsGetUpClip(torsoClip))
                    FollowGetUp(torsoClip, currentFrame);
                else
                {
                    EndStaleGetUp();
                    bool started = PlayTorso(torsoClip);
                    FollowTorsoFrame(torsoClip, currentFrame, started);
                }
            }
            else if (state == LocomotionState.Idle)
            {
                EndStaleGetUp();
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
                ApplyLocomotion(state, Vector3.zero, reverseLegs);

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
                    StopLegsAtNeutral();
            }
        }

        /// <summary>The copy's clip this many frames off its player's is put back on the player's frame.</summary>
        private const int TorsoFrameDrift = 2;

        /// <summary>
        /// The copy's torso clip runs on the player's frames. A clip the player starts part-way
        /// starts there too: vanilla plays a cancelled swing's AimReturn from the frame the windup
        /// had reached (Player.setLastClipFrame), and the copy started it from its first frame,
        /// the fully raised arm, so a swing let go at once snapped up and back down. tk2d's SetFrame,
        /// used before, shows a frame for one update and leaves the clip time alone. A running clip
        /// is put back on the player's frame only when it has drifted (packets come about 30 a
        /// second, a frame is longer): the copy plays smoothly on its own clock in between.
        /// </summary>
        private void FollowTorsoFrame(string clipName, short senderFrame, bool started)
        {
            if (senderFrame < 0 || _torsoAnimator == null || _torsoAnimator.CurrentClip == null
                || _torsoAnimator.CurrentClip.name != clipName)
                return;
            tk2dSpriteAnimationClip clip = _torsoAnimator.CurrentClip;
            int len = clip.frames != null ? clip.frames.Length : 0;
            if (len <= 1 || senderFrame >= len)
                return;
            if (started)
            {
                if (senderFrame > 0)
                    _torsoAnimator.PlayFromFrame(clip, senderFrame);
                return;
            }
            int off = _torsoAnimator.CurrentFrame - senderFrame;
            if (clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.Loop)
            {
                off = ((off % len) + len) % len;
                if (off > len / 2)
                    off -= len;
            }
            if (Mathf.Abs(off) >= TorsoFrameDrift)
                _torsoAnimator.PlayFromFrame(clip, senderFrame);
        }

        /// <summary>Frames a stand-up may still run on its own after its player left the clip.</summary>
        private const int GetUpTailFrames = 3;
        /// <summary>Packets without the stand-up clip before the copy takes its player to have left it.</summary>
        private const int GetUpMissPackets = 3;
        private int _getUpMisses;

        /// <summary>
        /// A stand-up shows exactly the frame its player is on: the copy's clip is kept paused
        /// and put on the player's frame with every packet (30 a second for a 15 fps clip).
        /// tk2d's SetFrame only shows a sprite for one frame and leaves the clip time alone, so
        /// a stand-up "started at the sender's frame" still ran from its own start, late by as
        /// long as the first packet was, and was cut when the player, already up, walked off.
        /// Run on the copy's own clock and only corrected, it drifted both ways behind a player
        /// whose frames come in bursts (a host that has just loaded).
        /// </summary>
        private void FollowGetUp(string clipName, short senderFrame)
        {
            _getUpMisses = 0;
            PlayTorso(clipName);
            if (_torsoAnimator == null || _torsoAnimator.CurrentClip == null
                || _torsoAnimator.CurrentClip.name != clipName)
                return;
            tk2dSpriteAnimationClip clip = _torsoAnimator.CurrentClip;
            if (senderFrame < 0)
            {
                // No frame to follow: the clip runs on its own.
                if (_torsoAnimator.Paused) _torsoAnimator.Resume();
                return;
            }

            int frame = Mathf.Min(senderFrame, clip.frames.Length - 1);
            if (!_torsoAnimator.Playing || _torsoAnimator.CurrentFrame != frame)
                _torsoAnimator.PlayFromFrame(clip, frame);
            _torsoAnimator.Pause();
        }

        /// <summary>
        /// The player is no longer in a stand-up this copy is still showing: the last frames
        /// run out, more than that is dropped. One packet without the clip is not the end of it.
        /// </summary>
        private void EndStaleGetUp()
        {
            if (_torsoAnimator == null || !_torsoAnimator.Playing || _torsoAnimator.CurrentClip == null
                || !IsGetUpClip(_torsoAnimator.CurrentClip.name))
                return;
            if (!_torsoAnimator.Paused)
                return; // already running out its tail
            if (++_getUpMisses < GetUpMissPackets)
                return;
            _getUpMisses = 0;
            _torsoAnimator.Resume();
            int left = _torsoAnimator.CurrentClip.frames.Length - 1 - _torsoAnimator.CurrentFrame;
            if (left > GetUpTailFrames)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[GetUp] " + name + ": player left the stand-up, copy dropped "
                        + left + " frames from its end");
                _torsoAnimator.Stop();
            }
        }

        public void ResetDeathState()
        {
            _deathClipPlayed = false;
        }

        /// <summary>Vanilla Player death clips (Death1_Water when dying in water).</summary>
        internal static bool IsDeathClip(string clipName)
            => clipName == "Death1" || clipName == "Death2" || clipName == "Death1_Water";

        /// <summary>
        /// Immediate death pose on PlayerDied (before next PlayerState tick).
        /// </summary>
        public void PlayDeathClip(string clipName = "Death1")
        {
            if (string.IsNullOrEmpty(clipName))
                clipName = "Death1";
            // The player's own state usually brings the death clip first (it travels faster than
            // PlayerDied): that play is this death's one play. Re-arming here played it twice.
            if (_deathClipPlayed)
                return;
            PlayTorso(clipName);
        }

        private void ApplyLocomotion(
            LocomotionState state,
            Vector3 velocity,
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
                    break;

                case LocomotionState.Walk:
                    PlayTorso("Idle");
                    PlayLegs(reverseLegs ? "LegsWalkReverse" : "LegsWalk");
                    break;

                default:
                    PlayTorso("Idle");
                    break;
            }
        }

        /// <summary>
        /// The peer's torso library changed (weapon, or a new look: the doctor dream's antagonist,
        /// the epilogue clothes, naked). Vanilla <c>switchAniLibrary</c> picks the legs and the
        /// bare-handed library from the same look, so they follow from the torso's name. The
        /// running clips are replayed from the new library at the same frame: tk2d keeps playing
        /// the old clip object, so a player standing still kept the old look until it moved.
        /// </summary>
        internal void ApplyTorsoLibrary(tk2dSpriteAnimation lib)
        {
            if (_torsoAnimator == null || lib == null)
                return;
            SkinLibraries(lib.name, out string noneName, out string legsName);
            var none = Resources.Load(noneName, typeof(tk2dSpriteAnimation)) as tk2dSpriteAnimation;
            if (none != null)
                _noneAnimsLib = none;

            if (_legsAnimator != null)
            {
                var legs = Resources.Load(legsName, typeof(tk2dSpriteAnimation)) as tk2dSpriteAnimation;
                if (legs != null && _legsAnimator.Library != legs)
                    SwapLibraryKeepClip(_legsAnimator, legs);
            }
            SwapLibraryKeepClip(_torsoAnimator, lib);
            if (_legsAnimator != null)
                ApplyLegsClipFps();
        }

        private static void SwapLibraryKeepClip(tk2dSpriteAnimator anim, tk2dSpriteAnimation lib)
        {
            string clip = anim.CurrentClip != null ? anim.CurrentClip.name : null;
            int frame = anim.CurrentFrame;
            bool playing = anim.Playing;
            anim.Library = lib;
            tk2dSpriteAnimationClip next = string.IsNullOrEmpty(clip) ? null : lib.GetClipByName(clip);
            if (next == null || next.frames == null || next.frames.Length == 0)
                return;
            anim.PlayFromFrame(clip, Mathf.Clamp(frame, 0, next.frames.Length - 1));
            if (!playing)
                anim.Stop();
        }

        /// <summary>Vanilla switchAniLibrary's look families: bare-handed torso and legs per look.</summary>
        internal static void SkinLibraries(string torsoLib, out string noneLib, out string legsLib)
        {
            noneLib = "PlayerNoneAnims";
            legsLib = "PlayerNoneAnims";
            if (string.IsNullOrEmpty(torsoLib))
                return;
            if (torsoLib.StartsWith("AntagonistAct1", System.StringComparison.Ordinal))
                noneLib = legsLib = "AntagonistAct1NoneAnims";
            else if (torsoLib.StartsWith("PlayerNaked", System.StringComparison.Ordinal))
                noneLib = legsLib = "PlayerNakedNoneAnims";
            else if (torsoLib.StartsWith("PlayerEpilogue", System.StringComparison.Ordinal))
                noneLib = "PlayerEpilogueAnims";
        }

        /// <returns>True when the clip was started now (not already playing).</returns>
        internal bool PlayTorso(string clipName)
        {
            if (_torsoAnimator == null || string.IsNullOrEmpty(clipName))
                return false;

            if (_torsoAnimator.GetClipByName(clipName) == null)
            {
                // Fall back to the "None" animation library if the clone doesn't have the clip
                if (_noneAnimsLib != null && _noneAnimsLib.GetClipByName(clipName) != null)
                {
                    _torsoAnimator.Library = _noneAnimsLib;
                }
                else
                {
                    return false;
                }
            }

            // Only skip if the animator is already actively playing this clip.
            // Allows replay when a non-looping clip finishes and restarts
            // (for example, double-barrel reload loop; the same clip plays twice).
            if (_torsoAnimator.Playing && _torsoAnimator.CurrentClip?.name == clipName)
                return false;

            // A stand-up the proxy already played through (it runs on its own clock) must not
            // restart while the sender is still on its tail.
            if (IsGetUpClip(clipName) && _torsoAnimator.CurrentClip?.name == clipName)
                return false;

            // Prevent replay of death clips once played to completion;
            // the host sends "Death1"/"Death2" every 30ms in PlayerStateMessage,
            // but the non-looping clip finishes and restarts endlessly.
            bool death = IsDeathClip(clipName);
            if (death && _deathClipPlayed)
                return false;

            // A stand-up left waiting on its first frame must not hold the next clip still.
            if (_torsoAnimator.Paused) _torsoAnimator.Resume();
            _torsoAnimator.Play(clipName);
            UpdateLegVisibility(clipName);

            // One play per death: any other clip (the respawn stand-up) ends that death.
            _deathClipPlayed = death;
            return true;
        }

        /// <summary>
        /// The stand-up clips a player starts while still loading or respawning. The watcher's
        /// copy follows the player's clip time (see <see cref="FollowGetUp"/>).
        /// </summary>
        internal static bool IsGetUpClip(string torsoClipName)
            => torsoClipName == "Sleep" || torsoClipName == "GetUpFromBed";

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
                StopLegsAtNeutral();
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
