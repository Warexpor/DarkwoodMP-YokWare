using DG.Tweening;
using DWMPHorde.Logging;
using UnityEngine;
using UnityEngine.Video;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The new-game opening (black screen, prologue text, music, intro movie, waking on the first
    /// prologue pad) for a joiner who plays its own prologue on a loaded world
    /// (<see cref="Sync.PersonalPrologue"/>). Vanilla runs it only from a new game's world
    /// generation (<c>WorldGenerator.tweenLoading</c>'s firstPlay branch); this is that branch on
    /// its own, ending in vanilla <c>activatePlayer</c>. The host's own opening is vanilla's, and
    /// nobody else watches it any more.
    /// </summary>
    internal static class PrologueIntro
    {
        private static VideoPlayer _prepared; // process-scoped: the joiner's intro movie
        private static float _startedAt;      // process-scoped: joiner intro clock
        private const float GiveUpSec = 120f;

        internal static void Play(WorldGenerator wg)
        {
            UI ui = Singleton<UI>.Instance;
            Controller ctrl = Singleton<Controller>.Instance;
            if (ui == null || ctrl == null)
                return;
            _startedAt = Time.realtimeSinceStartup;
            Core.forbidInputs = true;
            try
            {
                OutsideLocations outs = Singleton<OutsideLocations>.Instance;
                if (outs != null && outs.spawnedLocations != null
                    && outs.spawnedLocations.TryGetValue("dream_tutorial_00", out Location pad) && pad != null)
                    pad.gameObject.SetActive(false);
            }
            catch { /* pad missing: activatePlayer still wakes the player */ }
            wg.playingIntro = true;
            // As in a new game (WorldGenerator turns the camera off before the movie): activatePlayer
            // turns it back on and it centres on the player. A loaded world's camera stayed on and
            // kept the rough spot the teleport onto the pad gave it.
            if (Singleton<CamMain>.Instance != null)
                Singleton<CamMain>.Instance.gameObject.SetActive(false);
            ui.blackScreen.SetActive(true);
            ui.blackScreen.GetComponent<tk2dSprite>().color = new Color(0f, 0f, 0f, 1f);
            ctrl.Invoke(delegate
            {
                if (!wg.playingIntro)
                    return;
                ui.showPrologueText();
                ctrl.Invoke(delegate
                {
                    if (!wg.playingIntro)
                        return;
                    AudioController.Play("DW4_1");
                    ctrl.Invoke(delegate
                    {
                        if (wg.playingIntro)
                            StartVideo();
                    }, 3f, timeScaleDependent: false);
                }, 4f, timeScaleDependent: false);
            }, 2.5f, timeScaleDependent: false);
        }

        /// <summary>The movie played and stopped (or never came: gives up after a while).</summary>
        internal static bool Finished(ref bool seenPlaying)
        {
            if (_prepared != null && _prepared.isPlaying)
            {
                seenPlaying = true;
                return false;
            }
            if (seenPlaying)
                return true;
            return Time.realtimeSinceStartup - _startedAt > GiveUpSec;
        }

        /// <summary>Vanilla wake-up on the first prologue pad (activatePlayer's firstPlay branch).</summary>
        internal static void Wake(WorldGenerator wg)
        {
            if (_prepared != null)
            {
                _prepared.prepareCompleted -= DisplayIntro;
                _prepared = null;
            }
            Core.forbidInputs = true; // activatePlayer runs only from a locked start
            wg.activatePlayer();
        }

        private static void StartVideo()
        {
            UI ui = Singleton<UI>.Instance;
            if (ui == null || ui.videoOverlay == null)
                return;
            string lang = "en";
            try { lang = GameSettings.GetString("LanguageCode"); }
            catch { /* default */ }
            if (string.IsNullOrEmpty(lang))
                lang = "en";

            VideoClip clip = Resources.Load("Video/intro_cz1_" + lang, typeof(VideoClip)) as VideoClip;
            if (clip == null)
                clip = Resources.Load("Video/intro_cz1_en", typeof(VideoClip)) as VideoClip;
            if (clip == null)
            {
                ModLog.Warn(LogCat.Session, "[Prologue] intro movie not found");
                return;
            }

            ui.videoOverlay.SetActive(true);
            VideoPlayer player = ui.videoOverlay.GetComponent<VideoPlayer>();
            Renderer renderer = ui.videoOverlay.GetComponent<Renderer>();
            if (player == null)
                return;
            if (renderer != null)
                renderer.enabled = false;
            player.clip = clip;
            player.playOnAwake = false;
            if (_prepared != null)
                _prepared.prepareCompleted -= DisplayIntro;
            _prepared = player;
            player.prepareCompleted += DisplayIntro;
            player.Prepare();
        }

        private static void DisplayIntro(VideoPlayer player)
        {
            player.prepareCompleted -= DisplayIntro;
            UI ui = Singleton<UI>.Instance;
            if (ui == null || ui.videoOverlay == null)
                return;
            Renderer renderer = ui.videoOverlay.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.enabled = true;
                renderer.material.color = new Color(1f, 1f, 1f, 0f);
                renderer.material.DOFade(1f, 2f).SetUpdate(true);
            }
            player.Play();
            Core.hideGameCursor();
        }
    }
}
