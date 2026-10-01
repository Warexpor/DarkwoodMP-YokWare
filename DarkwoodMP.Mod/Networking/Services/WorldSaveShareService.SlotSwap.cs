using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Logging;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Crash-safe replacement of a save set (sav.dat / savs.dat / savch.dat) in one directory,
    /// used by the world-share commit and the F3 manual slots. A journal written before the first
    /// original moves aside marks the swap as uncommitted; <see cref="RecoverInterruptedSlotSwaps"/>
    /// rolls an uncommitted swap back at the next start.
    /// </summary>
    public sealed partial class WorldSaveShareService
    {
        /// <summary>
        /// Replace the save set in <paramref name="dir"/> with <paramref name="files"/> (all or
        /// nothing). Names of the set that <paramref name="files"/> does not carry are removed.
        /// </summary>
        internal static bool TryReplaceSaveSet(string dir, List<KeyValuePair<string, byte[]>> files, out string error)
        {
            Directory.CreateDirectory(dir);
            var swap = new SlotSwap(dir, files);
            if (!swap.TryStageAndSwap(out error))
                return false;
            swap.Commit();
            return true;
        }

        /// <summary>Read a directory's save set in one step (see <see cref="ReadSaveSetSnapshot"/>).</summary>
        internal static List<KeyValuePair<string, byte[]>> ReadSaveSet(string dir) => ReadSaveSetSnapshot(dir);

        /// <summary>
        /// Startup: finish every swap a crash interrupted under <c>&lt;root&gt;/1_4Save</c>. An
        /// uncommitted swap (journal present) is rolled back to the original files; without a
        /// journal, leftover backups are dropped when the live set is whole, else restored.
        /// </summary>
        internal static void RecoverInterruptedSlotSwaps(string persistentRoot)
        {
            try
            {
                string saveRoot = Path.Combine(persistentRoot, "1_4Save");
                if (!Directory.Exists(saveRoot))
                    return;
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in Directory.GetFiles(saveRoot, SlotSwap.JournalName, SearchOption.AllDirectories))
                    dirs.Add(Path.GetDirectoryName(f));
                foreach (string f in Directory.GetFiles(saveRoot, "*" + SlotSwap.BakExt, SearchOption.AllDirectories))
                    dirs.Add(Path.GetDirectoryName(f));
                foreach (string f in Directory.GetFiles(saveRoot, "*" + SlotSwap.TmpExt, SearchOption.AllDirectories))
                    dirs.Add(Path.GetDirectoryName(f));
                foreach (string dir in dirs)
                {
                    try { SlotSwap.Recover(dir); }
                    catch (Exception ex)
                    {
                        ModLog.Error(LogCat.Save, "Save swap recovery failed in " + dir, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "Save swap recovery scan failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Stage every new file as .dwmp_tmp, write the journal, move the current set aside as
        /// .dwmp_bak, rename the staged files into place. <see cref="Commit"/> deletes the journal
        /// (the commit point) and then the backups; <see cref="Rollback"/> restores the originals.
        /// </summary>
        private sealed class SlotSwap
        {
            internal const string TmpExt = ".dwmp_tmp";
            internal const string BakExt = ".dwmp_bak";
            internal const string JournalName = "dwmp_swap.journal";

            private readonly string _dir;
            private readonly List<KeyValuePair<string, byte[]>> _files;
            private readonly List<string> _staged = new List<string>(3);
            private readonly List<string> _installed = new List<string>(3);
            private readonly List<KeyValuePair<string, string>> _aside = new List<KeyValuePair<string, string>>(3);
            private bool _journalWritten;

            public SlotSwap(string dir, List<KeyValuePair<string, byte[]>> files)
            {
                _dir = dir;
                _files = files;
            }

            private string JournalPath => Path.Combine(_dir, JournalName);

            public bool TryStageAndSwap(out string error)
            {
                error = null;
                try
                {
                    foreach (KeyValuePair<string, byte[]> f in _files)
                    {
                        string tmp = Path.Combine(_dir, f.Key) + TmpExt;
                        File.WriteAllBytes(tmp, f.Value);
                        _staged.Add(tmp);
                        if (new FileInfo(tmp).Length != f.Value.Length)
                            throw new IOException("short write staging " + f.Key);
                    }

                    WriteJournal();

                    // The whole save set moves aside, including names this package does not carry:
                    // a stale savch.dat from another chapter must not survive next to the new files.
                    foreach (string name in FileNames)
                    {
                        string dest = Path.Combine(_dir, name);
                        if (!File.Exists(dest))
                            continue;
                        string bak = dest + BakExt;
                        if (File.Exists(bak))
                            File.Delete(bak);
                        File.Move(dest, bak);
                        _aside.Add(new KeyValuePair<string, string>(dest, bak));
                    }

                    foreach (KeyValuePair<string, byte[]> f in _files)
                    {
                        string dest = Path.Combine(_dir, f.Key);
                        File.Move(dest + TmpExt, dest);
                        _installed.Add(dest);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = "could not write the save files: " + ex.Message;
                    ModLog.Error(LogCat.Save, "Slot swap failed in " + _dir + " — rolling back", ex);
                    Rollback();
                    return false;
                }
            }

            /// <summary>One line per set name: name, had an original (1/0). Written atomically.</summary>
            private void WriteJournal()
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("owner\t").Append(OwnerTag()).Append('\n');
                foreach (string name in FileNames)
                    sb.Append(name).Append('\t').Append(File.Exists(Path.Combine(_dir, name)) ? '1' : '0').Append('\n');
                string tmp = JournalPath + TmpExt;
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(JournalPath))
                    File.Delete(JournalPath);
                File.Move(tmp, JournalPath);
                _journalWritten = true;
            }

            public void Rollback()
            {
                foreach (string dest in _installed)
                    TryDelete(dest);
                _installed.Clear();

                bool restoredAll = true;
                foreach (KeyValuePair<string, string> pair in _aside)
                {
                    try
                    {
                        if (File.Exists(pair.Key))
                            File.Delete(pair.Key);
                        File.Move(pair.Value, pair.Key);
                    }
                    catch (Exception ex)
                    {
                        restoredAll = false;
                        ModLog.Error(LogCat.Save,
                            "Could not restore " + pair.Key + " from " + pair.Value
                            + " — the original is still saved as the .dwmp_bak file", ex);
                    }
                }
                _aside.Clear();

                foreach (string tmp in _staged)
                    TryDelete(tmp);
                _staged.Clear();

                // Keep the journal when a restore failed: the next start finishes the rollback.
                if (_journalWritten && restoredAll)
                    TryDelete(JournalPath);
                _journalWritten = false;
            }

            /// <summary>New set is live and accepted: drop the journal, then the backups.</summary>
            public void Commit()
            {
                if (_journalWritten)
                    TryDelete(JournalPath);
                _journalWritten = false;
                foreach (KeyValuePair<string, string> pair in _aside)
                    TryDelete(pair.Value);
                _aside.Clear();
                _staged.Clear();
                _installed.Clear();
            }

            /// <summary>Finish whatever a crash left in <paramref name="dir"/> (see class summary).</summary>
            internal static void Recover(string dir)
            {
                string journal = Path.Combine(dir, JournalName);
                TryDelete(journal + TmpExt);
                if (File.Exists(journal))
                {
                    string[] lines = File.ReadAllLines(journal);
                    if (lines.Length > 0 && lines[0].StartsWith("owner\t", StringComparison.Ordinal)
                        && IsLiveOwner(lines[0].Substring(6)))
                        return; // another running game instance is mid-swap here

                    // Uncommitted: put the original set back exactly.
                    foreach (string line in lines)
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length != 2 || Array.IndexOf(FileNames, parts[0]) < 0)
                            continue;
                        string dest = Path.Combine(dir, parts[0]);
                        string bak = dest + BakExt;
                        if (parts[1] == "1")
                        {
                            if (File.Exists(bak))
                            {
                                if (File.Exists(dest))
                                    File.Delete(dest);
                                File.Move(bak, dest);
                            }
                        }
                        else if (File.Exists(dest))
                        {
                            File.Delete(dest); // installed by the swap; the slot had none
                        }
                        TryDelete(dest + TmpExt);
                    }
                    File.Delete(journal);
                    ModLog.Warn(LogCat.Save, "Rolled back an interrupted save swap in " + dir);
                    return;
                }

                // No journal: either the commit finished (only backup deletion was cut short) or a
                // swap from an older build was cut. Drop leftovers when the live pair is whole;
                // otherwise the backups are the only complete set, so restore all of them.
                bool liveWhole = File.Exists(Path.Combine(dir, "sav.dat"))
                    && File.Exists(Path.Combine(dir, "savs.dat"));
                foreach (string name in FileNames)
                {
                    string dest = Path.Combine(dir, name);
                    string bak = dest + BakExt;
                    TryDelete(dest + TmpExt);
                    if (!File.Exists(bak))
                        continue;
                    if (liveWhole)
                    {
                        TryDelete(bak);
                        continue;
                    }
                    if (File.Exists(dest))
                        File.Delete(dest);
                    File.Move(bak, dest);
                    ModLog.Warn(LogCat.Save, "Restored " + dest + " from an interrupted save swap");
                }
            }

            private static string OwnerTag()
            {
                try
                {
                    var p = System.Diagnostics.Process.GetCurrentProcess();
                    return p.Id + ":" + p.StartTime.ToUniversalTime().Ticks;
                }
                catch { return "0:0"; }
            }

            private static bool IsLiveOwner(string tag)
            {
                try
                {
                    string[] parts = tag.Split(':');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out int pid) || pid <= 0
                        || !long.TryParse(parts[1], out long ticks))
                        return false;
                    var p = System.Diagnostics.Process.GetProcessById(pid);
                    return p.StartTime.ToUniversalTime().Ticks == ticks
                        && pid != System.Diagnostics.Process.GetCurrentProcess().Id;
                }
                catch { return false; }
            }

            private static void TryDelete(string path)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Save, "Could not delete " + path + ": " + ex.Message);
                }
            }
        }
    }
}
