using DG.Tweening;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Spectator;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Video;

namespace DWMPHorde.Sync
{
    internal static partial class DreamSyncManager
    {
        private static float StartRemoteDreamTransition()
        {
            if (_remoteEntryTransitionPlaying)
            {
                float remain = _earlyEntryTransitionDoneAt - Time.realtimeSinceStartup;
                return Mathf.Max(0f, remain);
            }

            var transition = Dreams.Instance?.startTransition;
            if (transition == null || transition.transitionObjects == null)
            {
                ShowDreamTransitionFallback();
                return 0f;
            }

            try
            {
                Core.EnteringDream = true;
                _remoteEntryTransitionPlaying = true;

                // Stop all audio (same as DreamTransition.transition)
                AudioController.StopAll(transition.fadeAllAudioTime);
                Singleton<Controller>.Instance?.fadeAudio(fadeOut: true, 2f, musicToo: false);

                float videoLength = 0f;
                bool hasVideo = false;
                _remoteEntryAudioId = null;

                for (int i = 0; i < transition.transitionObjects.Count; i++)
                {
                    var obj = transition.transitionObjects[i];
                    if (obj == null) continue;

                    if (obj.type == DreamTransition.TransitionObject.Type.Audio)
                    {
                        // Play only the first entry stinger; multiple Audio objects stacked
                        // with the video soundtrack as a doubled/prolonged enter sound.
                        if (string.IsNullOrEmpty(_remoteEntryAudioId)
                            && !string.IsNullOrEmpty(obj.audioItemName))
                        {
                            AudioController.Play(obj.audioItemName);
                            _remoteEntryAudioId = obj.audioItemName;
                        }
                    }
                    else if (obj.type == DreamTransition.TransitionObject.Type.Video)
                    {
                        hasVideo = true;
                        Renderer renderer = Singleton<UI>.Instance.videoOverlay.GetComponent<Renderer>();
                        if (renderer == null) continue;

                        string path = "Video/" + obj.videoName;
                        if (obj.localizedVideo)
                            path = path + "_" + GameSettings.GetString("LanguageCode");

                        VideoClip clip = Resources.Load(path, typeof(VideoClip)) as VideoClip;
                        if (clip == null) continue;

                        videoLength = (float)clip.length;
                        renderer.enabled = false;
                        VideoPlayer vp = renderer.GetComponent<VideoPlayer>();
                        vp.clip = clip;
                        // Mute video audio when we already play the dedicated Audio stinger;
                        // otherwise client hears stinger + video track (doubled enter sound).
                        if (!string.IsNullOrEmpty(_remoteEntryAudioId))
                            vp.SetDirectAudioMute(0, true);

                        vp.prepareCompleted += OnRemoteTransitionVideoPrepared;
                        vp.Prepare();
                        Singleton<UI>.Instance.videoOverlay.gameObject.SetActive(true);

                        // Fade in
                        if (obj.fadeIn > 0f)
                        {
                            renderer.material.color = new Color(1f, 1f, 1f, 0f);
                            renderer.material.DOFade(1f, obj.fadeIn).SetUpdate(true);
                        }
                        else
                        {
                            renderer.material.color = new Color(1f, 1f, 1f, 1f);
                        }
                    }
                }

                // Use durationOverride if set (same logic as DreamTransition.transition)
                if (transition.durationOverride > 0f)
                    videoLength = transition.durationOverride;

                if (!hasVideo)
                {
                    ShowDreamTransitionFallback();
                    _remoteEntryTransitionPlaying = false;
                    return 0f;
                }

                // Set black screen behind the video overlay so there's no
                // visual gap when the video ends and the dream loads.
                if (Singleton<UI>.Instance != null)
                    Singleton<UI>.Instance.tweenBlackScreen(new Color(0f, 0f, 0f, 1f), 0.1f);

                _earlyEntryTransitionDoneAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, videoLength);
                ModRuntime.LegacyInfo($"[DreamSync] Remote dream transition started, wait={videoLength:F1}s");
                return videoLength;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to play remote dream transition: {ex}");
                _remoteEntryTransitionPlaying = false;
                ShowDreamTransitionFallback();
                return 0f;
            }
        }

        private static void OnRemoteTransitionVideoPrepared(VideoPlayer player)
        {
            player.prepareCompleted -= OnRemoteTransitionVideoPrepared;
            Singleton<UI>.Instance.videoOverlay.GetComponent<Renderer>().enabled = true;
            player.Play();
        }

        private static void ShowDreamTransitionFallback()
        {
            if (Singleton<UI>.Instance == null) return;

            try
            {
                var blackTop = Singleton<UI>.Instance.blackScreenTop;
                if (blackTop != null)
                {
                    var sprite = blackTop.GetComponent<tk2dBaseSprite>();
                    if (sprite != null)
                    {
                        Singleton<UI>.Instance.tweenBlackScreenTop(new Color(0f, 0f, 0f, 1f), 0.3f);
                        Singleton<Controller>.Instance?.waitFramesAndRun(delegate
                        {
                            if (sprite != null && sprite.color.a != 0f)
                            {
                                Singleton<UI>.Instance.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 0.5f);
                            }
                        }, 1);
                    }
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to show dream transition fallback: {ex}");
            }
        }

        /// <summary>
        /// Safety timeout: unfreezes remote proxies after <paramref name="delay"/> seconds
        /// of real time. Drops optimistic NoteRemoteInDream stamps that never confirmed
        /// (matches IsRemoteInDream post-deadline). DreamEntered after this still Confirm-s.
        /// </summary>
        private static System.Collections.IEnumerator UnfreezeProxiesAfterDelay(float delay)
        {
            yield return new UnityEngine.WaitForSecondsRealtime(delay);
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) yield break;

            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                proxy.FreezePosition = false;
                // N-peer: optimistic host stamp at dream start — clear if never entered.
                if (!_dreamEntryConfirmed.Contains(proxy.PlayerId))
                    ClearRemoteInDream(proxy.PlayerId);
            }
            foreach (int id in net.GetHandshakedPeerIds())
            {
                if (id <= 0 || id == net.LocalPlayerId) continue;
                if (!_dreamEntryConfirmed.Contains(id))
                    ClearRemoteInDream(id);
            }
        }
    }
}
