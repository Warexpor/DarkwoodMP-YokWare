using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Yokyy product (ported to Horde wire): in-game chat overlay.
    /// LeftCtrl+C opens input; Enter sends; Esc closes.
    /// </summary>
    public sealed class ChatHud : MonoBehaviour
    {
        private static ChatHud _instance;
        private readonly List<string> _lines = new List<string>(32);
        private bool _inputOpen;
        private string _draft = "";
        private float _lastLocalSend;

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
            // LeftCtrl+C — same idea as Yokyy chat hotkey
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.C))
            {
                _inputOpen = !_inputOpen;
                if (_inputOpen) _draft = "";
            }

            if (_inputOpen && Input.GetKeyDown(KeyCode.Escape))
            {
                _inputOpen = false;
                _draft = "";
            }
        }

        private void OnGUI()
        {
            var net = ModRuntime.Network;
            bool session = net != null && net.Role != NetworkRole.Offline;

            // Status strip (Yokyy HUD product, slim)
            if (session)
            {
                string role = net.Role == NetworkRole.Host ? "HOST" : "CLIENT";
                string line = PluginInfo.Name + "  " + role + "  id=" + net.LocalPlayerId
                    + "  peers=" + net.ConnectedPlayerCount
                    + "  |  Ctrl+C chat  F2 settings";
                GUI.Label(new Rect(8f, 4f, Screen.width - 16f, 22f), line);
            }

            // Recent chat lines
            if (_lines.Count > 0)
            {
                float y = session ? 28f : 8f;
                for (int i = Mathf.Max(0, _lines.Count - 8); i < _lines.Count; i++)
                {
                    GUI.Label(new Rect(8f, y, Screen.width * 0.55f, 18f), _lines[i]);
                    y += 18f;
                }
            }

            if (!_inputOpen) return;

            float w = Mathf.Min(520f, Screen.width - 40f);
            float h = 54f;
            Rect box = new Rect(20f, Screen.height - h - 40f, w, h);
            GUI.Box(box, "Chat (Enter send · Esc cancel)");
            GUI.SetNextControlName("YokWareChat");
            _draft = GUI.TextField(new Rect(box.x + 8f, box.y + 24f, box.width - 16f, 22f), _draft ?? "", 200);
            GUI.FocusControl("YokWareChat");

            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Return)
            {
                TrySend();
                e.Use();
            }
        }

        private void TrySend()
        {
            string msg = (_draft ?? "").Trim();
            _draft = "";
            _inputOpen = false;
            if (string.IsNullOrEmpty(msg)) return;
            if (Time.unscaledTime - _lastLocalSend < 0.25f) return; // anti-spam (Yokyy had none)
            _lastLocalSend = Time.unscaledTime;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Offline)
            {
                AddLine("[System] Not in a session.");
                return;
            }

            // Length clamp — Yokyy allowed huge strings
            if (msg.Length > 160) msg = msg.Substring(0, 160);

            string name = ModConfig.PlayerName != null ? ModConfig.PlayerName.Value : "Player";
            if (string.IsNullOrWhiteSpace(name)) name = "Player";

            var payload = new ChatMessagePayload
            {
                SenderId = net.LocalPlayerId,
                SenderName = name.Trim(),
                Message = msg
            };

            AddLine(payload.SenderName + ": " + payload.Message);
            TrySpeechBubble(payload.SenderId, payload.Message);

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
                // Local player
                var net = ModRuntime.Network;
                if (net != null && senderId == net.LocalPlayerId && Player.Instance != null)
                {
                    Player.Instance.displayMessage(message);
                    return;
                }
                // Remote proxy (if registry exposes it)
                // Best-effort: Core.displayMessage at player if available
            }
            catch
            {
                // never break chat on bubble failure (Yokyy design)
            }
        }
    }
}
