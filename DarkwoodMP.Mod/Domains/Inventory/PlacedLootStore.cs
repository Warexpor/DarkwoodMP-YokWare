using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Container slots a player has put items into, kept with the world (<c>prof*/savplc.dat</c>).
    /// The party loot bonus is for loot the world made: a stack a player stored and takes back
    /// must not earn it. The marks used to live in memory only, so after a reload, or for a player
    /// who joined later, a stored stack counted as world loot again. A slot is keyed like a
    /// cosmetic roll: the save id of the container's nearest saved object, the names below it and
    /// the slot. Written with every save, read at the start of every load, part of the world share.
    /// A container with no saved object above it is remembered for this run only.
    /// </summary>
    internal static class PlacedLootStore
    {
        internal const string FileName = "savplc.dat";

        private const int StoreMagic = 0x4C505744; // "DWPL"
        private const int StoreVersion = 1;
        private const int MaxStoreEntries = 1 << 20;
        /// <summary>Keeps slot keys apart from the cosmetic roll kinds hashed the same way.</summary>
        private const int SlotSalt = 0x504C0000;

        private static readonly HashSet<long> _keys = new HashSet<long>(); // process-scoped: world state, replaced when a world starts

        private static bool TryKey(Inventory inv, int slot, out long key)
        {
            key = 0;
            return inv != null && slot >= 0 && CosmeticRolls.TryObjectKey(inv.transform, SlotSalt + slot, out key);
        }

        /// <returns>False when the container has no saved object above it (the caller keeps its own mark).</returns>
        internal static bool Mark(Inventory inv, int slot)
        {
            if (!TryKey(inv, slot, out long key))
                return false;
            _keys.Add(key);
            return true;
        }

        internal static bool IsMarked(Inventory inv, int slot)
            => _keys.Count > 0 && TryKey(inv, slot, out long key) && _keys.Contains(key);

        private static string StorePath(SaveManager sm)
        {
            string dynamicFile = sm != null ? sm.dynamicFile : null;
            if (string.IsNullOrEmpty(dynamicFile))
                return null;
            string dir = Path.GetDirectoryName(dynamicFile);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, FileName);
        }

        /// <summary>A new world is generated: nothing carries over.</summary>
        internal static void BeginNewWorld() => _keys.Clear();

        /// <summary><c>SaveManager.Load</c> starts: read the marks its save carries.</summary>
        internal static void BeginLoad(SaveManager sm)
        {
            _keys.Clear();
            string path = StorePath(sm);
            if (path == null || !File.Exists(path))
                return;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new BinaryReader(fs))
                {
                    if (r.ReadInt32() != StoreMagic)
                        throw new InvalidDataException("not a stored-loot file");
                    int version = r.ReadInt32();
                    if (version != StoreVersion)
                    {
                        ModLog.Event(LogCat.Save, "[LootMarks] file version " + version + " is not read");
                        return;
                    }
                    int n = r.ReadInt32();
                    if (n < 0 || n > MaxStoreEntries)
                        throw new InvalidDataException("entry count " + n);
                    for (int i = 0; i < n; i++)
                        _keys.Add(r.ReadInt64());
                }
                ModLog.Event(LogCat.Save, "[LootMarks] player-stored slots from save: " + _keys.Count);
            }
            catch (Exception ex)
            {
                _keys.Clear();
                ModLog.Error(LogCat.Save, "[LootMarks] " + path + " unreadable; stored stacks count as loot again", ex);
            }
        }

        /// <summary>
        /// The game saved (or a world share is about to read the slot): write the marks of
        /// containers the world still has. Only a co-op world keeps the file.
        /// </summary>
        internal static void Write(SaveManager sm)
        {
            string path = StorePath(sm);
            if (!CosmeticRolls.Active || path == null || sm.uniqueIdDict == null)
                return;
            var live = new List<long>(_keys.Count);
            foreach (long key in _keys)
            {
                if (sm.uniqueIdDict.ContainsKey((int)(key >> 32)))
                    live.Add(key);
            }
            if (live.Count == 0 && !File.Exists(path))
                return;
            string tmp = path + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(StoreMagic);
                    w.Write(StoreVersion);
                    w.Write(live.Count);
                    for (int i = 0; i < live.Count; i++)
                        w.Write(live[i]);
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "[LootMarks] could not write " + path, ex);
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (IOException) { /* the next save writes it again */ }
            }
        }

        /// <summary>A deleted slot (or a new game over it) loses the file with its saves.</summary>
        internal static void Delete(SaveManager sm, int profileId)
        {
            string dir = sm != null ? sm.baseSaveDirectory : null;
            if (string.IsNullOrEmpty(dir))
                return;
            string path = Path.Combine(Path.Combine(dir, "prof" + profileId), FileName);
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException ex)
            {
                ModLog.Error(LogCat.Save, "[LootMarks] could not remove " + path, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                ModLog.Error(LogCat.Save, "[LootMarks] could not remove " + path, ex);
            }
        }
    }
}
