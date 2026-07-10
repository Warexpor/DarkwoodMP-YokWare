using System;
using UnityEngine;
using DarkwoodMP.Network;

namespace DarkwoodMP.UI;

/// <summary>
/// Chat overlay styled after the game's look.
///  - Passive mode: recent messages fade in the lower-left corner, so chat is
///    readable without opening anything.
///  - Active mode (CTRL+C): input line with focus, ENTER sends, ESC closes.
/// </summary>
public class ChatInput : MonoBehaviour
{
    private const float PassiveMessageLifetime = 12f; // seconds a message stays readable
    private const float PassiveFadeTime = 3f;         // fade-out at the end of that window
    private const int MaxVisibleMessages = 7;
    private const string InputControlName = "DWMP_ChatInput";

    private bool _active;
    private string _inputText = "";
    private bool _focusPending;
    private int _lastSendFrame = -1;

    // Per-frame cache of the recent-message list (OnGUI runs several times per
    // frame; rebuilding the list each pass caused GC churn)
    private System.Collections.Generic.List<ChatEntry> _cachedMessages;
    private int _cachedMessagesFrame = -1;

    public bool IsActive => _active;

    public void Toggle(bool state)
    {
        _active = state;
        if (_active)
        {
            _inputText = "";
            _focusPending = true;
        }
        ModLogger.Msg($"[ChatInput] Chat {(state ? "opened" : "closed")}");
    }

    private void Update()
    {
        // Robust fallback: some IMGUI states swallow the KeyDown event, so the
        // raw input check guarantees ENTER/ESC always work while typing
        if (!_active) return;
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            Send();
        else if (Input.GetKeyDown(KeyCode.Escape))
            Toggle(false);
    }

    public void OnGUI()
    {
        var prevSkin = GUI.skin;
        GUI.skin = DarkwoodTheme.Skin;
        try
        {
            HandleKeys();
            DrawMessages();
            if (_active)
                DrawInput();
        }
        finally
        {
            GUI.skin = prevSkin;
        }
    }

    private void HandleKeys()
    {
        if (!_active) return;
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown) return;

        if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
        {
            Send();
            e.Use();
        }
        else if (e.keyCode == KeyCode.Escape)
        {
            Toggle(false);
            e.Use();
        }
    }

    private void DrawMessages()
    {
        if (_cachedMessagesFrame != Time.frameCount)
        {
            _cachedMessagesFrame = Time.frameCount;
            _cachedMessages = ChatManager.Instance.GetRecentMessages(MaxVisibleMessages);
        }
        var messages = _cachedMessages;
        if (messages == null || messages.Count == 0) return;

        var now = Time.time;
        var lineHeight = 20f;
        var baseY = Screen.height - 96f;

        var shown = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var entry = messages[i];
            var age = now - entry.Timestamp;

            // While typing, the whole history stays visible; otherwise fade out
            float alpha;
            if (_active)
            {
                alpha = 1f;
            }
            else
            {
                if (age > PassiveMessageLifetime) continue;
                alpha = Mathf.Clamp01((PassiveMessageLifetime - age) / PassiveFadeTime);
            }

            var y = baseY - shown * lineHeight;
            shown++;

            var isSystem = entry.SenderId < 0;
            var nameStyle = isSystem ? DarkwoodTheme.ChatSystem : DarkwoodTheme.ChatName;
            var textStyle = isSystem ? DarkwoodTheme.ChatSystem : GUI.skin.label;

            var prevColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);

            var nameContent = new GUIContent(isSystem ? "†" : entry.SenderName + ":");
            var nameSize = nameStyle.CalcSize(nameContent);
            GUI.Label(new Rect(14f, y, nameSize.x + 2f, lineHeight), nameContent, nameStyle);
            GUI.Label(new Rect(14f + nameSize.x + 6f, y, Screen.width * 0.45f, lineHeight), entry.Message, textStyle);

            GUI.color = prevColor;
        }
    }

    private void DrawInput()
    {
        var y = Screen.height - 66f;
        var width = Mathf.Min(520f, Screen.width * 0.5f);

        GUI.Box(new Rect(10f, y - 6f, width + 8f, 56f), GUIContent.none);
        GUI.Label(new Rect(18f, y, width - 12f, 18f), "say something  —  ENTER: send   ESC: close", DarkwoodTheme.Dim);

        GUI.SetNextControlName(InputControlName);
        _inputText = GUI.TextField(new Rect(18f, y + 20f, width - 76f, 24f), _inputText ?? "");

        if (GUI.Button(new Rect(18f + width - 70f, y + 20f, 54f, 24f), "SEND"))
            Send();

        if (_focusPending)
        {
            GUI.FocusControl(InputControlName);
            if (Event.current.type == EventType.Repaint)
                _focusPending = false;
        }
    }

    private void Send()
    {
        // The Event handler and the Update fallback can both fire on one press
        if (_lastSendFrame == Time.frameCount) return;
        _lastSendFrame = Time.frameCount;

        var text = (_inputText ?? "").Trim();
        _inputText = "";
        if (text.Length == 0)
        {
            Toggle(false);
            return;
        }

        var manager = NetworkManager.Instance;
        if (manager == null || !manager.IsConnected)
        {
            ModLogger.Warning("[ChatInput] Not connected");
            Toggle(false);
            return;
        }

        ModLogger.Msg($"[ChatInput] Sending: {text}");
        manager.SendChat(text);
        Toggle(false);
    }
}
