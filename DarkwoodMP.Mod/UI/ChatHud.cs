using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Yokyy-style in-game chat. Ctrl+C opens; Enter/KeypadEnter sends; Esc closes.
    /// Send and close must work from both Update (raw input) and OnGUI (IMGUI events);
    /// IMGUI alone often swallows KeyDown so Enter appeared dead.
    /// While the input is open <see cref="UiInputLock"/> holds vanilla gameplay input
    /// (movement, hotbar keys, walkie TX) so typing does not drive the character.
    /// </summary>
    public sealed class ChatHud : MonoBehaviour
    {
        /// <summary>Config <c>[Network] ChatEnabled</c> (default on).</summary>
        public static bool Enabled => ModConfig.ChatEnabled != null && ModConfig.ChatEnabled.Value;

        private const string InputControlName = "YokWareChat";
        private const string LockOwner = "chat";
        private const float AntiSpamSec = 0.25f;
        /// <summary>History lines fade out of the corner after this long (all shown while typing).</summary>
        private const float LineVisibleSec = 14f;
        private const int MaxVisibleLines = 8;

        private static ChatHud _instance;
        private readonly List<string> _lines = new List<string>(32);
        private readonly List<float> _lineTimes = new List<float>(32);
        private bool _inputOpen;
        private string _draft = "";
        private float _lastLocalSend;
        private bool _focusPending;
        private int _lastSendFrame = -1;

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
                _instance._lineTimes.Clear();
                _instance._inputOpen = false;
                _instance._draft = "";
                _instance._focusPending = false;
            }
            UiInputLock.Set(LockOwner, false);
        }

        public static void EnsureExists()
        {
            if (!Enabled) return;
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
            string name = string.IsNullOrEmpty(msg.SenderName) ? ("P" + msg.SenderId) : msg.SenderName;
            _instance.AddLine(name + ": " + msg.Message);
            TrySpeechBubble(msg.SenderId, msg.Message);
        }

        /// <summary>A line from the mod itself (map pins and pings), shown with the chat history.</summary>
        public static void AddSystemLine(string line)
        {
            if (!Enabled || string.IsNullOrEmpty(line)) return;
            EnsureExists();
            if (_instance == null) return;
            _instance.AddLine(line);
        }

        private void Update()
        {
            if (!Enabled)
            {
                if (_inputOpen)
                    ToggleInput(false);
                return;
            }

            // Scene change / title: nothing to type into.
            if (_inputOpen && (Core.mainMenu || Core.loadingGame || Player.Instance == null))
                ToggleInput(false);

            // Open with Ctrl+C (either Ctrl). While open, Ctrl+C is the text-field copy shortcut.
            if (!_inputOpen
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && Input.GetKeyDown(KeyCode.C)
                && !Core.mainMenu && !Core.loadingGame && Player.Instance != null
                && UiInputLock.CanOpenOverlay)
            {
                ToggleInput(true);
            }

            // Hold the vanilla input gate every frame while typing (dialogue/cutscene code
            // clears Core.forbidInputs on its own schedule).
            UiInputLock.Set(LockOwner, _inputOpen);

            // Raw input fallback: IMGUI Event.current KeyDown is unreliable while TextField focused.
            if (!_inputOpen)
                return;

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                TrySend();
            else if (Input.GetKeyDown(KeyCode.Escape))
                ToggleInput(false);
        }

        private void ToggleInput(bool open)
        {
            _inputOpen = open;
            if (open)
            {
                _draft = "";
                _focusPending = true;
            }
            else
            {
                _draft = "";
                _focusPending = false;
                // Release now (not next Update) so the Esc/Enter frame is stamped for the Esc swallow.
                GUIUtility.keyboardControl = 0;
            }
            UiInputLock.Set(LockOwner, open);
        }

        private void OnGUI()
        {
            if (!Enabled) return;

            // Session status strip (HOST/CLIENT/peers) was removed because it cluttered the corner.
            // Role/peers still live in F2 settings menu. Chat lines only while history exists.

            if (_lines.Count > 0)
            {
                float now = Time.unscaledTime;
                float y = 8f;
                for (int i = Mathf.Max(0, _lines.Count - MaxVisibleLines); i < _lines.Count; i++)
                {
                    if (!_inputOpen && now - _lineTimes[i] > LineVisibleSec)
                        continue;
                    GUI.Label(new Rect(8f, y, Screen.width * 0.55f, 18f), _lines[i]);
                    y += 18f;
                }
            }

            if (!_inputOpen)
                return;

            // IMGUI path uses the same keys as Update; consume events so the game does not eat them.
            HandleGuiKeys();

            float w = Mathf.Min(520f, Screen.width - 40f);
            float h = 64f;
            Rect box = new Rect(20f, Screen.height - h - 40f, w, h);
            GUI.Box(box, Loc.T("Chat  —  ENTER send   ESC close"));
            GUI.SetNextControlName(InputControlName);
            _draft = GUI.TextField(
                new Rect(box.x + 8f, box.y + 28f, box.width - 88f, 24f),
                _draft ?? "",
                200);

            if (GUI.Button(new Rect(box.x + box.width - 72f, box.y + 28f, 60f, 24f), Loc.T("SEND")))
                TrySend();

            if (_focusPending)
            {
                GUI.FocusControl(InputControlName);
                if (Event.current != null && Event.current.type == EventType.Repaint)
                    _focusPending = false;
            }
        }

        private void HandleGuiKeys()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown)
                return;

            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                TrySend();
                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                ToggleInput(false);
                e.Use();
            }
        }

        private void TrySend()
        {
            // Update + OnGUI can both fire the same keypress.
            if (_lastSendFrame == Time.frameCount)
                return;
            _lastSendFrame = Time.frameCount;

            string msg = (_draft ?? "").Trim();
            _draft = "";
            ToggleInput(false);

            if (string.IsNullOrEmpty(msg))
                return;
            if (Time.unscaledTime - _lastLocalSend < AntiSpamSec)
                return;
            _lastLocalSend = Time.unscaledTime;

            var net = ModRuntime.Network;
            if (net == null || net.Role == NetworkRole.Offline)
            {
                AddLine(Loc.T("[System] Not in a session."));
                return;
            }

            if (msg.Length > 160)
                msg = msg.Substring(0, 160);

            string name = ModConfig.PlayerName != null ? ModConfig.PlayerName.Value : "Player";
            if (string.IsNullOrWhiteSpace(name))
                name = "Player";

            var payload = new ChatMessagePayload
            {
                SenderId = net.LocalPlayerId,
                SenderName = name.Trim(),
                Message = msg
            };

            AddLine(payload.SenderName + ": " + payload.Message);
            TrySpeechBubble(payload.SenderId, payload.Message);

            // Reliable + Forwardable: host fans out to other clients.
            net.Broadcast(NetMessageType.ChatMessage, w => payload.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.UI, "[CHAT] " + payload.SenderName + ": " + payload.Message);
        }

        private void AddLine(string line)
        {
            _lines.Add(line);
            _lineTimes.Add(Time.unscaledTime);
            while (_lines.Count > 40)
            {
                _lines.RemoveAt(0);
                _lineTimes.RemoveAt(0);
            }
        }

        private static void TrySpeechBubble(int senderId, string message)
        {
            try
            {
                DWMPHorde.Patches.PersonalFlavorHud.BeginBypass();
                try
                {
                    var net = ModRuntime.Network;
                    if (net != null && senderId == net.LocalPlayerId && Player.Instance != null)
                    {
                        Player.Instance.displayMessage(message);
                        return;
                    }

                    // Remote: bubble at proxy transform (Yokyy Core.displayMessage path)
                    if (net is LanNetworkManager lnm)
                    {
                        RemotePlayerProxy proxy = lnm.GetProxy(senderId);
                        if (proxy != null && proxy.transform != null)
                            Core.displayMessage(message, proxy.transform, 1f, false);
                    }
                }
                finally
                {
                    DWMPHorde.Patches.PersonalFlavorHud.EndBypass();
                }
            }
            catch
            {
                // never break chat on bubble failure
            }
        }
    }
}
