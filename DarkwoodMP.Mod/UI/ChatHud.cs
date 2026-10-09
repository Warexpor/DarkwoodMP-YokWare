using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Co-op text chat, drawn like the game's own text: the outlined hover-label font in the lower
    /// left corner, no box. Ctrl+C opens the line ("Say:"), Enter sends, Esc closes, Ctrl+V pastes.
    /// A line shows at once and fades out smoothly after a while; all come back while typing. What
    /// is said stays in the chat (nothing is shown over the players). While typing
    /// <see cref="UiInputLock"/> holds vanilla gameplay input (movement, hotbar keys, walkie TX).
    /// </summary>
    public sealed class ChatHud : MonoBehaviour
    {
        /// <summary>Config <c>[Network] ChatEnabled</c> (default on).</summary>
        public static bool Enabled => ModConfig.ChatEnabled != null && ModConfig.ChatEnabled.Value;

        private const string LockOwner = "chat";
        private const float AntiSpamSec = 0.25f;
        private const int MaxMessage = 160;
        /// <summary>Lines stay this long, then fade out over <see cref="FadeSec"/> (all shown while typing).</summary>
        private const float LineVisibleSec = 14f;
        private const float FadeSec = 2.5f;
        private const int MaxVisibleLines = 8;
        private const int MaxHistory = 40;

        // Layout in 1080p HUD pixels (scaled like the vanilla HUD).
        private const float Left = 40f;
        private const float InputBottom = 150f;
        private const float LineGap = 4f;
        private const float Width = 620f;

        private static readonly Color NameColor = new Color(0.62f, 0.62f, 0.62f, 1f);
        private static readonly Color TextColor = new Color(0.92f, 0.92f, 0.92f, 1f);
        private static readonly Color SystemColor = new Color(0.55f, 0.55f, 0.55f, 1f);

        private sealed class Line
        {
            public string Name;
            public string Text;
            public float At;
            /// <summary>Shown alpha: up at once, down smoothly (<see cref="LineAlpha"/>).</summary>
            public float Alpha;
        }

        private sealed class Row
        {
            public tk2dTextMesh Name;
            public tk2dTextMesh Text;
        }

        private static ChatHud _instance; // process-scoped: one HUD component
        private readonly List<Line> _lines = new List<Line>(MaxHistory);
        private readonly Row[] _rows = new Row[MaxVisibleLines];
        private tk2dTextMesh _sayLabel;
        private tk2dTextMesh _sayText;
        private bool _inputOpen;
        private int _openedFrame = -1;
        private string _draft = "";
        private float _lastLocalSend;

        public static bool IsInputOpen => Enabled && _instance != null && _instance._inputOpen;

        /// <summary>
        /// Session end: drop history, close the input and release the input lock so a
        /// half-typed draft or old lines never leak into the next session.
        /// </summary>
        public static void Reset()
        {
            if (_instance != null)
            {
                _instance._lines.Clear();
                _instance._inputOpen = false;
                _instance._draft = "";
            }
            UiInputLock.Set(LockOwner, false);
        }

        public static void EnsureExists()
        {
            if (_instance != null) return;
            var go = new GameObject("YokWare_ChatHud");
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<ChatHud>();
        }

        public static void OnRemote(ChatMessagePayload msg)
        {
            if (!Enabled) return;
            EnsureExists();
            if (_instance == null) return;
            string name = PlayerNames.Clean(msg.SenderName);
            if (string.IsNullOrEmpty(name) || name == "Player")
                name = PlayerNames.Shown(msg.SenderId);
            _instance.AddLine(name, msg.Message);
        }

        /// <summary>A line from the mod itself (map pins and pings), shown with the chat history.</summary>
        public static void AddSystemLine(string line)
        {
            if (!Enabled || string.IsNullOrEmpty(line)) return;
            EnsureExists();
            if (_instance == null) return;
            _instance.AddLine(null, line);
        }

        private static bool InWorld => !Core.mainMenu && !Core.loadingGame && Player.Instance != null;

        private void Update()
        {
            if (!Enabled)
            {
                if (_inputOpen)
                    ToggleInput(false);
                return;
            }

            // Scene change / title / pause menu: nothing to type into.
            if (_inputOpen && !InWorld)
                ToggleInput(false);

            // Open with Ctrl+C (either Ctrl).
            if (!_inputOpen
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && Input.GetKeyDown(KeyCode.C)
                && InWorld && UiInputLock.CanOpenOverlay)
            {
                ToggleInput(true);
            }

            // Hold the vanilla input gate every frame while typing (dialogue/cutscene code
            // clears Core.forbidInputs on its own schedule).
            UiInputLock.Set(LockOwner, _inputOpen);

            if (!_inputOpen || Time.frameCount == _openedFrame)
                return;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                ToggleInput(false);
                return;
            }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                TrySend();
                return;
            }
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
                || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
            if (ctrl && Input.GetKeyDown(KeyCode.V))
            {
                foreach (char c in GUIUtility.systemCopyBuffer ?? "")
                    Append(c == '\n' || c == '\r' || c == '\t' ? ' ' : c);
                return;
            }
            foreach (char c in Input.inputString)
            {
                if (c == '\b')
                {
                    if (_draft.Length > 0)
                        _draft = _draft.Substring(0, _draft.Length - 1);
                }
                else if (c == '\n' || c == '\r')
                {
                    TrySend();
                    return;
                }
                else if (!ctrl)
                {
                    Append(c);
                }
            }
        }

        private void Append(char c)
        {
            if (char.IsControl(c) || _draft.Length >= MaxMessage)
                return;
            _draft += c;
        }

        private void ToggleInput(bool open)
        {
            _inputOpen = open;
            _draft = "";
            if (open)
                _openedFrame = Time.frameCount;
            UiInputLock.Set(LockOwner, open);
        }

        // ------------------------------------------------------------------
        // Drawing
        // ------------------------------------------------------------------

        private void LateUpdate()
        {
            bool show = Enabled && InWorld && Singleton<UI>.Instance != null && Singleton<UI>.Instance.gameObject.activeInHierarchy;
            float s = HudText.Scale;
            float x = Left * s;
            float z = InputBottom * s;
            float now = Time.unscaledTime;

            // The "Say:" line.
            bool typing = show && _inputOpen;
            if (typing && EnsureInput())
            {
                bool caret = ((int)(now * 2f) & 1) == 0;
                HudText.FollowScale(_sayLabel);
                HudText.FollowScale(_sayText);
                string say = Loc.T("Say:");
                HudText.Set(_sayLabel, say, NameColor);
                HudText.Place(_sayLabel, x, z);
                float textX = x + HudText.Size(_sayLabel, say + " ").x;
                Wrap(_sayText, Width * s - (textX - x));
                HudText.Set(_sayText, _draft + (caret ? "_" : " "), TextColor);
                HudText.Place(_sayText, textX, z);
                SetActive(_sayLabel, true);
                SetActive(_sayText, true);
                z += Height(_sayText) + LineGap * 2f * s;
            }
            else
            {
                SetActive(_sayLabel, false);
                SetActive(_sayText, false);
            }

            // History, newest at the bottom.
            int row = 0;
            for (int i = _lines.Count - 1; show && i >= 0 && row < MaxVisibleLines; i--)
            {
                Line line = _lines[i];
                float alpha = LineAlpha(line, typing, now);
                if (alpha <= 0f)
                    break;
                Row r = EnsureRow(row);
                if (r == null)
                    break;
                row++;
                HudText.FollowScale(r.Name);
                HudText.FollowScale(r.Text);

                float textX = x;
                bool named = !string.IsNullOrEmpty(line.Name);
                if (named)
                {
                    string label = line.Name + ":";
                    HudText.Set(r.Name, label, Fade(NameColor, alpha));
                    textX = x + HudText.Size(r.Name, label + " ").x;
                }
                SetActive(r.Name, named);
                // Wrapped under its own start, so a long line reads as one block beside the name.
                Wrap(r.Text, Width * s - (textX - x));
                HudText.Set(r.Text, line.Text, Fade(named ? TextColor : SystemColor, alpha));
                SetActive(r.Text, true);
                float h = Height(r.Text);
                float top = z + h;
                if (named)
                    HudText.Place(r.Name, x, top);
                HudText.Place(r.Text, textX, top);
                z = top + LineGap * s;
            }
            for (int i = row; i < _rows.Length; i++)
            {
                if (_rows[i] == null)
                    continue;
                SetActive(_rows[i].Name, false);
                SetActive(_rows[i].Text, false);
            }
        }

        /// <summary>Wrap at <paramref name="widthPx"/> UI pixels (the mesh wraps in its own units).</summary>
        private static void Wrap(tk2dTextMesh tm, float widthPx)
        {
            float local = Mathf.Max(120f * HudText.Scale, widthPx) / Mathf.Max(0.0001f, tm.transform.localScale.x);
            int want = Mathf.RoundToInt(local);
            if (tm.formatting && tm.wordWrapWidth == want)
                return;
            tm.formatting = true;
            tm.wordWrapWidth = want;
            tm.Commit();
        }

        /// <summary>
        /// A line shows at once (new, or the chat opened) and goes smoothly: after its time, or when
        /// the chat closes on lines already past it, it eases out over <see cref="FadeSec"/>.
        /// </summary>
        private static float LineAlpha(Line line, bool typing, float now)
        {
            bool want = typing || now - line.At < LineVisibleSec;
            if (want)
                line.Alpha = 1f;
            else if (line.Alpha > 0f)
                line.Alpha = Mathf.Max(0f, line.Alpha - Time.unscaledDeltaTime / FadeSec);
            // Linear in time, eased on screen: a slow start and a soft end.
            return Mathf.SmoothStep(0f, 1f, line.Alpha);
        }

        private static Color Fade(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);

        private static void SetActive(tk2dTextMesh tm, bool on)
        {
            if (tm != null && tm.gameObject.activeSelf != on)
                tm.gameObject.SetActive(on);
        }

        /// <summary>Height of a laid-out text in UI pixels (one line at least).</summary>
        private static float Height(tk2dTextMesh tm)
        {
            Renderer rend = tm.GetComponent<Renderer>();
            float h = rend != null && rend.enabled && tm.gameObject.activeInHierarchy ? rend.bounds.size.z : 0f;
            float one = HudText.Size(tm, "Ay").y;
            return Mathf.Max(h, one);
        }

        private bool EnsureInput()
        {
            if (_sayLabel == null)
                _sayLabel = Create("YokWare_ChatSay", TextAnchor.UpperLeft);
            if (_sayText == null)
                _sayText = Create("YokWare_ChatDraft", TextAnchor.UpperLeft);
            if (_sayLabel != null)
                _sayLabel.anchor = TextAnchor.LowerLeft;
            if (_sayText != null)
                _sayText.anchor = TextAnchor.LowerLeft;
            return _sayLabel != null && _sayText != null;
        }

        private Row EnsureRow(int i)
        {
            Row r = _rows[i];
            if (r == null)
                r = _rows[i] = new Row();
            if (r.Name == null)
                r.Name = Create("YokWare_ChatName" + i, TextAnchor.UpperLeft);
            if (r.Text == null)
                r.Text = Create("YokWare_ChatLine" + i, TextAnchor.UpperLeft);
            return r.Name != null && r.Text != null ? r : null;
        }

        private static tk2dTextMesh Create(string name, TextAnchor anchor)
        {
            tk2dTextMesh tm = HudText.Create(name);
            if (tm == null)
                return null;
            tm.anchor = anchor;
            tm.Commit();
            return tm;
        }

        // ------------------------------------------------------------------
        // Sending
        // ------------------------------------------------------------------

        private void TrySend()
        {
            string msg = (_draft ?? "").Trim();
            ToggleInput(false);

            if (string.IsNullOrEmpty(msg))
                return;
            if (Time.unscaledTime - _lastLocalSend < AntiSpamSec)
                return;
            _lastLocalSend = Time.unscaledTime;

            var net = ModRuntime.Network;
            if (net == null || net.Role == NetworkRole.Offline)
            {
                AddLine(null, Loc.T("[System] Not in a session."));
                return;
            }

            if (msg.Length > MaxMessage)
                msg = msg.Substring(0, MaxMessage);

            var payload = new ChatMessagePayload
            {
                SenderId = net.LocalPlayerId,
                SenderName = PlayerNames.LocalName(),
                Message = msg
            };

            AddLine(PlayerNames.Shown(net.LocalPlayerId), payload.Message);

            // Reliable + Forwardable: host fans out to other clients.
            net.Broadcast(NetMessageType.ChatMessage, w => payload.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.UI, "[CHAT] " + payload.SenderName + ": " + payload.Message);
        }

        private void AddLine(string name, string text)
        {
            _lines.Add(new Line { Name = name, Text = text, At = Time.unscaledTime, Alpha = 1f });
            while (_lines.Count > MaxHistory)
                _lines.RemoveAt(0);
        }
    }
}
