using System;
using UnityEngine;

namespace DarkwoodMP.UI;

/// <summary>
/// IMGUI skin styled after Darkwood's own look: near-black brown panels,
/// bone-colored text, blood-red accents, thin dark borders. Built lazily on
/// the first OnGUI call; tries to reuse one of the game's own fonts.
/// </summary>
public static class DarkwoodTheme
{
    // Palette
    public static readonly Color Bg = new Color(0.055f, 0.045f, 0.038f, 0.97f);
    public static readonly Color Panel = new Color(0.10f, 0.082f, 0.065f, 0.97f);
    public static readonly Color Border = new Color(0.33f, 0.10f, 0.075f, 1f);
    public static readonly Color Accent = new Color(0.62f, 0.16f, 0.11f, 1f);
    public static readonly Color Text = new Color(0.80f, 0.74f, 0.62f, 1f);
    public static readonly Color TextDim = new Color(0.52f, 0.48f, 0.40f, 1f);
    public static readonly Color Ok = new Color(0.55f, 0.58f, 0.30f, 1f);
    public static readonly Color Warn = new Color(0.75f, 0.55f, 0.25f, 1f);
    public static readonly Color ButtonBg = new Color(0.15f, 0.115f, 0.088f, 0.97f);
    public static readonly Color ButtonHover = new Color(0.22f, 0.155f, 0.11f, 0.97f);
    public static readonly Color ButtonActive = new Color(0.34f, 0.12f, 0.09f, 0.97f);
    public static readonly Color FieldBg = new Color(0.04f, 0.033f, 0.028f, 0.97f);

    private static GUISkin _skin;
    private static GUIStyle _title;
    private static GUIStyle _header;
    private static GUIStyle _dim;
    private static GUIStyle _accentLabel;
    private static GUIStyle _statusLine;
    private static GUIStyle _chatName;
    private static GUIStyle _chatSystem;
    private static GUIStyle _okLabel;
    private static GUIStyle _warnLabel;

    public static GUISkin Skin { get { EnsureBuilt(); return _skin; } }
    public static GUIStyle Title { get { EnsureBuilt(); return _title; } }
    public static GUIStyle Header { get { EnsureBuilt(); return _header; } }
    public static GUIStyle Dim { get { EnsureBuilt(); return _dim; } }
    public static GUIStyle AccentLabel { get { EnsureBuilt(); return _accentLabel; } }
    public static GUIStyle StatusLine { get { EnsureBuilt(); return _statusLine; } }
    public static GUIStyle ChatName { get { EnsureBuilt(); return _chatName; } }
    public static GUIStyle ChatSystem { get { EnsureBuilt(); return _chatSystem; } }
    public static GUIStyle OkLabel { get { EnsureBuilt(); return _okLabel; } }
    public static GUIStyle WarnLabel { get { EnsureBuilt(); return _warnLabel; } }

    private static void EnsureBuilt()
    {
        if (_skin != null) return;

        var font = FindGameFont();

        // COPY the live runtime skin instead of starting from an empty one:
        // a blank GUISkin lacks working text-cursor/selection settings and all
        // the internal styles IMGUI relies on - that broke text input in v0.2.
        _skin = UnityEngine.Object.Instantiate(GUI.skin);
        _skin.hideFlags = HideFlags.HideAndDontSave;
        _skin.settings.cursorColor = Text;
        _skin.settings.selectionColor = new Color(Accent.r, Accent.g, Accent.b, 0.55f);
        _skin.settings.cursorFlashSpeed = 0f; // always-on caret, like a terminal
        if (font != null) _skin.font = font;

        var panelTex = MakeBorderedTex(Panel, Border);
        var bgTex = MakeBorderedTex(Bg, Border);
        var fieldTex = MakeBorderedTex(FieldBg, new Color(0.22f, 0.16f, 0.12f, 1f));
        var buttonTex = MakeBorderedTex(ButtonBg, new Color(0.26f, 0.18f, 0.13f, 1f));
        var buttonHoverTex = MakeBorderedTex(ButtonHover, Border);
        var buttonActiveTex = MakeBorderedTex(ButtonActive, Accent);
        var flatDark = MakeTex(new Color(0f, 0f, 0f, 0.55f));

        // Window
        _skin.window = new GUIStyle
        {
            normal = { background = bgTex, textColor = Accent },
            onNormal = { background = bgTex, textColor = Accent },
            border = new RectOffset(3, 3, 3, 3),
            padding = new RectOffset(12, 12, 24, 12),
            contentOffset = new Vector2(0, -18f),
            alignment = TextAnchor.UpperCenter,
            fontStyle = FontStyle.Bold,
            fontSize = 13
        };

        // Box (used as panel background)
        _skin.box = new GUIStyle
        {
            normal = { background = panelTex, textColor = Text },
            border = new RectOffset(2, 2, 2, 2),
            padding = new RectOffset(8, 8, 6, 6)
        };

        _skin.label = new GUIStyle
        {
            normal = { textColor = Text },
            padding = new RectOffset(2, 2, 2, 2),
            wordWrap = false,
            fontSize = 12
        };

        _skin.button = new GUIStyle
        {
            normal = { background = buttonTex, textColor = Text },
            hover = { background = buttonHoverTex, textColor = Color.white },
            active = { background = buttonActiveTex, textColor = Color.white },
            focused = { background = buttonHoverTex, textColor = Color.white },
            border = new RectOffset(2, 2, 2, 2),
            padding = new RectOffset(10, 10, 5, 5),
            margin = new RectOffset(2, 2, 3, 3),
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12
        };

        _skin.textField = new GUIStyle
        {
            normal = { background = fieldTex, textColor = Text },
            hover = { background = fieldTex, textColor = Text },
            focused = { background = MakeBorderedTex(FieldBg, Accent), textColor = Color.white },
            border = new RectOffset(2, 2, 2, 2),
            padding = new RectOffset(6, 6, 4, 4),
            margin = new RectOffset(2, 2, 3, 3),
            fontSize = 12,
            clipping = TextClipping.Clip
        };

        _skin.scrollView = new GUIStyle { normal = { background = flatDark } };
        _skin.verticalScrollbar = new GUIStyle
        {
            normal = { background = MakeTex(new Color(0.09f, 0.07f, 0.055f, 0.9f)) },
            fixedWidth = 8f
        };
        _skin.verticalScrollbarThumb = new GUIStyle
        {
            normal = { background = MakeTex(new Color(0.30f, 0.12f, 0.09f, 1f)) },
            fixedWidth = 8f
        };

        // Named helper styles
        _title = new GUIStyle(_skin.label)
        {
            normal = { textColor = Accent },
            fontStyle = FontStyle.Bold,
            fontSize = 14,
            alignment = TextAnchor.MiddleCenter
        };
        _header = new GUIStyle(_skin.label)
        {
            normal = { textColor = Accent },
            fontStyle = FontStyle.Bold,
            fontSize = 12
        };
        _dim = new GUIStyle(_skin.label) { normal = { textColor = TextDim }, fontSize = 11 };
        _accentLabel = new GUIStyle(_skin.label) { normal = { textColor = Warn }, wordWrap = true };
        _statusLine = new GUIStyle(_skin.label)
        {
            normal = { background = MakeTex(new Color(0f, 0f, 0f, 0.45f)), textColor = TextDim },
            padding = new RectOffset(8, 8, 3, 3),
            fontSize = 11
        };
        _chatName = new GUIStyle(_skin.label) { normal = { textColor = Accent }, fontStyle = FontStyle.Bold, fontSize = 12 };
        _chatSystem = new GUIStyle(_skin.label) { normal = { textColor = TextDim }, fontStyle = FontStyle.Italic, fontSize = 11 };
        _okLabel = new GUIStyle(_skin.label) { normal = { textColor = Ok } };
        _warnLabel = new GUIStyle(_skin.label) { normal = { textColor = Warn } };

        if (font != null)
        {
            foreach (var s in new[] { _title, _header, _dim, _accentLabel, _statusLine, _chatName, _chatSystem, _okLabel, _warnLabel })
                s.font = font;
        }

        ModLogger.Msg($"[DarkwoodTheme] Skin built (font: {(font != null ? font.name : "unity default")})");
    }

    /// <summary>
    /// Borrow one of the game's own fonts so the mod UI blends in - but only
    /// if it can actually render text (icon/glyph fonts would make the whole
    /// mod UI unreadable, which looked like "chat not working").
    /// </summary>
    private static Font FindGameFont()
    {
        try
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f == null || string.IsNullOrEmpty(f.name)) continue;
                var n = f.name.ToLowerInvariant();
                // Skip Unity's built-in fallback
                if (n == "arial" || n.Contains("liberation")) continue;

                try
                {
                    if (f.HasCharacter('A') && f.HasCharacter('a') && f.HasCharacter('0') && f.HasCharacter(':'))
                        return f;
                    ModLogger.Msg($"[DarkwoodTheme] Skipping font '{f.name}' (missing basic glyphs)");
                }
                catch { /* try the next one */ }
            }
        }
        catch { /* fall back to default */ }
        return null;
    }

    private static Texture2D MakeTex(Color color)
    {
        var tex = new Texture2D(1, 1, TextureFormat.ARGB32, false) { hideFlags = HideFlags.HideAndDontSave };
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }

    private static Texture2D MakeBorderedTex(Color fill, Color border)
    {
        const int size = 8;
        var tex = new Texture2D(size, size, TextureFormat.ARGB32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var isBorder = x == 0 || y == 0 || x == size - 1 || y == size - 1;
                tex.SetPixel(x, y, isBorder ? border : fill);
            }
        }
        tex.Apply();
        return tex;
    }
}
