using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge.Ui
{
    internal enum UiCfgOp { Remove, Rename, Replace, Copy, ClearUnlessListed }

    internal sealed class UiCfgStep
    {
        public readonly int Version; public readonly UiCfgOp Op;
        public readonly string Section, Key, NewKey;   // NewKey: Rename (the new name) and Copy (the target key), same section
        public readonly string OldValue, NewValue;      // Replace: the value is replaced when it equals OldValue (the untouched old default). ClearUnlessListed: OldValue = the list key, NewValue = the item
        public UiCfgStep(int version, UiCfgOp op, string section, string key, string newKey)
        { Version = version; Op = op; Section = section; Key = key; NewKey = newKey; }
        public UiCfgStep(int version, UiCfgOp op, string section, string key, string oldValue, string newValue)
        { Version = version; Op = op; Section = section; Key = key; OldValue = oldValue; NewValue = newValue; }
    }

    /// <summary>
    /// T-123: versioned upgrade of the stored UI config text (pure: no BepInEx, no Unity). The step table is the single source of truth
    /// for the mod; migrate_cfg.py keeps a copy (UI_CFG_STEPS) and a test compares both. Step line format is fixed:
    /// new UiCfgStep(int, UiCfgOp.Op, "Section", "Key", null|"NewKey"),
    /// </summary>
    internal static class UiConfigMigrator
    {
        public const int Current = 13;
        public const string VersionSection = "Ui", VersionKey = "ConfigVersion";

        public static readonly UiCfgStep[] Steps =
        {
            new UiCfgStep(2, UiCfgOp.Remove, "Overview", "Units", null),
            new UiCfgStep(2, UiCfgOp.Remove, "Overview", "DoubleClickZoom", null),
            new UiCfgStep(2, UiCfgOp.Remove, "RadarCoverage", "RefreshSeconds", null),
            new UiCfgStep(2, UiCfgOp.Remove, "Menu", "Language", null),
            // 0.28.0 (live check 0.8.7): the old defaults were taken by game bindings; only the untouched old default is replaced.
            new UiCfgStep(3, UiCfgOp.Replace, "RadarCoverage", "Hotkey", "LeftAlt+R", "LeftAlt+V|LeftAlt+Y"),
            new UiCfgStep(3, UiCfgOp.Replace, "ContactInfo", "Hotkey", "LeftAlt+I", "LeftAlt+N|LeftAlt+E"),
            new UiCfgStep(3, UiCfgOp.Replace, "ContactInfo", "Hotkey.Depth", "LeftAlt+LeftShift+Alpha1", "LeftAlt+LeftShift+Keypad1"),
            new UiCfgStep(3, UiCfgOp.Replace, "ContactInfo", "Hotkey.Speed", "LeftAlt+LeftShift+Alpha2", "LeftAlt+LeftShift+Keypad2"),
            new UiCfgStep(3, UiCfgOp.Replace, "ContactInfo", "Hotkey.Ttg", "LeftAlt+LeftShift+Alpha3", "LeftAlt+LeftShift+Keypad3"),
            new UiCfgStep(3, UiCfgOp.Replace, "ContactInfo", "Hotkey.Cpa", "LeftAlt+LeftShift+Alpha4", "LeftAlt+LeftShift+Keypad4"),
            new UiCfgStep(3, UiCfgOp.Replace, "ContactInfo", "Hotkey.Age", "LeftAlt+LeftShift+Alpha5", "LeftAlt+LeftShift+Keypad5"),
            new UiCfgStep(3, UiCfgOp.Replace, "ContactInfo", "Hotkey.Noise", "LeftAlt+LeftShift+Alpha6", "LeftAlt+LeftShift+Keypad6"),
            new UiCfgStep(3, UiCfgOp.Replace, "ContactInfo", "Hotkey.Waypoint", "LeftAlt+LeftShift+Alpha7", "LeftAlt+LeftShift+Keypad7"),
            new UiCfgStep(3, UiCfgOp.Replace, "RangeRings", "Hotkey", "LeftAlt+Z", "LeftAlt+G|LeftAlt+B"),
            new UiCfgStep(3, UiCfgOp.Replace, "RangeRings", "Hotkey.Group", "LeftAlt+LeftShift+Z", "LeftAlt+LeftShift+B|LeftAlt+LeftShift+H"),
            new UiCfgStep(3, UiCfgOp.Replace, "Settings", "Hotkey", "LeftAlt+Comma|F10", "LeftAlt+Comma"),
            // T-168: the Overview columns hits and dmg (hit estimate); only the untouched old default list gets them, a list the player edited stays as it is.
            new UiCfgStep(4, UiCfgOp.Replace, "Overview", "Columns", "icon,type,dist,brg,spd,alt,dep,crs,cpa,age,idn", "icon,type,dist,brg,spd,alt,dep,crs,cpa,age,idn,hits,dmg"),
            // T-171 (ADR-0068): Show.Weapons / Types become Show.Aaw / Show.Asuw / Show.Asw; the labels are on for everyone once.
            new UiCfgStep(5, UiCfgOp.Copy, "RangeRings", "Show.Weapons", "Show.Aaw"),
            new UiCfgStep(5, UiCfgOp.Copy, "RangeRings", "Show.Weapons", "Show.Asuw"),
            new UiCfgStep(5, UiCfgOp.Copy, "RangeRings", "Show.Weapons", "Show.Asw"),
            new UiCfgStep(5, UiCfgOp.ClearUnlessListed, "RangeRings", "Show.Aaw", "Types", "AAW"),
            new UiCfgStep(5, UiCfgOp.ClearUnlessListed, "RangeRings", "Show.Asuw", "Types", "ASuW"),
            new UiCfgStep(5, UiCfgOp.ClearUnlessListed, "RangeRings", "Show.Asw", "Types", "ASW"),
            new UiCfgStep(5, UiCfgOp.Remove, "RangeRings", "Show.Weapons", null),
            new UiCfgStep(5, UiCfgOp.Remove, "RangeRings", "Types", null),
            new UiCfgStep(5, UiCfgOp.Replace, "RangeRings", "Labels", "false", "true"),
            // 0.29.1 (customer 2026-10-22, ADR-0068 supplement): no category rings and no fire-sector layer any more (only the checked weapons draw
            // rings): their keys go; the sonar rings start off once (the old default "true"; a later "true" of the player stays).
            new UiCfgStep(6, UiCfgOp.Remove, "RangeRings", "Show.Aaw", null),
            new UiCfgStep(6, UiCfgOp.Remove, "RangeRings", "Show.Asuw", null),
            new UiCfgStep(6, UiCfgOp.Remove, "RangeRings", "Show.Asw", null),
            new UiCfgStep(6, UiCfgOp.Remove, "RangeRings", "Show.Sectors", null),
            new UiCfgStep(6, UiCfgOp.Remove, "RangeRings", "Show.Enemy", null),
            new UiCfgStep(6, UiCfgOp.Remove, "RangeRings", "ShowMinRange", null),
            new UiCfgStep(6, UiCfgOp.Remove, "RangeRings", "MaxSectorUnits", null),
            new UiCfgStep(6, UiCfgOp.Replace, "RangeRings", "Show.Sonar", "true", "false"),
            // T-173 (ADR-0074): the attack question is gone: its timeout key goes.
            new UiCfgStep(7, UiCfgOp.Remove, "Asw", "ConfirmTimeoutS", null),
            // T-176 (ADR-0076): the re-plan angle of a by-course order is replaced by ReanchorDeg (30 degrees held for ReanchorHoldS).
            new UiCfgStep(8, UiCfgOp.Remove, "Asw", "ReplanDeg", null),
            // T-179 (ADR-0079): [Cap] AlertOnUnknown (true / false) becomes AlertTargets (air,unknown / air); an untouched file has neither key.
            new UiCfgStep(9, UiCfgOp.Copy, "Cap", "AlertOnUnknown", "AlertTargets"),
            new UiCfgStep(9, UiCfgOp.Replace, "Cap", "AlertTargets", "true", "air,unknown"),
            new UiCfgStep(9, UiCfgOp.Replace, "Cap", "AlertTargets", "false", "air"),
            new UiCfgStep(9, UiCfgOp.Remove, "Cap", "AlertOnUnknown", null),
            // T-299: BuoyRevive on by default; a 0.30.25/0.30.26 file holds the old default false (on once, like Labels in v5)
            new UiCfgStep(10, UiCfgOp.Replace, "Harmony", "BuoyRevive", "false", "true"),
            // T-316 (ADR-0123): the MAD pass is off by default; a file up to 0.31.0 holds the old default true (off once, like BuoyRevive in v10)
            new UiCfgStep(11, UiCfgOp.Replace, "Asw", "MadVerify", "true", "false"),
            // T-330 (ADR-0128): lower return defaults (missiles 20 %, fuel floor 5 %); a file up to 0.31.x holds the old defaults 0.25 / 0.15 (replaced once, like MadVerify in v11)
            new UiCfgStep(12, UiCfgOp.Replace, "Cap", "MissilePct", "0.25", "0.2"),
            new UiCfgStep(12, UiCfgOp.Replace, "Cap", "FuelFloorPct", "0.15", "0.05"),
            // T-337 (ADR-0131): the fuel floor is off by default (return only by the reserve in miles); a file still holding the v12 default 0.05 gets 0 once, an own value stays
            new UiCfgStep(13, UiCfgOp.Replace, "Cap", "FuelFloorPct", "0.05", "0"),
        };

        private static string SectionOf(string line)
        {
            string s = line.Trim();
            if (s.Length > 2 && s[0] == '[' && s[s.Length - 1] == ']') return s.Substring(1, s.Length - 2);
            return null;
        }

        /// <summary>Key of a "key = value" line; null for comments, blanks, headers and other lines.</summary>
        private static string KeyOf(string line)
        {
            string s = line.Trim();
            if (s.Length == 0 || s[0] == '#' || s[0] == '[') return null;
            int eq = s.IndexOf('=');
            return eq <= 0 ? null : s.Substring(0, eq).Trim();
        }

        private static List<string> Split(string text)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') { lines.Add(text.Substring(start, i - start + 1)); start = i + 1; }
            }
            if (start < text.Length) lines.Add(text.Substring(start));
            return lines;
        }

        private static bool HasEntries(List<string> lines)
        {
            foreach (var l in lines) if (KeyOf(l) != null) return true;
            return false;
        }

        /// <summary>Stored version: the [Ui] ConfigVersion value; 1 when the file has entries but no such key (or junk); Current for an empty text.</summary>
        public static int DetectVersion(string text)
        {
            var lines = Split(text ?? "");
            if (!HasEntries(lines)) return Current;
            string section = null;
            foreach (var l in lines)
            {
                string n = SectionOf(l);
                if (n != null) { section = n; continue; }
                if (section == VersionSection && KeyOf(l) == VersionKey)
                {
                    string v = l.Substring(l.IndexOf('=') + 1).Trim();
                    int r;
                    if (int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out r) && r >= 1) return r;
                    return 1;
                }
            }
            return 1;
        }

        /// <summary>Index range [from, to) of the entry of key in section: the comment block above it plus the line itself. false = absent.</summary>
        private static bool FindEntry(List<string> lines, string section, string key, out int from, out int keyLine)
        {
            from = keyLine = -1;
            string cur = null; int blockStart = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string n = SectionOf(lines[i]);
                if (n != null) { cur = n; blockStart = i + 1; continue; }
                string s = lines[i].Trim();
                if (s.Length == 0) { blockStart = i + 1; continue; }
                if (s[0] == '#') continue;
                string k = KeyOf(lines[i]);
                if (k != null && cur == section && k == key) { from = blockStart; keyLine = i; return true; }
                blockStart = i + 1;
            }
            return false;
        }

        private static string Eol(string text) { return text.Contains("\r\n") ? "\r\n" : "\n"; }

        private static string WithEol(string line, string eol)
        {
            return line.EndsWith("\n") ? line : line + eol;
        }

        /// <summary>
        /// Applies the steps with Version > stored; Remove drops the key line with its comment block (plus one doubled blank line),
        /// Rename moves the value to NewKey unless NewKey already exists; sets [Ui] ConfigVersion = Current.
        /// Keeps \r\n vs \n and every other line byte for byte. changes = "removed [S] K", "renamed [S] K -> N", "version 1 -> 3".
        /// An empty text and a text already at Current are returned unchanged.
        /// </summary>
        public static string Upgrade(string text, out List<string> changes)
        {
            return Upgrade(text, Steps, out changes);
        }

        internal static string Upgrade(string text, UiCfgStep[] steps, out List<string> changes)
        {
            changes = new List<string>();
            text = text ?? "";
            int stored = DetectVersion(text);
            if (stored >= Current) return text;
            string eol = Eol(text);
            var lines = Split(text);
            foreach (var st in steps)
            {
                if (st.Version <= stored) continue;
                int from, kl;
                if (st.Op == UiCfgOp.Copy) { Copy(lines, st, eol, changes); continue; }
                if (st.Op == UiCfgOp.ClearUnlessListed) { ClearUnlessListed(lines, st, eol, changes); continue; }
                if (!FindEntry(lines, st.Section, st.Key, out from, out kl)) continue;
                if (st.Op == UiCfgOp.Remove)
                {
                    int end = kl + 1;
                    if (end < lines.Count && lines[end].Trim().Length == 0 && from > 0 && lines[from - 1].Trim().Length == 0) end++;
                    lines.RemoveRange(from, end - from);
                    changes.Add("removed [" + st.Section + "] " + st.Key);
                }
                else if (st.Op == UiCfgOp.Replace)
                {
                    string cur = lines[kl].Substring(lines[kl].IndexOf('=') + 1).Trim().Trim('"', '\'', ' ');
                    if (cur != st.OldValue) continue;   // the user's own value stays
                    lines[kl] = st.Key + " = " + st.NewValue + (lines[kl].EndsWith("\r\n") ? "\r\n" : lines[kl].EndsWith("\n") ? "\n" : "");
                    changes.Add("replaced [" + st.Section + "] " + st.Key + " " + st.OldValue + " -> " + st.NewValue);
                }
                else
                {
                    int f2, k2;
                    if (FindEntry(lines, st.Section, st.NewKey, out f2, out k2)) continue;   // the new key already has a value
                    string l = lines[kl];
                    int idx = l.IndexOf(st.Key, StringComparison.Ordinal);
                    lines[kl] = l.Substring(0, idx) + st.NewKey + l.Substring(idx + st.Key.Length);
                    changes.Add("renamed [" + st.Section + "] " + st.Key + " -> " + st.NewKey);
                }
            }
            SetVersion(lines, eol);
            changes.Add("version " + stored + " -> " + Current);
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(l);
            return sb.ToString();
        }

        private static string ValueOf(string line) { return line.Substring(line.IndexOf('=') + 1).Trim(); }

        private static string Plain(string v) { return v.Trim().Trim('"', '\'', ' '); }

        private static void InsertAfter(List<string> lines, int index, string entry, string eol)
        {
            lines[index] = WithEol(lines[index], eol);
            lines.Insert(index + 1, entry + eol);
        }

        /// <summary>Copy: the source key exists and the target does not -> "NewKey = value" right after the source line.</summary>
        private static void Copy(List<string> lines, UiCfgStep st, string eol, List<string> changes)
        {
            int from, kl, f2, k2;
            if (!FindEntry(lines, st.Section, st.Key, out from, out kl)) return;
            if (FindEntry(lines, st.Section, st.NewKey, out f2, out k2)) return;
            InsertAfter(lines, kl, st.NewKey + " = " + ValueOf(lines[kl]), eol);
            changes.Add("copied [" + st.Section + "] " + st.Key + " -> " + st.NewKey);
        }

        /// <summary>ClearUnlessListed: the list key (OldValue) exists and does not list the item (NewValue; CSV, trimmed, any case) -> Key = false (set, or inserted after the list line).</summary>
        private static void ClearUnlessListed(List<string> lines, UiCfgStep st, string eol, List<string> changes)
        {
            int lf, ll;
            if (!FindEntry(lines, st.Section, st.OldValue, out lf, out ll)) return;
            foreach (string part in Plain(ValueOf(lines[ll])).Split(','))
                if (string.Equals(part.Trim(), st.NewValue, StringComparison.OrdinalIgnoreCase)) return;
            int from, kl;
            if (FindEntry(lines, st.Section, st.Key, out from, out kl))
            {
                if (Plain(ValueOf(lines[kl])) == "false") return;
                lines[kl] = st.Key + " = false" + (lines[kl].EndsWith("\r\n") ? "\r\n" : lines[kl].EndsWith("\n") ? "\n" : "");
            }
            else InsertAfter(lines, ll, st.Key + " = false", eol);
            changes.Add("cleared [" + st.Section + "] " + st.Key + " (" + st.OldValue + " has no " + st.NewValue + ")");
        }

        private static void SetVersion(List<string> lines, string eol)
        {
            string vline = VersionKey + " = " + Current;
            int from, kl;
            if (FindEntry(lines, VersionSection, VersionKey, out from, out kl))
            {
                lines[kl] = vline + (lines[kl].EndsWith("\r\n") ? "\r\n" : lines[kl].EndsWith("\n") ? "\n" : "");
                return;
            }
            // append to an existing [Ui] section (after its last entry), else create the section at the end
            int hdr = -1, last = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string n = SectionOf(lines[i]);
                if (n != null) { if (hdr >= 0) break; if (n == VersionSection) { hdr = i; last = i; } continue; }
                if (hdr >= 0 && lines[i].Trim().Length > 0) last = i;
            }
            if (hdr >= 0)
            {
                lines[last] = WithEol(lines[last], eol);
                lines.Insert(last + 1, vline + eol);
                return;
            }
            if (lines.Count > 0)
            {
                lines[lines.Count - 1] = WithEol(lines[lines.Count - 1], eol);
                if (lines[lines.Count - 1].Trim().Length > 0) lines.Add(eol);
            }
            lines.Add("[" + VersionSection + "]" + eol);
            lines.Add(eol);
            lines.Add(vline + eol);
        }

        /// <summary>Comma lists a then b, trimmed, without duplicates (ordinal), a first.</summary>
        public static string UnionCsv(string a, string b)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outp = new List<string>();
            foreach (var src in new[] { a ?? "", b ?? "" })
            {
                foreach (var part in src.Split(','))
                {
                    string t = part.Trim();
                    if (t.Length > 0 && seen.Add(t)) outp.Add(t);
                }
            }
            return string.Join(",", outp.ToArray());
        }

        public static string SelfTest()
        {
            try
            {
                if (DetectVersion("") != Current) return "migrator: empty text must be Current";
                if (DetectVersion("# only a comment\n") != Current) return "migrator: comments only must be Current";
                if (DetectVersion("[A]\nK = 1\n") != 1) return "migrator: no key = 1";
                if (DetectVersion("[Ui]\nConfigVersion = 2\n") != 2) return "migrator: ConfigVersion = 2";
                if (DetectVersion("[Ui]\nConfigVersion = junk\n") != 1) return "migrator: junk = 1";
                if (DetectVersion("[Other]\nConfigVersion = 2\n") != 1) return "migrator: wrong section = 1";

                List<string> ch;
                string old = "[Overview]\n\n## Units doc\n# Setting type: String\nUnits = nm,kn,ft\n\n## Keep me\nEnabled = true\n\n[Menu]\n\n## lang\nLanguage = ru\n\n[Ui]\n\n## lang\nLanguage = auto\n";
                string up = Upgrade(old, out ch);
                string want = "[Overview]\n\n## Keep me\nEnabled = true\n\n[Menu]\n\n[Ui]\n\n## lang\nLanguage = auto\nConfigVersion = " + Current + "\n";
                if (up != want) return "migrator: remove result = " + up.Replace("\n", "\\n");
                if (string.Join("|", ch.ToArray()) != "removed [Overview] Units|removed [Menu] Language|version 1 -> " + Current) return "migrator: changes = " + string.Join("|", ch.ToArray());
                string again = Upgrade(up, out ch);
                if (again != up || ch.Count != 0) return "migrator: second Upgrade must not change anything";

                string none = "[Overview]\nEnabled = true\n";
                string u2 = Upgrade(none, out ch);
                if (u2 != none + "\n[Ui]\n\nConfigVersion = " + Current + "\n") return "migrator: [Ui] must be created = " + u2.Replace("\n", "\\n");
                if (ch.Count != 1 || ch[0] != "version 1 -> " + Current) return "migrator: absent key changes = " + string.Join("|", ch.ToArray());

                string crlf = old.Replace("\n", "\r\n");
                string uc = Upgrade(crlf, out ch);
                if (uc != want.Replace("\n", "\r\n")) return "migrator: CRLF = " + uc.Replace("\r", "\\r").Replace("\n", "\\n");

                string noEol = "[Overview]\nEnabled = true";
                string ue = Upgrade(noEol, out ch);
                if (ue != noEol + "\n\n[Ui]\n\nConfigVersion = " + Current + "\n") return "migrator: no final EOL = " + ue.Replace("\n", "\\n");

                if (Upgrade("", out ch) != "" || ch.Count != 0) return "migrator: empty text must stay empty";
                string cur = "[Ui]\nConfigVersion = " + Current + "\n[Overview]\nUnits = x\n";
                if (Upgrade(cur, out ch) != cur) return "migrator: a file at Current must stay as it is";

                string absent = Upgrade("[Menu]\nEnabled = true\n", out ch);
                if (absent.Contains("Language")) return "migrator: remove in a missing key";

                string t171 = T171Test();
                if (t171 != null) return t171;

                string off = RingsOffTest();
                if (off != null) return off;

                string al = AlertTargetsTest();
                if (al != null) return al;
                string cr = CapReturnDefaultsTest();
                if (cr != null) return cr;
                string mv = MadVerifyOffTest();
                if (mv != null) return mv;
                string br = BuoyReviveOnTest();
                if (br != null) return br;
                string rep = ReplaceTest();
                if (rep != null) return rep;

                string renamed = RenameTest();
                if (renamed != null) return renamed;

                if (UnionCsv("a,b", " b , c,,a ") != "a,b,c") return "migrator: UnionCsv = " + UnionCsv("a,b", " b , c,,a ");
                if (UnionCsv("", null) != "") return "migrator: UnionCsv empty";
                return null;
            }
            catch (Exception e) { return "migrator: " + e.GetType().Name + ": " + e.Message; }
        }

        // T-179 (ADR-0079): [Cap] AlertOnUnknown true / false -> AlertTargets air,unknown / air; no key = nothing created; a file at 9 is left alone
        private static string AlertTargetsTest()
        {
            List<string> ch;
            string t = Upgrade("[Ui]\nConfigVersion = 8\n\n[Cap]\n## d\nAlertOnUnknown = true\nAlertNm = 90\n", out ch);
            if (!t.Contains("AlertTargets = air,unknown\n") || t.Contains("AlertOnUnknown") || !t.Contains("AlertNm = 90\n")) return "migrator: AlertOnUnknown true = " + t.Replace("\n", "\\n");
            string f = Upgrade("[Ui]\nConfigVersion = 8\n\n[Cap]\nAlertOnUnknown = false\n", out ch);
            if (!f.Contains("AlertTargets = air\n") || f.Contains("AlertOnUnknown")) return "migrator: AlertOnUnknown false = " + f.Replace("\n", "\\n");
            string n = Upgrade("[Ui]\nConfigVersion = 8\n\n[Cap]\nAlertNm = 90\n", out ch);
            if (n.Contains("AlertTargets") || !n.Contains("AlertNm = 90\n")) return "migrator: no AlertOnUnknown creates nothing = " + n.Replace("\n", "\\n");
            string at9 = "[Ui]\nConfigVersion = " + Current + "\n\n[Cap]\nAlertTargets = air,helo\n";
            if (Upgrade(at9, out ch) != at9 || ch.Count != 0) return "migrator: a file at the current version stays";
            return null;
        }

        // T-330: [Cap] MissilePct 0.25 -> 0.2 and FuelFloorPct 0.15 -> 0.05 once; own values stay; FuelMarginS untouched; a file at 12 is left alone
        private static string CapReturnDefaultsTest()
        {
            List<string> ch;
            string t = Upgrade("[Ui]\nConfigVersion = 11\n\n[Cap]\n## d\nMissilePct = 0.25\nFuelFloorPct = 0.15\nFuelMarginS = 300\n", out ch);
            if (!t.Contains("MissilePct = 0.2\n") || !t.Contains("FuelFloorPct = 0\n") || !t.Contains("FuelMarginS = 300\n")) return "migrator: cap return defaults = " + t.Replace("\n", "\\n");
            string u = Upgrade("[Ui]\nConfigVersion = 11\n\n[Cap]\nMissilePct = 0.3\nFuelFloorPct = 0.1\n", out ch);
            if (!u.Contains("MissilePct = 0.3\n") || !u.Contains("FuelFloorPct = 0.1\n")) return "migrator: own cap values stay = " + u.Replace("\n", "\\n");
            string n = Upgrade("[Ui]\nConfigVersion = 11\n\n[Cap]\nAlertNm = 90\n", out ch);
            if (n.Contains("MissilePct") || n.Contains("FuelFloorPct")) return "migrator: no cap keys creates nothing = " + n.Replace("\n", "\\n");
            string at12 = "[Ui]\nConfigVersion = " + Current + "\n\n[Cap]\nMissilePct = 0.25\nFuelFloorPct = 0.05\n";
            if (Upgrade(at12, out ch) != at12 || ch.Count != 0) return "migrator: a file at the current version stays";
            return null;
        }

        // T-316: [Asw] MadVerify true -> false once; a user false stays; no key = nothing created; a file at 11 is left alone
        private static string MadVerifyOffTest()
        {
            List<string> ch;
            string t = Upgrade("[Ui]\nConfigVersion = 10\n\n[Asw]\n## d\nMadVerify = true\nMadVerifyS = 240\n", out ch);
            if (!t.Contains("MadVerify = false\n") || t.Contains("MadVerify = true") || !t.Contains("MadVerifyS = 240\n")) return "migrator: MadVerify true = " + t.Replace("\n", "\\n");
            string u = Upgrade("[Ui]\nConfigVersion = 10\n\n[Asw]\nMadVerify = false\n", out ch);
            if (!u.Contains("MadVerify = false\n")) return "migrator: MadVerify false stays = " + u.Replace("\n", "\\n");
            string n = Upgrade("[Ui]\nConfigVersion = 10\n\n[Overview]\nEnabled = true\n", out ch);
            if (n.Contains("MadVerify")) return "migrator: no MadVerify creates nothing = " + n.Replace("\n", "\\n");
            string at11 = "[Ui]\nConfigVersion = " + Current + "\n\n[Asw]\nMadVerify = true\n";
            if (Upgrade(at11, out ch) != at11 || ch.Count != 0) return "migrator: a file at the current version stays";
            return null;
        }

        // T-299: [Harmony] BuoyRevive false -> true once; a user true stays; no key = nothing created; a file at 10 is left alone
        private static string BuoyReviveOnTest()
        {
            List<string> ch;
            string t = Upgrade("[Ui]\nConfigVersion = 9\n\n[Harmony]\n## d\nBuoyRevive = false\nEnabled = true\n", out ch);
            if (!t.Contains("BuoyRevive = true\n") || t.Contains("BuoyRevive = false") || !t.Contains("Enabled = true\n")) return "migrator: BuoyRevive false = " + t.Replace("\n", "\\n");
            string u = Upgrade("[Ui]\nConfigVersion = 9\n\n[Harmony]\nBuoyRevive = true\n", out ch);
            if (!u.Contains("BuoyRevive = true\n")) return "migrator: BuoyRevive true stays = " + u.Replace("\n", "\\n");
            string n = Upgrade("[Ui]\nConfigVersion = 9\n\n[Cap]\nAlertNm = 90\n", out ch);
            if (n.Contains("BuoyRevive")) return "migrator: no BuoyRevive creates nothing = " + n.Replace("\n", "\\n");
            string at10 = "[Ui]\nConfigVersion = " + Current + "\n\n[Harmony]\nBuoyRevive = false\n";
            if (Upgrade(at10, out ch) != at10 || ch.Count != 0) return "migrator: a file at the current version stays";
            return null;
        }

        // T-163 follow-up: the old default hotkey is replaced, a user value stays, a missing key is not created.
        private static string ReplaceTest()
        {
            List<string> ch;
            string r = Upgrade("[RadarCoverage]\n## d\nHotkey = LeftAlt+R\n\n[RangeRings]\nHotkey = LeftAlt+Q\n\n[Settings]\nHotkey = LeftAlt+Comma|F10\r\n", out ch);
            if (!r.Contains("Hotkey = LeftAlt+V|LeftAlt+Y\n")) return "migrator: replace radar = " + r.Replace("\n", "\\n");
            if (!r.Contains("Hotkey = LeftAlt+Q\n")) return "migrator: replace must keep a user value = " + r.Replace("\n", "\\n");
            if (!r.Contains("[Settings]\nHotkey = LeftAlt+Comma\r\n")) return "migrator: replace keeps CRLF of the line = " + r.Replace("\r", "\\r").Replace("\n", "\\n");
            if (r.Contains("[ContactInfo]")) return "migrator: replace must not create a key";
            if (string.Join("|", ch.ToArray()) != "replaced [RadarCoverage] Hotkey LeftAlt+R -> LeftAlt+V|LeftAlt+Y|replaced [Settings] Hotkey LeftAlt+Comma|F10 -> LeftAlt+Comma|version 1 -> " + Current) return "migrator: replace changes = " + string.Join("#", ch.ToArray());
            // T-168: the Overview columns of the untouched old default get hits,dmg; an edited list stays
            string c1 = Upgrade("[Overview]\nColumns = icon,type,dist,brg,spd,alt,dep,crs,cpa,age,idn\n", out ch);
            if (!c1.Contains("Columns = icon,type,dist,brg,spd,alt,dep,crs,cpa,age,idn,hits,dmg\n")) return "migrator: columns default = " + c1.Replace("\n", "\\n");
            string c2 = Upgrade("[Overview]\nColumns = icon,type,dist\n", out ch);
            if (!c2.Contains("Columns = icon,type,dist\n")) return "migrator: columns edited must stay = " + c2.Replace("\n", "\\n");
            return null;
        }

        // T-171: Show.Weapons / Types -> Show.Aaw / Asuw / Asw, Labels false -> true; the user's other choices stay.
        private static string T171Test()
        {
            List<string> ch;
            const string pre = "[Ui]\nConfigVersion = 4\n\n[RangeRings]\n";
            // version 6 then removes the three keys version 5 made (no category rings since 2026-10-22): the 5 -> 6 chain leaves none of them
            string r = Upgrade(pre + "## w\nShow.Weapons = false\nLabels = false\nTypes = AAW\nOpacity = 0.5\n", out ch);
            if (r.Contains("Show.Aaw") || r.Contains("Show.Asuw") || r.Contains("Show.Asw") || r.Contains("Show.Weapons") || r.Contains("Types") || !r.Contains("Labels = true\n") || !r.Contains("Opacity = 0.5\n"))
                return "migrator: T-171 off = " + r.Replace("\n", "\\n");
            if (!ch.Contains("copied [RangeRings] Show.Weapons -> Show.Aaw") || !ch.Contains("removed [RangeRings] Show.Aaw")) return "migrator: T-171 chain = " + string.Join("|", ch.ToArray());
            if (!r.Contains("ConfigVersion = " + Current)) return "migrator: T-171 version";
            // the version-5 steps in isolation (the table up to 5): Types / Show.Weapons -> Show.Aaw / Asuw / Asw
            var v5 = new List<UiCfgStep>();
            foreach (var st in Steps) if (st.Version <= 5) v5.Add(st);
            string on = Upgrade(pre + "Show.Weapons = true\nTypes = AAW, asuw\n", v5.ToArray(), out ch);
            if (!on.Contains("Show.Aaw = true\n") || !on.Contains("Show.Asuw = true\n") || !on.Contains("Show.Asw = false\n")) return "migrator: T-171 Types list = " + on.Replace("\n", "\\n");
            string noWeapons = Upgrade(pre + "Types = ASW\n", v5.ToArray(), out ch);
            if (!noWeapons.Contains("Show.Aaw = false\n") || !noWeapons.Contains("Show.Asuw = false\n") || noWeapons.Contains("Show.Asw")) return "migrator: T-171 no Show.Weapons = " + noWeapons.Replace("\n", "\\n");
            string crlf = Upgrade((pre + "Show.Weapons = false\nTypes = ASW\nLabels = false\n").Replace("\n", "\r\n"), out ch);
            if (crlf.Replace("\r\n", "").Contains("\n") || crlf.Contains("Show.Aaw") || !crlf.Contains("Labels = true\r\n")) return "migrator: T-171 CRLF = " + crlf.Replace("\r", "\\r").Replace("\n", "\\n");
            string again = Upgrade(r, out ch);
            if (again != r || ch.Count != 0) return "migrator: T-171 second Upgrade must not change anything";
            return null;
        }

        // 0.29.1: the category / fire-sector / enemy / min-range keys go with their comments; Show.Sonar "true" -> "false" once; the rest and a file at 6 stay.
        private static string RingsOffTest()
        {
            List<string> ch;
            const string pre = "[Ui]\nConfigVersion = 5\n\n[RangeRings]\n";
            string r = Upgrade(pre + "## a\nShow.Aaw = true\n\n## b\nShow.Asuw = false\nShow.Asw = true\nShow.Sectors = true\nShow.Sonar = true\nShow.Enemy = true\nShowMinRange = true\nMaxSectorUnits = 4\nShow = true\nEnabled = true\n", out ch);
            foreach (string k in new[] { "Show.Aaw", "Show.Asuw", "Show.Asw", "Show.Sectors", "Show.Enemy", "ShowMinRange", "MaxSectorUnits", "## a", "## b" })
                if (r.Contains(k)) return "migrator: rings key left " + k + " = " + r.Replace("\n", "\\n");
            if (!r.Contains("Show.Sonar = false\n") || !r.Contains("Show = true\n") || !r.Contains("Enabled = true\n") || !r.Contains("ConfigVersion = " + Current)) return "migrator: rings keeps the rest = " + r.Replace("\n", "\\n");
            if (ch.Count < 9 || ch[0] != "removed [RangeRings] Show.Aaw" || ch[7] != "replaced [RangeRings] Show.Sonar true -> false" || ch[ch.Count - 1] != "version 5 -> " + Current) return "migrator: rings changes = " + string.Join("|", ch.ToArray());
            string at6 = "[Ui]\nConfigVersion = " + Current + "\n\n[RangeRings]\nShow.Sonar = true\n";
            if (Upgrade(at6, out ch) != at6 || ch.Count != 0) return "migrator: a player's true after the migration stays";
            string crlf = Upgrade((pre + "Show.Sonar = true\nShow.Aaw = true\n").Replace("\n", "\r\n"), out ch);
            if (!crlf.Contains("Show.Sonar = false\r\n") || crlf.Contains("Show.Aaw")) return "migrator: rings CRLF";
            return null;
        }

        // The rename engine has no real step in version 2: check it on a synthetic table.
        private static string RenameTest()
        {
            var table = new[] { new UiCfgStep(2, UiCfgOp.Rename, "S", "Old", "New") };
            List<string> ch;
            string r = Upgrade("[S]\n## d\nOld = 5\n", table, out ch);
            if (r != "[S]\n## d\nNew = 5\n\n[Ui]\n\nConfigVersion = " + Current + "\n") return "migrator: rename = " + r.Replace("\n", "\\n");
            if (ch.Count != 2 || ch[0] != "renamed [S] Old -> New") return "migrator: rename changes = " + string.Join("|", ch.ToArray());
            string keep = Upgrade("[S]\nOld = 5\nNew = 7\n", table, out ch);
            if (!keep.Contains("Old = 5") || !keep.Contains("New = 7")) return "migrator: rename must not overwrite = " + keep.Replace("\n", "\\n");
            return null;
        }
    }
}
