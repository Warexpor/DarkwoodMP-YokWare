using DWMPHorde;
using UnityEngine;
using YokWare.VanillaMenu;

namespace YokWare.ManualSaves
{
    /// <summary>
    /// The Saves screen of the pause menu, built like the game's own menus: F3 in the world opens
    /// the pause menu on it. Ten slots of the current profile, each with Save and Load; overwriting
    /// a slot and loading one are confirmed in the vanilla yes/no box.
    /// </summary>
    public sealed class SavesScreen : MonoBehaviour
    {
        private VmScreen _screen;
        private readonly SaveSlots _slots = new SaveSlots();
        private tk2dTextMesh _status;
        private string _statusLine;
        private float _statusUntil;
        private bool _openedPause;

        private const float RowTopZ = 130f;
        private const float RowStep = 32f;
        private const float SlotNameX = -230f;
        private const float InfoX = -205f;
        private const float SaveX = 200f;
        private const float LoadX = 290f;

        private static string T(string english) => AddOnApi.Translate(english);

        private void Awake()
        {
            _screen = new VmScreen("Saves") { Build = Build, Tick = TickStatus, OnBack = Back };
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F3))
                Toggle();
        }

        private bool InWorld => Core.coreStarted && !Core.loadingGame && Player.Instance != null;

        private void Toggle()
        {
            if (_screen.Visible)
            {
                Back();
                if (_openedPause && Core.mainMenu)
                    Core.showHidePauseMenu();
                _openedPause = false;
                return;
            }
            if (!InWorld || Core.currentProfile == null)
                return;
            MainMenu menu = Singleton<MainMenu>.Instance;
            if (menu == null)
                return;
            if (!Core.mainMenu)
            {
                // Only when the pause menu could open by Esc too (no dialogue, cutscene, inventory).
                Player p = Player.Instance;
                if (Core.forbidInputs || !p.alive || p.dying || p.inMenu())
                    return;
                Core.showHidePauseMenu();
                _openedPause = true;
            }
            if (!menu.gameObject.activeInHierarchy)
                return;
            _slots.Refresh();
            _statusLine = null;
            Vm.Open(_screen);
        }

        private void Back()
        {
            _openedPause = false;
            Vm.CloseAll(restoreMenu: true);
        }

        private void Build(VmBuilder b)
        {
            b.Header("Saves");
            _status = b.Label("", 0f, -245f, TextAnchor.MiddleCenter, Vm.Grey, 560);
            TickStatus();

            GameProfile profile = Core.currentProfile;
            if (profile != null)
                b.Label(Vm.Vanilla("Profile") + " " + profile.id + " · " + ChapterDay(profile.chapter, profile.day),
                    0f, 175f, TextAnchor.MiddleCenter, Vm.Grey);

            bool canSave = SaveSlots.SaveBlockReason() == null;
            bool canLoad = SaveSlots.LoadBlockReason() == null;
            for (int i = 0; i < SaveSlots.Count; i++)
            {
                int idx = i;
                float z = RowTopZ - RowStep * i;
                ManualSaveSlotMeta m = _slots.Metas[i];
                b.Name(T("Slot") + " " + (i + 1), z, SlotNameX);
                string info = m.hasData
                    ? ChapterDay(m.chapter, m.day) + (string.IsNullOrEmpty(m.timeSaved) ? "" : " · " + m.timeSaved)
                        + (_slots.IsLegacy[i] ? " · " + T("shared (old)") : "")
                    : T("Empty");
                b.Label(info, InfoX, z, TextAnchor.MiddleLeft, m.hasData ? new Color(0.7058824f, 0.7058824f, 0.7058824f, 1f) : Vm.Dim);
                b.Value(T("Save"), z, () => AskSave(idx), SaveX, enabled: canSave);
                b.Value(T("Load"), z, () => AskLoad(idx), LoadX, enabled: canLoad && m.hasData);
            }
            b.Back(Back, -205f);
        }

        private static string ChapterDay(int chapter, int day)
        {
            return Vm.Vanilla("chapter") + " " + Mathf.Clamp(chapter, 1, 10) + ", " + Vm.Vanilla("Day") + " " + day;
        }

        private void AskSave(int idx)
        {
            if (!_slots.Metas[idx].hasData)
            {
                DoSave(idx);
                return;
            }
            _screen.Confirm("Overwrite slot " + (idx + 1) + "?", yes => { if (yes) DoSave(idx); });
        }

        private void DoSave(int idx)
        {
            Status(_slots.Save(idx));
            _screen.Rebuild();
        }

        private void AskLoad(int idx)
        {
            _screen.Confirm("Load slot " + (idx + 1) + "? Progress since your last save is lost.", yes =>
            {
                if (!yes)
                    return;
                string err = _slots.Load(idx);
                if (err != null)
                {
                    Status(err);
                    _screen.Rebuild();
                }
            });
        }

        private void Status(string english)
        {
            _statusLine = english;
            _statusUntil = Time.realtimeSinceStartup + 5f;
        }

        private void TickStatus()
        {
            if (_status == null)
                return;
            string line = _statusLine != null && Time.realtimeSinceStartup < _statusUntil ? T(_statusLine) : "";
            if (_status.text != line)
                Vm.SetText(_status, line);
        }
    }
}
