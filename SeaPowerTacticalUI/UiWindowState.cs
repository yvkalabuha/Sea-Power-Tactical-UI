// T-148 U2: codec of [Ui] WindowState: positions/widths/collapse of the mod windows (fractions of the screen) and the open state of sections.
// Format: "v=2;asw=0.04,0.08,0.31,0;cap=0.52,0.5,0.3,0;sec=cap.thresholds:0,asw.loadout:1;opt=asw.area_route:1". Garbage never throws: bad entries are skipped.
// T-174 W2: "opt=" keeps boolean window options (Flag / SetFlag), separate from the section states ("sec=").
// T-155 K4: "v=2" marks the dense layout. A state without it (v1) is loaded with W = 0 ("no saved width": the window opens at its default width),
// X / Y / collapsed and the section states are kept.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    internal struct UiWinPos { public double X, Y, W; public bool Collapsed; }

    internal interface IUiWindowState { bool SectionOpen(string id, bool dflt); void SetSectionOpen(string id, bool open); }

    internal sealed class UiWindowStateStore : IUiWindowState, IUiWindowValues
    {
        private readonly SortedDictionary<string, UiWinPos> _win = new SortedDictionary<string, UiWinPos>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, bool> _sec = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, bool> _opt = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, string> _val = new SortedDictionary<string, string>(StringComparer.Ordinal);
        private readonly Action<string> _save;

        public UiWindowStateStore(string text, Action<string> save)
        {
            _save = save;
            Load(text);
        }

        public bool TryGet(string window, out UiWinPos p) { return _win.TryGetValue(window ?? "", out p); }

        public void Set(string window, UiWinPos p)
        {
            if (string.IsNullOrEmpty(window) || !Valid(window)) return;
            UiWinPos old;
            if (_win.TryGetValue(window, out old) && Same(old, p)) return;
            _win[window] = p;
            Save();
        }

        public bool SectionOpen(string id, bool dflt)
        {
            bool v;
            return id != null && _sec.TryGetValue(id, out v) ? v : dflt;
        }

        public void SetSectionOpen(string id, bool open)
        {
            if (string.IsNullOrEmpty(id) || !Valid(id)) return;
            bool v;
            if (_sec.TryGetValue(id, out v) && v == open) return;
            _sec[id] = open;
            Save();
        }

        /// <summary>T-174 W2: a remembered boolean option of a window ("asw.area_route"); dflt when none was saved.</summary>
        public bool Flag(string id, bool dflt)
        {
            bool v;
            return id != null && _opt.TryGetValue(id, out v) ? v : dflt;
        }

        public void SetFlag(string id, bool on)
        {
            if (string.IsNullOrEmpty(id) || !Valid(id)) return;
            bool v;
            if (_opt.TryGetValue(id, out v) && v == on) return;
            _opt[id] = on;
            Save();
        }

        /// <summary>T-176 V1: a remembered string value of a window ("asw.last.course"); dflt when none was saved.</summary>
        public string Value(string id, string dflt)
        {
            string v;
            return id != null && _val.TryGetValue(id, out v) ? v : dflt;
        }

        /// <summary>T-176 V1: remembers a string value. Only [A-Za-z0-9+._/-] is written (null / empty / other text is ignored).</summary>
        public void SetValue(string id, string v)
        {
            if (string.IsNullOrEmpty(id) || !Valid(id) || !ValidValue(v)) return;
            string old;
            if (_val.TryGetValue(id, out old) && old == v) return;
            _val[id] = v;
            Save();
        }

        private static bool ValidValue(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            foreach (char c in v)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '+' || c == '.' || c == '_' || c == '/' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>T-174 W7: drops the remembered state of a section that no longer exists (saves only when there was one).</summary>
        public void ForgetSection(string id)
        {
            if (id == null || !_sec.Remove(id)) return;
            Save();
        }

        private void Save() { if (_save != null) { try { _save(Format()); } catch { } } }

        private static bool Same(UiWinPos a, UiWinPos b)
        {
            return Math.Abs(a.X - b.X) < 5e-5 && Math.Abs(a.Y - b.Y) < 5e-5 && Math.Abs(a.W - b.W) < 5e-5 && a.Collapsed == b.Collapsed;
        }

        private static bool Valid(string name)
        {
            foreach (char c in name) if (c == '=' || c == ';' || c == ',' || c == ':' || char.IsWhiteSpace(c)) return false;
            return true;
        }

        private static string N(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

        public string Format()
        {
            var sb = new StringBuilder();
            if (_win.Count > 0 || _sec.Count > 0 || _opt.Count > 0 || _val.Count > 0) sb.Append("v=2");
            foreach (var kv in _win)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(kv.Key).Append('=').Append(N(kv.Value.X)).Append(',').Append(N(kv.Value.Y)).Append(',').Append(N(kv.Value.W)).Append(',').Append(kv.Value.Collapsed ? '1' : '0');
            }
            if (_sec.Count > 0)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append("sec=");
                bool first = true;
                foreach (var kv in _sec)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(kv.Key).Append(':').Append(kv.Value ? '1' : '0');
                }
            }
            if (_opt.Count > 0)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append("opt=");
                bool first = true;
                foreach (var kv in _opt)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(kv.Key).Append(':').Append(kv.Value ? '1' : '0');
                }
            }
            if (_val.Count > 0)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append("val=");
                bool first = true;
                foreach (var kv in _val)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(kv.Key).Append(':').Append(kv.Value);
                }
            }
            return sb.ToString();
        }

        public static UiWindowStateStore Parse(string text) { return new UiWindowStateStore(text, null); }

        private void Load(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            bool v2 = false;
            foreach (string part in text.Split(';')) if (part.Trim() == "v=2") v2 = true;
            foreach (string part in text.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string name = part.Substring(0, eq).Trim(), val = part.Substring(eq + 1).Trim();
                if (!Valid(name) || name == "v") continue;
                if (name == "sec" || name == "opt")
                {
                    var into = name == "sec" ? _sec : _opt;
                    foreach (string it in val.Split(','))
                    {
                        int c = it.LastIndexOf(':');
                        if (c <= 0) continue;
                        string id = it.Substring(0, c).Trim(), f = it.Substring(c + 1).Trim();
                        if (id.Length == 0 || (f != "0" && f != "1")) continue;
                        into[id] = f == "1";
                    }
                    continue;
                }
                if (name == "val")
                {
                    foreach (string it in val.Split(','))
                    {
                        int c = it.IndexOf(':');
                        if (c <= 0) continue;
                        string id = it.Substring(0, c).Trim(), f = it.Substring(c + 1).Trim();
                        if (id.Length == 0 || !Valid(id) || !ValidValue(f)) continue;
                        _val[id] = f;
                    }
                    continue;
                }
                string[] n = val.Split(',');
                double x, y, w;
                if (n.Length != 4 || !Num(n[0], out x) || !Num(n[1], out y) || !Num(n[2], out w)) continue;
                string cf = n[3].Trim();
                if ((cf != "0" && cf != "1") || w < 0 || w > 1.5 || x < -1 || x > 2 || y < -1 || y > 2) continue;
                _win[name] = new UiWinPos { X = x, Y = y, W = w, Collapsed = cf == "1" };
                if (!v2) { var q = _win[name]; q.W = 0; _win[name] = q; }   // v1: the saved width belonged to the old layout
            }
        }

        private static bool Num(string s, out double v)
        {
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
        }

        public static string SelfTest()
        {
            string fail = null;
            Action<string, bool> eq = (n, ok) => { if (!ok && fail == null) fail = n; };
            var s = Parse("v=2;asw=0.04,0.08,0.31,0;cap=0.52,0.5,0.3,1;sec=cap.thresholds:0,asw.loadout:1");
            UiWinPos p;
            eq("asw parsed", s.TryGet("asw", out p) && Math.Abs(p.X - 0.04) < 1e-9 && Math.Abs(p.Y - 0.08) < 1e-9 && Math.Abs(p.W - 0.31) < 1e-9 && !p.Collapsed);
            eq("cap collapsed", s.TryGet("cap", out p) && p.Collapsed);
            eq("section closed", !s.SectionOpen("cap.thresholds", true));
            eq("section open", s.SectionOpen("asw.loadout", false));
            eq("section default", s.SectionOpen("nosuch", true) && !s.SectionOpen("nosuch", false));
            eq("round trip", Parse(s.Format()).Format() == s.Format());
            eq("format sorted", s.Format() == "v=2;asw=0.04,0.08,0.31,0;cap=0.52,0.5,0.3,1;sec=asw.loadout:1,cap.thresholds:0");
            // T-155 K4: v1 (no "v=2") drops the saved width, keeps X / Y / collapsed / sections; the next write is v=2
            var old = Parse("asw=0.04,0.08,0.31,0;cap=0.52,0.5,0.3,1;sec=cap.thresholds:0");
            eq("v1 drops W", old.TryGet("asw", out p) && p.W == 0 && Math.Abs(p.X - 0.04) < 1e-9 && Math.Abs(p.Y - 0.08) < 1e-9 && !p.Collapsed);
            eq("v1 keeps collapsed", old.TryGet("cap", out p) && p.W == 0 && p.Collapsed && Math.Abs(p.X - 0.52) < 1e-9);
            eq("v1 keeps sections", !old.SectionOpen("cap.thresholds", true));
            eq("v1 is rewritten as v2", old.Format() == "v=2;asw=0.04,0.08,0,0;cap=0.52,0.5,0,1;sec=cap.thresholds:0" && Parse(old.Format()).Format() == old.Format());
            eq("v2 keeps W", Parse("v=2;asw=0.1,0.2,0.3,0").TryGet("asw", out p) && Math.Abs(p.W - 0.3) < 1e-9);
            eq("v is not a window", Parse("v=2").Format() == "");
            string[] junk = { null, "", "  ", "garbage", "=", "a=", "a=1,2", "a=x,y,z,0", "a=0.1,0.1,0.3,7", "a=0.1,0.1,-1,0", "sec=", "sec=x", "sec=x:9", ";;;;", "a b=0.1,0.1,0.3,0", "a=NaN,0,0.3,0" };
            foreach (string j in junk)
            {
                UiWindowStateStore t = null;
                try { t = Parse(j); } catch { }
                eq("junk '" + j + "' -> empty", t != null && t.Format() == "");
            }
            var mix = Parse("bad;asw=0.1,0.2,0.3,0;sec=a:1,b,c:2");
            eq("good entries survive junk", mix.TryGet("asw", out p) && mix.SectionOpen("a", false) && mix.Format() == "v=2;asw=0.1,0.2,0,0;sec=a:1");
            string saved = null; int saves = 0;
            var w = new UiWindowStateStore("", t => { saved = t; saves++; });
            w.SetSectionOpen("cap.x", false);
            eq("save on section", saved == "v=2;sec=cap.x:0" && saves == 1);
            w.SetSectionOpen("cap.x", false);
            eq("no save when unchanged", saves == 1);
            w.Set("cap", new UiWinPos { X = 0.5, Y = 0.25, W = 0.3, Collapsed = true });
            eq("save on window", saved == "v=2;cap=0.5,0.25,0.3,1;sec=cap.x:0" && saves == 2);
            w.Set("bad name", new UiWinPos { X = 0.1, Y = 0.1, W = 0.3 });
            eq("bad window name ignored", saves == 2);
            // T-174 W2: boolean options ("opt="), apart from the sections
            var o = Parse("v=2;asw=0.1,0.2,0.3,0;sec=a:1;opt=asw.area_route:1,x.y:0");
            eq("flag read", o.Flag("asw.area_route", false) && !o.Flag("x.y", true));
            eq("flag default", o.Flag("nosuch", true) && !o.Flag("nosuch", false) && !o.Flag(null, false));
            eq("flag is not a section", !o.SectionOpen("asw.area_route", false) && o.SectionOpen("a", false));
            eq("flag round trip", Parse(o.Format()).Flag("asw.area_route", false) && Parse(o.Format()).Format() == o.Format());
            eq("flag format", o.Format() == "v=2;asw=0.1,0.2,0.3,0;sec=a:1;opt=asw.area_route:1,x.y:0");
            eq("old text without opt", !Parse("v=2;sec=a:1").Flag("asw.area_route", false) && Parse("v=2;sec=a:1").Format() == "v=2;sec=a:1");
            eq("flag junk", Parse("opt=").Format() == "" && Parse("opt=x").Format() == "" && Parse("opt=x:9").Format() == "" && Parse("opt=:1").Format() == "");
            saved = null; saves = 0;
            var f = new UiWindowStateStore("", t => { saved = t; saves++; });
            f.SetFlag("asw.area_route", true);
            eq("save on flag", saved == "v=2;opt=asw.area_route:1" && saves == 1);
            f.SetFlag("asw.area_route", true);
            eq("no save when the flag is unchanged", saves == 1);
            f.SetFlag("bad id", true);
            eq("bad flag id ignored", saves == 1);
            f.SetSectionOpen("asw.noise", true);
            f.ForgetSection("asw.noise");
            eq("forget section saves", saved == "v=2;opt=asw.area_route:1" && saves == 3 && f.SectionOpen("asw.noise", false) == false);
            f.ForgetSection("asw.noise"); f.ForgetSection(null);
            eq("forget unknown is silent", saves == 3);
            // T-176 V1: string values ("val="), apart from the flags and sections
            var vv = Parse("v=2;sec=a:1;opt=x.y:1;val=asw.last.course:corridor+dip/20/20/40/40/20/auto,asw.last.area:grid");
            eq("value read", vv.Value("asw.last.course", "") == "corridor+dip/20/20/40/40/20/auto" && vv.Value("asw.last.area", "") == "grid");
            eq("value default", vv.Value("nosuch", "d") == "d" && vv.Value(null, "d") == "d");
            eq("value does not disturb flags", vv.Flag("x.y", false) && vv.SectionOpen("a", false));
            eq("value round trip", Parse(vv.Format()).Format() == vv.Format());
            eq("value format", vv.Format() == "v=2;sec=a:1;opt=x.y:1;val=asw.last.area:grid,asw.last.course:corridor+dip/20/20/40/40/20/auto");
            eq("old text without val", Parse("v=2;sec=a:1").Value("a", "z") == "z" && Parse("v=2;sec=a:1").Format() == "v=2;sec=a:1");
            eq("value junk", Parse("val=").Format() == "" && Parse("val=x").Format() == "" && Parse("val=:x").Format() == "" && Parse("val=x:").Format() == "" && Parse("val=x:a b").Format() == "" && Parse("val=x:a=b").Format() == "");
            eq("value junk keeps good", Parse("val=a:1,b,c:x y,d:ok").Format() == "v=2;val=a:1,d:ok");
            saved = null; saves = 0;
            var g = new UiWindowStateStore("", t => { saved = t; saves++; });
            g.SetValue("asw.last.area", "grid");
            eq("save on value", saved == "v=2;val=asw.last.area:grid" && saves == 1 && g.Value("asw.last.area", "") == "grid");
            g.SetValue("asw.last.area", "grid");
            eq("no save when the value is unchanged", saves == 1);
            g.SetValue("asw.last.area", "bad value"); g.SetValue("asw.last.area", "a;b"); g.SetValue("asw.last.area", "a,b"); g.SetValue("asw.last.area", ""); g.SetValue("asw.last.area", null); g.SetValue("bad id", "x");
            eq("bad value ignored", saves == 1 && g.Value("asw.last.area", "") == "grid");
            g.SetValue("asw.last.area", "mad");
            eq("value update", saves == 2 && Parse(saved).Value("asw.last.area", "") == "mad");
            return fail;
        }
    }
}
