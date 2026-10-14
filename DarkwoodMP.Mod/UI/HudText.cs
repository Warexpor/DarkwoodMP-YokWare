using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// In-world text in the look of the game's own hover label (the name shown over what the
    /// cursor points at): the same outlined tahoma, scaled with the resolution like the HUD, on the
    /// UI camera. Positions are UI space: x right and z up in screen pixels.
    /// </summary>
    internal static class HudText
    {
        /// <summary>The vanilla hover label the copies are made from (lives under the scene's UI).</summary>
        internal static GameObject Source
        {
            get
            {
                Player p = Player.Instance;
                return p != null ? p.MouseText : null;
            }
        }

        /// <summary>A copy of the hover label, hidden, under the vanilla UI. Null while there is no UI.</summary>
        internal static tk2dTextMesh Create(string name)
        {
            GameObject src = Source;
            if (src == null || src.transform.parent == null)
                return null;
            GameObject go = Object.Instantiate(src, src.transform.parent, false);
            go.name = name;
            // Its scale is copied from the source every frame (FollowScale): the source's own
            // ScaleToResolution already applied the resolution, a second one would apply it twice.
            ScaleToResolution str = go.GetComponent<ScaleToResolution>();
            if (str != null)
                Object.DestroyImmediate(str);
            go.SetActive(false);
            tk2dTextMesh tm = go.GetComponent<tk2dTextMesh>();
            if (tm == null)
            {
                Object.Destroy(go);
                return null;
            }
            tm.inlineStyling = false;
            tm.formatting = false;
            tm.wordWrapWidth = 0;
            tm.text = "";
            tm.Commit();
            return tm;
        }

        /// <summary>Keep the copy at the hover label's scale and facing (resolution changes).</summary>
        internal static void FollowScale(tk2dTextMesh tm) => FollowScale(tm, Source);

        /// <summary><see cref="FollowScale(tk2dTextMesh)"/> with <see cref="Source"/> read once by the caller.</summary>
        internal static void FollowScale(tk2dTextMesh tm, GameObject src)
        {
            if (tm == null || src == null)
                return;
            Transform t = tm.transform;
            Transform st = src.transform;
            Vector3 scale = st.localScale;
            if (t.localScale != scale)
                t.localScale = scale;
            Quaternion rot = st.rotation;
            if (t.rotation != rot)
                t.rotation = rot;
        }

        /// <summary>Set text and colour, rebuilding the mesh only when either changed.</summary>
        internal static void Set(tk2dTextMesh tm, string text, Color color)
        {
            if (tm == null)
                return;
            text = YokWare.VanillaMenu.Vm.ForMenuFont(text ?? "");
            bool dirty = false;
            if (tm.maxChars < text.Length + 4)
            {
                tm.maxChars = text.Length + 32;
                dirty = true;
            }
            if (tm.text != text)
            {
                tm.text = text;
                dirty = true;
            }
            if (tm.color != color)
            {
                tm.color = color;
                dirty = true;
            }
            if (dirty)
                tm.Commit();
        }

        /// <summary>Size of the text in UI pixels (x across, y up).</summary>
        internal static Vector2 Size(tk2dTextMesh tm, string text)
        {
            if (tm == null || string.IsNullOrEmpty(text))
                return Vector2.zero;
            Bounds b = tm.GetEstimatedMeshBoundsForString(YokWare.VanillaMenu.Vm.ForMenuFont(text));
            Vector3 s = tm.transform.localScale;
            return new Vector2(Mathf.Abs(b.size.x * s.x), Mathf.Abs(b.size.y * s.y));
        }

        /// <summary>
        /// <see cref="Size"/> of one text, remembered: measured again only when the text or what the
        /// measure depends on (font, scale, wrap, max chars) changed. Keep one per text and mesh.
        /// </summary>
        internal struct SizeCache
        {
            private bool _valid;
            private string _text;
            private tk2dFontData _font;
            private Vector3 _local;
            private Vector3 _scale;
            private bool _formatting;
            private int _wrap;
            private int _maxChars;
            private Vector2 _size;

            internal Vector2 Get(tk2dTextMesh tm, string text)
            {
                if (tm == null || string.IsNullOrEmpty(text))
                    return Vector2.zero;
                tk2dFontData font = tm.font;
                Vector3 local = tm.transform.localScale;
                Vector3 scale = tm.scale;
                bool formatting = tm.formatting;
                int wrap = tm.wordWrapWidth;
                int maxChars = tm.maxChars;
                if (_valid && ReferenceEquals(font, _font) && local == _local && scale == _scale
                    && formatting == _formatting && wrap == _wrap && maxChars == _maxChars
                    && string.Equals(text, _text, System.StringComparison.Ordinal))
                    return _size;
                _valid = true;
                _text = text;
                _font = font;
                _local = local;
                _scale = scale;
                _formatting = formatting;
                _wrap = wrap;
                _maxChars = maxChars;
                _size = Size(tm, text);
                return _size;
            }
        }

        /// <summary>Place at a UI-space point (screen pixels), at the hover label's depth.</summary>
        internal static void Place(tk2dTextMesh tm, float x, float z) => Place(tm, Source, x, z);

        /// <summary><see cref="Place(tk2dTextMesh, float, float)"/> with <see cref="Source"/> read once by the caller.</summary>
        internal static void Place(tk2dTextMesh tm, GameObject src, float x, float z)
        {
            if (tm == null || src == null)
                return;
            tm.transform.position = new Vector3(x, src.transform.position.y, z);
        }

        /// <summary>One of the game's fonts by its data name (FontsDB, e.g. <c>tahoma_11px_light_AAdata</c>), or null.</summary>
        internal static tk2dFontData Font(string dataName)
        {
            FontsDB db = Singleton<FontsDB>.Instance;
            if (db == null || db.fonts == null)
                return null;
            for (int i = 0; i < db.fonts.Count; i++)
            {
                tk2dFontData f = db.fonts[i];
                if (f != null && f.name == dataName)
                    return f;
            }
            return null;
        }

        /// <summary>The game's own resolution factor for HUD offsets (1 at 1080p).</summary>
        internal static float Scale => Mathf.Min(Core.ResolutionHeightModifier, Core.ResolutionWidthModifier);
    }
}
