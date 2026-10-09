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
        internal static void FollowScale(tk2dTextMesh tm)
        {
            GameObject src = Source;
            if (tm == null || src == null)
                return;
            Transform t = tm.transform;
            if (t.localScale != src.transform.localScale)
                t.localScale = src.transform.localScale;
            if (t.rotation != src.transform.rotation)
                t.rotation = src.transform.rotation;
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

        /// <summary>Place at a UI-space point (screen pixels), at the hover label's depth.</summary>
        internal static void Place(tk2dTextMesh tm, float x, float z)
        {
            GameObject src = Source;
            if (tm == null || src == null)
                return;
            tm.transform.position = new Vector3(x, src.transform.position.y, z);
        }

        /// <summary>The game's own resolution factor for HUD offsets (1 at 1080p).</summary>
        internal static float Scale => Mathf.Min(Core.ResolutionHeightModifier, Core.ResolutionWidthModifier);
    }
}
