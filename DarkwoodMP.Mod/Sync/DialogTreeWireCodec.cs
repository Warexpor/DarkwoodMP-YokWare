using System;
using System.Collections.Generic;
using System.Text;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Pure Yokyy DialogueSync v2 wire codec (no Unity).
    /// Payload is base64 UTF-8 text:
    ///   line 0: dialogue name
    ///   line 1: node flags, one digit per node (bit0=alreadyShown, bit1=disabled)
    ///   line 2: portraitType (int)
    ///   line 3: NPC gameObject name ("" = none)
    ///   line 4: wantsToTalk '1'/'0'/'-'
    ///   line 5: reputation int or '-'
    ///   line 6+: special options "name|type"
    /// </summary>
    public static class DialogTreeWireCodec
    {
        public static string Encode(
            string dialogueName,
            int[] nodeFlags,
            int portraitType,
            string npcName,
            char wantsToTalk,
            string reputation,
            IList<(string name, string type)> specials)
        {
            if (string.IsNullOrEmpty(dialogueName))
                return "";

            var sb = new StringBuilder();
            sb.Append(dialogueName).Append('\n');

            if (nodeFlags != null)
            {
                for (int i = 0; i < nodeFlags.Length; i++)
                {
                    int v = nodeFlags[i] & 3;
                    sb.Append((char)('0' + v));
                }
            }
            sb.Append('\n');

            sb.Append(portraitType).Append('\n');
            sb.Append(npcName ?? "").Append('\n');
            sb.Append(wantsToTalk).Append('\n');
            sb.Append(string.IsNullOrEmpty(reputation) ? "-" : reputation).Append('\n');

            if (specials != null)
            {
                for (int i = 0; i < specials.Count; i++)
                {
                    var (n, t) = specials[i];
                    if (string.IsNullOrEmpty(n) && string.IsNullOrEmpty(t)) continue;
                    sb.Append(n ?? "").Append('|').Append(t ?? "").Append('\n');
                }
            }

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public static bool TryDecode(
            string payload,
            out string dialogueName,
            out int[] nodeFlags,
            out int portraitType,
            out string npcName,
            out char wantsToTalk,
            out string reputation,
            out List<(string name, string type)> specials)
        {
            dialogueName = null;
            nodeFlags = Array.Empty<int>();
            portraitType = 0;
            npcName = "";
            wantsToTalk = '-';
            reputation = "-";
            specials = new List<(string, string)>();

            if (string.IsNullOrEmpty(payload))
                return false;

            string text;
            try
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            }
            catch
            {
                return false;
            }

            var lines = text.Split('\n');
            if (lines.Length < 6)
                return false;

            dialogueName = lines[0];
            if (string.IsNullOrEmpty(dialogueName))
                return false;

            string flagStr = lines[1] ?? "";
            nodeFlags = new int[flagStr.Length];
            for (int i = 0; i < flagStr.Length; i++)
            {
                char c = flagStr[i];
                nodeFlags[i] = (c >= '0' && c <= '3') ? c - '0' : 0;
            }

            int.TryParse(lines[2], out portraitType);
            npcName = lines[3] ?? "";
            wantsToTalk = lines[4] != null && lines[4].Length == 1 ? lines[4][0] : '-';
            reputation = lines[5] ?? "-";

            for (int i = 6; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;
                int bar = line.IndexOf('|');
                if (bar < 0) continue;
                specials.Add((line.Substring(0, bar), line.Substring(bar + 1)));
            }

            return true;
        }

        /// <summary>
        /// Node flag packing used by both encode and HasProgress checks.
        /// bit0 = alreadyShown, bit1 = disabled.
        /// </summary>
        public static int PackNodeFlag(bool alreadyShown, bool disabled)
        {
            int v = 0;
            if (alreadyShown) v |= 1;
            if (disabled) v |= 2;
            return v;
        }

        public static bool UnpackAlreadyShown(int packed) => (packed & 1) != 0;
        public static bool UnpackDisabled(int packed) => (packed & 2) != 0;

        /// <summary>
        /// True if tree has progression worth syncing (mirrors Yokyy HasProgress).
        /// </summary>
        public static bool HasProgress(
            int[] nodeFlags,
            int specialOptionCount,
            int portraitType,
            int initialPortraitType)
        {
            if (specialOptionCount > 0) return true;
            if (portraitType != initialPortraitType) return true;
            if (nodeFlags == null) return false;
            for (int i = 0; i < nodeFlags.Length; i++)
            {
                if (UnpackAlreadyShown(nodeFlags[i]))
                    return true;
            }
            return false;
        }
    }
}
