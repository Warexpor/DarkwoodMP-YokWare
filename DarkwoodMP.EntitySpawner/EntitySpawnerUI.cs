using System.Collections.Generic;
using UnityEngine;

namespace YokWare.EntitySpawner
{
    /// <summary>
    /// From Horde remaster DebugTools — standalone, no multiplayer dependency.
    /// F5 toggle. Spawns via Core.AddPrefab near the local player.
    /// </summary>
    public sealed class EntitySpawnerUI : MonoBehaviour
    {
        private bool _show;
        private Vector2 _scroll;
        private string _filter = "";
        private string _filterPrev = "";
        private int _spawnCount = 1;
        private bool _confirmNight;
        private readonly List<string> _allEntities = new List<string>
        {
            "Characters/Dog",
            "Characters/DogMutated",
            "Characters/ChomperRed",
            "Characters/ChomperBlue",
            "Characters/ChomperGreen",
            "Characters/ChomperBlack",
            "Characters/ChomperRed_FOVmobility2",
            "Characters/ChomperHalf",
            "Characters/Centipede",
            "Characters/Pig",
            "Characters/Chicken",
            "Characters/Deer",
            "Characters/Kid",
            "Characters/Lizard",
            "Characters/Redneck",
            "Characters/Redneck02",
            "Characters/Villager",
            "Characters/Villager3_plank",
            "Characters/Villager_pistol",
            "Characters/Robber",
            "Characters/Banshee",
            "Characters/Ghost",
            "Characters/Spider01",
            "Characters/Spider03_day",
            "Characters/Tank",
            "Characters/Swamper1",
            "Characters/HumanSpider",
            "Characters/Villager1_Burning",
            "Characters/Wolfman_att",
            "Characters/BansheeBaby",
            "Characters/ForestSpirit2",
            "Characters/FakeChars/AreaBird",
            "characters/fakechars/shadow",
            "characters/fakechars/shadow_immortal",
            "characters/fakechars/NightWorms_01",
            "Characters/NPC/NightTrader",
            "Characters/NPC/TheThree",
            "Characters/NPC/Wolfman_att",
            "Characters/NPC/Porter",
        };

        private List<string> _filtered;

        private void Awake()
        {
            _filtered = new List<string>(_allEntities);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F5))
                _show = !_show;

            if (_filter != _filterPrev)
            {
                _filterPrev = _filter;
                ApplyFilter();
            }
        }

        private void ApplyFilter()
        {
            _filtered.Clear();
            if (string.IsNullOrWhiteSpace(_filter))
            {
                _filtered.AddRange(_allEntities);
                return;
            }
            string lower = _filter.ToLowerInvariant();
            foreach (string e in _allEntities)
            {
                if (e.ToLowerInvariant().Contains(lower))
                    _filtered.Add(e);
            }
        }

        private void OnGUI()
        {
            if (!_show) return;

            float w = 420f;
            float h = 600f;
            Rect rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUILayout.Window(991155, rect, DrawWindow, PluginInfo.Name + " (F5)");
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            GUILayout.Label("Spawn count: " + _spawnCount);
            _spawnCount = Mathf.RoundToInt(GUILayout.HorizontalSlider(_spawnCount, 1, 20));

            GUILayout.Label("Filter:");
            _filter = GUILayout.TextField(_filter);

            GUILayout.Space(4f);

            _scroll = GUILayout.BeginScrollView(_scroll);
            foreach (string entity in _filtered)
            {
                if (GUILayout.Button(entity, GUILayout.Height(28f)))
                    SpawnEntity(entity);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4f);

            var ctrl = Singleton<Controller>.Instance;
            bool canFF = ctrl != null
                && Singleton<Dreams>.Instance != null
                && !Singleton<Dreams>.Instance.dreaming
                && ctrl.CurrentTime < (int)ctrl.nightTime
                && ctrl.DoUpdateTime;

            if (_confirmNight)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Fast-forward to night?", GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Yes", GUILayout.Width(50f)))
                {
                    _confirmNight = false;
                    _show = false;
                    DoFastForwardToNight();
                }
                if (GUILayout.Button("No", GUILayout.Width(50f)))
                    _confirmNight = false;
                GUILayout.EndHorizontal();
            }
            else
            {
                GUI.enabled = canFF;
                if (GUILayout.Button("<< Fast Forward to Night >>", GUILayout.Height(28f)))
                    _confirmNight = true;
                GUI.enabled = true;
            }

            if (GUILayout.Button("Close", GUILayout.Height(30f)))
                _show = false;

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void DoFastForwardToNight()
        {
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;

            ctrl.CurrentTime = (int)ctrl.nightTime;
            ctrl.refreshTime();
            var save = Singleton<SaveManager>.Instance;
            if (save != null)
                save.saveGameProfiles();
        }

        private void SpawnEntity(string prefabPath)
        {
            Player player = Player.Instance;
            if (player == null) return;

            for (int i = 0; i < _spawnCount; i++)
            {
                Vector3 pos = player.transform.position + player.transform.forward * 2f;
                pos += new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f));

                Quaternion rot = Quaternion.Euler(90f, Random.Range(0f, 360f), 0f);
                GameObject go = Core.AddPrefab(prefabPath, pos, rot, null);
                if (go != null)
                {
#if BEPINEX
                    EntitySpawnerPlugin.Log?.LogInfo("[EntitySpawner] spawned " + prefabPath + " at " + pos);
#endif
                }
            }
        }
    }
}
