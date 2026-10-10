using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Spectator
{
    public sealed partial class SpectatorModeController
    {
        /// <summary>Seconds: the F4 line fades in, stays, fades away.</summary>
        private const float PromptFadeIn = 0.6f, PromptHold = 5f, PromptFadeOut = 1.2f;
        /// <summary>1080p pixels above the bottom edge.</summary>
        private const float PromptZ = 96f;
        private static readonly Color PromptColor = new Color(0.62f, 0.62f, 0.62f, 1f);

        private tk2dTextMesh _prompt;
        private bool _promptWasSpectating;
        private float _promptSince = -100f;
        private string _promptText;

        /// <summary>
        /// One line in the hover-label font when spectating begins: what F4 does from here. A dead
        /// player with nobody else to watch gets none (F4 has nowhere to go).
        /// </summary>
        private void LateUpdate()
        {
            bool spectating = IsSpectating;
            if (spectating && !_promptWasSpectating)
            {
                _promptText = PromptLine();
                _promptSince = Time.unscaledTime;
            }
            _promptWasSpectating = spectating;

            float t = Time.unscaledTime - _promptSince;
            float alpha = !spectating || _promptText == null ? 0f
                : t < PromptFadeIn ? t / PromptFadeIn
                : t < PromptFadeIn + PromptHold ? 1f
                : 1f - (t - PromptFadeIn - PromptHold) / PromptFadeOut;
            if (alpha <= 0f || Core.mainMenu || Core.loadingGame || HudText.Source == null)
            {
                if (_prompt != null && _prompt.gameObject.activeSelf)
                    _prompt.gameObject.SetActive(false);
                return;
            }
            if (_prompt == null)
            {
                _prompt = HudText.Create("YokWare_SpectatePrompt");
                if (_prompt == null)
                    return;
                _prompt.anchor = TextAnchor.LowerCenter;
                _prompt.Commit();
            }
            HudText.FollowScale(_prompt);
            HudText.Set(_prompt, _promptText,
                new Color(PromptColor.r, PromptColor.g, PromptColor.b, Mathf.SmoothStep(0f, 1f, alpha) * 0.9f));
            HudText.Place(_prompt, Mathf.Round(Screen.width * 0.5f), Mathf.Round(PromptZ * HudText.Scale));
            if (!_prompt.gameObject.activeSelf)
                _prompt.gameObject.SetActive(true);
        }

        private static string PromptLine()
        {
            // Held in spectator (dead at night or in a dream): F4 only cycles, it never returns to the body.
            bool held = DeathStateTracker.LocalNightDeath || FinalDreamsceneManager.IsLocalDead;
            if (!held)
                return Loc.T("F4 - next player, then back to yourself");
            var net = ModRuntime.Network;
            int others = 0;
            if (net != null)
                foreach (var p in net.GetAllProxies())
                    if (p != null) others++;
            return others > 1 ? Loc.T("F4 - next player") : null;
        }
    }
}
