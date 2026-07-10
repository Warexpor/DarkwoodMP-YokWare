using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde
{
    public class ManualSaveSlotMeta
    {
        public int day;
        public int chapter;
        public string timeSaved;
        public int majorVersion;
        public int minorVersion;
        public bool hasData;
    }

    public sealed class ManualSaveGUI : MonoBehaviour
    {
        private static ManualSaveGUI _instance;
        private bool _visible;
        private bool _scheduledBackupRestore;
        private Rect _windowRect;
        private bool _windowRectInitialized;
        private Vector2 _scroll;
        private string _statusMsg = "";
        private float _statusTimer;
        private bool _confirmingOverwrite;
        private int _pendingSlot;
        private bool _pendingIsSave;

        private const int SlotCount = 10;
        private readonly ManualSaveSlotMeta[] _slotMetas = new ManualSaveSlotMeta[SlotCount];
        private static string[] _slotPaths;

        private static float UiScale => Mathf.Clamp(Screen.height / 900f, 1f, 2f);

        private static string SaveDir => Application.persistentDataPath + "/1_4Save";
        private static string SlotsBase => SaveDir + "/manual_saves";

        public static void ToggleVisible()
        {
            if (_instance == null) return;

            if (!_instance._visible)
            {
                if (Core.mainMenu || Core.loadingGame || Core.forbidInputs)
                    return;
            }

            _instance._visible = !_instance._visible;
            if (_instance._visible)
                _instance.RefreshMetas();
        }

        public static void EnsureExists()
        {
            if (_instance != null) return;
            GameObject go = new GameObject("ManualSaveGUI");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ManualSaveGUI>();
            _instance.Init();
        }

        private void Init()
        {
            _slotPaths = new string[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                _slotPaths[i] = SlotsBase + "/slot" + (i + 1);
                Directory.CreateDirectory(_slotPaths[i]);
            }
            RefreshMetas();
        }

        private void RefreshMetas()
        {
            for (int i = 0; i < SlotCount; i++)
                _slotMetas[i] = ReadMeta(i);
        }

        private ManualSaveSlotMeta ReadMeta(int idx)
        {
            string metaPath = _slotPaths[idx] + "/meta.json";
            if (File.Exists(metaPath))
            {
                try
                {
                    var m = JsonConvert.DeserializeObject<ManualSaveSlotMeta>(File.ReadAllText(metaPath));
                    if (m != null) return m;
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.LogError("[ManualSave] failed to read slot meta: " + ex);
                }
            }
            return new ManualSaveSlotMeta { hasData = File.Exists(_slotPaths[idx] + "/sav.dat") };
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F3) && !Core.mainMenu && !Core.loadingGame && !Core.forbidInputs)
            {
                _visible = !_visible;
                if (_visible) RefreshMetas();
            }

            if (_statusTimer > 0)
            {
                _statusTimer -= Time.unscaledDeltaTime;
                if (_statusTimer <= 0) _statusMsg = "";
            }

            if (_scheduledBackupRestore && Player.Instance != null)
            {
                _scheduledBackupRestore = false;
                // Local self snapshot (not host-keyed peer file) — multi-client safe
                var data = Networking.ClientStateBackup.LoadLocalSelfBackupFile();
                if (data != null)
                {
                    Networking.ClientStateBackup.RestoreFromBackup(data);
                    ModRuntime.LegacyInfo("[ManualSave] restored local self backup after load");
                }
            }
        }

        private void SetStatus(string msg)
        {
            _statusMsg = msg;
            _statusTimer = 3f;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            if (!_windowRectInitialized)
            {
                _windowRect = new Rect(Screen.width / 2 - 300f, Screen.height / 2 - 250f, 600f, 500f);
                _windowRectInitialized = true;
            }

            Matrix4x4 old = GUI.matrix;
            float s = UiScale;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));

            Rect sr = new Rect(_windowRect.x / s, _windowRect.y / s, _windowRect.width / s, _windowRect.height / s);
            sr = GUI.Window(987655, sr, DrawWindow, "Manual Saves (F3)");
            _windowRect = new Rect(sr.x * s, sr.y * s, sr.width * s, sr.height * s);

            GUI.matrix = old;
        }

        private void DrawWindow(int id)
        {
            if (!string.IsNullOrEmpty(_statusMsg))
            {
                GUI.color = Color.yellow;
                GUILayout.Label(_statusMsg);
                GUI.color = Color.white;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Profile " + Core.currentProfile.id + " | Day " + Core.currentProfile.day + " | Ch." + Core.currentProfile.chapter, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("X", GUILayout.Width(24))) { _visible = false; }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            _scroll = GUILayout.BeginScrollView(_scroll);

            for (int i = 0; i < SlotCount; i++)
                DrawSlotRow(i);

            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private void DrawSlotRow(int idx)
        {
            ManualSaveSlotMeta m = _slotMetas[idx];
            GUILayout.BeginHorizontal(GUILayout.Height(36f));

            GUILayout.Label("Slot " + (idx + 1), GUILayout.Width(70f));

            if (m.hasData)
            {
                string info = "Day " + m.day + " | Ch." + m.chapter;
                if (!string.IsNullOrEmpty(m.timeSaved))
                    info += " | " + m.timeSaved;
                GUILayout.Label(info, GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label("[Empty]", GUILayout.ExpandWidth(true));
            }

            if (_confirmingOverwrite && _pendingSlot == idx)
            {
                GUILayout.Label("Overwrite?", GUILayout.Width(90f));
                if (GUILayout.Button("Yes", GUILayout.Width(50f)))
                {
                    _confirmingOverwrite = false;
                    if (_pendingIsSave) DoSave(idx);
                    else DoLoad(idx);
                }
                if (GUILayout.Button("No", GUILayout.Width(50f)))
                    _confirmingOverwrite = false;
            }
            else
            {
                if (GUILayout.Button("Save", GUILayout.Width(60f)))
                {
                    if (_slotMetas[idx].hasData)
                    {
                        _confirmingOverwrite = true;
                        _pendingSlot = idx;
                        _pendingIsSave = true;
                    }
                    else
                    {
                        DoSave(idx);
                    }
                }

                GUI.enabled = m.hasData;
                if (GUILayout.Button("Load", GUILayout.Width(60f)))
                    DoLoad(idx);
                GUI.enabled = true;
            }

            GUILayout.EndHorizontal();
        }

        private void DoSave(int idx)
        {
            try
            {
                if (Singleton<SaveManager>.Instance == null)
                {
                    SetStatus("Error: SaveManager not available");
                    return;
                }

                // P2.3: block manual save during partial night death or active dream session
                if (DeathStateTracker.LocalNightDeath && !DeathStateTracker.AllDeadAtNight)
                {
                    SetStatus("Blocked: cannot save during partial night death");
                    return;
                }
                if (Sync.DreamSession.IsActive || Sync.DreamSyncManager.IsLocalDreamActive)
                {
                    SetStatus("Blocked: cannot save during dream session");
                    return;
                }

                Singleton<SaveManager>.Instance.Save(true, true, true, false, true);

                string profDir = SaveDir + "/prof" + Core.currentProfile.id;
                string slotDir = _slotPaths[idx];

                CopyIfExists(profDir + "/sav.dat", slotDir + "/sav.dat");
                CopyIfExists(profDir + "/savs.dat", slotDir + "/savs.dat");
                CopyIfExists(profDir + "/savch.dat", slotDir + "/savch.dat");

                var meta = new ManualSaveSlotMeta
                {
                    day = Core.currentProfile.day,
                    chapter = Core.currentProfile.chapter,
                    timeSaved = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    majorVersion = Core.majorVersion,
                    minorVersion = Core.minorVersion,
                    hasData = true
                };
                File.WriteAllText(slotDir + "/meta.json", JsonConvert.SerializeObject(meta, Formatting.Indented));

                _slotMetas[idx] = meta;
                SetStatus("Saved to slot " + (idx + 1));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ManualSave] Save error: " + ex);
                SetStatus("Save error: " + ex.Message);
            }
        }

        private void DoLoad(int idx)
        {
            try
            {
                if (!File.Exists(_slotPaths[idx] + "/sav.dat"))
                {
                    SetStatus("Slot " + (idx + 1) + " is empty");
                    return;
                }

                if (Core.currentProfile == null)
                {
                    SetStatus("Error: no active profile");
                    return;
                }

                if (Singleton<SaveManager>.Instance == null)
                {
                    SetStatus("Error: SaveManager not available");
                    return;
                }

                ManualSaveSlotMeta meta = _slotMetas[idx];
                string profDir = SaveDir + "/prof" + Core.currentProfile.id;
                string slotDir = _slotPaths[idx];

                CopyIfExists(slotDir + "/sav.dat", profDir + "/sav.dat");
                CopyIfExists(slotDir + "/savs.dat", profDir + "/savs.dat");
                CopyIfExists(slotDir + "/savch.dat", profDir + "/savch.dat");

                Core.currentProfile.day = meta.day;
                Core.currentProfile.chapter = meta.chapter;
                Core.currentProfile.timeSaved = meta.timeSaved;
                Core.currentProfile.majorVersion = meta.majorVersion;
                Core.currentProfile.minorVersion = meta.minorVersion;

                // Snapshot this machine's inventory before overwriting with host world files
                var backupData = Networking.ClientStateBackup.CollectBackupData();
                string backupJson = Networking.ClientStateBackup.SerializeToJson(backupData);
                Networking.ClientStateBackup.SaveLocalSelfBackupFile(backupJson);

                Singleton<SaveManager>.Instance.saveGameProfiles();

                _visible = false;

                // Reset multiplayer state before loading save
                Sync.WorldPhysicsSyncService.Reset();
                Sync.DreamSyncManager.OnDisconnected();
                Sync.MultiplayerMapManager.Reset();

                _scheduledBackupRestore = true;

                int chapterId = meta.chapter > 0 ? meta.chapter : 1;

                Core.coreStarted = false;
                Core.mainMenu = false;
                Core.loadingGame = true;
                Core.loadedGame = true;
                Time.timeScale = 1f;

                if (Singleton<MainMenu>.Instance != null)
                    Singleton<MainMenu>.Instance.close();

                SceneManager.LoadScene("chapter" + chapterId);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ManualSave] Load error: " + ex);
                SetStatus("Load error: " + ex.Message);
            }
        }

        private static void CopyIfExists(string src, string dst)
        {
            if (File.Exists(src))
                File.Copy(src, dst, true);
        }
    }
}
