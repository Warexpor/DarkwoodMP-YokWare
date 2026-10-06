using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Sync
{
    internal static partial class CosmeticRolls
    {
        /// <summary>
        /// The seed file next to the save (<c>prof*/savcos.dat</c>): the seed of every roll under a
        /// saved object, keyed by that object's save id and the names below it. Written with every
        /// save, read at the start of every load, and part of the world share package. A seed never
        /// changes once rolled (only a mover taking the host's key rolls again) and save ids are
        /// never reused, so a file from an older save of the same world is still right for every
        /// entry it has.
        /// </summary>
        internal const string KeyStoreFileName = "savcos.dat";

        private const int StoreMagic = 0x4B435744; // "DWCK"
        private const int StoreVersion = 2;
        private const int MaxStoreEntries = 1 << 21;

        /// <summary>A roll made in this world, kept for the next save.</summary>
        private struct RollMark
        {
            public GameObject Go;
            public int Salt;
            public int Seed;
            /// <summary>Save id and path once known (0: not worked out yet).</summary>
            public long StoreKey;
        }

        /// <summary>Rolls made in this world: (instance id, kind salt) → mark.</summary>
        private static readonly Dictionary<long, RollMark> _marks = new Dictionary<long, RollMark>(8192); // process-scoped: cleared when a world starts; dead entries dropped at save

        /// <summary>Seeds the loaded save carried: store key → seed.</summary>
        private static readonly Dictionary<long, int> _storedSeeds = new Dictionary<long, int>(8192); // process-scoped: replaced when a world starts

        private static void Mark(GameObject go, int salt, int seed)
        {
            if (go == null)
                return;
            long id = ((long)go.GetInstanceID() << 32) | (uint)salt;
            if (_marks.TryGetValue(id, out RollMark m) && m.Go == go)
            {
                m.Seed = seed;
                _marks[id] = m;
                return;
            }
            _marks[id] = new RollMark { Go = go, Salt = salt, Seed = seed };
        }

        /// <summary>
        /// The store key of a roll of kind <paramref name="salt"/> on <paramref name="t"/>: the save
        /// id of its nearest saved ancestor (or itself) and the names below it, with each name's
        /// place among same-named siblings. Below a saved object everything comes from that object's
        /// prefab, so a load rebuilds these names exactly; offsets are left out (parallax layers move
        /// theirs). False when nothing above it is saved.
        /// </summary>
        private static bool TryStoreKey(Transform t, int salt, out long key)
        {
            key = 0;
            // The saved ancestor first (a cheap component check per level); names and sibling
            // places only below it.
            Transform saved = null;
            int savedId = 0;
            for (Transform n = t; n != null; n = n.parent)
            {
                SaveableObject so = n.GetComponent<SaveableObject>();
                if (so != null && so.assigned && !so.dontSave && so.uniqueId > 0)
                {
                    saved = n;
                    savedId = so.uniqueId;
                    break;
                }
            }
            if (saved == null)
                return false;
            uint h = CosmeticKey.Add(CosmeticKey.Start(), salt);
            for (Transform n = t; n != saved; n = n.parent)
            {
                string name = n.name;
                h = CosmeticKey.Add(h, CleanName(name));
                h = CosmeticKey.Add(h, SameNameOrdinal(n, name));
            }
            key = ((long)savedId << 32) | (uint)CosmeticKey.Finish(h);
            return true;
        }

        /// <summary>
        /// Sibling places asked for in this frame. A location's objects roll together and share
        /// their upper levels, so each level is counted once per flush.
        /// </summary>
        private static readonly Dictionary<int, int> _ordinalCache = new Dictionary<int, int>(256); // process-scoped: cleared every frame
        private static int _ordinalFrame = -1; // process-scoped: frame of _ordinalCache

        /// <summary>How many earlier siblings share <paramref name="n"/>'s name.</summary>
        private static int SameNameOrdinal(Transform n, string name)
        {
            Transform p = n.parent;
            if (p == null)
                return 0;
            int frame = Time.frameCount;
            if (frame != _ordinalFrame)
            {
                _ordinalCache.Clear();
                _ordinalFrame = frame;
            }
            int id = n.GetInstanceID();
            if (_ordinalCache.TryGetValue(id, out int cached))
                return cached;
            int index = n.GetSiblingIndex();
            int count = 0;
            for (int i = 0; i < index; i++)
            {
                if (string.Equals(p.GetChild(i).name, name, StringComparison.Ordinal))
                    count++;
            }
            _ordinalCache[id] = count;
            return count;
        }

        private static string StorePath(SaveManager sm)
        {
            string dynamicFile = sm != null ? sm.dynamicFile : null;
            if (string.IsNullOrEmpty(dynamicFile))
                return null;
            string dir = Path.GetDirectoryName(dynamicFile);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, KeyStoreFileName);
        }

        /// <summary>A new world is generated (<c>Controller.generateChapter</c>): nothing carries over.</summary>
        internal static void BeginNewWorld()
        {
            _storedSeeds.Clear();
            _marks.Clear();
            _anchors.Clear();
        }

        /// <summary><c>SaveManager.Load</c> starts: read the seeds its save carries.</summary>
        internal static void BeginLoad(SaveManager sm)
        {
            BeginNewWorld();
            string path = StorePath(sm);
            if (path == null || !File.Exists(path))
            {
                ModLog.Event(LogCat.Save, "[Cosmetic] no roll seed file with this save; seeds come from places");
                return;
            }
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new BinaryReader(fs))
                {
                    if (r.ReadInt32() != StoreMagic)
                        throw new InvalidDataException("not a roll seed file");
                    int version = r.ReadInt32();
                    if (version != StoreVersion)
                    {
                        ModLog.Event(LogCat.Save, "[Cosmetic] roll seed file version " + version + " is not read; seeds come from places");
                        return;
                    }
                    int n = r.ReadInt32();
                    if (n < 0 || n > MaxStoreEntries)
                        throw new InvalidDataException("entry count " + n);
                    for (int i = 0; i < n; i++)
                    {
                        long key = r.ReadInt64();
                        _storedSeeds[key] = r.ReadInt32();
                    }
                }
                ModLog.Event(LogCat.Save, "[Cosmetic] roll seeds from save: " + _storedSeeds.Count);
            }
            catch (Exception ex)
            {
                _storedSeeds.Clear();
                ModLog.Error(LogCat.Save, "[Cosmetic] roll seed file " + path + " unreadable; seeds come from places", ex);
            }
        }

        /// <summary>
        /// The game saved: write the seed of every roll under a saved object (the ones made in this
        /// world, and the ones the loaded save carried for objects that have not rolled yet, such as
        /// objects still inactive). Entries of objects the world no longer has are dropped.
        /// </summary>
        internal static void WriteStore(SaveManager sm)
        {
            string path = StorePath(sm);
            if (path == null || sm.uniqueIdDict == null)
                return;
            var seeds = new Dictionary<long, int>(_storedSeeds.Count + _marks.Count);
            foreach (KeyValuePair<long, int> kv in _storedSeeds)
            {
                if (sm.uniqueIdDict.ContainsKey((int)(kv.Key >> 32)))
                    seeds[kv.Key] = kv.Value;
            }
            _deadMarkScratch.Clear();
            _markUpdateScratch.Clear();
            foreach (KeyValuePair<long, RollMark> kv in _marks)
            {
                RollMark m = kv.Value;
                if (m.Go == null)
                {
                    _deadMarkScratch.Add(kv.Key);
                    continue;
                }
                if (m.StoreKey == 0)
                {
                    if (!TryStoreKey(m.Go.transform, m.Salt, out long storeKey))
                        continue;
                    m.StoreKey = storeKey;
                    _markUpdateScratch.Add(new KeyValuePair<long, RollMark>(kv.Key, m));
                }
                seeds[m.StoreKey] = m.Seed;
            }
            for (int i = 0; i < _deadMarkScratch.Count; i++)
                _marks.Remove(_deadMarkScratch[i]);
            for (int i = 0; i < _markUpdateScratch.Count; i++)
                _marks[_markUpdateScratch[i].Key] = _markUpdateScratch[i].Value;
            _deadMarkScratch.Clear();
            _markUpdateScratch.Clear();

            string tmp = path + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(StoreMagic);
                    w.Write(StoreVersion);
                    w.Write(seeds.Count);
                    foreach (KeyValuePair<long, int> kv in seeds)
                    {
                        w.Write(kv.Key);
                        w.Write(kv.Value);
                    }
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
                ModLog.Event(LogCat.Save, "[Cosmetic] roll seeds saved: " + seeds.Count);
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "[Cosmetic] could not write " + path, ex);
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (IOException) { /* the next save writes it again */ }
            }
        }

        private static readonly List<long> _deadMarkScratch = new List<long>(256); // process-scoped: scratch buffer
        private static readonly List<KeyValuePair<long, RollMark>> _markUpdateScratch = new List<KeyValuePair<long, RollMark>>(1024); // process-scoped: scratch buffer
    }
}
