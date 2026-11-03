using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-288 B1: the card body (ru/en, scale 1 and 1.33): with a group, without, replacing an area order, closed, auto.</summary>
    internal static class SubDatumCardTests
    {
        private sealed class State : IUiWindowState
        {
            public bool SectionOpen(string id, bool dflt) { return dflt; }
            public void SetSectionOpen(string id, bool open) { }
        }

        private sealed class Calls { public int Search, Window, Show, Hide, Undo; }

        private static SubDatumCardModel Model()
        {
            return new SubDatumCardModel { Id = 1, BrgDeg = 352, RNm = 9, CoreNm = 6, SigmaDeg = 15, AgeS = 60, Src = "vel", Ammo = "71M", State = "live", Group = "ПЛО-1", GroupEtaS = 340 };
        }

        private static string AllText(RecordPainter p) { return " " + string.Join(" ", p.Items.FindAll(it => it.Text != null).ConvertAll(it => it.Text).ToArray()) + " "; }

        private static string Run(SubDatumCardModel m, string lang, Func<string, RecordPainter, Calls, string> check, string click = null)
        {
            foreach (double s in new[] { 1.0, 1.33 })
                using (UiText.Push(lang))
                {
                    float w = (float)(360 * s);
                    var p = new RecordPainter(s); var st = new State();
                    var k = new UiKit(p, UiTheme.For(s, UiDensity.Compact), st, "toast");
                    var c = new Calls();
                    var a = new SubDatumCardActions { Search = () => c.Search++, OpenWindow = () => c.Window++, Show = () => c.Show++, Hide = () => c.Hide++, Undo = () => c.Undo++ };
                    p.NextFrame(); k.Begin(w); SubDatumCard.Draw(k, m, a); k.End();
                    string bad = p.CheckLayout(w, (float)(900 * s));
                    if (bad != null) return lang + " s=" + s + " layout: " + bad;
                    if (k.Overflows.Count > 0) return lang + " s=" + s + " overflow: " + k.Overflows[0];
                    string e = check(lang, p, c);
                    if (e != null) return lang + " s=" + s + ": " + e;
                    if (click != null)
                    {
                        p.Click(click); p.NextFrame(); k.Begin(w); SubDatumCard.Draw(k, m, a); k.End();
                        int n = click == "search" ? c.Search : click == "window" ? c.Window : click == "show" ? c.Show : click == "hide" ? c.Hide : c.Undo;
                        if (n != 1) return lang + " click " + click + " -> " + n;
                    }
                }
            return null;
        }

        public static string WithGroup()
        {
            foreach (string lang in new[] { "ru", "en" })
            {
                string e = Run(Model(), lang, (l, p, c) =>
                {
                    string t = AllText(p);
                    if (!t.Contains(SubDatumCard.Title())) return "title";
                    if (!t.Contains(SubDatumCard.BearingLine(Model()))) return "bearing line";
                    if (!t.Contains(SubDatumCard.AccuracyLine(Model()))) return "accuracy line";
                    foreach (string id in new[] { "search", "window", "show", "hide" }) if (p.Find(id) == null) return "button " + id;
                    if (p.Find("undo") != null) return "no undo before a click";
                    if (l == "ru")
                    {
                        if (SubDatumCard.BearingLine(Model()) != "Пеленг 352° от точки обнаружения, 0–9 миль (ядро 0–6)") return "ru bearing: " + SubDatumCard.BearingLine(Model());
                        if (SubDatumCard.AccuracyLine(Model()) != "Точность ±15° (по курсу трека) · 71M · 1 мин назад") return "ru accuracy: " + SubDatumCard.AccuracyLine(Model());
                        if (SubDatumCard.SearchLabel(Model()) != "Искать: ПЛО-1 (≈ 6 мин)") return "ru search label: " + SubDatumCard.SearchLabel(Model());
                        if (p.Find("window").Text != "В окно ПЛО…" || p.Find("show").Text != "Показать" || p.Find("hide").Text != "Скрыть") return "ru labels";
                    }
                    return null;
                }, "search");
                if (e != null) return e;
                foreach (string id in new[] { "window", "show", "hide" })
                {
                    e = Run(Model(), lang, (l, p, c) => null, id);
                    if (e != null) return e;
                }
            }
            var m = Model();
            using (UiText.Push("ru"))
            {
                m.Src = "fixes"; if (!SubDatumCard.AccuracyLine(m).Contains("по отсчётам")) return "fixes accuracy";
                m.Src = "los"; m.SigmaDeg = 30; m.Ammo = null;
                string a = SubDatumCard.AccuracyLine(m);
                if (!a.Contains("±30°") || !a.Contains("?") || !a.Contains("линии обзора")) return "los accuracy: " + a;
                m.AgeS = 5; if (!SubDatumCard.AccuracyLine(m).Contains("только что")) return "age now";
                m.AgeS = 40; if (!SubDatumCard.AccuracyLine(m).Contains("40 с назад")) return "age seconds";
                if (SubDatumCard.MapLabel(Model()) != "ПЛ? 71M · ±15° · 1 мин") return "map label: " + SubDatumCard.MapLabel(Model());
            }
            return null;
        }

        public static string NoGroup()
        {
            foreach (string lang in new[] { "ru", "en" })
            {
                var m = Model(); m.Group = null; m.GroupEtaS = -1;
                string e = Run(m, lang, (l, p, c) =>
                {
                    if (p.Find("search") != null) return "no search button without a group";
                    if (p.Find("window") == null || p.Find("show") == null || p.Find("hide") == null) return "other buttons";
                    if (!AllText(p).Contains(UiText.Fmt("datum.no_group"))) return "no_group hint";
                    if (l == "ru" && !AllText(p).Contains("нет свободной группы ПЛО") && !AllText(p).Contains("Нет свободной группы ПЛО")) return "ru hint text";
                    return null;
                }, "window");
                if (e != null) return e;
            }
            return null;
        }

        public static string Replace()
        {
            foreach (string lang in new[] { "ru", "en" })
            {
                var m = Model(); m.Group = "ПЛО-2"; m.ReplacesArea = true;
                string e = Run(m, lang, (l, p, c) =>
                {
                    string lab = SubDatumCard.SearchLabel(m);
                    if (p.Find("search") == null) return "search button";
                    string bt = p.Find("search").Text;
                    if (bt != lab && !(bt.EndsWith("\u2026", StringComparison.Ordinal) && lab.StartsWith(bt.Substring(0, bt.Length - 1), StringComparison.Ordinal))) return "search label on the button: " + bt;   // a long label is cut, the tip has it all
                    if (l == "ru" && lab != "Искать: ПЛО-2 (вместо района)") return "ru replace label: " + lab;
                    if (l == "en" && lab.IndexOf("instead", StringComparison.OrdinalIgnoreCase) < 0 && lab.IndexOf("replac", StringComparison.OrdinalIgnoreCase) < 0) return "en replace label: " + lab;
                    return null;
                });
                if (e != null) return e;
                // after a click: searching line and the 10 s undo
                var m2 = Model(); m2.Group = "ПЛО-2"; m2.ReplacesArea = true; m2.Searching = "ПЛО-2"; m2.UndoLeftS = 7.2;
                e = Run(m2, lang, (l, p, c) =>
                {
                    if (p.Find("search") != null) return "no search while searching";
                    if (p.Find("undo") == null) return "undo button";
                    if (!p.Find("undo").Text.Contains("8") && !p.Find("undo").Text.Contains("7")) return "undo seconds: " + p.Find("undo").Text;
                    if (!AllText(p).Contains(UiText.Fmt("datum.searching", "ПЛО-2"))) return "searching line";
                    if (l == "ru" && p.Find("undo").Text != "Вернуть (8 с)") return "ru undo: " + p.Find("undo").Text;
                    return null;
                }, "undo");
                if (e != null) return e;
                // searching without a replaced order: no undo
                var m3 = Model(); m3.Searching = "ПЛО-1";
                e = Run(m3, lang, (l, p, c) => p.Find("undo") != null ? "no undo when nothing was replaced" : null);
                if (e != null) return e;
            }
            return null;
        }

        public static string Closed()
        {
            foreach (string lang in new[] { "ru", "en" })
                foreach (string st in new[] { "found", "expired" })
                {
                    var m = Model(); m.State = st;
                    string e = Run(m, lang, (l, p, c) =>
                    {
                        if (p.Find("search") != null || p.Find("window") != null || p.Find("undo") != null) return st + ": search buttons";
                        if (p.Find("show") == null || p.Find("hide") == null) return st + ": show/hide";
                        if (!AllText(p).Contains(UiText.Fmt(st == "found" ? "datum.state_found" : "datum.state_expired"))) return st + ": state line";
                        return null;
                    }, "hide");
                    if (e != null) return e;
                }
            return null;
        }

        public static string Auto()
        {
            foreach (string lang in new[] { "ru", "en" })
            {
                var m = Model(); m.AutoStarted = true; m.Searching = "ПЛО-1"; m.UndoLeftS = 10;
                string e = Run(m, lang, (l, p, c) =>
                {
                    if (p.Find("search") != null) return "no search button";
                    if (p.Find("undo") == null) return "cancel button";
                    if (!AllText(p).Contains(UiText.Fmt("datum.auto", "ПЛО-1"))) return "auto line";
                    if (l == "ru" && (p.Find("undo").Text != "Отменить" || !AllText(p).Contains("Группа ПЛО-1 ищет"))) return "ru auto: " + p.Find("undo").Text;
                    return null;
                }, "undo");
                if (e != null) return e;
            }
            return null;
        }
    }
}
