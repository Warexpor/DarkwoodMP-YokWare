using System;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Mid-menu after host world download: pick a permanent local profile slot,
    /// confirm overwrite, then hand off to ENTER WORLD.
    /// </summary>
    public sealed class JoinWorldSlotPicker : MonoBehaviour
    {
        private static JoinWorldSlotPicker _instance; // process-scoped: UI singleton

        private const string LockOwner = "slotpicker";

        private bool _confirmOverwrite;
        private bool _wasActive;
        private object _lastShare;
        private int _pendingSlot;
        private bool _pendingCampaignMismatch;
        private string _status = "";
        private float _statusTimer;
        private Vector2 _scroll;
        private Rect _windowRect;
        private bool _rectInit;

        private static float UiScale => Mathf.Clamp(Screen.height / 900f, 1f, 2f);

        public static void EnsureExists()
        {
            if (_instance != null) return;
            var go = new GameObject("DWMPHorde_JoinSlotPicker");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<JoinWorldSlotPicker>();
        }

        /// <summary>The picker is on screen: title menu, share downloaded, still connected, no terminal failure.</summary>
        private static bool IsPickerActive(out LanNetworkManager net)
        {
            net = ModRuntime.Network;
            var share = net?.WorldSaveShare;
            return share != null
                && net.Role != NetworkRole.Offline
                && share.IsAwaitingSlotPick
                && !share.HasTerminalShareFailure
                && GameScreen.AtTitle;
        }

        /// <summary>Drop a pending overwrite confirm / stale status (open, close, disconnect, new share).</summary>
        private void ResetPickerState()
        {
            _confirmOverwrite = false;
            _pendingCampaignMismatch = false;
            _pendingSlot = 0;
            _status = "";
            _statusTimer = 0f;
            _scroll = Vector2.zero;
        }

        private void OnGUI()
        {
            if (!IsPickerActive(out LanNetworkManager net))
                return;
            var share = net.WorldSaveShare;

            if (!_rectInit)
            {
                float w = 520f;
                float h = 420f;
                _windowRect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
                _rectInit = true;
            }

            Matrix4x4 old = GUI.matrix;
            float s = UiScale;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            Rect sr = new Rect(_windowRect.x / s, _windowRect.y / s, _windowRect.width / s, _windowRect.height / s);
            sr = GUI.Window(987656, sr, DrawWindow, Loc.T("Permanent world copy — pick profile slot"));
            _windowRect = new Rect(sr.x * s, sr.y * s, sr.width * s, sr.height * s);
            GUI.matrix = old;
        }

        private void Update()
        {
            bool active = IsPickerActive(out LanNetworkManager net);
            object share = net?.WorldSaveShare;
            // Open / close / a different share (disconnect + rejoin): never carry a pending
            // "Overwrite & keep permanently" confirm for a slot onto the next session.
            if (active != _wasActive || (active && !ReferenceEquals(share, _lastShare)))
                ResetPickerState();
            _wasActive = active;
            _lastShare = active ? share : null;
            UiInputLock.Set(LockOwner, active);

            if (_statusTimer > 0f)
            {
                _statusTimer -= Time.unscaledDeltaTime;
                if (_statusTimer <= 0f)
                    _status = "";
            }
        }

        private void DrawWindow(int id)
        {
            var net = ModRuntime.Network;
            var share = net?.WorldSaveShare;
            if (share == null)
                return;

            GUILayout.Label(Loc.T(
                "Host world downloaded. Choose which local PLAY profile keeps a permanent copy.\n"
                + "It stays on this machine until you delete that profile. Live co-op still uses the host's world."));
            GUILayout.Space(6f);

            if (!string.IsNullOrEmpty(_status))
            {
                GUI.color = Color.yellow;
                GUILayout.Label(Loc.T(_status));
                GUI.color = Color.white;
            }

            if (_confirmOverwrite)
            {
                GUILayout.Space(8f);
                GUI.color = new Color(1f, 0.55f, 0.35f);
                if (_pendingCampaignMismatch)
                {
                    GUILayout.Label(Loc.T(
                        WrongSaveWarning.Format(
                            "Profile " + _pendingSlot
                            + " belongs to a different co-op campaign than this host.\n"
                            + "Overwrite will replace that save with the host world.")));
                }
                else
                {
                    GUILayout.Label(Loc.T(
                        "Profile " + _pendingSlot + " already has a save.\n"
                        + "Overwrite with the host world? This cannot be undone."));
                }
                GUI.color = Color.white;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Loc.T("Overwrite & keep permanently"), GUILayout.Height(32f)))
                {
                    Commit(_pendingSlot, overwriteConfirmed: true);
                    _confirmOverwrite = false;
                    _pendingCampaignMismatch = false;
                }
                if (GUILayout.Button(Loc.T("Cancel"), GUILayout.Height(32f), GUILayout.Width(100f)))
                {
                    _confirmOverwrite = false;
                    _pendingCampaignMismatch = false;
                }
                GUILayout.EndHorizontal();
                GUI.DragWindow();
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            ProfileSlotInfo[] slots = share.GetProfileSlotInfos();
            int preferred = 0;
            try
            {
                if (ModConfig.PreferredCoopCopySlot != null)
                    preferred = ModConfig.PreferredCoopCopySlot.Value;
            }
            catch { /* config optional */ }

            for (int i = 0; i < slots.Length; i++)
            {
                ProfileSlotInfo s = slots[i];
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();

                string title = Loc.T("Profile") + " " + s.Id;
                if (s.Id == preferred && preferred >= 1)
                    title += "  " + Loc.T("(last used)");
                GUILayout.Label(title, GUILayout.Width(140f));

                string detail;
                string dayCh = Loc.T("Day") + " " + s.Day + " " + Loc.T("Ch.") + s.Chapter;
                if (s.IsEmpty)
                    detail = Loc.T("[Empty] — safe to use");
                else if (s.IsCoopCopy)
                    detail = Loc.T("Co-op copy") + "  " + dayCh
                        + (string.IsNullOrEmpty(s.TimeSaved) ? "" : "  " + s.TimeSaved)
                        + (s.MatchesIncomingPackage ? "  " + Loc.T("[SAME AS HOST]") : "")
                        + (s.CampaignMismatchWithHost ? "  " + Loc.T("[DIFFERENT CAMPAIGN]") : "");
                else
                    detail = Loc.T("Campaign") + "  " + dayCh
                        + (string.IsNullOrEmpty(s.TimeSaved) ? "" : "  " + s.TimeSaved)
                        + (s.CampaignMismatchWithHost ? "  " + Loc.T("[DIFFERENT CAMPAIGN]") : "");

                if (s.CampaignMismatchWithHost)
                    GUI.color = new Color(1f, 0.55f, 0.35f);
                GUILayout.Label(detail, GUILayout.ExpandWidth(true));
                GUI.color = Color.white;

                string btn = Loc.T(s.IsEmpty ? "Use empty" : (s.IsCoopCopy ? "Update copy" : "Overwrite"));
                if (GUILayout.Button(btn, GUILayout.Width(130f), GUILayout.Height(28f)))
                    OnPickSlot(s);

                GUILayout.EndHorizontal();
                if (s.IsCoopCopy && !string.IsNullOrEmpty(s.CoopNote))
                {
                    GUI.color = new Color(0.75f, 0.85f, 1f);
                    GUILayout.Label(Loc.T(s.CoopNote));
                    GUI.color = Color.white;
                }
                GUILayout.EndVertical();
            }

            GUILayout.EndScrollView();
            GUILayout.Space(4f);
            GUILayout.Label(Loc.T("Tip: empty slots first. Deleting a profile in PLAY removes the permanent copy."));
            GUI.DragWindow();
        }

        private void OnPickSlot(ProfileSlotInfo s)
        {
            if (s.IsEmpty)
            {
                Commit(s.Id, overwriteConfirmed: true);
                return;
            }
            _pendingSlot = s.Id;
            _pendingCampaignMismatch = s.CampaignMismatchWithHost;
            _confirmOverwrite = true;
        }

        private void Commit(int slotId, bool overwriteConfirmed)
        {
            var net = ModRuntime.Network;
            var share = net?.WorldSaveShare;
            if (share == null)
                return;

            if (!share.TryCommitPermanentSlot(slotId, overwriteConfirmed, out string err))
            {
                _status = err ?? "Failed to save world copy";
                _statusTimer = 5f;
                ModLog.Warn(LogCat.Save, "Join slot commit failed: " + _status);
                return;
            }

            try
            {
                if (ModConfig.PreferredCoopCopySlot != null)
                    ModConfig.PreferredCoopCopySlot.Value = slotId;
            }
            catch { /* ignore */ }

            _status = "Permanent copy on Profile " + slotId + " — press ENTER WORLD";
            _statusTimer = 6f;
            ModLog.Event(LogCat.Save, "Permanent co-op world committed to profile slot " + slotId);
        }
    }
}
