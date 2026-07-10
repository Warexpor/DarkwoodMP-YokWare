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
    /// Send/close must work from both Update (raw input) and OnGUI (IMGUI events) —
    /// IMGUI alone often swallows KeyDown so Enter appeared dead.
    /// </summary>
    public sealed class ChatHud : MonoBehaviour
    {
        private const string InputControlName = "YokWareChat";
        private const float AntiSpamSec = 0.25f;

        private static ChatHud _instance;
        private readonly List<string> _lines = new List<string>(32);
        private bool _inputOpen;
        private string _draft = "";
        private float _lastLocalSend;
        private bool _focusPending;
        private int _lastSendFrame = -1;

        public static bool IsInputOpen => _instance != null && _instance._inputOpen;

        public static void EnsureExists()
        {
            if (_instance != null) return;
            var go = new GameObject("YokWare_ChatHud");
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<ChatHud>();
        }

        public static void AddLocalSystem(string msg)
        {
            EnsureExists();
            _instance?.AddLine("[System] " + msg);
        }

        public static void OnRemote(ChatMessagePayload msg)
        {
            EnsureExists();
            if (_instance == null) return;
            string name = string.IsNullOrEmpty(msg.SenderName) ? ("P" + msg.SenderId) : msg.SenderName;
            _instance.AddLine(name + ": " + msg.Message);
            TrySpeechBubble(msg.SenderId, msg.Message);
        }

        private void Update()
        {
            // Open/close toggle — either Ctrl works (Yokyy was Left only).
            if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && Input.GetKeyDown(KeyCode.C))
            {
                ToggleInput(!_inputOpen);
            }

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
            }
        }

        private void OnGUI()
        {
            var net = ModRuntime.Network;
            bool session = net != null && net.Role != NetworkRole.Offline;

            if (session)
            {
                string role = net.Role == NetworkRole.Host ? "HOST" : "CLIENT";
                string line = PluginInfo.Name + "  " + role + "  id=" + net.LocalPlayerId
                    + "  peers=" + net.ConnectedPlayerCount
                    + "  |  Ctrl+C chat  F2 settings";
                GUI.Label(new Rect(8f, 4f, Screen.width - 16f, 22f), line);
            }

            if (_lines.Count > 0)
            {
                float y = session ? 28f : 8f;
                for (int i = Mathf.Max(0, _lines.Count - 8); i < _lines.Count; i++)
                {
                    GUI.Label(new Rect(8f, y, Screen.width * 0.55f, 18f), _lines[i]);
                    y += 18f;
                }
            }

            if (!_inputOpen)
                return;

            // IMGUI path (same keys as Update) — consume events so game doesn't eat them.
            HandleGuiKeys();

            float w = Mathf.Min(520f, Screen.width - 40f);
            float h = 64f;
            Rect box = new Rect(20f, Screen.height - h - 40f, w, h);
            GUI.Box(box, "Chat  —  ENTER send   ESC close");
            GUI.SetNextControlName(InputControlName);
            _draft = GUI.TextField(
                new Rect(box.x + 8f, box.y + 28f, box.width - 88f, 24f),
                _draft ?? "",
                200);

            if (GUI.Button(new Rect(box.x + box.width - 72f, box.y + 28f, 60f, 24f), "SEND"))
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

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Offline)
            {
                AddLine("[System] Not in a session.");
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
            while (_lines.Count > 40)
                _lines.RemoveAt(0);
        }

        private static void TrySpeechBubble(int senderId, string message)
        {
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
            catch
            {
                // never break chat on bubble failure
            }
        }
    }
}
