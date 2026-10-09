using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde
{
    public static partial class MainMenuMultiplayerInject
    {
        private static void InjectMultiplayerButton()
        {
            GameObject template = _menu.quitBtn;
            if (template == null || template.GetComponent<Button>() == null)
                return;

            // Pixel art from the vanilla menu glyphs (matches PLAY/OPTIONS). Fallback = text label.
            _mpButton = CloneButton(template, template.transform.parent,
                MpButtonName, "MULTIPLAYER", MultiplayerScreens.OpenRoot, TagKindMp);

            float y = MultiplayerRowOffsetY();
            SetRow(_mpButton, y);
            WireButton(_mpButton, MultiplayerScreens.OpenRoot);

            // Attach after SetRow so collider bounds match final pose.
            if (!MenuButtonArt.TryAttachMultiplayerArt(_mpButton))
            {
                tk2dTextMesh tm = CreateLabel(_mpButton.transform, "MULTIPLAYER", settingsStyle: true);
                Button btn = _mpButton.GetComponent<Button>();
                if (btn != null && tm != null)
                    ApplySettingsButtonColors(btn, tm);
                FitButtonHitbox(_mpButton);
            }

            if (_mpButton != null)
                _mpButton.transform.SetAsLastSibling();

            ModLog.Event(LogCat.Session,
                "Injected MULTIPLAYER button @ " + Screen.width + "x" + Screen.height
                + " offsetY=" + y.ToString("F1") + (GameScreen.AtTitle ? " (title)" : " (pause)"));
        }

        /// <summary>
        /// The row MULTIPLAYER sits under: PLAY on the title screen, OPTIONS in the pause menu
        /// (RESUME, HELP, OPTIONS, then MULTIPLAYER above MAIN MENU and EXIT).
        /// </summary>
        private static PositionMe AnchorRow(MainMenu menu)
        {
            if (menu == null)
                return null;
            GameObject anchor = menu.playBtn != null && menu.playBtn.activeSelf ? menu.playBtn : menu.optionsBtn;
            return anchor != null ? anchor.GetComponent<PositionMe>() : null;
        }

        private static float MultiplayerRowOffsetY()
        {
            PositionMe anchor = AnchorRow(_menu);
            return (anchor != null ? anchor.offset.y : 60f) - RowSpacing;
        }

        /// <summary>How far the version / player-id labels move down (buttons move <see cref="RowSpacing"/>).</summary>
        private const float LabelShift = 30f;

        /// <summary>Rows moved down for MULTIPLAYER, with their vanilla offsets.</summary>
        private static readonly List<KeyValuePair<PositionMe, Vector2>> _shiftedRows = new List<KeyValuePair<PositionMe, Vector2>>(8); // reset-in: ResetMenuStack
        private static MainMenu _shiftedMenu; // reset-in: ResetMenuStack

        /// <summary>
        /// Every shown row under the anchor (title: OPTIONS, CREDITS, EXIT; pause: MAIN MENU, EXIT)
        /// moves one row down so MULTIPLAYER takes the row under the anchor; the version / player-id
        /// labels move half a row. Offsets are taken as vanilla set them (vanilla sets them again on
        /// every menu open, see <see cref="MenuStackOpenPatch"/>) and checked every UI poll.
        /// </summary>
        private static void ApplyMenuStack()
        {
            if (_menu == null || _menu.Menu0 == null)
                return;
            PositionMe anchor = AnchorRow(_menu);
            if (anchor == null)
                return;
            if (_shiftedMenu != _menu)
            {
                ResetMenuStack();
                _shiftedMenu = _menu;
                PositionMe[] pms = _menu.Menu0.GetComponentsInChildren<PositionMe>(true);
                for (int i = 0; i < pms.Length; i++)
                {
                    PositionMe pm = pms[i];
                    if (pm == null || pm == anchor || !pm.gameObject.activeSelf || pm.offset.y >= anchor.offset.y)
                        continue;
                    if (pm.GetComponent<YokWareUiTag>() != null || pm.gameObject.name.StartsWith("YokWare_", StringComparison.Ordinal))
                        continue;
                    if (pm.GetComponent<tk2dBaseSprite>() == null && pm.GetComponent<tk2dTextMesh>() == null)
                        continue;
                    _shiftedRows.Add(new KeyValuePair<PositionMe, Vector2>(pm, pm.offset));
                }
            }
            for (int i = 0; i < _shiftedRows.Count; i++)
            {
                PositionMe pm = _shiftedRows[i].Key;
                if (pm == null)
                    continue;
                // Buttons (sprites) move a full row; the text labels under the stack half a row.
                float shift = pm.GetComponent<tk2dBaseSprite>() != null ? RowSpacing : LabelShift;
                Vector2 want = _shiftedRows[i].Value - new Vector2(0f, shift);
                if (pm.offset == want)
                    continue;
                pm.offset = want;
                pm.init();
            }
        }

        /// <summary>Put the moved rows back at their vanilla offsets (menu closing / changing).</summary>
        private static void ResetMenuStack()
        {
            for (int i = 0; i < _shiftedRows.Count; i++)
            {
                PositionMe pm = _shiftedRows[i].Key;
                if (pm == null || !pm)
                    continue;
                pm.offset = _shiftedRows[i].Value;
                pm.init();
            }
            _shiftedRows.Clear();
            _shiftedMenu = null;
        }

        /// <summary>
        /// Vanilla <c>MainMenu.OnEnable</c> sets the button offsets for title or pause on every
        /// open: the moved rows go back first, then the stack is taken again from what vanilla set.
        /// </summary>
        [HarmonyPatch(typeof(MainMenu), "OnEnable")]
        internal static class MenuStackOpenPatch
        {
            private static void Prefix()
            {
                try { ResetMenuStack(); }
                catch (Exception ex) { ModLog.Warn(LogCat.Session, "menu stack reset: " + ex.Message); }
            }

            private static void Postfix(MainMenu __instance)
            {
                try
                {
                    if (!Core.mainMenu || __instance == null)
                        return;
                    _menu = __instance;
                    _wasAtTitle = GameScreen.AtTitle;
                    if (__instance.Menu0 == null || !__instance.Menu0.activeInHierarchy)
                        return;
                    ApplyMenuStack();
                    if (_mpButton != null && _mpButton)
                        SetRow(_mpButton, MultiplayerRowOffsetY());
                }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Session, "menu stack: " + ex.Message);
                }
            }
        }

        private static void SetRow(GameObject go, float y)
        {
            if (go == null || !go)
                return;
            PositionMe pm = go.GetComponent<PositionMe>();
            if (pm == null || pm.offset.y == y)
                return;
            pm.offset = new Vector2(pm.offset.x, y);
            pm.init();
        }

        private static void Guarded(Action a)
        {
            try { a(); }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Session, "menu click: " + ex.Message, ex);
            }
        }
    }
}
