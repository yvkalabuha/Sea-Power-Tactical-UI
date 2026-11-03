// T-148 U1: offline tests of UiKit (ru and en strings, scale 1 and 1.33) on the RecordPainter.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class MemState : IUiWindowState
    {
        public readonly Dictionary<string, bool> Open = new Dictionary<string, bool>();
        public bool SectionOpen(string id, bool dflt) { bool v; return Open.TryGetValue(id, out v) ? v : dflt; }
        public void SetSectionOpen(string id, bool open) { Open[id] = open; }
    }

    internal static class UiKitTests
    {
        private sealed class Lx
        {
            public string Lang, Long, Name, Depth, LongLabel, Short, Stop, Apply, Start, Hint, ColA, ColB, ColC, ColD;
        }

        private static readonly Lx[] Langs =
        {
            new Lx { Lang = "ru", Long = "Это очень длинный текст ячейки, который никак не поместится в узкое окно", Name = "Показать маршрут",
                     Depth = "Глубина буёв", LongLabel = "Когда спасательных плотов больше не осталось:", Short = "Кратко", Stop = "Стоп ПЛО-1", Apply = "Применить", Start = "Начать поиск",
                     Hint = "Нет групп - отметьте борты в Доступных и нажмите Новая", ColA = "Борт", ColB = "Тип", ColC = "Где", ColD = "Топливо" },
            new Lx { Lang = "en", Long = "This is a very long cell text that can never fit into the narrow window", Name = "Show route plan",
                     Depth = "Buoy depth", LongLabel = "When there are no rafts left to rescue anymore:", Short = "Short", Stop = "Stop ASW-1", Apply = "Apply", Start = "Start search",
                     Hint = "No groups - tick aircraft in Available and press New", ColA = "Unit", ColB = "Type", ColC = "Where", ColD = "Fuel" },
        };

        private static string _fail;
        private static void Lay(string name, RecordPainter p, float w, float h)
        {
            string m = p.CheckLayout(w, h);
            if (m != null && _fail == null) _fail = name + ": " + m;
        }
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }

        private sealed class Ctx
        {
            public RecordPainter P; public UiKit K; public MemState S; public UiTheme T; public float Pad; public List<string> Logs = new List<string>();
            public float Frame(float width, Action<UiKit> body)
            {
                P.NextFrame();
                K.Begin(width);
                body(K);
                return K.End();
            }
        }

        private static Ctx Make(double s)
        {
            var c = new Ctx { P = new RecordPainter(s), S = new MemState(), T = UiTheme.For(s) };
            c.K = new UiKit(c.P, c.T, c.S, "t");
            c.K.Log = m => c.Logs.Add(m);
            c.Pad = c.T.Pad;
            return c;
        }

        /// <summary>T-178 Kd3: the hit zones of the previous pass reach the painter (the body comes with Kd3).</summary>
        public static string HitWiring()
        {
            _fail = null;
            foreach (double s in new[] { 1.0, 0.85, 1.33 })
            {
                var c = Make(s);
                Action<UiKit> body = k => { k.IconButton("a", "+", "t"); k.IconButton("b", "\u2212", "t"); k.Break(); k.Check("chk", false, "Флажок", "t"); };
                c.Frame(300f * (float)s, body);
                Eq("hit: the first pass hands over no zones yet", c.P.HitRects == null || c.P.HitRects.Count == 0);
                c.Frame(300f * (float)s, body);
                var z = c.P.HitRects;
                Eq("hit: the zones of the previous pass reach the painter", z != null && z.ContainsKey("a") && z.ContainsKey("b") && z.ContainsKey("chk"));
                if (_fail != null) return _fail;
                var a = c.P.Find("a").R; var b = c.P.Find("b").R; var za = z["a"]; var zb = z["b"];
                Eq("hit: neighbours with the theme gap do not overlap", !za.Overlaps(zb));
                float slop = UiHit.Slop(s);
                Eq("hit: the outer side of a control grows by the slop", Math.Abs(za.X - (a.X - slop)) < 0.01f && Math.Abs(zb.X + zb.W - (b.X + b.W + slop)) < 0.01f);
                var ch = c.P.Find("chk").R; var zc = z["chk"];
                Eq("hit: a lone control grows by the slop on every side", Math.Abs(zc.X - (ch.X - slop)) < 0.01f && Math.Abs(zc.W - (ch.W + 2 * slop)) < 0.01f);
                Eq("hit: the gap between neighbours is shared, the zones touch", Math.Abs((a.X + a.W + b.X) / 2f - (za.X + za.W)) < 0.6f);
                if (_fail != null) return _fail;
            }
            return _fail;
        }

        public static string Run()
        {
            _fail = null;
            foreach (var l in Langs)
                foreach (double s in new[] { 1.0, 1.33 })
                {
                    string tag = l.Lang + "@" + s + ": ";
                    Wrap(l, s, tag); Truncate(l, s, tag); Labels(l, s, tag); Sections(l, s, tag); RadioCase(l, s, tag);
                    ActionsCase(l, s, tag); TableCase(l, s, tag); OverflowCase(l, s, tag); Misc(l, s, tag); HotkeyCase(l, s, tag); Recorder(s, tag);
                    if (_fail != null) return _fail;
                }
            MeasureCapCase();   // T-151
            AllocCase();   // T-239
            DefaultTipCase();   // T-259
            ThemeCase(); FrameCase();   // T-155 K1, K4
            foreach (var l in Langs)
                foreach (double s in new[] { 1.0, 1.33 })
                {
                    string tag = l.Lang + "@" + s + ": ";
                    IconCase(l, s, tag); SummaryCase(l, s, tag); LinkCase(l, s, tag); FitCase(l, s, tag);
                    if (_fail != null) return _fail;
                }
            return _fail;
        }

        /// <summary>T-259: a tip that already ends with the settings "Default: X" line gets no second "default: X" (ru, en).</summary>
        private static void DefaultTipCase()
        {
            if (_fail != null) return;
            foreach (string lang in new[] { "ru", "en" })
            {
                UiText.LoadForTests(lang);
                var k = new UiKit(new NullPainter(), UiTheme.For(1.0), new MemState(), "dt");
                string tip = "hint\n" + UiText.Fmt("settings.ui.default_is", "Auto");
                string r = k.DefaultTip(tip, "Auto");
                Eq(lang + " default tip: once", r == tip);
                string plain = k.DefaultTip("hint", "Auto");
                Eq(lang + " default tip: added when absent", plain == "hint\n" + UiText.Fmt("ui.default.tip", "Auto"));
            }
            UiText.LoadForTests("ru");
        }

        /// <summary>T-239: a fixed set of Row / Section / Check / Text / Segments / Slider on a NullPainter allocates (almost) nothing per pass once warm.</summary>
        private static void AllocCase()
        {
            if (_fail != null) return;
            var k = new UiKit(new NullPainter(), UiTheme.For(1.0), new MemState(), "alloc");
            var labels = new[] { "Один", "Два", "Три" };
            Action pass = () =>
            {
                k.Begin(560f);
                k.LabelColumn("Подпись один", "Подпись два");
                k.Section("a", "Раздел A", "◂ 2", true);
                k.Row("Подпись один", "подсказка"); k.Check("c1", true, "", "tip");
                k.Row("Подпись два, довольно длинная подпись для переноса по строкам", "подсказка"); k.Segments("s1", labels, 1, labels, null, 0);
                k.Row("Число", "tip"); k.Slider("sl", 0, 100, 5, 50, x => "v", "tip", 10);
                k.Section("b", "Раздел B", null, false);
                k.Text("Строка текста", UiTone.Dim);
                k.Text("Длинный текст с переносом по словам, который не помещается в одну строку окна", UiTone.Dim, true);
                k.End();
            };
            if (Environment.GetEnvironmentVariable("T239_PROFILE") != null)
            {
                var parts = new Dictionary<string, Action>
                {
                    { "section", () => { k.Section("a", "Раздел A", "◂ 2", true); } },
                    { "row+check", () => { k.Row("Подпись один", "подсказка"); k.Check("c1", true, "", "tip"); } },
                    { "longrow", () => { k.Row("Подпись два, довольно длинная подпись для переноса по строкам", "подсказка"); } },
                    { "segments", () => { k.Segments("s1", labels, 1, labels, null, 0); } },
                    { "slider", () => { k.Slider("sl", 0, 100, 5, 50, x => "v", "tip", 10); } },
                    { "text", () => { k.Text("Строка текста", UiTone.Dim); } },
                    { "wrap", () => { k.Text("Длинный текст с переносом по словам, который не помещается в одну строку окна", UiTone.Dim, true); } },
                };
                foreach (var kv in parts)
                {
                    Action one = () => { k.Begin(560f); k.LabelColumn("Подпись один"); kv.Value(); k.End(); };
                    for (int i = 0; i < 3; i++) one();
                    long q0 = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20; i++) one();
                    Console.WriteLine("alloc part " + kv.Key + ": " + (GC.GetAllocatedBytesForCurrentThread() - q0) / 20);
                }
            }
            for (int i = 0; i < 3; i++) pass();
            GC.Collect();
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) pass();
            long per = (GC.GetAllocatedBytesForCurrentThread() - b0) / 20;
            Eq("alloc: a warm pass allocates at most 128 B (" + per + ")", per <= 128);
        }

        // T-155 K1: two metric sets
        private static void ThemeCase()
        {
            var n = UiTheme.For(1, UiDensity.Normal);
            Eq("theme normal: heights 24/20/20/20", n.RowH == 24 && n.CtlH == 20 && n.TableRowH == 20 && n.SectionH == 20 && n.TitleH == 24 && n.HintH == 24);
            Eq("theme normal: pads 6/4/6", n.Pad == 6 && n.Gap == 4 && n.SectionGap == 6);
            Eq("theme normal: fonts 13/12/14/13", n.FontBody == 13 && n.FontSmall == 12 && n.FontTitle == 14 && n.FontSection == 13);
            Eq("theme normal: label 84-140, num 44, button 10/56, scroll 14, icon 20", n.LabelMin == 84 && n.LabelMax == 140 && n.NumW == 44 && n.BtnPad == 10 && n.BtnMin == 56 && n.ScrollW == 14 && n.IconW == 20);
            var c = UiTheme.For(1, UiDensity.Compact);
            Eq("theme compact: heights", c.RowH == 20 && c.CtlH == 18 && c.TableRowH == 16 && c.SectionH == 18 && c.TitleH == 20 && c.HintH == 18);
            Eq("theme compact: pads 4/3/3", c.Pad == 4 && c.Gap == 3 && c.SectionGap == 3);
            Eq("theme compact: fonts 12/11/12/12", c.FontBody == 12 && c.FontSmall == 11 && c.FontTitle == 12 && c.FontSection == 12);
            Eq("theme compact: label 64-110, num 34, button 6/36, scroll 10, icon 18", c.LabelMin == 64 && c.LabelMax == 110 && c.NumW == 34 && c.BtnPad == 6 && c.BtnMin == 36 && c.ScrollW == 10 && c.IconW == 18);
            Eq("theme: the default is compact", UiTheme.For(1).Density == UiDensity.Compact && UiTheme.For(1).TableRowH == 16);
            var z = UiTheme.For(1.333, UiDensity.Compact);
            Eq("theme compact 1.333: font 16, table row 21", z.FontBody == 16 && z.TableRowH == 21 && z.FontSmall == 15 && z.IconW == 24 && z.CtlH == 24);
            var h = UiTheme.For(0.5, UiDensity.Compact);
            Eq("theme: font never below 10", h.FontBody >= 10 && h.FontSmall >= 10 && h.FontTitle >= 10 && h.FontSection >= 10);
            Eq("theme: normal at 0.5 too", UiTheme.For(0.5, UiDensity.Normal).FontSmall >= 10);
            // T-178 Kd2: the scale floor 0.85 (720p)
            Eq("theme: 0.5 and 0.667 are 0.85", UiTheme.For(0.5).CtlH == UiTheme.For(0.85).CtlH && UiTheme.For(0.667).S == 0.85f && UiTheme.For(0.5).Pad == UiTheme.For(0.85).Pad);
            Eq("theme: 0.85 gives a 15 px control", UiTheme.For(0.85).CtlH == 15 && UiTheme.For(1.33).S == 1.33f);
        }

        // T-155 K4: the body height leaves room for the title and the hint line of the theme
        private static void FrameCase()
        {
            foreach (var d in new[] { UiDensity.Compact, UiDensity.Normal })
            {
                var t = UiTheme.For(1, d);
                bool sc;
                float bh = UiFrameMath.BodyHeight(5000, 1080, out sc, t.TitleH + t.HintH);
                Eq("frame " + d + ": chrome = title + hint", sc && Math.Abs(bh - (918f - t.TitleH - t.HintH)) < 0.01f);
                bh = UiFrameMath.BodyHeight(100, 1080, out sc, t.TitleH + t.HintH);
                Eq("frame " + d + ": short content fits", !sc && bh == 100);
            }
            Eq("frame: density pref defaults to compact", UiWindowPrefs.Density == UiDensity.Compact);
        }

        // T-155 K2: square glyph button
        private static void IconCase(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 300f * (float)s;
            bool plus = false, minus = false;
            c.P.Click("ib.plus");
            c.Frame(w, k =>
            {
                plus = k.IconButton("ib.plus", "+", "new group");
                minus = k.IconButton("ib.minus", "\u2212", "disband", false);
                k.Button("btn", l.Apply);
            });
            var a = c.P.Find("ib.plus"); var b = c.P.Find("ib.minus");
            Eq(tag + "icon: pressed", plus && !minus);
            Eq(tag + "icon: square CtlH x CtlH", Math.Abs(a.R.W - c.T.CtlH) < 0.01f && Math.Abs(a.R.H - c.T.CtlH) < 0.01f && a.Tip == "new group");
            Eq(tag + "icon: grey one is disabled", !b.Enabled && b.Tone == UiTone.Normal);
            Eq(tag + "icon: on the same line, in order", Math.Abs(a.R.Y - b.R.Y) < 0.01f && a.R.X + a.R.W <= b.R.X);
            Lay(tag + "icon: layout clean", c.P, w, 10000);
            // wraps like a button in a narrow window
            float narrow = 60f * (float)s;
            c.Frame(narrow, k => { for (int i = 0; i < 8; i++) k.IconButton("i" + i, "+", "t"); });
            var ys = new HashSet<float>();
            foreach (var it in c.P.Of("Button")) ys.Add(it.R.Y);
            Eq(tag + "icon: wraps", ys.Count >= 2 && c.P.CheckLayout(narrow, 10000) == null);
            c.P.Click("ib.minus");
            string dc = null;
            c.Frame(w, k => { k.IconButton("ib.minus", "\u2212", "why grey", false); dc = k.DisabledClick; });
            Eq(tag + "icon: grey click gives the tip", dc == "why grey");
        }

        // T-155 K2: summary in the bar of a collapsed section
        private static void SummaryCase(Lx l, double s, string tag)
        {
            string sum = "F-14A: auto";
            var c = Make(s);
            float w = 400f * (float)s;
            Action<UiKit> body = k =>
            {
                if (k.Section("load", l.Depth, "3", false, sum)) k.Text(l.Short);
                k.SectionButton("go", "...", "tip");
            };
            c.Frame(w, body);
            var hdr = c.P.FindText("\u25b6 " + l.Depth);
            var sm = c.P.FindText(sum);
            var cnt = c.P.FindText("3");
            var btn = c.P.Find("go");
            Eq(tag + "summary: header, summary, counter, button drawn", hdr != null && sm != null && cnt != null && btn != null);
            Eq(tag + "summary: dim, in order header < summary < button < counter", sm.Tone == UiTone.Dim && hdr.R.X + hdr.R.W <= sm.R.X && sm.R.X + sm.R.W <= btn.R.X && btn.R.X + btn.R.W <= cnt.R.X + 0.01f);
            Eq(tag + "summary: same bar line", Math.Abs(sm.R.Y - hdr.R.Y) < 0.01f);
            Lay(tag + "summary: layout clean", c.P, w, 10000);
            Eq(tag + "summary: collapsed body not drawn", c.P.FindText(l.Short) == null);
            // narrow: the summary is cut first, the header stays whole
            string longSum = "F-14A: auto, F-14B: 2 x AIM-54, F-4J: nothing at all";
            float narrow = 230f * (float)s;
            var d = Make(s);
            d.Frame(narrow, k => { k.Section("load", l.Depth, "3", false, longSum); k.SectionButton("go", "...", "tip"); });
            var h2 = d.P.FindText("\u25b6 " + l.Depth);
            Eq(tag + "summary: header whole when the bar is narrow", h2 != null && !h2.Truncated);
            var s2 = d.P.Items.Find(x => x.Kind == "Label" && x.Text != null && x.Text.StartsWith("F-14A"));
            Eq(tag + "summary: cut first, with the full tip", s2 != null && s2.Truncated && s2.Tip == longSum);
            Lay(tag + "summary: narrow layout clean", d.P, narrow, 10000);
            // very narrow: no room left, no summary, header still has a tip when cut
            var e = Make(s);
            e.Frame(120f * (float)s, k => { k.Section("load", l.Depth, "3", false, longSum); k.SectionButton("go", "...", "tip"); });
            Eq(tag + "summary: very narrow layout clean", e.P.CheckLayout(120f * (float)s, 10000) == null);
            // open: the summary is not shown
            var f = Make(s);
            f.S.Open["t.load"] = true;
            f.Frame(w, k => { if (k.Section("load", l.Depth, "3", false, sum)) k.Text(l.Short); });
            Eq(tag + "summary: open section has none", f.P.FindText(sum) == null && f.P.FindText(l.Short) != null);
        }

        private static UiTable LinkSpec(Lx l)
        {
            return new UiTable { Id = "tbl", Columns = new[] { new UiColumn(l.ColA, 6f, false, false, true), new UiColumn(l.ColB, 6f, false, false, true), new UiColumn(l.ColC, 4f), new UiColumn(l.ColD, 0f, true) } };
        }

        // T-155 K3: link cells
        private static void LinkCase(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 480f * (float)s;
            var picked = new HashSet<int>();
            string longShip = "Very Long Ship Name Class";
            Func<int, int, string> cell = (r, col) =>
                col == 0 ? "Invader " + r : col == 1 ? (r == 1 ? "\u2014" : r == 2 ? longShip : "Yorktown") : col == 2 ? "F-14A" : "zone";
            var rows = new List<int>(); var links = new List<string>();
            Action<UiKit> body = k => k.Table(LinkSpec(l), 4, cell, null, picked, r => 100 + r, 8, null, rows.Add, "rowtip", (r, col) => links.Add(r + ":" + col), (r, col) => "show " + r + ":" + col);
            c.Frame(w, body);
            Eq(tag + "link: a link column draws Link items", c.P.Find("tbl.link.0.0") != null && c.P.Find("tbl.link.0.1") != null && c.P.Find("tbl.link.0.0").Kind == "Link");
            Eq(tag + "link: other columns stay labels", c.P.Find("tbl.link.0.2") == null && c.P.FindText("F-14A") != null && c.P.FindText("F-14A").Kind == "Label");
            var dash = c.P.FindText("\u2014");
            Eq(tag + "link: the dash is a plain label", dash != null && dash.Kind == "Label" && c.P.Find("tbl.link.1.1") == null);
            Eq(tag + "link: tip of a link", c.P.Find("tbl.link.0.0").Tip == "show 0:0");
            Lay(tag + "link: layout clean", c.P, w, 10000);
            // the row Hit does not cover a link
            bool clean = true; int segs = 0;
            foreach (var h in c.P.Of("Hit"))
            {
                if (h.Id == null || !h.Id.StartsWith("tbl.row.0")) continue;
                segs++;
                foreach (var lk in c.P.Of("Link")) if (lk.Id.StartsWith("tbl.link.0.") && h.R.Overlaps(lk.R)) clean = false;
                if (h.Tip != "rowtip") clean = false;
            }
            Eq(tag + "link: row hit segments avoid the links (" + segs + ")", clean && segs >= 2);
            Eq(tag + "link: the first segment keeps the row id", c.P.Find("tbl.row.0") != null || c.P.Find("tbl.row.0.1") != null);
            // truncated link keeps the full text in the tip
            var cut = c.P.Find("tbl.link.2.1");
            Eq(tag + "link: truncated link has the full text in its tip", cut != null && cut.Truncated && cut.Tip.Contains(longShip) && cut.Tip.Contains("show 2:1"));
            // click on the link: linkClick only
            c.P.Click("tbl.link.0.1");
            c.Frame(w, body);
            Eq(tag + "link: click reports (row, col) only", links.Count == 1 && links[0] == "0:1" && rows.Count == 0 && picked.Count == 0);
            // click on the row outside the links
            string rid = null;
            foreach (var h in c.P.Of("Hit")) if (h.Id.StartsWith("tbl.row.3")) rid = h.Id;   // any segment of row 3, the last one is the widest
            Eq(tag + "link: row segment exists", rid != null);
            c.P.Click(rid);
            c.Frame(w, body);
            Eq(tag + "link: click outside the links is a row click", rows.Count == 1 && rows[0] == 3 && links.Count == 1);
            // without linkClick the Link column is plain text
            var d = Make(s);
            d.Frame(w, k => k.Table(LinkSpec(l), 2, cell, null, picked, r => 100 + r, 8, null, rows.Add));
            Eq(tag + "link: no handler = no links", d.P.Of("Link").Count == 0 && d.P.FindText("Invader 0") != null);
            // narrow: still clean
            float nw = 300f * (float)s;
            var e = Make(s);
            e.Frame(nw, k => k.Table(LinkSpec(l), 4, cell, null, picked, r => 100 + r, 8, null, rows.Add, "rowtip", (r, col) => links.Add("x"), (r, col) => "tip"));
            Lay(tag + "link: narrow layout clean", e.P, nw, 10000);
            // scrolled table: links inside the view
            e.Frame(nw, k => k.Table(LinkSpec(l), 6, cell, null, picked, r => 100 + r, 3, null, rows.Add, "rowtip", (r, col) => links.Add("x"), (r, col) => "tip"));
            Lay(tag + "link: scrolled layout clean", e.P, nw, 10000);
        }

        // live finding 0.27.2: content-fit columns (UiColumn.MaxEm) and the width wish of the table (UiKit.WantWidth)
        private static void FitCase(Lx l, double s, string tag)
        {
            const string ship = "Dwight D. Eisenhower CVN-69";
            string where = l.Lang == "ru" ? "готовится · 26 мин · взлёт 29 мин" : "preparing · 26 min · takeoff 29 min";
            Func<UiTable> spec = () => new UiTable { Id = "tbl", Columns = new[] { new UiColumn(l.ColA, 6f, false, false, true), new UiColumn(l.ColB, 6f, false, false, true) { MaxEm = UiColumn.ShipMaxEm },
                new UiColumn("Type", 4f), new UiColumn(l.ColC, 0f, true) { MaxEm = UiColumn.StateMaxEm }, new UiColumn(l.ColD, 6f) } };
            Func<int, int, string> cell = (r, col) => col == 0 ? "F-14A " + r : col == 1 ? (r == 0 ? ship : "Kiev") : col == 2 ? "F-14A" : col == 3 ? (r == 0 ? where : "in zone") : "54 % · 1:12";
            var calls = new int[1];
            Func<int, int, string> counted = (r, col) => { calls[0]++; return cell(r, col); };
            var c = Make(s);
            float narrow = 500f * (float)s;
            c.Frame(narrow, k => k.Table(spec(), 2, counted, null, new HashSet<int>(), r => r, 8, null, r => { }, null, (r, col) => { }));
            Eq(tag + "fit: cell() once per cell (" + calls[0] + ")", calls[0] == 10);
            float want = c.K.WantWidth;
            Eq(tag + "fit: a narrow window wishes more width (" + want + ")", want > narrow + 1f);
            Lay(tag + "fit: narrow layout clean", c.P, narrow, 10000);
            var cutShip = c.P.Find("tbl.link.0.1");
            Eq(tag + "fit: narrow ship cut keeps the full tip", cutShip != null && cutShip.Truncated && cutShip.Tip.Contains(ship));
            // at the wished width nothing is cut and the wish is the same
            c.Frame(want, k => k.Table(spec(), 2, cell, null, new HashSet<int>(), r => r, 8, null, r => { }, null, (r, col) => { }));
            var full = c.P.Find("tbl.link.0.1");
            Eq(tag + "fit: wished width shows the full ship " + (full == null ? "-" : full.Text), full != null && full.Text == ship && !full.Truncated);
            Eq(tag + "fit: wished width shows the full place", c.P.FindText(where) != null);
            Eq(tag + "fit: the wish is stable (" + c.K.WantWidth + " vs " + want + ")", Math.Abs(c.K.WantWidth - want) < 0.6f);
            var f = Make(s);
            f.Frame(want, k => k.Table(spec(), 2, cell, null, new HashSet<int>(), r => r, 8, null, r => { }, null, (r, col) => { }));
            Eq(tag + "fit: no overflow at the wished width (" + string.Join(" | ", f.K.Overflows) + ")", f.K.Overflows.Count == 0);
            Lay(tag + "fit: wished layout clean", c.P, want, 10000);
            // short texts: the fitted column keeps its base width, no wish beyond a normal window
            var d = Make(s);
            float w = 580f * (float)s;
            d.Frame(w, k => k.Table(spec(), 2, (r, col) => col == 1 ? "Kiev" : col == 3 ? "zone" : "x", null, null, r => r, 8));
            Eq(tag + "fit: short texts need no more width (" + d.K.WantWidth + ")", d.K.WantWidth <= w + 0.5f);
            // a cap: a very long text never asks beyond MaxEm
            var e = Make(s);
            e.Frame(w, k => k.Table(spec(), 1, (r, col) => col == 1 ? l.Long + l.Long : "x", null, null, r => r, 8));
            float capW = UiColumn.ShipMaxEm * e.T.FontBody;
            Eq(tag + "fit: the wish is capped (" + e.K.WantWidth + ")", e.K.WantWidth < w + capW);
            // Begin resets the wish
            e.Frame(w, k => k.Text(l.Short));
            Eq(tag + "fit: Begin resets the wish", e.K.WantWidth == 0);
        }

        // T-151: the text measure cache is bounded (dynamic captions would grow it for hours)
        private static void MeasureCapCase()
        {
            var c = Make(1.0);
            for (int f = 0; f < 3; f++)
                c.Frame(400f, k => { for (int i = 0; i < 2000; i++) k.Text("distance " + (f * 100000 + i) + " nm"); });
            Eq("measure cache is bounded (" + c.K.MeasureEntries + ")", c.K.MeasureEntries <= 4096 * 4 && UiKit.MeasureClears >= 1);
        }

        private static void Wrap(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 400f * (float)s;
            c.Frame(w, k => { for (int i = 0; i < 6; i++) k.Button("b" + i, l.Name + " " + i); });
            var bs = c.P.Of("Button");
            var ys = new HashSet<float>();
            bool inside = true;
            foreach (var b in bs) { ys.Add(b.R.Y); if (b.R.X + b.R.W > w - c.Pad + 0.01f) inside = false; }
            Eq(tag + "wrap: 6 buttons in 2+ lines", bs.Count == 6 && ys.Count >= 2);
            Eq(tag + "wrap: nothing past the width", inside);
            Lay(tag + "wrap: layout clean", c.P, w, 10000);
            // chips wrap too
            c.Frame(w, k => k.Chips("g", new[] { "ASW-1 " + l.Short, "ASW-2 " + l.Short, "SAR-1 " + l.Short, "CAP-1 " + l.Short, "CAP-2 " + l.Short, "CAP-3 " + l.Short, "CAP-4 " + l.Short }, 0));
            Eq(tag + "chips wrap", c.P.CheckLayout(w, 10000) == null && c.P.Of("Button").Count == 7);
        }

        private static void Truncate(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 260f * (float)s;
            c.Frame(w, k => { k.Text(l.Long); k.Text(l.Short); });
            var labels = c.P.Of("Label");
            Eq(tag + "text: long is truncated with the full tip", labels.Count == 2 && labels[0].Truncated && labels[0].Tip == l.Long);
            Eq(tag + "text: short is untouched", labels[1].Text == l.Short && !labels[1].Truncated && labels[1].Tip == null);
            Lay(tag + "text: layout clean", c.P, w, 10000);
            // a user tip is kept next to the full text
            c.Frame(w, k => k.Text(l.Long, UiTone.Normal, false, 3, "tip!"));
            Eq(tag + "text: user tip kept", c.P.Of("Label")[0].Tip.Contains("tip!") && c.P.Of("Label")[0].Tip.Contains(l.Long));
            // wrapped text: up to maxLines, last truncated with the tip
            c.Frame(w, k => k.Text(l.Long + " " + l.Long + " " + l.Long, UiTone.Normal, true, 2));
            labels = c.P.Of("Label");
            Eq(tag + "wrap text: 2 lines, last truncated with tip", labels.Count == 2 && labels[1].Truncated && !string.IsNullOrEmpty(labels[1].Tip) && labels[1].R.Y > labels[0].R.Y);
            c.Frame(w, k => k.Hint(l.Hint + " " + l.Hint));
            Eq(tag + "hint wraps", c.P.Of("Label").Count >= 2 && c.P.CheckLayout(w, 10000) == null);
        }

        private static void Labels(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 600f * (float)s;
            float col = 0;
            c.Frame(w, k => col = k.LabelColumn(l.Depth, l.LongLabel));
            Eq(tag + "labels: column within limits", col >= c.T.LabelMin - 0.01f && col <= c.T.LabelMax + 0.01f);
            col = 0;
            c.Frame(w, k => col = k.LabelColumn(l.Depth));
            Eq(tag + "labels: short label gives at least the minimum", Math.Abs(col - c.T.LabelMin) < 0.01f || col > c.T.LabelMin);
            c.Frame(w, k =>
            {
                k.LabelColumn(l.Depth, l.LongLabel);
                k.Row(l.LongLabel); k.Button("a", l.Apply);
                k.Row(l.Depth); k.Button("b", l.Stop);
            });
            var lab = c.P.Of("Label");
            Eq(tag + "labels: long label takes 2-3 lines", lab.Count >= 3 && lab.Count <= 2 + UiKit.RowLabelLines);
            UiRec second = null; float lastLabelBottom = 0; int n = 0;
            foreach (var it in lab) { if (it.Text == l.Depth) second = it; else { n++; lastLabelBottom = Math.Max(lastLabelBottom, it.R.Y + it.R.H); } }
            Eq(tag + "labels: 2-3 label lines of the long one", n >= 2 && n <= UiKit.RowLabelLines);
            Eq(tag + "labels: next row is below both lines", second != null && second.R.Y >= lastLabelBottom - 0.01f);
            Eq(tag + "labels: control right of the column", c.P.Find("a").R.X >= c.T.Pad + col - 0.01f || c.P.Find("a").R.X >= c.T.Pad + c.T.LabelMin - 0.01f);
            Lay(tag + "labels: layout clean", c.P, w, 10000);
            // a settings label of 3 lines is shown whole (was cut after 2 lines: "Light smoke of missiles and expl…")
            string smoke = l.Lang == "ru" ? "Облегчённый дым ракет и взрывов" : "Light smoke of missiles and explosions";
            c.Frame(w, k =>
            {
                k.LabelColumn(smoke, l.Depth);
                k.Row(smoke); k.Button("a", l.Apply);
                k.Row(l.Depth); k.Button("b", l.Stop);
            });
            var sl = c.P.Of("Label").FindAll(it => it.Text != l.Depth);
            Eq(tag + "labels: settings label not truncated", sl.Count >= 1 && sl.Count <= UiKit.RowLabelLines && !sl.Exists(it => it.Truncated) && string.Join(" ", sl.ConvertAll(it => it.Text).ToArray()) == smoke);
            UiRec dep = c.P.Of("Label").Find(it => it.Text == l.Depth);
            float smokeBottom = 0; foreach (var it in sl) smokeBottom = Math.Max(smokeBottom, it.R.Y + it.R.H);
            Eq(tag + "labels: next row below the wrapped settings label", dep != null && dep.R.Y >= smokeBottom - 0.01f);
            Lay(tag + "labels: wrapped settings label layout clean", c.P, w, 10000);
        }

        private static void Sections(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 500f * (float)s;
            Action<UiKit> body = k =>
            {
                if (k.Section("sec1", l.Depth, "3")) { k.Text(l.Short); k.Button("x", l.Apply); }
                k.SectionButton("stopall", l.Stop, "tip");
                if (k.Section("sec2", l.Name)) { k.Text(l.Short); }
            };
            float open = c.Frame(w, body);
            Eq(tag + "section open: body drawn", c.P.FindText(l.Short) != null && c.P.Find("x") != null);
            c.S.Open["t.sec1"] = false;
            float closed = c.Frame(w, body);
            Eq(tag + "section closed: body not drawn, shorter", c.P.Find("x") == null && closed < open);
            c.S.Open.Clear();
            c.P.Click("sec.t.sec1");
            c.Frame(w, body);
            Eq(tag + "section: header click changes the state", c.S.Open.ContainsKey("t.sec1") && c.S.Open["t.sec1"] == false);
            c.P.Click("sec.t.sec1");
            c.Frame(w, body);
            Eq(tag + "section: second click opens again", c.S.Open["t.sec1"]);
            c.Frame(w, body);
            Lay(tag + "section: layout clean (bar button, counter)", c.P, w, 10000);
            c.P.Click("stopall");
            bool stop = false;
            c.P.NextFrame(); c.K.Begin(w);
            if (c.K.Section("sec1", l.Depth)) { }
            stop = c.K.SectionButton("stopall", l.Stop, "tip");
            c.K.End();
            Eq(tag + "section button pressed", stop);
            // default closed
            var d = Make(s);
            bool o = true;
            d.Frame(w, k => o = k.Section("z", l.Name, null, false));
            Eq(tag + "section: default closed", !o);
        }

        private static void RadioCase(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 600f * (float)s;
            var labels = new[] { l.Short, l.Apply, l.Stop };
            var tips = new[] { "t0", "t1", "reason-why-grey" };
            var en = new[] { true, true, false };
            int r = -1;
            c.Frame(w, k => r = k.Radio("r", labels, 0, tips, en));
            Eq(tag + "radio: nothing pressed keeps the index", r == 0);
            Eq(tag + "radio: selected segment marked", c.P.Find("r.0").Selected && !c.P.Find("r.1").Selected && !c.P.Find("r.2").Enabled);
            c.P.Click("r.2");
            string dc = null;
            c.Frame(w, k => { r = k.Radio("r", labels, 0, tips, en); dc = k.DisabledClick; });
            Eq(tag + "radio: grey click keeps index", r == 0);
            Eq(tag + "radio: grey click gives its tip", dc == "reason-why-grey");
            c.P.Click("r.1");
            c.Frame(w, k => { r = k.Radio("r", labels, 0, tips, en); dc = k.DisabledClick; });
            Eq(tag + "radio: enabled click changes the index", r == 1 && dc == null);
            // grey button click too
            c.P.Click("gb");
            c.Frame(w, k => { k.Button("gb", l.Apply, "need an area", false); dc = k.DisabledClick; });
            Eq(tag + "button: grey click gives its tip", dc == "need an area");
        }

        private static void ActionsCase(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 600f * (float)s;
            string pressed = null;
            c.P.Click("start");
            c.Frame(w, k => pressed = k.Actions(new UiAction("route", l.Name), new UiAction("stop", l.Stop), new UiAction("start", l.Start)));
            var a = c.P.Find("route"); var b = c.P.Find("stop"); var d = c.P.Find("start");
            Eq(tag + "actions: pressed id", pressed == "start");
            Eq(tag + "actions: order kept", a.R.X < b.R.X && b.R.X < d.R.X);
            Eq(tag + "actions: pressed to the right", Math.Abs(d.R.X + d.R.W - (w - c.Pad)) < 0.01f);
            Eq(tag + "actions: primary highlighted", d.Tone == UiTone.Accent && a.Tone == UiTone.Normal);
            // narrow: wraps into rows, still inside
            float narrow = 300f * (float)s;
            c.Frame(narrow, k => k.Actions(new UiAction("route", l.Name), new UiAction("stop", l.Stop), new UiAction("start", l.Start)));
            Eq(tag + "actions: wrap inside the width", c.P.CheckLayout(narrow, 10000) == null);
            // a grey action
            c.P.Click("stop");
            string dc = null;
            c.Frame(w, k => { k.Actions(new UiAction("stop", l.Stop, "no group", false), new UiAction("start", l.Start)); dc = k.DisabledClick; });
            Eq(tag + "actions: grey click tip", dc == "no group");
        }

        private static UiTable Spec(Lx l)
        {
            return new UiTable { Id = "tbl", Columns = new[] { new UiColumn(l.ColA, 8f), new UiColumn(l.ColB, 5f), new UiColumn(l.ColC, 0f, true), new UiColumn(l.ColD, 5f, false, true) } };
        }

        private static void TableCase(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 580f * (float)s;
            var picked = new HashSet<int>();
            Func<int, int, string> cell = (r, col) => col == 0 ? "F-14A " + (301 + r) : col == 1 ? "F-14A" : col == 2 ? (r == 1 ? l.Long : "in zone") : (50 + r) + " %";
            c.Frame(w, k => k.Table(Spec(l), 3, cell, r => r == 1 ? UiTone.Warn : UiTone.Normal, picked, r => 100 + r, 8, "empty"));
            Lay(tag + "table: layout clean", c.P, w, 10000);
            var flex = c.P.FindText("in zone");
            var right = c.P.FindText("50 %");
            Eq(tag + "table: flex column takes the rest", flex != null && right != null && flex.R.X < right.R.X);
            Eq(tag + "table: right-aligned column ends at the same edge", right != null && Math.Abs((right.R.X + right.R.W) - (w - c.Pad)) < 0.6f);
            var longCell = c.P.Of("Label").Find(x => x.Text != null && x.Text.EndsWith("…"));
            if (longCell == null) Eq(tag + "table: flex cell truncated (none): " + string.Join(" | ", c.P.Items), false); else Eq(tag + "table: flex cell truncated with the full tip " + longCell + " tip=" + longCell.Tip, longCell.Tip == l.Long);
            Eq(tag + "table: flex truncation is not an overflow", c.K.Overflows.Count == 0);
            // checkboxes
            c.P.Click("tbl.chk.101");
            c.Frame(w, k => k.Table(Spec(l), 3, cell, null, picked, r => 100 + r, 8));
            Eq(tag + "table: checkbox adds the uid", picked.Contains(101) && picked.Count == 1);
            c.P.Click("tbl.chk.101");
            c.Frame(w, k => k.Table(Spec(l), 3, cell, null, picked, r => 100 + r, 8));
            Eq(tag + "table: checkbox removes the uid", picked.Count == 0);
            // scroll
            c.Frame(w, k => k.Table(Spec(l), 5, cell, null, picked, r => 100 + r, 3));
            Eq(tag + "table: more than maxRows scrolls", c.P.ScrollViews == 1);
            Lay(tag + "table: scrolled layout clean", c.P, w, 10000);
            float hScroll = c.Frame(w, k => k.Table(Spec(l), 5, cell, null, picked, r => 100 + r, 3));
            float hFull = c.Frame(w, k => k.Table(Spec(l), 5, cell, null, picked, r => 100 + r, 8));
            Eq(tag + "table: scrolled table is shorter", hScroll < hFull);
            // a click on a row (not on its checkbox) is reported by index (T-148 A: "show on the map")
            var clicked = new List<int>();
            c.Frame(w, k => k.Table(Spec(l), 3, cell, null, picked, r => 100 + r, 8, null, clicked.Add, "tip"));
            Lay(tag + "table: row hit areas keep the layout clean", c.P, w, 10000);
            var hitRow = c.P.Find("tbl.row.1");
            var chkRow = c.P.Find("tbl.chk.101");
            Eq(tag + "table: row hit area exists and starts right of the checkbox", hitRow != null && chkRow != null && hitRow.R.X >= chkRow.R.X + chkRow.R.W && hitRow.Tip == "tip");
            c.P.Click("tbl.row.1");
            c.Frame(w, k => k.Table(Spec(l), 3, cell, null, picked, r => 100 + r, 8, null, clicked.Add, "tip"));
            Eq(tag + "table: row click reports the index and leaves the pick alone", clicked.Count == 1 && clicked[0] == 1 && picked.Count == 0);
            c.Frame(w, k => k.Table(Spec(l), 3, cell, null, picked, r => 100 + r, 8));
            Eq(tag + "table: no row hit areas without a handler", c.P.Find("tbl.row.1") == null);
            // empty
            float hEmpty = c.Frame(w, k => k.Table(Spec(l), 0, cell, null, picked, r => r, 8, l.Hint));
            Eq(tag + "table: empty shows the hint", c.P.FindText(l.Hint) != null && c.P.Find("tbl.chk.0") == null && hEmpty > 0);
            c.Frame(w, k => k.Table(Spec(l), 0, cell, null, picked, r => r, 8));
            Eq(tag + "table: empty without a hint says the stock text", c.P.FindText(UiText.Get("ui.kit.empty")) != null);
        }

        private static void OverflowCase(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 580f * (float)s;
            var spec = new UiTable { Id = "tbl", Columns = new[] { new UiColumn(l.ColA, 3f), new UiColumn(l.ColC, 0f, true) } };
            Func<int, int, string> cell = (r, col) => col == 0 ? l.Long : "x";
            UiKit.ResetOverflowLog();
            for (int i = 0; i < 3; i++) c.Frame(w, k => k.Table(spec, 2, cell, null, new HashSet<int>(), r => r, 8));
            Eq(tag + "overflow: one record for the fixed column", c.K.Overflows.Count == 1 && c.K.Overflows[0].StartsWith("t tbl.0 need="));
            Eq(tag + "overflow: a short cut (before the window grows) is not logged", c.Logs.Count == 0);
            // a cut that goes away before it is stable: never logged
            Func<int, int, string> fits = (r, col) => col == 0 ? "a" : "x";
            c.Frame(w, k => k.Table(spec, 2, fits, null, new HashSet<int>(), r => r, 8));
            for (int i = 0; i < UiKit.OverflowStablePasses - 1; i++) c.Frame(w, k => k.Table(spec, 2, cell, null, new HashSet<int>(), r => r, 8));
            Eq(tag + "overflow: the streak restarts after a pass that fits", c.Logs.Count == 0);
            for (int i = 0; i < UiKit.OverflowStablePasses * 2; i++) c.Frame(w, k => k.Table(spec, 2, cell, null, new HashSet<int>(), r => r, 8));
            Eq(tag + "overflow: one log line once it stays", c.Logs.Count == 1 && c.Logs[0].StartsWith("ui overflow t tbl.0"));
            // once per window per session: a new kit of the same window (scale change) logs nothing more
            var c2 = Make(s);
            for (int i = 0; i < UiKit.OverflowStablePasses + 5; i++) c2.Frame(w, k => k.Table(spec, 2, cell, null, new HashSet<int>(), r => r, 8));
            Eq(tag + "overflow: once per window per session", c2.Logs.Count == 0 && c2.K.Overflows.Count == 1);
        }

        private static void Misc(Lx l, double s, string tag)
        {
            var c = Make(s);
            float w = 600f * (float)s;
            string num = null; bool chk = false; int cyc = 0;
            c.P.Type("n", "42"); c.P.Click("c"); c.P.Click("cy.next");
            c.Frame(w, k =>
            {
                k.Row(l.Depth);
                num = k.Number("n", "10"); k.Unit("nm");
                chk = k.Check("c", false, l.Short);
                cyc = k.Cycle("cy", l.Short);
            });
            Eq(tag + "misc: number returns typed text", num == "42");
            Eq(tag + "misc: check toggles", chk);
            Eq(tag + "misc: cycle next", cyc == 1);
            Lay(tag + "misc: layout clean", c.P, w, 10000);
            c.P.Click("cy.prev");
            c.Frame(w, k => cyc = k.Cycle("cy", l.Short));
            Eq(tag + "misc: cycle prev", cyc == -1);
            // width never grows: a row of many numbers wraps
            c.Frame(300f * (float)s, k => { k.Row(l.Depth); for (int i = 0; i < 12; i++) k.Number("n" + i, "1"); });
            Eq(tag + "misc: many fields stay inside the width", c.P.CheckLayout(300f * (float)s, 10000) == null);
            // separator and height
            float h0 = c.Frame(w, k => { });
            float h1 = c.Frame(w, k => { k.Text(l.Short); k.Separator(); k.Text(l.Short); });
            Eq(tag + "misc: height grows with content", h1 > h0 && h0 > 0);
        }

        // T-144 S2: the key row control
        private static void HotkeyCase(Lx l, double s, string tag)
        {
            foreach (float basew in new[] { 300f, 480f, 760f })
            {
                var c = Make(s);
                float w = basew * (float)s;
                string text = null; bool rec = false, clr = false;
                c.P.Type("k.f", "F9"); c.P.Click("k.rec");
                c.Frame(w, k =>
                {
                    k.LabelColumn(l.LongLabel);
                    k.Row(l.LongLabel);
                    text = k.Hotkey("k", "LeftAlt+Comma|F10", l.Long, UiTone.Warn, false, out rec, out clr);
                });
                Eq(tag + "hotkey@" + basew + ": field returns the typed text", text == "F9");
                Eq(tag + "hotkey@" + basew + ": record pressed, clear not", rec && !clr);
                Lay(tag + "hotkey@" + basew + ": layout clean", c.P, w, 10000);
                Eq(tag + "hotkey@" + basew + ": field, record and clear exist", c.P.Find("k.f") != null && c.P.Find("k.rec") != null && c.P.Find("k.clr") != null);
                c.P.Click("k.clr");
                c.Frame(w, k => k.Hotkey("k", "F1", null, UiTone.Normal, true, out rec, out clr));
                Eq(tag + "hotkey@" + basew + ": clear pressed", clr && !rec);
                Eq(tag + "hotkey@" + basew + ": recording selects the button", c.P.Find("k.rec").Selected);
                Lay(tag + "hotkey@" + basew + ": no status layout clean", c.P, w, 10000);
            }
        }

        private static void Recorder(double s, string tag)
        {
            var p = new RecordPainter(s);
            p.NextFrame();
            p.Label(new UiRect(0, 0, 100, 20), "a", UiFont.Body, UiTone.Normal, null);
            Eq(tag + "recorder: clean layout is null", p.CheckLayout(200, 100) == null);
            p.Label(new UiRect(150, 30, 100, 20), "b", UiFont.Body, UiTone.Normal, null);
            Eq(tag + "recorder: catches the width", p.CheckLayout(200, 100) != null);
            p.NextFrame();
            p.Label(new UiRect(0, 0, 100, 20), "a", UiFont.Body, UiTone.Normal, null);
            p.Label(new UiRect(50, 5, 100, 20), "b", UiFont.Body, UiTone.Normal, null);
            Eq(tag + "recorder: catches an overlap", p.CheckLayout(400, 100) != null);
            p.NextFrame();
            p.Label(new UiRect(0, 0, 100, 20), "abc…", UiFont.Body, UiTone.Normal, null);
            Eq(tag + "recorder: catches a truncated text without a tip", p.CheckLayout(400, 100) != null);
            p.NextFrame();
            p.Label(new UiRect(0, 0, 100, 20), "abc…", UiFont.Body, UiTone.Normal, "abcdef");
            Eq(tag + "recorder: truncated with a tip is fine", p.CheckLayout(400, 100) == null);
            p.NextFrame();
            p.Label(new UiRect(0, 90, 100, 20), "a", UiFont.Body, UiTone.Normal, null);
            Eq(tag + "recorder: catches the height", p.CheckLayout(400, 100) != null);
        }
    }
}
