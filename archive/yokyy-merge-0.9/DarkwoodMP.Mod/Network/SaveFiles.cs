using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DarkwoodMP.Network;

/// <summary>
/// Reads and writes Darkwood's on-disk world save files - the foundation of the
/// world-download feature (transfer the host's generated world to clients
/// instead of relying on deterministic worldgen).
///
/// Layout (IL-verified, SaveManager getters):
///   &lt;persistentDataPath&gt;/1_4Save/            (baseSaveDirectory; "1_4" = game ver)
///     profs.dat                              (profile index)
///     prof&lt;id&gt;/sav.dat                        (dynamic save ~2MB)
///     prof&lt;id&gt;/savs.dat                       (static save ~9MB = the world)
///     prof&lt;id&gt;/savch.dat                      (chapter save)
///
/// Files are encrypted by the game; we move raw bytes so encryption is
/// transparent. This class does pure file I/O - no game types - so it is safe
/// to unit-check and never touches the game's save logic.
/// </summary>
public static class SaveFiles
{
    // The world files that make up a profile's save. profs.dat (the index) is
    // handled separately because it is shared across all profiles.
    public static readonly string[] WorldFileNames = { "sav.dat", "savs.dat", "savch.dat" };

    /// <summary>Slot reserved for downloaded multiplayer worlds so a client's own saves are never clobbered.</summary>
    public const int MultiplayerProfileId = 5;

    /// <summary>&lt;persistentDataPath&gt;/1_4Save - the version-stamped save root.</summary>
    public static string SaveRoot => Path.Combine(Application.persistentDataPath, "1_4Save");

    public static string ProfileDir(int profileId) => Path.Combine(SaveRoot, "prof" + profileId);

    /// <summary>True if the profile folder has at least the dynamic + static world files.</summary>
    public static bool ProfileHasWorld(int profileId)
    {
        var dir = ProfileDir(profileId);
        return File.Exists(Path.Combine(dir, "sav.dat")) && File.Exists(Path.Combine(dir, "savs.dat"));
    }

    /// <summary>Read every present world file for a profile as (name, bytes) pairs.</summary>
    public static List<(string name, byte[] data)> ReadWorld(int profileId)
    {
        var result = new List<(string, byte[])>();
        var dir = ProfileDir(profileId);
        foreach (var name in WorldFileNames)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try
            {
                result.Add((name, File.ReadAllBytes(path)));
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SaveFiles] read '{name}' failed: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// Write received world files into a profile folder, replacing what is there.
    /// Existing files are backed up to *.mpbak once (first write) so a client can
    /// be restored if they used a real slot.
    /// </summary>
    public static bool WriteWorld(int profileId, IEnumerable<(string name, byte[] data)> files)
    {
        try
        {
            var dir = ProfileDir(profileId);
            Directory.CreateDirectory(dir);

            foreach (var (name, data) in files)
            {
                if (!IsAllowedName(name))
                {
                    ModLogger.Warning($"[SaveFiles] refusing to write unexpected file '{name}'");
                    continue;
                }
                var path = Path.Combine(dir, name);
                BackupOnce(path);
                File.WriteAllBytes(path, data);
            }
            ModLogger.Msg($"[SaveFiles] wrote world into prof{profileId} ({dir})");
            return true;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[SaveFiles] write world failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Restore *.mpbak backups over a slot we overwrote (called on disconnect/cleanup).</summary>
    public static void RestoreBackups(int profileId)
    {
        var dir = ProfileDir(profileId);
        if (!Directory.Exists(dir)) return;
        foreach (var bak in Directory.GetFiles(dir, "*.mpbak"))
        {
            try
            {
                var original = bak.Substring(0, bak.Length - ".mpbak".Length);
                File.Copy(bak, original, true);
                File.Delete(bak);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SaveFiles] restore '{bak}' failed: {ex.Message}");
            }
        }
    }

    private static void BackupOnce(string path)
    {
        var bak = path + ".mpbak";
        if (File.Exists(path) && !File.Exists(bak))
        {
            try { File.Copy(path, bak); } catch { /* best effort */ }
        }
    }

    // Guard against a malicious/garbled manifest writing arbitrary paths.
    private static bool IsAllowedName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains("..")) return false;
        return Array.IndexOf(WorldFileNames, name) >= 0;
    }
}
