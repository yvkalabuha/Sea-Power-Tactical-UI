// T-148 A4/A5: layout and click tests of the ASW / SAR window body (AswWindowBody) on the RecordPainter.
//  layout: ru/en x scale 1 / 1.33 x width 440 / 520 / 800 (Compact, ADR-0052) x data (empty / normal / long names / screen) x tabs course / area / sar: nothing overlaps,
//          nothing past the width, a truncated text carries a tooltip, the height stays within 85 % of the screen (course tab at 520 px, s = 1: <= 520 px);
//          no "Excluded" section; the reason line under an empty "Available"; the rename row hidden until the "..." button; the "Aircraft" / "Ship" columns.
//  clicks: which click sends which command (a fake link records them); the draft controls send nothing; links centre the map, a row ticks, no double click.
using System;
using System.Collections.Generic;
using System.Text;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal sealed class WinState : IUiWindowState, IUiWindowValues
    {
        public readonly Dictionary<string, bool> Open = new Dictionary<string, bool>();
        private readonly Dictionary<string, string> _vals = new Dictionary<string, string>();
        // T-176: the value memory of the window is the one of the window state when a stand sets it (as in the game: UiWindowPrefs.State), else a plain dictionary
        public string Value(string id, string dflt) { if (UiWindowPrefs.State != null) return UiWindowPrefs.State.Value(id, dflt); string v; return _vals.TryGetValue(id, out v) ? v : dflt; }
        public void SetValue(string id, string v) { if (UiWindowPrefs.State != null) UiWindowPrefs.State.SetValue(id, v); else _vals[id] = v; }
        public bool SectionOpen(string id, bool dflt) { bool v; return Open.TryGetValue(id, out v) ? v : dflt; }
        public void SetSectionOpen(string id, bool open) { Open[id] = open; }
    }

    internal sealed class FakeEnv : IAswWindowEnv
    {
        public double Time;
        public List<int> Focused = new List<int>();
        public string FocusError;
        public double Now() { return Time; }
        public List<string> Logs = new List<string>();
        public void Log(string m) { Logs.Add(m); }
        public string Focus(int uid) { Focused.Add(uid); return FocusError; }
    }

    internal sealed class Win
    {
        public RecordPainter P; public UiKit K; public AswWindowBody B; public AswView V; public WinState S = new WinState();
        public List<string> Sent = new List<string>();
        public List<Dictionary<string, object>> Args = new List<Dictionary<string, object>>();
        public Func<string, string> Ack = n => "{\"type\":\"ack\",\"id\":\"0\",\"ok\":true}";
        public FakeEnv Env = new FakeEnv();
        public float W;

        public Win(double s, float width, AswView v)
        {
            P = new RecordPainter(s); K = new UiKit(P, UiTheme.For(s), S, "asw"); W = width; V = v;
            B = new AswWindowBody((n, a) => { Sent.Add(n); Args.Add(a); return Ack(n); }, Env);
        }

        public float H;
        public void Frame() { P.NextFrame(); K.Begin(W); B.Draw(K, V); H = K.End(); }
        public void Click(string id) { P.Click(id); Frame(); }
        /// <summary>T-174: a click on the mode row of "Order"; the section is left for that frame, so one more frame builds the new draft.</summary>
        public void Mode(int i) { Click("mode." + i); Frame(); }
        public void Type(string id, string text) { P.Type(id, text); Frame(); }
        /// <summary>T-176: an item of a drop-down list: open it, click the item (drawn in End()), one more frame delivers the pick.</summary>
        public void Pick(string id, int index) { Click(id); Click(id + ".i." + index); Frame(); }
        public bool Has(string id) { return P.Find(id) != null; }
        public string Cmds() { return string.Join(",", Sent); }
    }

    internal static class AswWindowTests
    {
        private static string T(string key) { return UiText.Get(key); }

        private static AswRow Row(int uid, string name, string state, bool buoys = true, bool dip = true, bool mad = true)
        {
            return new AswRow { Uid = uid, Name = name, Type = "SH-3H", State = state, ShipUid = 700, DeckUid = state.StartsWith("deck", StringComparison.Ordinal) ? 700 : 0, Base = "Kirov", ReadyInS = 330, Loadout = "ASW", FuelFrac = 0.62, MinutesOnStation = 90, Passive = buoys ? 12 : 0, ActiveBuoys = buoys ? 8 : 0, Vlad = 4, Torpedoes = 2, Dipping = dip, Mad = mad, Tactic = "dip", Onboard = 3, Capacity = 8 };
        }

        private static AswGroupView Grp(string name, string order, int members, int firstUid, params string[] tactics)
        {
            var g = new AswGroupView { Name = name, Order = order, Depth = "below", TargetName = order == "course" ? "Kirov" : null, RNm = 2.1, RSource = "game", BuoysInWater = 6, Status = order == "none" ? "none" : "searching" };
            g.Tactics.AddRange(tactics);
            if (order == "course") foreach (string t in tactics) g.Course.Tactics.Add(t == "rows" ? "corridor" : t == "cross" ? "barrier" : t);
            if (order == "area") g.AreaPattern = tactics.Length == 1 ? (tactics[0] == "rows" ? "grid" : tactics[0]) : "auto";
            for (int i = 0; i < members; i++) { g.Members.Add(firstUid + i); g.MemberRows.Add(Row(firstUid + i, "SH-3H " + (610 + i), i == 1 ? "deck_prep" : "air")); }
            if (order == "sar") { g.SarArea = "all"; g.SarOnComplete = "rtb"; g.SarRafts = 5; g.SarPeople = 23; g.SarActive = 2; g.SarRescued = 11; g.SarNextLaunchS = 200; }
            if (order == "area") { g.Rect = new[] { 35.0, 20.0, 35.0, 20.2, 35.1, 20.2, 35.1, 20.0 }; g.TargetText = "район около Kirov"; }
            return g;
        }

        public static AswView Empty() { return new AswView(); }

        public static AswView Normal()
        {
            var v = new AswView();
            v.Groups.Add(Grp("ПЛО-1", "course", 3, 1, "dip"));
            v.Groups[0].TargetUid = 500; v.Groups[0].AxisDeg = 145; v.Groups[0].ReplanInS = 12;   // T-142 K5: target / axis / loadout rows are part of the layout
            v.Groups[0].CourseTargets.Add(new AswTargetOption { Uid = 500, Name = "Kirov" });
            v.Groups[0].LoadoutRows.Add(new AswLoadoutView { Type = "SH-3H", Label = "SH-3H", Chosen = "ASWKiller", Names = { "ASWHunter", "ASWKiller" } });
            v.Groups.Add(Grp("ПЛО-2", "area", 1, 10, "rows"));
            v.Groups.Add(Grp("SAR-1", "sar", 2, 20));
            v.Available.Add(Row(30, "SH-60B 701", "deck_ready"));
            v.AvailableSar.Add(Row(31, "SH-2F 703", "deck_ready"));
            v.Excluded.Add(new AswExcludedRow { Row = Row(40, "E-2C 901", "air"), Reason = "aew" });
            v.Excluded.Add(new AswExcludedRow { Row = Row(41, "SH-3H 699", "air"), Reason = "lost" });
            v.ExcludedSar.Add(new AswExcludedRow { Row = Row(42, "SH-3H 698", "air"), Reason = "no_capacity" });
            v.Warnings.Add("no_aircraft (ПЛО-1)");
            return v;
        }

        public static AswView LongNames()
        {
            var v = new AswView();
            for (int i = 0; i < 8; i++)
            {
                var g = Grp("Противолодочная группа номер " + (i + 1), i % 3 == 0 ? "sar" : i % 3 == 1 ? "course" : "area", 3, 100 + i * 10, "dip");
                g.TargetName = "Very Long Cruiser Name With Hull 1234";
                for (int m = 0; m < g.MemberRows.Count; m++) g.MemberRows[m].Name = "Sikorsky SH-3H Sea King board " + (i * 10 + m);
                v.Groups.Add(g);
            }
            for (int i = 0; i < 9; i++) { var r = Row(500 + i, "Westland Sea King HAS.5 long name " + i, "deck_service"); r.Loadout = "ASW long loadout"; v.Available.Add(r); v.AvailableSar.Add(r); }
            v.Excluded.Add(new AswExcludedRow { Row = Row(600, "Sikorsky SH-3H Sea King board", "air"), Reason = "in_group:Противолодочная группа номер 1" });
            v.Warnings.Add("continuity_gap (Противолодочная группа номер 1): 16 min");
            return v;
        }

        /// <summary>The data of the customer's screen: a deck row "SH-3H x2" of Yorktown (the old name "SH-3H x2 (Yorktown)" overflowed the column), airborne machines of Kirov.</summary>
        public static AswView Screen()
        {
            var v = Normal();
            var g = v.Groups[0];
            g.MemberRows[0] = new AswRow { Uid = -5, Name = "SH-3H \u00d72", Type = "SH-3H", State = "deck_prep", ShipUid = 9, DeckUid = 9, Base = "Yorktown", ReadyInS = 330, FuelFrac = 1, MinutesOnStation = 100, Tactic = "dip", Dipping = true, Mad = true, Passive = 12, ActiveBuoys = 8, Vlad = 4, Loadout = "ASW" };
            g.MemberRows[1].Name = "Hawk 1"; g.MemberRows[2].Name = "Hawk 2";
            return v;
        }

        // ------------------------------------------------------------------ layout

        private static string NoExcluded(Win win)
        {
            foreach (var it in win.P.Items)
                if (it.Id != null && (it.Id.StartsWith("excl", StringComparison.Ordinal) || it.Id.Contains("sec.asw.excluded") || it.Id.StartsWith("sec.asw.excl", StringComparison.Ordinal))) return "the Excluded section is back: " + it;
            if (win.P.FindText(T("asw.col.reason")) != null) return "reason column";
            return null;
        }

        public static string Layout()
        {
            var sb = new StringBuilder();
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                    foreach (double s in new[] { 1.0, 1.33 })
                        foreach (float width in new[] { 440f, 520f, 800f })
                        {
                            float w = width * (float)s;
                            string[] names = { "empty", "normal", "long", "screen" };
                            for (int di = 0; di < 4; di++)
                                for (int tab = 0; tab < 3; tab++)
                                {
                                    string tag = lang + " s=" + s + " w=" + width + " data=" + names[di] + " tab=" + tab;
                                    var v = di == 0 ? Empty() : di == 1 ? Normal() : di == 2 ? LongNames() : Screen();
                                    var win = new Win(s, w, v);
                                    var shipData = ShipData(di == 2);
                                    win.B.SarShips = () => shipData;
                                    win.Frame();
                                    win.Mode(tab);
                                    win.Frame();
                                    if (win.B.Mode != AswWindowModel.OrderModes[tab]) { sb.Append(tag + ": tab did not switch; "); continue; }
                                    string r = win.P.CheckLayout(w, (float)(0.85 * 1080 * s));
                                    if (r != null) sb.Append(tag + ": " + r + "\n");
                                    r = NoExcluded(win);
                                    if (r != null) sb.Append(tag + ": " + r + "\n");
                                    if (di == 0 && win.P.FindText(T("asw.hint.no_groups")) == null) sb.Append(tag + ": empty window lacks the no-groups hint\n");
                                    if ((di == 1 || di == 3) && width >= 520f && win.K.Overflows.Count > 0) sb.Append(tag + ": overflow " + string.Join("|", win.K.Overflows) + "\n");
                                    // a selected group of every kind (the tab follows the order)
                                    if (di != 0)
                                    {
                                        for (int gi = 0; gi < 3; gi++)
                                        {
                                            win.B.SelectedGroup = v.Groups[gi].Name;
                                            win.Frame(); win.Frame();
                                            r = win.P.CheckLayout(w, (float)(0.85 * 1080 * s));
                                            if (r != null) sb.Append(tag + " group " + gi + ": " + r + "\n");
                                            if (di == 3 && width >= 520f && win.K.Overflows.Count > 0) sb.Append(tag + " group " + gi + ": overflow " + string.Join("|", win.K.Overflows) + "\n");
                                            r = NoExcluded(win);
                                            if (r != null) sb.Append(tag + " group " + gi + ": " + r + "\n");
                                        }
                                    }
                                    if (sb.Length > 2500) return sb.ToString();
                                }
                        }
            string e = Compact(); if (e != null) sb.Append(e);
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>The height of the screen data on the course tab at s = 1, 520 px, Compact: at most 520 px (the mockup: about 493); the structure of the sections.</summary>
        private static string Compact()
        {
            var sb = new StringBuilder();
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    var v = Screen();
                    var win = new Win(1.0, 520f, v);
                    win.Frame(); win.B.SelectedGroup = "\u041f\u041b\u041e-1"; win.Frame(); win.Frame();
                    float h = win.K.End();
                    float bottom = 0;
                    foreach (var it in win.P.Items) if (it.Kind != "Fill" && it.Kind != "Scroll") bottom = Math.Max(bottom, it.R.Y + it.R.H);
                    if (bottom > 520f) sb.Append(lang + ": the course window is " + (int)bottom + " px high (max 520)\n");
                    if (win.Has("mode.0") == false) sb.Append(lang + ": no mode row\n");
                    // the structure: the loadout is collapsed and says what it holds in the bar; the rename row is hidden; the icon buttons are there
                    if (win.P.FindText("\u25b6 " + T("asw.sec.loadout")) == null) sb.Append(lang + ": the loadout section is not collapsed\n");
                    if (win.Has("rename") || win.Has("rename_ok")) sb.Append(lang + ": the rename row is shown without the button\n");
                    foreach (string id in new[] { "grp_new", "grp_disband", "grp_rename", "stop_all", "remove", "add" }) if (!win.Has(id)) sb.Append(lang + ": no button " + id + "\n");
                    var plus = win.P.Find("grp_new");
                    if (plus != null && Math.Abs(plus.R.W - plus.R.H) > 0.5f) sb.Append(lang + ": the + button is not square\n");
                    win.Click("grp_rename"); win.Frame();
                    if (!win.Has("rename") || !win.Has("rename_ok")) sb.Append(lang + ": the rename row is not opened by the button\n");
                    string rc = win.P.CheckLayout(520f, 560f);
                    if (rc != null) sb.Append(lang + ": rename row layout: " + rc + "\n");
                    // the tables: Aircraft / Ship columns as links, the Ship column header, no overflow on the screen data
                    win = new Win(1.0, 520f, Screen());
                    win.Frame(); win.B.SelectedGroup = "\u041f\u041b\u041e-1"; win.Frame(); win.Frame();
                    if (win.P.FindText(T("asw.col.ship")) == null) sb.Append(lang + ": no Ship column header\n");
                    if (!win.Has("members.link.0.0") || !win.Has("members.link.0.1") || !win.Has("members.link.1.0") || !win.Has("avail.link.0.0") || !win.Has("avail.link.0.1")) sb.Append(lang + ": link cells missing\n");
                    var dl = win.P.Find("members.link.0.0");
                    if (dl == null || dl.Text != "SH-3H \u00d72") sb.Append(lang + ": the deck row Aircraft cell is " + (dl == null ? "missing" : dl.Text) + "\n");
                    if (win.K.Overflows.Count > 0) sb.Append(lang + ": overflow " + string.Join("|", win.K.Overflows) + "\n");
                    // the Excluded section is gone; an empty Available says why in one line, the tip lists them
                    var v2 = Normal(); v2.Available.Clear();
                    win = new Win(1.0, 520f, v2);
                    win.Frame(); win.Frame();
                    string why = AswWindowModel.RefusalSummary(v2.Excluded);
                    var wl = why == null ? null : win.P.FindText(why);
                    if (wl == null) sb.Append(lang + ": no reason line under an empty Available (" + why + ")\n");
                    else if (string.IsNullOrEmpty(wl.Tip) || !wl.Tip.Contains("E-2C 901") || !wl.Tip.Contains("SH-3H 699")) sb.Append(lang + ": the reason line tip lacks the list: " + wl.Tip + "\n");
                    string ne = NoExcluded(win); if (ne != null) sb.Append(lang + ": " + ne + "\n");
                    win.Mode(2);
                    v2.AvailableSar.Clear(); win.Frame();
                    why = AswWindowModel.RefusalSummary(v2.ExcludedSar);
                    var sl = why == null ? null : win.P.FindText(why);
                    if (sl == null || string.IsNullOrEmpty(sl.Tip) || !sl.Tip.Contains("SH-3H 698")) sb.Append(lang + ": SAR tab: no reason line / list\n");
                    // the third tab is short and its tip carries the full name
                    var tab2 = win.P.Find("mode.2");
                    if (tab2 == null || tab2.Text != T("asw.tab.sar") || tab2.Tip == null || !tab2.Tip.Contains(SarPanel.TabTip())) sb.Append(lang + ": SAR tab label / tip\n");
                }
            return sb.Length == 0 ? null : sb.ToString();
        }

        // ------------------------------------------------------------------ clicks

        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }
        private static string Str(Dictionary<string, object> a, string k) { object v; return a != null && a.TryGetValue(k, out v) ? v as string : null; }

        private static Win Make(AswView v, double s = 1.0)
        {
            UiWindowPrefs.State = new UiWindowStateStore("", null);   // every case starts with an empty memory (the last order of a mode would change the defaults of the next case)
            var w = new Win(s, 520f, v);
            w.Frame();
            return w;
        }

        private static string Err(string tail) { return "{\"type\":\"ack\",\"id\":\"0\",\"ok\":false,\"error\":\"" + tail + "\"}"; }

        public static string Clicks()
        {
            var oldPrefs = UiWindowPrefs.State;
            UiWindowPrefs.State = new UiWindowStateStore("", null);   // the "Route" flags and the last orders of the window are remembered here
            try { return ClicksIn(); }
            finally { UiWindowPrefs.State = oldPrefs; }
        }

        private static string ClicksIn()
        {
            _fail = null;
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    Draft(lang); if (_fail != null) return lang + ": " + _fail;
                    Orders(lang); if (_fail != null) return lang + ": " + _fail;
                    Groups(lang); if (_fail != null) return lang + ": " + _fail;
                    Links(lang); if (_fail != null) return lang + ": " + _fail;
                    Refusals(lang); if (_fail != null) return lang + ": " + _fail;
                    Sar(lang); if (_fail != null) return lang + ": " + _fail;
                    SarShips(lang); if (_fail != null) return lang + ": " + _fail;
                    Sockets(lang); if (_fail != null) return lang + ": " + _fail;
                    Course(lang); if (_fail != null) return lang + ": " + _fail;
                    Contacts(lang); if (_fail != null) return lang + ": " + _fail;
                    AreaTactics(lang); if (_fail != null) return lang + ": " + _fail;
                }
            return null;
        }

        /// <summary>T-154 B1: the area tab with an unknown load keeps "buoys" selectable; a greyed primary tactic is not taken into the draft.</summary>
        private static void AreaTactics(string tag)
        {
            // case 1: a hangar machine with an unknown load next to a buoy machine; the group keeps tactic dip from a past order
            var v = new AswView();
            var g = Grp("ПЛО-5", "none", 0, 60, "dip");
            var air = Row(60, "SH-2F 601", "air", buoys: true, dip: false, mad: false); air.Passive = 15; air.ActiveBuoys = 0;
            var hangar = Row(61, "SH-2F 602", "deck_service", buoys: false, dip: false, mad: false); hangar.LoadKnown = false;
            g.Members.Add(60); g.Members.Add(61); g.MemberRows.Add(air); g.MemberRows.Add(hangar);
            v.Groups.Add(g);
            var w = Make(v);
            w.B.SelectedGroup = "ПЛО-5"; w.Frame(); w.Frame();
            w.Mode(1);
            Eq(tag + " buoys enabled with an unknown load", w.Has("pattern.1") && w.P.Find("pattern.1").Enabled);
            w.Click("pattern.1");
            Eq(tag + " the draft holds the grid: " + w.B.Draft.AreaPattern, w.B.Draft.AreaPattern == "grid");
            Eq(tag + " the press is logged: " + string.Join("|", w.Env.Logs), w.Env.Logs.Exists(s => s.StartsWith("asw window press pattern.1 -> grid")));
            w.Click("asrc.0");
            w.B.OnRect(new[] { 35.0, 20.0, 35.0, 20.2, 35.1, 20.2, 35.1, 20.0 });
            w.Frame(); w.Click("start_area");
            Eq(tag + " Start -> asw_search_area pattern grid: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_search_area" && Str(w.Args[0], "pattern") == "grid");
            Eq(tag + " Start press logged", w.Env.Logs.Exists(s => s.StartsWith("asw window press start_area")));

            // case 2: everything known, only buoys are offered, the group says dip -> the draft starts on rows
            v = new AswView();
            g = Grp("ПЛО-6", "none", 0, 70, "dip");
            var a1 = Row(70, "SH-3H 1", "air", buoys: true, dip: false, mad: false);
            g.Members.Add(70); g.MemberRows.Add(a1);
            v.Groups.Add(g);
            w = Make(v);
            w.B.SelectedGroup = "ПЛО-6"; w.Frame(); w.Frame();
            w.Mode(1);
            Eq(tag + " draft is auto, not the greyed dip: " + w.B.Draft.AreaPattern, w.B.Draft.AreaPattern == "auto");
            Eq(tag + " dip is grey and not shown selected", w.P.Find("pattern.2") != null && !w.P.Find("pattern.2").Enabled && !w.P.Find("pattern.2").Selected);
        }

        /// <summary>Everything that edits the draft sends nothing.</summary>
        private static void Draft(string tag)
        {
            var v = Normal();
            var w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            Eq(tag + " the group follows its order to the course tab", w.B.Mode == "course");
            w.Mode(1); w.Mode(2); w.Mode(0);
            w.Click("tactic.0"); w.Click("tactic.2"); w.Click("depth.1"); w.Click("depth.3");
            Eq(tag + " there is no text field in the order section", !w.Has("lead") && !w.Has("width") && !w.Has("bearing") && !w.Has("dist"));
            Eq(tag + " draft edits send nothing: " + w.Cmds(), w.Sent.Count == 0);
            w.Mode(1);
            w.Click("asrc.0");
            Eq(tag + " draw wants the map input", w.B.WantInput == "asw_rect" && w.Sent.Count == 0);
            w.B.OnRect(new[] { 35.0, 20.0, 35.0, 20.2, 35.1, 20.2, 35.1, 20.0 });
            Eq(tag + " the drawn area is a draft, nothing sent", w.Sent.Count == 0 && w.B.WantInput == null && w.B.Draft.AreaSource == "rect");
            w.Click("asrc.1"); w.Click("brg.p.2"); w.Pick("dist", 4); w.Pick("size", 2);
            Eq(tag + " the compass, the distance and the size send nothing: " + w.Cmds(), w.Sent.Count == 0 && w.B.Draft.AreaSource == "bearing");
            // the draft survives frames
            w.Frame(); w.Frame();
            Eq(tag + " draft is kept: " + w.B.Draft.BearingDeg + "/" + w.B.Draft.DistNm + "/" + w.B.Draft.SizeNm, w.B.Draft.BearingDeg == 90 && w.B.Draft.DistNm == 30 && w.B.Draft.SizeNm == 15);
        }

        private static void Orders(string tag)
        {
            // Start: a group without an order -> one full search with the draft tactic and depth
            var v = Normal();
            v.Groups.Add(Grp("ПЛО-9", "none", 1, 50));
            var w = Make(v);
            w.B.SelectedGroup = "ПЛО-9"; w.Frame(); w.Frame();
            w.Click("tactic.1"); w.Click("depth.2");
            Eq(tag + " nothing before Start", w.Sent.Count == 0);
            Eq(tag + " Start course exists", w.Has("start_course") && w.P.Find("start_course").Enabled);
            w.Click("start_course");
            Eq(tag + " Start -> one asw_search_course: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_search_course" && Str(w.Args[0], "group") == "ПЛО-9" && Str(w.Args[0], "depth") == "below"
                && ((List<object>)w.Args[0]["tactics"])[0] as string == "corridor" && w.Args[0].ContainsKey("corridor_x_nm") && w.Args[0].ContainsKey("standoff"));
            Eq(tag + " the answer says so", w.B.Note == T("asw.say.ordered") && w.B.NoteTone == UiTone.Ok);

            // Apply on a running order: the minimal difference
            v = Normal();
            w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            Eq(tag + " Apply is grey without changes", w.Has("apply") && !w.P.Find("apply").Enabled);
            w.Click("apply");
            Eq(tag + " grey Apply sends nothing and explains itself", w.Sent.Count == 0 && w.B.Note == T("asw.hint.same"));
            float applyY0 = w.P.Find("apply").R.Y, h0 = w.H;
            w.Click("depth.1");   // a chip adds a row of its own (barrier): the reserved room is checked with the depth
            Eq(tag + " Apply is live after a change", w.P.Find("apply").Enabled);
            // 0.28.1: the "Changed: ... press Apply" line has reserved room, the window height and the buttons do not move
            Eq(tag + " dirty hint shown", w.P.Of("Label").Exists(it => it.Tone == UiTone.Warn));
            Eq(tag + " buttons and height stay when the dirty hint appears: y " + applyY0 + "->" + w.P.Find("apply").R.Y + " h " + h0 + "->" + w.H,
               Math.Abs(w.P.Find("apply").R.Y - applyY0) < 0.01f && Math.Abs(w.H - h0) < 0.01f);
            w.Click("apply");
            Eq(tag + " depth -> asw_depth: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_depth");
            w.Sent.Clear(); w.Args.Clear();
            v = Normal(); w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            w.Click("tactic.2"); w.Click("apply");
            Eq(tag + " tactic only -> asw_set_tactics: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_set_tactics" && Str(w.Args[0], "group") == "ПЛО-1");
            w.Sent.Clear(); w.Args.Clear();
            v = Normal(); w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            w.Click("depth.1"); w.Click("apply");
            Eq(tag + " depth only -> asw_depth: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_depth" && Str(w.Args[0], "mode") == "above");
            // geometry -> the full order
            v = Normal(); w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            w.Pick("dip_l", 3); w.Click("apply");
            Eq(tag + " standoff edit -> full asw_search_course: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_search_course" && ((Dictionary<string, object>)w.Args[0]["standoff"])["dip"] as double? == 50.0);
            w.Sent.Clear(); w.Args.Clear();
            w.Frame(); w.Frame();
            Eq(tag + " after Apply the draft equals the order again", w.Has("apply") && !w.P.Find("apply").Enabled);

            // area tab: no area -> grey Start with the reason
            v = Normal(); v.Groups.Add(Grp("ПЛО-9", "none", 1, 50));
            w = Make(v);
            w.B.SelectedGroup = "ПЛО-9"; w.Frame(); w.Mode(1);
            Eq(tag + " area Start is grey without an area", w.Has("start_area") && !w.P.Find("start_area").Enabled);
            w.Click("start_area");
            Eq(tag + " grey Start area sends nothing, says why", w.Sent.Count == 0 && w.B.Note == T("asw.hint.need_area"));
            w.Click("asrc.0");
            w.B.OnRect(new[] { 35.0, 20.0, 35.0, 20.2, 35.1, 20.2, 35.1, 20.0 });
            w.Frame();
            w.Click("start_area");
            Eq(tag + " area Start after OnRect -> asw_search_area with 4 pairs: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_search_area" && ((List<object>)w.Args[0]["rect"]).Count == 4);
            // a grey MAD explains itself
            v = Normal(); v.Groups[1].MemberRows[0].Mad = false;
            w = Make(v);
            w.B.SelectedGroup = "ПЛО-2"; w.Frame(); w.Frame();
            Eq(tag + " the area group opens the area tab", w.B.Mode == "area");
            int madIdx = -1;
            for (int i = 0; i < 4; i++) { var r = w.P.Find("pattern." + i); if (r != null && !r.Enabled) madIdx = i; }
            Eq(tag + " a pattern is grey", madIdx >= 0);
            w.Click("pattern." + madIdx);
            Eq(tag + " grey tactic click: reason, no command", w.Sent.Count == 0 && (w.B.Note == T("asw.tactic.no_mad") || w.B.Note == T("asw.tactic.no_dip")));

            // route preview
            v = Normal(); w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            w.Click("tactic.2");   // the draft says more than the order: the dry run goes
            w.Click("route");
            Eq(tag + " route -> asw_preview (kept): " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_preview" && w.Args[0]["keep"] is bool && (bool)w.Args[0]["keep"] && AswDrawPlan.CourseRouteOn());
            v.Preview.Add(new AswLineView { Kind = "preview" });
            w.Sent.Clear(); w.Args.Clear();
            w.Frame(); w.Click("route");
            Eq(tag + " route again -> asw_preview_clear: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_preview_clear" && !AswDrawPlan.CourseRouteOn());
            // stop
            v = Normal(); w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            w.Click("stop");
            Eq(tag + " Stop ASW-1 -> asw_stop {group}: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_stop" && Str(w.Args[0], "group") == "ПЛО-1" && !w.Args[0].ContainsKey("all"));
            w = Make(Empty());
            Eq(tag + " Stop is grey without a group", w.Has("stop") && !w.P.Find("stop").Enabled);
            w.Click("stop");
            Eq(tag + " grey Stop sends nothing", w.Sent.Count == 0);
            w = Make(Normal());
            w.Click("stop_all");
            Eq(tag + " Stop all -> asw_stop {all:true}", w.Sent.Count == 1 && w.Sent[0] == "asw_stop" && w.Args[0].ContainsKey("all"));
            // an answer with an error
            v = Normal(); w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            w.Ack = n => Err("!no_aircraft");
            w.Click("tactic.2"); w.Click("apply");
            string want = UiText.Fmt("sar.say.failed", T("sar.err.no_aircraft"));
            Eq(tag + " error answer is shown: " + w.B.Note, w.B.Note == want && w.B.NoteTone == UiTone.Error);
            int serial = w.B.NoteSerial;
            w.Frame();
            Eq(tag + " a note is announced once", w.B.NoteSerial == serial);
        }

        private static void Groups(string tag)
        {
            var v = Normal();
            var w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            w.Click("avail.chk.30"); w.Click("grp_new");
            Eq(tag + " + New -> asw_group create with the ticked: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_group" && Str(w.Args[0], "op") == "create" && ((List<object>)w.Args[0]["uids"]).Count == 1);
            w.Sent.Clear(); w.Args.Clear();
            w.Click("grp_disband");
            Eq(tag + " Disband -> disband {group}", w.Sent.Count == 1 && Str(w.Args[0], "op") == "disband" && Str(w.Args[0], "group") == "ПЛО-1");
            w.Sent.Clear(); w.Args.Clear();
            w.B.SelectedGroup = "ПЛО-1"; w.Frame();
            w.Click("members.chk.2"); w.Click("remove");
            Eq(tag + " Remove ticked -> remove {group, uids}", w.Sent.Count == 1 && Str(w.Args[0], "op") == "remove" && Str(w.Args[0], "group") == "ПЛО-1" && ((List<object>)w.Args[0]["uids"]).Count == 1);
            w.Sent.Clear(); w.Args.Clear();
            w.B.SelectedGroup = "ПЛО-1"; w.Frame();
            w.Click("avail.chk.30"); w.Click("add");
            Eq(tag + " To group -> add {group, uids}", w.Sent.Count == 1 && Str(w.Args[0], "op") == "add" && Str(w.Args[0], "group") == "ПЛО-1");
            w.Sent.Clear(); w.Args.Clear();
            Eq(tag + " the button names the group", w.P.Find("add") != null && w.P.Find("add").Text.Contains("ПЛО-1"));

            // rename (T-150 adds the controller command; the window sends it)
            w = Make(Normal());
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            Eq(tag + " the rename row is hidden until the button", !w.Has("rename") && !w.Has("rename_ok") && w.Has("grp_rename"));
            w.Click("grp_rename"); w.Frame();
            Eq(tag + " rename button is grey while the name is unchanged", w.Has("rename_ok") && !w.P.Find("rename_ok").Enabled);
            w.Type("rename", "Охрана");
            Eq(tag + " rename is live after typing", w.P.Find("rename_ok").Enabled);
            w.Click("rename_ok");
            Eq(tag + " rename -> asw_group {op:rename, group, name}: " + w.Cmds(), w.Sent.Count == 1 && Str(w.Args[0], "op") == "rename" && Str(w.Args[0], "group") == "ПЛО-1" && Str(w.Args[0], "name") == "Охрана");
            Eq(tag + " the selection follows the new name", w.B.SelectedGroup == "Охрана");
            w.Frame();
            Eq(tag + " the rename row is hidden again after OK", !w.Has("rename"));
            w = Make(Normal());
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            w.Ack = n => Err("bad_args");
            w.Click("grp_rename"); w.Frame();
            w.Type("rename", "Охрана"); w.Click("rename_ok");
            Eq(tag + " a refused rename keeps the selection and says so", w.B.SelectedGroup == "ПЛО-1" && w.B.NoteTone == UiTone.Error);

            // a click on a row ticks it, nothing else (the links centre the map; no double click)
            w = Make(Normal());
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            Eq(tag + " Remove is grey with nothing ticked", w.Has("remove") && !w.P.Find("remove").Enabled && w.P.Find("remove").Tip == T("asw.hint.pick_first"));
            w.Click("remove");
            Eq(tag + " grey Remove is never silent", w.Sent.Count == 0 && w.B.Note == T("asw.hint.pick_first"));
            w.Click("grp_new");
            Eq(tag + " + New is grey with nothing ticked and explains itself", w.Sent.Count == 0 && w.B.Note == T("asw.hint.pick_first"));
            w.Click(RowHit(w, "members", 1)); w.Frame();   // the section buttons are drawn above the table: they see the tick one frame later
            Eq(tag + " one click on a member row ticks it, no focus", w.Env.Focused.Count == 0 && w.P.Find("remove").Enabled && w.Sent.Count == 0);
            w.Env.Time = 5;
            w.Click(RowHit(w, "avail", 0)); w.Frame();
            Eq(tag + " one click on an available row ticks it", w.Env.Focused.Count == 0 && w.P.Find("add").Enabled);
            w.Env.Time = 5.2;
            w.Click(RowHit(w, "avail", 0)); w.Frame();
            Eq(tag + " a quick second click does NOT centre the map: the tick is simply off", w.Env.Focused.Count == 0 && !w.P.Find("add").Enabled);
        }

        /// <summary>The id of the widest click zone of a table row (the rest of the row outside the links).</summary>
        private static string RowHit(Win w, string table, int row)
        {
            UiRec best = null;
            foreach (var it in w.P.Items)
                if (it.Kind == "Hit" && it.Id != null && (it.Id == table + ".row." + row || it.Id.StartsWith(table + ".row." + row + ".", StringComparison.Ordinal)) && (best == null || it.R.W > best.R.W)) best = it;
            return best == null ? table + ".row." + row : best.Id;
        }

        private static bool RowHitsClear(Win w, string table, int row)
        {
            foreach (var h in w.P.Items)
                if (h.Kind == "Hit" && h.Id != null && h.Id.StartsWith(table + ".row." + row, StringComparison.Ordinal))
                    foreach (var l in w.P.Items)
                        if (l.Kind == "Link" && l.Id != null && l.Id.StartsWith(table + ".link." + row + ".", StringComparison.Ordinal) && h.R.Overlaps(l.R)) return false;
            return true;
        }

        /// <summary>T-155: the "Aircraft" and "Ship" cells are links: they centre the map and leave the ticks alone; the rest of the row ticks.</summary>
        private static void Links(string tag)
        {
            var v = Screen();
            var w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            // row 0 = the deck row (Uid -5, ship 9), row 1 = a machine in the air (uid 2? the deck_prep fixture) ; find them by the data
            var rows = v.Groups[0].MemberRows;
            Eq(tag + " fixture: deck row first", rows[0].Uid == -5 && rows[0].State == "deck_prep");
            int airIdx = rows.FindIndex(r => r.State == "air");
            Eq(tag + " fixture: an airborne row exists", airIdx > 0);
            w.Click("members.link.0.0");
            Eq(tag + " the deck aircraft link centres on the ship: " + string.Join(",", w.Env.Focused), w.Env.Focused.Count == 1 && w.Env.Focused[0] == 9);
            Eq(tag + " a link does not tick the row", w.P.Find("members.chk.-5") != null && !w.P.Find("members.chk.-5").Selected && !w.P.Find("remove").Enabled && w.Sent.Count == 0);
            w.Env.Focused.Clear();
            w.Click("members.link.0.1");
            Eq(tag + " the ship link centres on the ship", w.Env.Focused.Count == 1 && w.Env.Focused[0] == 9);
            w.Env.Focused.Clear();
            w.Click("members.link." + airIdx + ".0");
            Eq(tag + " the airborne aircraft link centres on the machine", w.Env.Focused.Count == 1 && w.Env.Focused[0] == rows[airIdx].Uid);
            w.Env.Focused.Clear();
            w.Click("members.link." + airIdx + ".1");
            Eq(tag + " its ship link centres on the home ship", w.Env.Focused.Count == 1 && w.Env.Focused[0] == 700);
            Eq(tag + " links send nothing and tick nothing", w.Sent.Count == 0 && !w.P.Find("remove").Enabled);
            // the available table
            w.Env.Focused.Clear();
            w.Click("avail.link.0.0");
            Eq(tag + " available: the deck aircraft link centres on the ship", w.Env.Focused.Count == 1 && w.Env.Focused[0] == 700 && !w.P.Find("add").Enabled);
            w.Env.Focused.Clear();
            w.Click("avail.link.0.1");
            Eq(tag + " available: the ship link", w.Env.Focused.Count == 1 && w.Env.Focused[0] == 700);
            // the rest of the row ticks and centres nothing
            w.Env.Focused.Clear();
            w.Click(RowHit(w, "avail", 0)); w.Frame();
            Eq(tag + " the rest of the row ticks", w.Env.Focused.Count == 0 && w.P.Find("add").Enabled);
            Eq(tag + " the row hits do not overlap the links", RowHitsClear(w, "avail", 0) && RowHitsClear(w, "members", 0));
            // a tip names the target; a row without a ship has no ship link
            Eq(tag + " the link tip", w.P.Find("members.link.0.0").Tip != null && w.P.Find("members.link.0.0").Tip.Contains("SH-3H \u00d72") && w.P.Find("members.link.0.1").Tip.Contains("Yorktown"));
            v = Screen(); v.Groups[0].MemberRows[1].ShipUid = 0; v.Groups[0].MemberRows[1].Base = null;
            w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            Eq(tag + " no ship = no ship link, a dash", !w.Has("members.link.1.1") && w.Has("members.link.1.0"));
            // a failed centring says why (Warn)
            w.Env.FocusError = "no position";
            w.Click("members.link.0.0");
            Eq(tag + " a failed focus says why", w.B.Note == "no position" && w.B.NoteTone == UiTone.Warn);
        }

        /// <summary>T-155: an aircraft that became excluded while ticked: nothing is sent, the line says why, the log gets "asw window refused".</summary>
        private static void Refusals(string tag)
        {
            foreach (int how in new[] { 0, 1, 2 })   // 0 = "+ To group", 1 = "+ New" icon, 2 = "Start rescue" without a group
            {
                var v = Normal();
                if (how == 2) { v.Groups.Clear(); v.AvailableSar.Add(Row(32, "SH-2F 704", "deck_ready")); }
                else v.Available.Add(Row(34, "SH-60B 705", "deck_ready"));   // another free one keeps the "To group" button
                var w = Make(v);
                if (how != 2) { w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame(); }
                else { w.Mode(2); }
                string chk = how == 2 ? "avail.chk.31" : "avail.chk.30";
                w.Click(chk);
                if (how == 2) w.Click("avail.chk.32");
                // the ticked one goes out of the controller's hands
                var list = how == 2 ? v.AvailableSar : v.Available;
                var gone = list.Find(r => r.Uid == (how == 2 ? 31 : 30));
                list.Remove(gone);
                (how == 2 ? v.ExcludedSar : v.Excluded).Add(new AswExcludedRow { Row = gone, Reason = "player" });
                w.Frame();
                w.Click(how == 0 ? "add" : how == 1 ? "grp_new" : "start");
                Eq(tag + " refused " + how + ": no command: " + w.Cmds(), w.Sent.Count == 0);
                string want = UiText.Fmt("asw.say.refused", gone.Name + " (Kirov)", AswWindowModel.ExcludedText("player"));
                Eq(tag + " refused " + how + ": the line says why: " + w.B.Note, w.B.Note == want && w.B.NoteTone == UiTone.Warn);
                Eq(tag + " refused " + how + ": logged: " + string.Join("|", w.Env.Logs), w.Env.Logs.Exists(l => l == "asw window refused " + gone.Uid + " reason=player"));
            }
            // the other ticks still work after the refusal (the excluded one is dropped from the ticks)
            var v2 = Normal(); v2.Available.Add(Row(33, "SH-60B 702", "deck_ready"));
            var w2 = Make(v2);
            w2.B.SelectedGroup = "ПЛО-1"; w2.Frame(); w2.Frame();
            w2.Click("avail.chk.30"); w2.Click("avail.chk.33");
            var g30 = v2.Available.Find(r => r.Uid == 30); v2.Available.Remove(g30);
            v2.Excluded.Add(new AswExcludedRow { Row = g30, Reason = "lost" });
            w2.Frame(); w2.Click("add");
            Eq(tag + " first click refused", w2.Sent.Count == 0);
            w2.Click("add");
            Eq(tag + " second click sends the rest: " + w2.Cmds(), w2.Sent.Count == 1 && Str(w2.Args[0], "op") == "add" && ((List<object>)w2.Args[0]["uids"]).Count == 1);
        }

        private static List<SarShipRow> ShipData(bool many)
        {
            var l = new List<SarShipRow>
            {
                new SarShipRow { Uid = 801, Name = "Kirov", Type = "CGN", Kind = "ship", Active = true, Done = 2, Total = 5 },
                new SarShipRow { Uid = 802, Name = "Yorktown", Type = "CG-47", Kind = "ship" },
                new SarShipRow { Uid = 803, Name = "Spruance", Type = "DD-963", Kind = "ship", Busy = "busy:asw_group", Group = "ПЛО-1" },
                new SarShipRow { Uid = 804, Name = "Seawolf", Type = "SSN", Kind = "sub", Active = true, Surfaced = true, Done = 0, Total = 3 },
                new SarShipRow { Uid = 805, Name = "Perry", Type = "FFG-7", Kind = "ship" },
            };
            if (many)
                for (int i = 0; i < 16; i++)
                    l.Add(new SarShipRow { Uid = 900 + i, Name = "Very Long Cruiser Name With Hull " + (1000 + i), Type = "Ticonderoga class guided missile cruiser", Kind = "ship", Active = i % 3 == 0, Done = 12, Total = 40, Busy = i % 3 == 1 ? "busy:asw_group" : null, Group = "Противолодочная группа номер 1" });
            return l;
        }

        /// <summary>T-161: the "Ships and submarines" section of the SAR tab.</summary>
        private static void SarShips(string tag)
        {
            var data = ShipData(false);
            int reads = 0;
            var w = Make(Normal());
            w.B.SarShips = () => { reads++; return data; };
            w.Frame();
            Eq(tag + " no section on the course tab", !w.Has("sar_ships.chk.801") && reads == 0);
            w.Mode(2);
            Eq(tag + " section drawn on the SAR tab", w.Has("sar_ships.chk.801") && w.Has("sar_ships.chk.805") && reads >= 1);
            Eq(tag + " no old hint", w.P.Items.TrueForAll(r => r.Text == null || !r.Text.Contains("Старпом") && !r.Text.Contains("XO")));
            // cache: frames in the same second read once; after the time passes, again
            int r0 = reads;
            w.Frame(); w.Frame();
            Eq(tag + " rows cached within a second", reads == r0);
            w.Env.Time += 2; w.Frame();
            Eq(tag + " rows read again later", reads == r0 + 1);
            // sorted: the active runs first
            var c801 = w.P.Find("sar_ships.chk.801"); var c805 = w.P.Find("sar_ships.chk.805"); var c803 = w.P.Find("sar_ships.chk.803");
            Eq(tag + " active first, busy last", c801 != null && c805 != null && c803 != null && c801.R.Y < c805.R.Y && c805.R.Y < c803.R.Y);
            Eq(tag + " buttons grey without ticks", w.Has("ship_start") && !w.P.Find("ship_start").Enabled && !w.P.Find("ship_stop").Enabled);
            // a tick and a radius send nothing
            w.Click("sar_ships.chk.802"); w.Click("shiprad.1");
            Eq(tag + " tick + radius send nothing", w.Sent.Count == 0 && w.P.Find("ship_start").Enabled && !w.P.Find("ship_stop").Enabled);
            // link centres the map
            w.Click("sar_ships.link.0.0");
            Eq(tag + " link centres the map", w.Env.Focused.Count == 1 && w.Env.Focused[0] == 801 && w.Sent.Count == 0);
            // Start: two free + one busy ticked -> 2 commands with the radius, the line explains the busy one
            w.Click("sar_ships.chk.805"); w.Click("sar_ships.chk.803");
            reads = 0;
            w.Click("ship_start");
            Eq(tag + " start -> 2 sar_ship start with radius: " + w.Cmds(), w.Sent.Count == 2 && w.Sent[0] == "sar_ship" && Str(w.Args[0], "op") == "start" && (double)w.Args[0]["radius_nm"] == 10.0
                && (double)w.Args[0]["uid"] == 805 && (double)w.Args[1]["uid"] == 802);
            Eq(tag + " the line says the busy one: " + w.B.Note, w.B.NoteTone == UiTone.Warn && w.B.Note.Contains("Spruance") && w.B.Note.Contains("ПЛО-1") && w.B.Note.Contains("2"));
            w.Frame(); Eq(tag + " rows re-read after a send", reads >= 1);
            // Stop: only the active ones
            w.Sent.Clear(); w.Args.Clear();
            w.Click("sar_ships.chk.801"); w.Click("sar_ships.chk.804");
            w.Click("ship_stop");
            Eq(tag + " stop -> only active ones: " + w.Cmds(), w.Sent.Count == 2 && Str(w.Args[0], "op") == "stop" && Str(w.Args[1], "op") == "stop" && !w.Args[0].ContainsKey("radius_nm"));
            // radius "All" = 0
            w = Make(Normal()); w.B.SarShips = () => data; w.Mode(2);
            w.Click("sar_ships.chk.802"); w.Click("ship_start");
            Eq(tag + " default radius All = 0", w.Sent.Count == 1 && (double)w.Args[0]["radius_nm"] == 0.0);
            // a refused ack: Error
            w = Make(Normal()); w.B.SarShips = () => data; w.Mode(2);
            w.Ack = n => Err("!busy:asw_group");
            w.Click("sar_ships.chk.802"); w.Click("ship_start");
            Eq(tag + " refusal: Error line: " + w.B.Note, w.B.NoteTone == UiTone.Error && w.B.Note.Contains("Yorktown"));
            // socket null: no section; empty: hint
            w = Make(Normal()); w.B.SarShips = () => null; w.Mode(2);
            Eq(tag + " null socket: no rows drawn", !w.Has("ship_start") && !w.Has("shiprad.0"));
            w = Make(Normal()); w.B.SarShips = () => new List<SarShipRow>(); w.Mode(2);
            Eq(tag + " empty list: hint", w.P.FindText(T("sar.ships.hint.none")) != null && !w.Has("ship_start"));
            // collapsed: nothing read
            w = Make(Normal()); reads = 0; w.B.SarShips = () => { reads++; return data; }; w.S.Open["asw.sar_ships"] = false; w.Mode(2);
            Eq(tag + " collapsed section reads nothing", reads == 0 && !w.Has("ship_start"));
        }

        private static void Sar(string tag)
        {
            // no group, nothing ticked: Start is grey with the hint
            var w = Make(Empty());
            w.Mode(2);
            Eq(tag + " SAR tab sends nothing", w.Sent.Count == 0 && w.B.Mode == "sar");
            w.Click("swhere.0"); w.Click("swhere.2"); w.Click("safter.1"); w.Pick("sradius", 2);
            Eq(tag + " no text field for the radius", !w.Has("sradius.text"));
            Eq(tag + " SAR draft controls send nothing", w.Sent.Count == 0);
            w.B.CancelInput(null);
            Eq(tag + " Start rescue is grey without a group", w.Has("start") && !w.P.Find("start").Enabled && w.P.FindText(T("sar.hint.no_group")) != null);
            w.Click("start");
            Eq(tag + " grey Start sends nothing", w.Sent.Count == 0);
            Eq(tag + " Stop is grey without a group", w.Has("sar_stop") && !w.P.Find("sar_stop").Enabled);

            // no group, two ticked: create SAR-1, then the order
            var v = Normal(); v.Groups.Clear();
            v.AvailableSar.Add(Row(32, "SH-2F 704", "deck_ready"));
            w = Make(v);
            w.Mode(2);
            w.Click("avail.chk.31"); w.Click("avail.chk.32");
            Eq(tag + " Start is live with ticks", w.P.Find("start").Enabled);
            w.Click("start");
            Eq(tag + " create SAR-1 then sar_order: " + w.Cmds(), w.Sent.Count == 2 && w.Sent[0] == "asw_group" && Str(w.Args[0], "op") == "create" && Str(w.Args[0], "group") == T("sar.group.prefix") + "-1" && w.Sent[1] == "sar_order" && Str(w.Args[1], "group") == T("sar.group.prefix") + "-1");
            // a failed create: no order
            w = Make(v);
            w.Mode(2); w.Click("avail.chk.31");
            w.Ack = n => Err("!no_aircraft");
            w.Click("start");
            Eq(tag + " a failed create sends no sar_order: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_group" && w.B.NoteTone == UiTone.Error);

            // an ASW group: replace
            v = Normal(); w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            w.Mode(2);
            Eq(tag + " replace hint on a course group", w.P.Items.Exists(r => r.Kind == "Label" && r.Text != null && r.Text.Contains("ПЛО-1") && r.Text.Contains(T("asw.order.course"))));
            w.Click("start");
            Eq(tag + " replace -> sar_order: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "sar_order" && Str(w.Args[0], "group") == "ПЛО-1");

            // a running SAR group: Apply grey, circle + point + radius -> sar_order
            v = Normal(); w = Make(v);
            w.B.SelectedGroup = "SAR-1"; w.Frame(); w.Frame();
            Eq(tag + " the SAR group opens the SAR tab", w.B.Mode == "sar");
            Eq(tag + " Apply is grey while the draft equals the order", w.Has("apply") && !w.P.Find("apply").Enabled);
            w.Click("swhere.2");
            Eq(tag + " circle wants a point", w.B.WantInput == "sar_point" && w.Sent.Count == 0);
            w.B.OnPoint(35.5, 20.5);
            w.Pick("sradius", 2);
            Eq(tag + " point + radius are drafts", w.Sent.Count == 0 && w.P.Find("apply").Enabled);
            w.Click("apply");
            var area = w.Sent.Count == 1 && w.Sent[0] == "sar_order" ? w.Args[0]["area"] as Dictionary<string, object> : null;
            Eq(tag + " Apply -> sar_order with the circle: " + w.Cmds(), area != null && (double)area["lat"] == 35.5 && (double)area["radius_nm"] == 10.0);
            w.Sent.Clear(); w.Args.Clear();
            w.Click("sar_stop");
            Eq(tag + " Stop rescue -> asw_stop {group}, never all: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_stop" && Str(w.Args[0], "group") == "SAR-1" && !w.Args[0].ContainsKey("all"));
            // a rectangle
            w = Make(v);
            w.B.SelectedGroup = "SAR-1"; w.Frame(); w.Frame();
            w.Click("swhere.1");
            Eq(tag + " rect wants the map", w.B.WantInput == "sar_rect" && w.Sent.Count == 0);
            w.B.OnRect(new[] { 35.0, 20.0, 35.0, 20.2, 34.9, 20.2, 34.9, 20.0 });
            w.Frame();
            w.Click("apply");
            Eq(tag + " rect Apply -> sar_order with 4 numbers: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "sar_order" && ((List<object>)w.Args[0]["area"]).Count == 4);
            // Esc
            w = Make(v);
            w.Mode(2); w.Click("swhere.1");
            w.B.CancelInput("sar.ui.cancelled");
            Eq(tag + " Esc cancels the input", w.B.WantInput == null && w.B.Note == T("sar.ui.cancelled"));
        }

        // T-142 K5: target, loadout and axis line: drafts, nothing is sent before Start / Apply, asw_loadout goes before the order
        private static void Course(string tag)
        {
            var v = Normal();
            var g = v.Groups[0];
            g.TargetUid = 500; g.TargetKind = "ship"; g.TargetName = "Kirov"; g.AxisDeg = 145; g.ReplanInS = 9.2;
            g.CourseTargets.Clear(); g.LoadoutRows.Clear();
            g.CourseTargets.Add(new AswTargetOption { Uid = 500, Name = "Kirov", Selected = true });
            g.CourseTargets.Add(new AswTargetOption { Uid = 600, Name = "Convoy", Kind = "formation", WidthNm = 9 });
            g.LoadoutRows.Add(new AswLoadoutView { Type = "SH-3H", Label = "SH-3H", Chosen = "auto", Names = { "ASWHunter", "ASWKiller" } });
            var w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            string axisSeg = UiText.Fmt("asw.status.axis", "145") + " · " + UiText.Fmt("asw.status.replan_in", "10");
            var statusLine = w.P.Items.Find(it => it.Kind == "Label" && it.Text != null && it.Text.Contains(axisSeg));   // T-178 A3: one fact chip of the status chips
            Eq(tag + " axis and re-plan are one fact chip of the group status", statusLine != null);
            Eq(tag + " Apply grey", w.Has("apply") && !w.P.Find("apply").Enabled);
            // T-178 A2: the target is a ship pick (a drop-down with the ships and formations, nearest first; no positions here = by name)
            var pickList = ShipPickModel.Sorted(new List<ShipItem> { new ShipItem { Uid = 500, Name = "Kirov", DistNm = double.MaxValue, Own = true }, new ShipItem { Uid = -600, Name = UiText.Fmt("asw.tgt.formation", "Convoy"), DistNm = double.MaxValue, Own = true } });
            int convoyAt = pickList.FindIndex(x => x.Uid == -600);
            Eq(tag + " the formation is in the ship list", w.Has("tgt") && w.Has("tgt.map") && convoyAt >= 0);
            w.Pick("tgt", convoyAt);
            Eq(tag + " target pick sends nothing", w.Sent.Count == 0);
            Eq(tag + " Apply live after a target pick", w.P.Find("apply").Enabled);
            Eq(tag + " formation shown", w.P.Items.Exists(x => x.Text != null && x.Text.Contains(UiText.Fmt("asw.tgt.formation", "Convoy"))));
            w.Click("apply");
            Eq(tag + " retarget -> one asw_search_course with the formation: " + w.Cmds(), w.Sent.Count == 1 && w.Sent[0] == "asw_search_course"
                && w.Args[0].ContainsKey("target_uid") && (double)w.Args[0]["target_uid"] == 600 && Str(w.Args[0], "target_kind") == "formation");
            // loadout: asw_loadout first, then the order (geometry edit)
            v = Normal(); g = v.Groups[0]; g.TargetUid = 500; g.LoadoutRows.Clear();
            g.LoadoutRows.Add(new AswLoadoutView { Type = "SH-3H", Label = "SH-3H", Chosen = "auto", Names = { "ASWHunter", "ASWKiller" } });
            w = Make(v);
            w.B.SelectedGroup = "ПЛО-1"; w.Frame(); w.Frame();
            w.S.Open["asw.loadout"] = true;   // the section is closed by default
            w.Frame();
            Eq(tag + " loadout row", w.Has("lo.SH-3H.next"));
            if (!w.Has("lo.SH-3H.next")) return;
            w.Click("lo.SH-3H.next"); w.Frame();
            Eq(tag + " loadout pick sends nothing", w.Sent.Count == 0 && w.P.Find("apply").Enabled);
            w.Pick("dip_l", 3); w.Click("apply");
            Eq(tag + " loadout then order: " + w.Cmds(), w.Sent.Count == 2 && w.Sent[0] == "asw_loadout" && Str(w.Args[0], "loadout") == "ASWHunter" && Str(w.Args[0], "type") == "SH-3H" && w.Sent[1] == "asw_search_course");
        }

        private sealed class FakeCi : IContactInfo
        {
            public Dictionary<int, ContactInfoRow> Rows = new Dictionary<int, ContactInfoRow>();
            public List<int> Own = new List<int>();
            public ContactInfoRow Get(int id) { ContactInfoRow r; return Rows.TryGetValue(id, out r) ? r : null; }
            public ContactInfoRow GetOwn(int id) { ContactInfoRow r; return Rows.TryGetValue(id, out r) ? r : null; }
            public IList<int> OwnKeys { get { return Own; } }
            public int Version { get { return 1; } }
        }

        // T-136 E3: a line per contact (T-174 W7: the own noise block is gone)
        private static void Contacts(string tag)
        {
            var v = Normal();
            v.Groups[0].Contacts.Add(new AswContactView { Id = 7, Name = "Sub #7", Held = true });
            var ci = new FakeCi();
            ci.Rows[7] = new ContactInfoRow { Key = 7, DepthFt = 340, DepthErrFt = 60, LayerSide = "below", LayerOffsetFt = 80, AgeS = 45 };
            ci.Rows[1] = new ContactInfoRow { Key = 1, Own = true, Name = "Own A", NoiseDb = 90, CavOnsetKn = 12, SpeedKn = 8 };
            ci.Rows[2] = new ContactInfoRow { Key = 2, Own = true, Name = "Own B", NoiseDb = 110, CavOnsetKn = 12, SpeedKn = 14, Cavitating = true };
            ci.Rows[3] = new ContactInfoRow { Key = 3, Own = true, Name = "Own C", NoiseDb = 80, CavOnsetKn = 12, SpeedKn = 4 };
            ci.Rows[4] = new ContactInfoRow { Key = 4, Own = true, Name = "No data" };
            ci.Own.AddRange(new[] { 1, 2, 3, 4 });
            var w = Make(v);
            w.B.ContactInfo = () => ci;
            w.B.SelectedGroup = "ПЛО-1"; w.S.Open["asw.noise"] = true; w.Frame(); w.Frame();
            string txt = AswWindowBody.ContactCiText(ci.Rows[7], default(CiUnits));
            Eq(tag + " contact text " + txt, txt.Contains("340") && txt.Contains("60") && txt.Contains(" · "));
            Eq(tag + " contact line shown", w.P.FindText(UiText.Fmt("asw.ci.contact", "Sub #7", txt)) != null);
            // T-174 W7: the own noise block is gone even with a saved "asw.noise:1"; the data stay in the service for contact_info
            Eq(tag + " no noise section", !w.Has("sec.asw.noise") && w.P.FindText("Own B") == null && w.P.FindText("Own A") == null);
            // no module: dashes, no crash
            Eq(tag + " no module -> dash", AswWindowBody.ContactCiText(null, default(CiUnits)) == UiText.Get("asw.ci.none"));
            w = Make(v); w.B.SelectedGroup = "ПЛО-1"; w.S.Open["asw.noise"] = true; w.Frame(); w.Frame();
            Eq(tag + " without the service the contact line is a dash", w.P.FindText(UiText.Fmt("asw.ci.contact", "Sub #7", UiText.Get("asw.ci.none"))) != null);
        }

        private static void Sockets(string tag)
        {
            // the sockets without a body are silent: the stand compiles without AswWindowBody.Course/Contacts/Deck
            var w = Make(Normal());
            w.B.SelectedGroup = "ПЛО-1"; w.Frame();
            Eq(tag + " sockets: draft is exposed to them", w.B.Draft != null && w.B.Draft.Mode == "course");
            // T-119 I1: the deck label shows only while the group waits, in the warning tone, as mm:ss
            string want = UiText.Fmt("asw.status.deck_wait", "01:15");
            Func<UiRec> line = () => w.P.Items.Find(it => it.Kind == "Label" && it.Text != null && it.Text.Contains(want));
            Eq(tag + " deck label hidden", line() == null);
            w.V.Groups[0].DeckWaitS = 75; w.Frame();
            var lab = line();
            // T-178 A3: the wait is a warning chip (a Warn-coloured chip behind the label), never folded into "+N"
            bool onWarnChip = lab != null && w.P.Items.Exists(it => it.Kind == "Fill" && it.Id == UiColorRole.Warn.ToString() && lab.R.X >= it.R.X - 0.5f && lab.R.X + lab.R.W <= it.R.X + it.R.W + 0.5f && lab.R.Y >= it.R.Y - 0.5f && lab.R.Y + lab.R.H <= it.R.Y + it.R.H + 0.5f);
            Eq(tag + " deck wait is a warning chip", onWarnChip);
            w.V.Groups[0].DeckWaitS = -1; w.Frame();
            Eq(tag + " deck label gone", line() == null);
        }
    }
}

namespace VoiceBotBridge
{
    internal static class AswWindowDump
    {
        // dev helper: `aswwintest dump ru 0 1` prints the elements of one frame (lang, tab, group index)
        public static string Run(string lang, int tab, int group)
        {
            using (UiText.Push(lang))
            {
                var v = AswWindowTests.Normal();
                var w = new Win(1.0, 520f, v);
                w.Frame();
                if (group >= 0) { w.B.SelectedGroup = v.Groups[group].Name; w.Frame(); }
                w.Mode(tab);
                var sb = new System.Text.StringBuilder();
                foreach (var it in w.P.Items) if (it.Kind != "Fill") sb.AppendLine(it.ToString());
                return sb.ToString();
            }
        }
    }
}
