using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-109 / ADR-0039: model of the SAR parts of the window "Aviation: ASW / SAR".
    // Strings are UiText keys sar.* (lang/language_xx/sea_power_tactical_ui.sar.ini); the language is UiText.Lang.
    internal sealed class SarGroupLine { public string Group, AreaText; public int Rafts, People, Active, Rescued; public double NextLaunchS = -1; }

    internal static class SarWindow
    {
        private static string T(string key) { return UiText.Get(key); }

        private static string F(string key, params object[] a) { return UiText.Fmt(key, a); }

        private static string Mmss(double s)
        {
            int t = (int)Math.Round(Math.Max(0, s));
            return (t / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (t % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        public static string TaskLine(SarGroupLine l)
        {
            string next = l.NextLaunchS < 0 ? "—" : Mmss(l.NextLaunchS);
            return F("sar.task", l.Group, l.AreaText ?? "", l.Rafts, l.People, l.Active, l.Rescued, next);
        }

        public static string OnboardCell(int onboard, int capacity)
        {
            return onboard.ToString(CultureInfo.InvariantCulture) + "/" + capacity.ToString(CultureInfo.InvariantCulture);
        }

        public static string TargetCell(int raftId, double distNm)
        {
            if (raftId <= 0) return "—";
            string d = Math.Round(distNm, 1).ToString("0.0", CultureInfo.InvariantCulture);
            string unit;
            if (UiText.Lang == "ru")
            {
                // Russian rule: fractional -> genitive singular; whole numbers by last digits.
                double r = Math.Round(distNm, 1);
                int n = (int)r;
                if (r != n) unit = "sar.mile2";
                else if (n % 100 >= 11 && n % 100 <= 14) unit = "sar.mile5";
                else if (n % 10 == 1) unit = "sar.mile1";
                else if (n % 10 >= 2 && n % 10 <= 4) unit = "sar.mile2";
                else unit = "sar.mile5";
            }
            else unit = "sar.mile1";
            return F("sar.target", raftId, d, T(unit));
        }

        public static string ReasonText(string code)
        {
            if (code == null) return "";
            code = SarSave.MapLegacyReason(code);
            if (code.StartsWith("in_group:", StringComparison.Ordinal)) return F("sar.reason.in_group", code.Substring(9));
            string key = "sar.reason." + code;
            return UiText.Has(UiText.DefaultSection, key) ? T(key) : code;
        }

        public static string[] OrderButtons()
        {
            return new[] { T("sar.order.course"), T("sar.order.area"), T("sar.order.sar") };
        }

        public static string SelfTest()
        {
            using (UiText.Push("ru"))
            {
                var l = new SarGroupLine { Group = "SAR-1", AreaText = "везде", Rafts = 5, People = 23, Active = 2, Rescued = 11, NextLaunchS = 200 };
                if (TaskLine(l) != "SAR-1 · везде · плотов 5 (людей 23) · вертолётов в работе 2 · спасено 11 · взлёт через 03:20") return "task ru: " + TaskLine(l);
                if (OnboardCell(3, 8) != "3/8") return "onboard";
                if (TargetCell(12, 4.3) != "плот 12, 4.3 мили") return "target ru: " + TargetCell(12, 4.3);
                if (TargetCell(12, 5) != "плот 12, 5.0 мили" && TargetCell(12, 5) != "плот 12, 5.0 миль") return "target whole";
                if (TargetCell(0, 4.3) != "—" || TargetCell(-1, 1) != "—") return "target none";
                if (ReasonText("in_group:ПЛО-1") != "в группе ПЛО-1") return "reason in_group ru";
                if (ReasonText("sar") != ReasonText("ext_sar")) return "reason legacy";
                if (ReasonText("weird") != "weird") return "reason unknown";
                var b = OrderButtons();
                if (b.Length != 3 || b[0] != "По курсу" || b[1] != "В районе" || b[2] != "Поиск и спасение") return "buttons ru";
            }
            using (UiText.Push("en"))
            {
                var l = new SarGroupLine { Group = "SAR-1", AreaText = "everywhere", Rafts = 5, People = 23, Active = 2, Rescued = 11, NextLaunchS = -1 };
                if (TaskLine(l) != "SAR-1 · everywhere · rafts 5 (people 23) · helicopters at work 2 · rescued 11 · launch in —") return "task en: " + TaskLine(l);
                l.NextLaunchS = 61.4;
                if (!TaskLine(l).EndsWith("launch in 01:01")) return "mmss";
                if (TargetCell(3, 4.3) != "raft 3, 4.3 nm") return "target en";
                if (ReasonText("in_group:ASW-1") != "in group ASW-1") return "reason in_group en";
                if (OrderButtons().Length != 3 || OrderButtons()[2] != "Search and rescue") return "buttons en";
            }
            string[] codes = { "no_capacity", "game_sar_zone", "ext_sar", "threat", "unreachable" };
            foreach (string c in codes)
                foreach (string lg in new[] { "ru", "en" })
                    using (UiText.Push(lg))
                    {
                        string t = ReasonText(c);
                        if (string.IsNullOrEmpty(t) || t == c || t.StartsWith("SeaPowerTacticalUI.", StringComparison.Ordinal)) return "reason " + c + " " + lg;
                    }
            return SarPanel.SelfTest();
        }
    }

    // ---- T-147: model of the SAR control panel (draft + one action button); no dependency on AswView ----

    /// <summary>Snapshot of the selected group (the window copies it from AswGroupView).</summary>
    internal sealed class SarPanelGroup
    {
        public string Name, Order = "none" /* course|area|sar|none */, Area = "all" /* all|rect|circle */, OnComplete = "rtb" /* rtb|loiter */;
        public double[] Rect;            // lat0, lon0, lat1, lon1
        public double Lat, Lon, RadiusNm;
        public int Members;
        public string OrderText;         // ready text of the current order (for sar.hint.replace)
    }

    /// <summary>Window draft of the SAR order: nothing is sent until "start" / "apply".</summary>
    internal sealed class SarDraft
    {
        public string Area = "all", OnComplete = "rtb";
        public double[] Rect;            // null = not drawn
        public bool HasPoint;
        public double Lat, Lon;
        public string RadiusText = "20";
        public string ForGroup;
    }

    internal sealed class SarButton { public string Id, Label, Tip; public bool On, Enabled = true; }

    internal static class SarPanel
    {
        private static string T(string key) { return UiText.Get(key); }

        private static string F(string key, params object[] a) { return UiText.Fmt(key, a); }

        private static string Num(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        private static double[] CopyRect(double[] r) { return r == null || r.Length != 4 ? null : new[] { r[0], r[1], r[2], r[3] }; }

        /// <summary>SAR group -> its parameters; any other group (or none) keeps the draft (or defaults). ForGroup = the group name.</summary>
        public static SarDraft DraftFrom(SarPanelGroup g, SarDraft keep)
        {
            var d = new SarDraft();
            if (g != null && g.Order == "sar")
            {
                d.Area = g.Area == "rect" || g.Area == "circle" ? g.Area : "all";
                d.OnComplete = g.OnComplete == "loiter" ? "loiter" : "rtb";
                if (d.Area == "rect") d.Rect = CopyRect(g.Rect);
                if (d.Area == "circle") { d.HasPoint = true; d.Lat = g.Lat; d.Lon = g.Lon; d.RadiusText = Num(g.RadiusNm); }
                if (d.Area == "rect" && d.Rect == null) d.Area = "all";
            }
            else if (keep != null)
            {
                d.Area = keep.Area; d.OnComplete = keep.OnComplete; d.Rect = CopyRect(keep.Rect);
                d.HasPoint = keep.HasPoint; d.Lat = keep.Lat; d.Lon = keep.Lon; d.RadiusText = keep.RadiusText;
            }
            d.ForGroup = g == null ? null : g.Name;
            return d;
        }

        private static bool Near(double a, double b, double eps) { return Math.Abs(a - b) <= eps; }

        /// <summary>The draft equals the running SAR order (radius compared to 0.05).</summary>
        public static bool SameAsOrder(SarPanelGroup g, SarDraft d)
        {
            if (g == null || d == null || g.Order != "sar") return false;
            if (d.Area != g.Area || d.OnComplete != g.OnComplete) return false;
            if (d.Area == "rect")
            {
                if (d.Rect == null || g.Rect == null || g.Rect.Length != 4) return false;
                for (int i = 0; i < 4; i++) if (!Near(d.Rect[i], g.Rect[i], 1e-6)) return false;
            }
            else if (d.Area == "circle")
            {
                double r;
                if (!d.HasPoint || !TryRadius(d.RadiusText, out r)) return false;
                if (!Near(d.Lat, g.Lat, 1e-6) || !Near(d.Lon, g.Lon, 1e-6) || !Near(r, g.RadiusNm, 0.05)) return false;
            }
            return true;
        }

        private static bool TryRadius(string text, out double r)
        {
            r = 0;
            if (text == null) return false;
            return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out r) && r > 0 && r <= 500 && !double.IsNaN(r);
        }

        public static List<SarButton> AreaButtons(SarDraft d, bool drawing, bool pointing)
        {
            string a = d == null ? "all" : d.Area;
            return new List<SarButton>
            {
                new SarButton { Id = "all", Label = T("sar.area.btn_all"), Tip = T("sar.tip.all"), On = a == "all" },
                new SarButton { Id = "rect", Label = T("sar.area.btn_rect"), Tip = T("sar.tip.rect"), On = a == "rect" || drawing },
                new SarButton { Id = "circle", Label = T("sar.area.btn_circle"), Tip = T("sar.tip.circle"), On = a == "circle" || pointing },
            };
        }

        public static List<SarButton> AfterButtons(SarDraft d)
        {
            string c = d == null ? "rtb" : d.OnComplete;
            return new List<SarButton>
            {
                new SarButton { Id = "rtb", Label = T("sar.complete.btn_rtb"), Tip = T("sar.tip.rtb"), On = c == "rtb" },
                new SarButton { Id = "loiter", Label = T("sar.complete.btn_loiter"), Tip = T("sar.tip.loiter"), On = c == "loiter" },
            };
        }

        private static string NeedShape(SarDraft d)
        {
            if (d == null) return null;
            if (d.Area == "rect" && d.Rect == null) return "need_rect";
            if (d.Area == "circle" && !d.HasPoint) return "need_point";
            return null;
        }

        public static SarButton Action(SarPanelGroup g, SarDraft d, int pickedAvail)
        {
            bool sar = g != null && g.Order == "sar";
            var b = new SarButton { Id = sar ? "apply" : "start", Label = T(sar ? "sar.btn.apply" : "sar.btn.start"), Tip = T(sar ? "sar.tip.apply" : "sar.tip.start") };
            if (NeedShape(d) != null) b.Enabled = false;
            else if (g == null) b.Enabled = pickedAvail > 0;
            else if (g.Order == "strike") b.Enabled = false;   // 0.30.1: a strike group never takes the rescue order (controller: wrong_group_kind)
            else if (sar) b.Enabled = !SameAsOrder(g, d);
            return b;
        }

        public static SarButton Stop(SarPanelGroup g)
        {
            return new SarButton { Id = "stop", Label = T("sar.btn.stop"), Tip = T("sar.tip.stop"), Enabled = g != null && g.Order == "sar" };
        }

        /// <summary>The line under the buttons or null.</summary>
        public static string Hint(SarPanelGroup g, SarDraft d, int pickedAvail)
        {
            if (g == null && pickedAvail <= 0) return T("sar.hint.no_group");
            if (g != null && g.Order == "strike") return F("sar.hint.wrong_group_kind", g.Name);
            string need = NeedShape(d);
            if (need != null) return T("sar.hint." + need);
            if (g != null && (g.Order == "course" || g.Order == "area")) return F("sar.hint.replace", g.Name, g.OrderText ?? g.Order);
            if (g != null && g.Order == "sar" && !SameAsOrder(g, d)) return T("sar.hint.dirty");
            return null;
        }

        /// <summary>Command body of sar_order from the draft; err: need_rect|need_point|bad_radius|missing_group.</summary>
        public static Dictionary<string, object> OrderCommand(string group, SarDraft d, out string err)
        {
            err = null;
            if (string.IsNullOrEmpty(group)) { err = "missing_group"; return null; }
            if (d == null) d = new SarDraft();
            var c = new Dictionary<string, object> { { "group", group }, { "on_complete", d.OnComplete == "loiter" ? "loiter" : "rtb" } };
            if (d.Area == "rect")
            {
                if (d.Rect == null) { err = "need_rect"; return null; }
                c["area"] = new List<object> { d.Rect[0], d.Rect[1], d.Rect[2], d.Rect[3] };
            }
            else if (d.Area == "circle")
            {
                if (!d.HasPoint) { err = "need_point"; return null; }
                double r;
                if (!TryRadius(d.RadiusText, out r)) { err = "bad_radius"; return null; }
                c["area"] = new Dictionary<string, object> { { "lat", d.Lat }, { "lon", d.Lon }, { "radius_nm", r } };
            }
            else c["area"] = "all";
            return c;
        }

        /// <summary>"SAR-1", "SAR-2", ... (prefix sar.group.prefix), not in existing (case-insensitive).</summary>
        public static string NextGroupName(IEnumerable<string> existing)
        {
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing != null) foreach (string e in existing) if (e != null) have.Add(e);
            string pre = T("sar.group.prefix");
            for (int n = 1; ; n++)
            {
                string name = pre + "-" + n.ToString(CultureInfo.InvariantCulture);
                if (!have.Contains(name)) return name;
            }
        }

        public static string Badge(string order)
        {
            if (order == "sar") return T("sar.badge.sar");
            if (order == "strike") return T("sar.badge.strike");
            if (order == "course" || order == "area") return T("sar.badge.asw");
            return T("sar.badge.none");
        }

        /// <summary>Text of a client-side error of OrderCommand (also usable for ack codes).</summary>
        public static string ErrText(string code)
        {
            switch (code)
            {
                case "need_rect": return T("sar.hint.need_rect");
                case "need_point": return T("sar.hint.need_point");
                case "bad_radius": return T("sar.ui.bad_radius");
                case "missing_group": return T("sar.err.missing_group");
                case "no_aircraft": return T("sar.err.no_aircraft");
                case "no_group": return T("sar.err.no_group");
                case "unsupported": return T("sar.err.unsupported");
                case "bad_area": return T("sar.err.bad_area");
                case "wrong_group_kind": return T("sar.err.wrong_group_kind");
                default: return SarWindow.ReasonText(code);
            }
        }

        /// <summary>Ack JSON of a command -> null when ok; else "Not done: reason".</summary>
        public static string AckText(string ackJson)
        {
            Dictionary<string, object> d = null;
            try { d = string.IsNullOrEmpty(ackJson) ? null : Json.Parse(ackJson) as Dictionary<string, object>; }
            catch (FormatException) { }
            if (d == null) return F("sar.say.failed", "bad_ack");
            object ok;
            if (d.TryGetValue("ok", out ok) && ok is bool && (bool)ok) return null;
            object e;
            string code = d.TryGetValue("error", out e) && e is string ? (string)e : "failed";
            if (code.StartsWith("!", StringComparison.Ordinal)) code = code.Substring(1);
            return F("sar.say.failed", ErrText(code));
        }

        /// <summary>kind: started|applied|stopped. detail = area/ending text (ignored by "stopped").</summary>
        public static string SayText(string kind, string group, string detail)
        {
            switch (kind)
            {
                case "started": return F("sar.say.started", group, detail);
                case "applied": return F("sar.say.applied", group, detail);
                default: return F("sar.say.stopped", group);
            }
        }

        public static string WhereLabel() { return T("sar.lbl.where"); }

        public static string AfterLabel() { return T("sar.lbl.after"); }

        public static string MilesLabel() { return T("sar.ui.miles"); }

        public static string TabTip() { return T("sar.tab.tip"); }

        public static string SeatsColumn() { return T("sar.col.seats"); }

        public static string StopTip() { return T("asw.tip.stop"); }

        public static string StopAllTip() { return T("asw.tip.stop_all"); }

        public static string SelfTest()
        {
            string err;
            foreach (string lg in new[] { "ru", "en" })
                using (UiText.Push(lg))
                {
                    bool ru = lg == "ru";
                    var circ = new SarPanelGroup { Name = "SAR-1", Order = "sar", Area = "circle", OnComplete = "loiter", Lat = 35, Lon = 20, RadiusNm = 12.5, Members = 2, OrderText = "x" };
                    var d = DraftFrom(circ, null);
                    if (d.Area != "circle" || d.OnComplete != "loiter" || d.RadiusText != "12.5" || !d.HasPoint || d.ForGroup != "SAR-1") return "panel draft sar " + lg;
                    if (!SameAsOrder(circ, d)) return "panel same " + lg;
                    d.RadiusText = "13";
                    if (SameAsOrder(circ, d)) return "panel radius changed " + lg;
                    d.RadiusText = "12.52";
                    if (!SameAsOrder(circ, d)) return "panel radius eps " + lg;
                    var kept = new SarDraft { Area = "rect", Rect = new[] { 1.0, 2.0, 3.0, 4.0 }, OnComplete = "loiter" };
                    var asw = new SarPanelGroup { Name = "ASW-1", Order = "course", OrderText = "course 90", Members = 3 };
                    var d2 = DraftFrom(asw, kept);
                    if (d2.Area != "rect" || d2.Rect == null || d2.OnComplete != "loiter" || d2.ForGroup != "ASW-1") return "panel draft kept " + lg;
                    var dn = DraftFrom(null, null);
                    if (dn.Area != "all" || dn.OnComplete != "rtb" || dn.ForGroup != null) return "panel draft default " + lg;

                    var a = Action(null, dn, 0);
                    if (a.Id != "start" || a.Enabled || Hint(null, dn, 0) != T("sar.hint.no_group")) return "panel no group " + lg;
                    a = Action(null, dn, 2);
                    if (a.Id != "start" || !a.Enabled || Hint(null, dn, 2) != null) return "panel start picked " + lg;
                    a = Action(asw, dn, 0);
                    if (a.Id != "start" || !a.Enabled) return "panel start asw " + lg;
                    string h = Hint(asw, dn, 0);
                    if (h == null || !h.Contains("ASW-1") || !h.Contains("course 90")) return "panel replace " + lg;
                    // 0.30.1: a strike group never gets the rescue order from the panel
                    var stk = new SarPanelGroup { Name = "STRIKE-1", Order = "strike", OrderText = "strike", Members = 1 };
                    if (Action(stk, dn, 0).Enabled || Action(stk, dn, 3).Enabled) return "panel strike enabled " + lg;
                    string hs = Hint(stk, dn, 0);
                    if (hs == null || !hs.Contains("STRIKE-1") || hs != F("sar.hint.wrong_group_kind", "STRIKE-1")) return "panel strike hint " + lg + ": " + hs;
                    if (ErrText("wrong_group_kind") != T("sar.err.wrong_group_kind")) return "err text wrong_group_kind " + lg;
                    var same = DraftFrom(circ, null);
                    a = Action(circ, same, 0);
                    if (a.Id != "apply" || a.Enabled || Hint(circ, same, 0) != null) return "panel apply same " + lg;
                    same.OnComplete = "rtb";
                    a = Action(circ, same, 0);
                    if (a.Id != "apply" || !a.Enabled || Hint(circ, same, 0) != T("sar.hint.dirty")) return "panel apply dirty " + lg;
                    var rect = new SarDraft { Area = "rect" };
                    a = Action(asw, rect, 0);
                    if (a.Enabled || Hint(asw, rect, 0) != T("sar.hint.need_rect")) return "panel need rect " + lg;
                    var pt = new SarDraft { Area = "circle" };
                    if (Action(asw, pt, 0).Enabled || Hint(asw, pt, 0) != T("sar.hint.need_point")) return "panel need point " + lg;
                    if (Stop(asw).Enabled || Stop(null).Enabled || !Stop(circ).Enabled) return "panel stop " + lg;
                    var ab = AreaButtons(rect, false, false);
                    if (ab.Count != 3 || ab[0].Id != "all" || ab[0].On || !ab[1].On || ab[2].On) return "panel area buttons " + lg;
                    if (!AreaButtons(dn, true, false)[1].On || !AreaButtons(dn, false, true)[2].On) return "panel drawing on " + lg;
                    var ob = AfterButtons(kept);
                    if (ob.Count != 2 || ob[0].On || !ob[1].On || !ob[0].Enabled) return "panel after buttons " + lg;

                    var c = OrderCommand("SAR-1", dn, out err);
                    var o = SarCommands.ParseOrder(c, out err);
                    if (o == null || o.Area.Kind != "all" || o.OnComplete != "rtb") return "panel cmd all " + lg;
                    var rd = new SarDraft { Area = "rect", Rect = new[] { 35.0, 20.0, 36.0, 21.0 }, OnComplete = "loiter" };
                    o = SarCommands.ParseOrder(OrderCommand("G", rd, out err), out err);
                    if (o == null || o.Area.Kind != "rect" || o.Area.Rect[2] != 36.0 || o.OnComplete != "loiter") return "panel cmd rect " + lg;
                    var cd = new SarDraft { Area = "circle", HasPoint = true, Lat = 35, Lon = 20, RadiusText = "7,5" };
                    o = SarCommands.ParseOrder(OrderCommand("G", cd, out err), out err);
                    if (o == null || o.Area.Kind != "circle" || o.Area.RadiusNm != 7.5 || o.Area.Center.Lat != 35) return "panel cmd circle " + lg;
                    cd.RadiusText = "0";
                    if (OrderCommand("G", cd, out err) != null || err != "bad_radius") return "panel radius 0 " + lg;
                    cd.RadiusText = "abc";
                    if (OrderCommand("G", cd, out err) != null || err != "bad_radius") return "panel radius abc " + lg;
                    if (OrderCommand("G", rect, out err) != null || err != "need_rect") return "panel cmd need rect " + lg;
                    if (OrderCommand("G", pt, out err) != null || err != "need_point") return "panel cmd need point " + lg;
                    if (OrderCommand("", dn, out err) != null || err != "missing_group") return "panel cmd no group " + lg;

                    string sp = T("sar.group.prefix");
                    if (NextGroupName(new[] { sp + "-1", "ASW-1" }) != sp + "-2" || NextGroupName(new string[0]) != sp + "-1" || NextGroupName(new[] { sp.ToLowerInvariant() + "-1", sp + "-2" }) != sp + "-3") return "panel next name " + lg;
                    if (AckText("{\"type\":\"ack\",\"id\":1,\"ok\":true,\"data\":{}}") != null) return "panel ack ok " + lg;
                    string na = AckText("{\"type\":\"ack\",\"id\":1,\"ok\":false,\"error\":\"no_aircraft\"}");
                    if (na != (ru ? "Не выполнено: в группе нет бортов" : "Not done: the group has no aircraft")) return "panel ack no_aircraft " + na;
                    string ex = AckText("{\"ok\":false,\"error\":\"ext_sar\"}");
                    if (ex != F("sar.say.failed", SarWindow.ReasonText("ext_sar")) || ex.EndsWith("ext_sar", StringComparison.Ordinal)) return "panel ack ext_sar " + ex;
                    string wd = AckText("{\"ok\":false,\"error\":\"weird_code\"}");
                    if (!wd.EndsWith("weird_code", StringComparison.Ordinal)) return "panel ack unknown " + wd;
                    if (AckText("not json") == null || AckText(null) == null) return "panel ack bad " + lg;

                    foreach (string b in new[] { "sar", "course", "area", "none" })
                        if (Badge(b).StartsWith("SeaPowerTacticalUI.", StringComparison.Ordinal)) return "panel badge " + b;
                    if (SayText("started", "SAR-1", "x") != (ru ? "SAR-1: спасение начато — x" : "SAR-1: rescue started — x")) return "panel say started " + SayText("started", "SAR-1", "x");
                    string[] texts = { SayText("applied", "G", "x"), SayText("stopped", "G", ""), WhereLabel(), AfterLabel(), MilesLabel(), TabTip(), SeatsColumn(), StopTip(), StopAllTip(),
                        ErrText("no_group"), ErrText("unsupported"), ErrText("bad_area"), ErrText("missing_group"), ErrText("need_rect"), ErrText("bad_radius"), Action(null, dn, 0).Label, Action(circ, dn, 0).Label, Stop(circ).Tip,
                        AreaButtons(dn, false, false)[2].Tip, AfterButtons(dn)[1].Tip, AfterButtons(dn)[0].Label, T("sar.warn.no_reserve_deck"), T("sar.status.no_reserve_deck") };
                    foreach (string t in texts)
                        if (string.IsNullOrEmpty(t) || t.StartsWith("SeaPowerTacticalUI.", StringComparison.Ordinal)) return "panel text missing " + lg + " " + t;
                }
            return null;
        }
    }
}
