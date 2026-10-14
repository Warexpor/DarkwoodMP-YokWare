using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// The co-op files kept beside a save for one campaign: every player's character
    /// (<c>client_backup_*_{campaign}.json</c>) and the world records written with the host's save
    /// (story-item handoffs, oxygen tanks, party home moves, this player's home choice). They live
    /// in the profile folder with the campaign id in their names, so they followed the profile:
    /// a manual save slot loaded later met the newest characters, not the ones of its own save
    /// (and, with the slot's new campaign id, none at all). A slot now carries its own copy in
    /// <c>characters/</c>, taken with the slot's save, and puts it back renamed to the campaign id
    /// minted for the loaded slot.
    /// </summary>
    public static class CampaignFiles
    {
        internal const string SubDir = "characters";
        private const string Token = "{campaign}";
        private const string CampaignFile = "campaign.txt";

        /// <summary>
        /// A slot saved while hosting: the clients' characters of that save come in over the next
        /// moments (each client sends its own after the host's Save). Each one stored then is
        /// written into the slot too, for one save round.
        /// </summary>
        private static string _mirrorSlot; // reset-in: Reset
        private static string _mirrorCampaign; // reset-in: Reset
        private static float _mirrorUntil; // reset-in: Reset

        public static void Reset()
        {
            _mirrorSlot = null;
            _mirrorCampaign = null;
            _mirrorUntil = 0f;
        }

        private static string Sanitize(string campaign)
        {
            if (string.IsNullOrEmpty(campaign))
                return null;
            var sb = new System.Text.StringBuilder(campaign.Length);
            foreach (char c in campaign)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>The campaign's files in <paramref name="profDir"/>, named with <see cref="Token"/> for the id.</summary>
        private static List<KeyValuePair<string, byte[]>> Read(string profDir, string campaign)
        {
            var files = new List<KeyValuePair<string, byte[]>>();
            if (campaign == null || !Directory.Exists(profDir))
                return files;
            string suffix = "_" + campaign + ".json";
            foreach (string path in Directory.GetFiles(profDir, "*.json"))
            {
                string name = Path.GetFileName(path);
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string template = name.Substring(0, name.Length - suffix.Length) + "_" + Token + ".json";
                files.Add(new KeyValuePair<string, byte[]>(template, File.ReadAllBytes(path)));
            }
            return files;
        }

        /// <summary>
        /// Slot save: the current campaign's files from the profile into
        /// <c>&lt;slotDir&gt;/characters</c>, replacing what the slot held. While hosting, the
        /// clients' characters of this save that arrive later are added (<see cref="OnCharacterStored"/>).
        /// </summary>
        public static void CopyIntoSlot(int profileId, string slotDir)
        {
            string campaign = Sanitize(CoopWorldCopyMeta.TryGetCampaignIdForCurrentProfile());
            string profDir = CoopWorldCopyMeta.ProfileDir(profileId);
            string dst = Path.Combine(slotDir, SubDir);
            var files = Read(profDir, campaign);
            Directory.CreateDirectory(dst);
            foreach (string old in Directory.GetFiles(dst))
                File.Delete(old);
            foreach (var f in files)
                File.WriteAllBytes(Path.Combine(dst, f.Key), f.Value);
            if (campaign != null)
                CoopWorldCopyMeta.WriteAllTextAtomic(Path.Combine(dst, CampaignFile), campaign);
            ModLog.Event(LogCat.Save, "[ManualSave] slot keeps " + files.Count + " co-op file(s) of this save → " + dst);

            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Host && net.IsConnected && campaign != null)
            {
                _mirrorSlot = dst;
                _mirrorCampaign = campaign;
                _mirrorUntil = Time.unscaledTime + PartyCharacterPolicy.SaveRoundWindowSec;
            }
            else
                Reset();
        }

        /// <summary>Host: a client's character was stored at <paramref name="path"/> (its save round).</summary>
        internal static void OnCharacterStored(string path)
        {
            if (_mirrorSlot == null)
                return;
            if (Time.unscaledTime > _mirrorUntil)
            {
                Reset();
                return;
            }
            try
            {
                string name = Path.GetFileName(path);
                string suffix = "_" + _mirrorCampaign + ".json";
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(_mirrorSlot))
                    return;
                string template = name.Substring(0, name.Length - suffix.Length) + "_" + Token + ".json";
                File.Copy(path, Path.Combine(_mirrorSlot, template), true);
                ModLog.Event(LogCat.Save, "[ManualSave] slot got " + name + " from this save round");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ManualSave] could not add a character to the slot: " + ex.Message);
            }
        }

        /// <summary>
        /// Slot load, after the new campaign id is minted: the slot's files go into the profile under
        /// that id, the old id inside them (a character names its campaign) replaced too. A slot
        /// saved before slots kept these has none; nothing is put back then.
        /// </summary>
        public static void RestoreFromSlot(int profileId, string slotDir)
        {
            Reset();
            string src = Path.Combine(slotDir, SubDir);
            if (!Directory.Exists(src))
                return;
            string newCampaign = CoopWorldCopyMeta.TryLoad(profileId)?.CampaignId;
            string newSafe = Sanitize(newCampaign);
            if (newSafe == null)
                return;
            string oldCampaign = null;
            string campaignPath = Path.Combine(src, CampaignFile);
            if (File.Exists(campaignPath))
                oldCampaign = Sanitize(File.ReadAllText(campaignPath).Trim());
            string profDir = CoopWorldCopyMeta.ProfileDir(profileId);
            int n = 0;
            foreach (string path in Directory.GetFiles(src, "*.json"))
            {
                string name = Path.GetFileName(path);
                if (name.IndexOf(Token, StringComparison.Ordinal) < 0)
                    continue;
                string text = File.ReadAllText(path);
                if (oldCampaign != null)
                    text = ReplaceIgnoreCase(text, oldCampaign, newCampaign);
                CoopWorldCopyMeta.WriteAllTextAtomic(Path.Combine(profDir, name.Replace(Token, newSafe)), text);
                n++;
            }
            ModLog.Event(LogCat.Save, "[ManualSave] put back " + n + " co-op file(s) of the slot's save under campaign " + newSafe.Substring(0, Math.Min(8, newSafe.Length)) + "…");
        }

        private static string ReplaceIgnoreCase(string text, string find, string with)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            int at = 0;
            while (true)
            {
                int i = text.IndexOf(find, at, StringComparison.OrdinalIgnoreCase);
                if (i < 0)
                    break;
                sb.Append(text, at, i - at).Append(with);
                at = i + find.Length;
            }
            sb.Append(text, at, text.Length - at);
            return sb.ToString();
        }
    }
}
