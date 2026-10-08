using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using Newtonsoft.Json;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The party map board kept next to the world (<c>prof{n}/dwmp_map_pins.json</c>), stamped with
    /// the world's campaign id so a different world on the same slot never inherits it. Every machine
    /// writes its copy a moment after the board changes: the host's pins survive a restart, and a
    /// client promoted by host migration (or hosting that copy later) has them too.
    /// </summary>
    internal static class MapPinStore
    {
        internal const string FileName = "dwmp_map_pins.json";
        private const float WriteDelay = 2f;

        [Serializable]
        public sealed class SaveFile
        {
            public string CampaignId;
            public int NextId;
            public List<SavedPin> Pins;
        }

        [Serializable]
        public sealed class SavedPin
        {
            public int Id;
            public int Kind;
            public int Color;
            public int Chapter;
            public float X, Z;
            public int Day;
            public string OwnerTag;
            public string OwnerName;
            public string Label;
        }

        private static bool _dirty;
        private static float _writeAt;

        internal static void MarkDirty()
        {
            if (!_dirty)
                _writeAt = Time.unscaledTime + WriteDelay;
            _dirty = true;
        }

        internal static void Tick()
        {
            if (!_dirty || Time.unscaledTime < _writeAt)
                return;
            Flush();
            if (_dirty)
                _writeAt = Time.unscaledTime + WriteDelay; // not writable yet: try again shortly
        }

        /// <summary>
        /// Write now if the board changed and the world it belongs to is the one loaded; otherwise
        /// keep it pending. The host writes into the world it seeded the board from; a client only
        /// while it plays in the host's world (its copy carries the host's campaign id then).
        /// </summary>
        internal static void Flush()
        {
            if (!_dirty)
                return;
            try
            {
                if (Core.currentProfile == null || DWMPHorde.GameScreen.AtTitle || Core.loadingGame || Player.Instance == null)
                    return;
                int pid = Core.currentProfile.id;
                if (pid < 1 || pid > 5)
                    return;
                var net = ModRuntime.Network;
                if (net == null)
                    return;
                string campaign = CoopWorldCopyMeta.TryGetCampaignIdForCurrentProfile();
                if (string.IsNullOrEmpty(campaign))
                    return;
                if (net.Role == NetworkRole.Host)
                {
                    if (!string.Equals(campaign, MapPinBoard.SeededCampaign, StringComparison.OrdinalIgnoreCase))
                        return;
                }
                else if (net.Role != NetworkRole.Client || !net.IsConnected || !LanNetworkManager.ClientCanApplyWorldBulk())
                    return;
                var file = new SaveFile
                {
                    CampaignId = campaign,
                    NextId = MapPinBoard.NextId,
                    Pins = new List<SavedPin>(MapPinBoard.Pins.Count)
                };
                foreach (MapPin p in MapPinBoard.Pins)
                {
                    file.Pins.Add(new SavedPin
                    {
                        Id = p.Id, Kind = (int)p.Kind, Color = p.Color, Chapter = p.Chapter, X = p.X, Z = p.Z,
                        Day = p.Day, OwnerTag = p.OwnerTag, OwnerName = p.OwnerName, Label = p.Label
                    });
                }
                string dir = CoopWorldCopyMeta.ProfileDir(pid);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, FileName);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(file, Formatting.Indented));
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
                _dirty = false;
            }
            catch (Exception ex)
            {
                _dirty = false; // a disk that refuses once will refuse every tick
                ModLog.Warn(LogCat.Save, "[MapPin] board save failed: " + ex.Message);
            }
        }

        /// <summary>The board saved for this world, or null (none, unreadable, or another world's).</summary>
        internal static List<MapPin> Load(int profileId, string campaignId, out int nextId)
        {
            nextId = 1;
            if (profileId < 1 || profileId > 5 || string.IsNullOrEmpty(campaignId))
                return null;
            try
            {
                string path = Path.Combine(CoopWorldCopyMeta.ProfileDir(profileId), FileName);
                if (!File.Exists(path))
                    return null;
                var file = JsonConvert.DeserializeObject<SaveFile>(File.ReadAllText(path));
                if (file == null || file.Pins == null)
                    return null;
                if (!string.Equals(file.CampaignId, campaignId, StringComparison.OrdinalIgnoreCase))
                {
                    ModLog.Event(LogCat.Save, "[MapPin] saved board belongs to another world; not loaded");
                    return null;
                }
                var list = new List<MapPin>(file.Pins.Count);
                foreach (SavedPin s in file.Pins)
                {
                    if (s == null || s.Id <= 0 || list.Count >= MapPinBoard.MaxPins) continue;
                    list.Add(new MapPin
                    {
                        Id = s.Id, Kind = MapPinBoard.ClampKind(s.Kind), Color = (byte)Mathf.Clamp(s.Color, 0, 255),
                        Chapter = s.Chapter, X = s.X, Z = s.Z, Day = s.Day, OwnerId = 0,
                        OwnerTag = s.OwnerTag ?? "", OwnerName = s.OwnerName ?? "", Label = MapPinBoard.SanitizeLabel(s.Label)
                    });
                }
                nextId = Math.Max(1, file.NextId);
                return list;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "[MapPin] board load failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Session teardown: MapPinBoard.Reset flushes first, this drops what is left pending.</summary>
        internal static void ResetPending()
        {
            _dirty = false;
            _writeAt = 0f;
        }
    }
}
