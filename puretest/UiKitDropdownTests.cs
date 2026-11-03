// T-178 Ka: tests of UiKit.Dropdown, Slider and Segments on the RecordPainter (ru strings, scale 1 and 0.85 / 1.33).
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>Shared small harness of the widget tests: a painter, a kit and one frame at a time.</summary>
    internal sealed class WidgetCtx
    {
        public RecordPainter P; public UiKit K; public UiTheme T; public MemState S = new MemState();
        public float Width;
        public WidgetCtx(double s, float width = 300f)
        {
            P = new RecordPainter(s); T = UiTheme.For(s); K = new UiKit(P, T, S, "t"); Width = width * (float)s;
        }
        public float Frame(Action<UiKit> body, float? width = null)
        {
            P.NextFrame();
            K.Begin(width ?? Width);
            body(K);
            return K.End();
        }
        public int Count(string idPrefix)
        {
            int n = 0;
            foreach (var it in P.Items) if (it.Id != null && it.Id.StartsWith(idPrefix)) n++;
            return n;
        }
    }

    internal static class UiKitDropdownTests
    {
        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }

        public static string Run()
        {
            _fail = null;
            foreach (double s in new[] { 1.0, 0.85, 1.33 })
            {
                string tag = "dd@" + s + ": ";
                OpenPickClose(s, tag); Stale(s, tag); DisabledItems(s, tag); Overlay(s, tag); ItemsWithIcons(s, tag); Placement(s, tag);
                if (_fail != null) return _fail;
            }
            return _fail;
        }

        private static readonly string[] Names = { "Корабль", "Берег", "Борт", "Точка" };

        private static void OpenPickClose(double s, string tag)
        {
            var c = new WidgetCtx(s);
            int sel = 1;
            Action<UiKit> body = k => { sel = k.Dropdown("dd", Names, sel, "Что искать", 1, null, null, 2); k.Text("после"); };
            c.Frame(body);
            var b = c.P.Find("dd");
            Eq(tag + "closed: one button with the value and the arrow", b != null && b.Text.StartsWith("Берег") && b.Text.EndsWith("▾") && !string.IsNullOrEmpty(b.Tip));
            Eq(tag + "closed: no list, no overlay", c.K.OverlayRect == null && c.Count("dd.i.") == 0);
            c.P.Click("dd");
            float hOpen = c.Frame(body);
            Eq(tag + "click opens the list", c.K.OverlayRect != null && c.Count("dd.i.") == 4);
            Eq(tag + "the content height covers the list", c.K.OverlayRect != null && hOpen >= c.K.OverlayRect.Value.Y + c.K.OverlayRect.Value.H);
            Eq(tag + "open: no overflow, layout clean", c.K.Overflows.Count == 0 && c.P.CheckLayout(c.Width, 10000) == null);
            Eq(tag + "default mark in the list", c.P.FindText("· по умолч.") != null);
            Eq(tag + "list items are overlay items", c.P.Find("dd.i.0").Overlay && !c.P.Find("dd").Overlay);
            // the pick is delivered by the next call
            c.P.Click("dd.i.2");
            c.Frame(body);
            c.Frame(body);
            Eq(tag + "pick: the value, the list is closed", sel == 2 && c.K.OverlayRect == null && c.Count("dd.i.") == 0);
            Eq(tag + "pick: the button shows the new value", c.P.Find("dd").Text.StartsWith("Борт"));
            // the button closes the list again
            c.P.Click("dd"); c.Frame(body);
            Eq(tag + "reopened", c.K.OverlayRect != null);
            c.P.Click("dd"); c.Frame(body);
            Eq(tag + "the button closes the open list", c.K.OverlayRect == null);
            // a click outside closes
            c.P.Click("dd"); c.Frame(body);
            c.P.ClickAt(c.Width - 2, 2); c.Frame(body);
            Eq(tag + "a click outside closes", c.K.OverlayRect == null);
            // Esc closes
            c.P.Click("dd"); c.Frame(body);
            c.P.Esc(); c.Frame(body);
            Eq(tag + "Esc closes", c.K.OverlayRect == null && c.Count("dd.i.") == 0);
            // a click on an item by coordinates selects it
            c.P.Click("dd"); c.Frame(body);
            var it3 = c.P.Find("dd.i.3");
            c.P.ClickAt(it3.R.X + it3.R.W / 2, it3.R.Y + it3.R.H / 2); c.Frame(body); c.Frame(body);
            Eq(tag + "ClickAt on an item selects it", sel == 3 && c.K.OverlayRect == null);
        }

        // KR N1 / N4: a stale pick is dropped; a click outside only closes the list
        private static void Stale(double s, string tag)
        {
            var c = new WidgetCtx(s);
            int sel = 0; bool show = true; int under = 0;
            Action<UiKit> body = k =>
            {
                if (show) sel = k.Dropdown("dd", Names, sel, "tip");
                k.Break();
                if (k.Button("under", "Кнопка", "t")) under++;
            };
            c.Frame(body); c.P.Click("dd"); c.Frame(body);
            // a click outside the list on another control: the list closes and the control does not react
            var u = c.P.Find("under");
            c.P.ClickAt(c.Width - 3, u.R.Y + 2); c.Frame(body);
            Eq(tag + "a click outside only closes the list", c.K.OverlayRect == null && under == 0);
            c.P.ClickAt(u.R.X + 3, u.R.Y + 3); c.Frame(body);
            Eq(tag + "afterwards the control reacts again", under == 1);
            // a pick made while the window no longer asks for the dropdown is dropped
            c.P.Click("dd"); c.Frame(body);
            c.P.Click("dd.i.2"); show = false; c.Frame(body);   // picked in End(), the next pass does not show the dropdown
            c.Frame(body); c.Frame(body);
            show = true; sel = 0; c.Frame(body);
            Eq(tag + "a stale pick does not come back", sel == 0);
        }

        private static void DisabledItems(double s, string tag)
        {
            var c = new WidgetCtx(s);
            int sel = 0;
            var en = new[] { true, false, true };
            var tips = new[] { null, "Нет такого борта", null };
            Action<UiKit> body = k => { sel = k.Dropdown("dd", new[] { "A", "B", "C" }, sel, "tip", 1, en, tips); };
            c.Frame(body); c.P.Click("dd"); c.Frame(body);
            c.P.Click("dd.i.1");
            c.Frame(body);
            string dc = c.K.DisabledClick;   // the list is drawn by End(): the reason is known after the frame
            c.Frame(body);
            Eq(tag + "a grey item is not picked, the list stays open", sel == 0 && c.K.OverlayRect != null);
            Eq(tag + "grey item: the reason is in the tip", !c.P.Find("dd.i.1").Enabled && c.P.Find("dd.i.1").Tip == "Нет такого борта");
            Eq(tag + "grey item click gives DisabledClick", dc == "Нет такого борта");
            // an empty list is a grey button
            var c2 = new WidgetCtx(s);
            c2.Frame(k => k.Dropdown("e", new string[0], -1, "Нет бортов"));
            Eq(tag + "empty list: grey button with the reason", !c2.P.Find("e").Enabled && c2.P.Find("e").Tip == "Нет бортов");
        }

        private static void Overlay(double s, string tag)
        {
            var c = new WidgetCtx(s);
            int sel = 0; int under = 0;
            Action<UiKit> body = k =>
            {
                sel = k.Dropdown("dd", Names, sel, "tip");
                k.Break();
                if (k.Button("under", "Кнопка под списком", "t")) under++;
            };
            c.Frame(body); c.P.Click("dd"); c.Frame(body);
            var u = c.P.Find("under");
            c.P.ClickAt(u.R.X + 3, u.R.Y + 3);
            c.Frame(body);
            Eq(tag + "overlay: a control under the open list does not react (the list item takes the click)", under == 0);
            c.Frame(body);
            Eq(tag + "overlay: that click picked the item under it and closed the list", c.K.OverlayRect == null && sel >= 0);
            c.P.ClickAt(u.R.X + 3, u.R.Y + 3); c.Frame(body);
            Eq(tag + "closed: the same control reacts to a click at that point", under == 1);
        }

        private static void ItemsWithIcons(double s, string tag)
        {
            var c = new WidgetCtx(s);
            var items = new List<UiItem>
            {
                UiItem.Of("Yorktown CG-48", "4,3 мили", "ntds:Surface:Own"),
                UiItem.Of("Ticonderoga", "12 миль", "ntds:Surface:Own", "tip", true),
            };
            Action<UiKit> body = k => k.Dropdown("sh", items, 0, "Корабль");
            c.Frame(body); c.P.Click("sh"); c.Frame(body);
            Eq(tag + "icon item: icons drawn", c.P.Of("icon").Count == 2 && c.P.Of("icon")[0].Text == "ntds:Surface:Own");
            Eq(tag + "icon item: distance on the right, default mark joined", c.P.FindText("4,3 мили") != null && c.P.FindText("12 миль · по умолч.") != null);
            Eq(tag + "icon item: layout clean", c.P.CheckLayout(c.Width, 10000) == null);
        }

        private static void Placement(double s, string tag)
        {
            var c = new WidgetCtx(s);
            Action<UiKit> body = k => { for (int i = 0; i < 6; i++) { k.Row("r" + i); k.Button("pad" + i, "x", "t"); } k.Row("v"); k.Dropdown("dd", Names, 0, "tip"); };
            c.K.ViewH = 6 * c.T.RowH + 3 * c.T.RowH + c.T.Pad;   // the button lies near the bottom: no room below, plenty above
            c.Frame(body);
            c.P.Click("dd"); c.Frame(body);
            var a = c.P.Find("dd");
            var ov = c.K.OverlayRect;
            Eq(tag + "no room below: the list is above the button", ov != null && ov.Value.Y + ov.Value.H <= a.R.Y + 0.5f);
            var c2 = new WidgetCtx(s);
            c2.Frame(k => k.Dropdown("dd", Names, 0, "tip"));
            c2.P.Click("dd"); c2.Frame(k => k.Dropdown("dd", Names, 0, "tip"));
            var a2 = c2.P.Find("dd"); var ov2 = c2.K.OverlayRect;
            Eq(tag + "unknown view height: the list is below", ov2 != null && ov2.Value.Y >= a2.R.Y + a2.R.H - 0.5f);
        }

        // ---------------------------------------------------------------- Slider (Ka4)

        public static string Slider()
        {
            _fail = null;
            foreach (double s in new[] { 1.0, 0.85, 1.33 })
            {
                string tag = "slider@" + s + ": ";
                SliderCases(s, tag);
                if (_fail != null) return _fail;
            }
            return _fail;
        }

        private static float TrackX(UiRec trk, double s, double frac)
        {
            float hw = Math.Max(14f * (float)s, 8f);
            return trk.R.X + hw / 2f + (float)frac * (trk.R.W - hw);
        }

        private static void SliderCases(double s, string tag)
        {
            var c = new WidgetCtx(s);
            double v = 0;
            Func<double, string> fmt = x => x + "%";
            Action<UiKit> body = k => { v = k.Slider("sl", 0, 100, 5, v, fmt, "Порог", 50); };
            c.Frame(body);
            var trk = c.P.Find("sl.trk");
            Eq(tag + "track, ◀, ▶ and the value exist", trk != null && c.P.Find("sl.dec") != null && c.P.Find("sl.inc") != null && c.P.FindText("0%") != null);
            Eq(tag + "default tick: one line", c.P.Of("line").Count == 1);
            float hw = 0;
            foreach (var f in c.P.Of("Fill")) if (f.Id == "Accent") hw = f.R.W;
            Eq(tag + "handle >= 14 px x s", hw >= 14f * (float)s - 0.51f);
            Eq(tag + "tooltip of the track says the default", c.P.Items.Exists(i => i.Kind == "Label" && i.Tip != null && i.Tip.Contains("по умолчанию: 50%")));
            Eq(tag + "layout clean", c.P.CheckLayout(c.Width, 10000) == null);
            // a click at 3/4 of the track
            c.P.ClickAt(TrackX(trk, s, 0.75), trk.R.Y + 2); c.Frame(body);
            Eq(tag + "click at 3/4 -> 75", Math.Abs(v - 75) < 0.01);
            c.P.ClickAt(TrackX(trk, s, 0.77), trk.R.Y + 2); c.Frame(body);
            Eq(tag + "the value sticks to the step (77% -> 75)", Math.Abs(v - 75) < 0.01);
            // the drag goes on outside the track
            c.P.DragTo("sl.trk", trk.R.X + trk.R.W + 300, trk.R.Y + 400); c.Frame(body);
            Eq(tag + "drag beyond the right end -> max", Math.Abs(v - 100) < 0.01);
            c.P.DragTo("sl.trk", trk.R.X - 300, 0); c.Frame(body);
            Eq(tag + "drag beyond the left end -> min", Math.Abs(v - 0) < 0.01);
            c.P.Release(); c.Frame(body);
            Eq(tag + "released: the value stays", Math.Abs(v - 0) < 0.01);
            // arrows
            c.P.Click("sl.dec"); c.Frame(body);
            Eq(tag + "◀ at the minimum stays at the minimum", Math.Abs(v) < 0.01);
            v = 50; c.P.Click("sl.dec"); c.Frame(body);
            Eq(tag + "◀ = one step down", Math.Abs(v - 45) < 0.01);
            c.P.Click("sl.inc"); c.Frame(body); c.P.Click("sl.inc"); c.Frame(body);
            Eq(tag + "▶ = one step up", Math.Abs(v - 55) < 0.01);
            v = 100; c.P.Click("sl.inc"); c.Frame(body);
            Eq(tag + "▶ at the maximum stays", Math.Abs(v - 100) < 0.01);
            // the wheel does nothing: no scroll event on the painter, nothing to check but the value is stable over idle frames
            c.Frame(body); c.Frame(body);
            Eq(tag + "idle frames keep the value", Math.Abs(v - 100) < 0.01);

            // AutoSlider
            var c2 = new WidgetCtx(s);
            bool auto = true; double av = 40;
            Action<UiKit> abody = k => { av = k.AutoSlider("as", ref auto, 12, 0, 60, 1, av, x => x + " фт", "Высота", 20); };
            c2.Frame(abody);
            Eq(tag + "auto: the flag is on, the slider is grey and shows the auto value", c2.P.Find("as.auto").Selected && !c2.P.Find("as.dec").Enabled && !c2.P.Find("as.inc").Enabled && c2.P.FindText("12 фт") != null);
            var ad = c2.P.Find("as.dec");
            c2.P.DragTo("as.trk", ad.R.X + 200, ad.R.Y); c2.Frame(abody);
            Eq(tag + "auto: dragging does nothing", Math.Abs(av - 40) < 0.01);
            c2.P.Release();
            c2.P.Click("as.auto"); c2.Frame(abody); c2.Frame(abody);
            Eq(tag + "the flag turned off: the slider works and shows the own value", !auto && c2.P.Find("as.dec").Enabled && c2.P.FindText("40 фт") != null);
            c2.P.Click("as.inc"); c2.Frame(abody);
            Eq(tag + "the own value changes", Math.Abs(av - 41) < 0.01);
            string dc = null;
            var c3 = new WidgetCtx(s);
            bool a3 = true;
            c3.P.Click("g.dec");
            c3.Frame(k => { k.AutoSlider("g", ref a3, 1, 0, 5, 1, 2, x => "" + x, "Причина серого"); dc = k.DisabledClick; });
            Eq(tag + "a click on a grey arrow gives the reason", dc != null && dc.Contains("Причина серого"));
        }

        // ---------------------------------------------------------------- Segments (Ka5)

        public static string Segments()
        {
            _fail = null;
            foreach (double s in new[] { 1.0, 0.85, 1.33 })
            {
                string tag = "segments@" + s + ": ";
                var labels = new[] { "10", "20", "30", "40", "50" };
                var c = new WidgetCtx(s, 320f);
                int sel = 1;
                Action<UiKit> body = k => { sel = k.Segments("sg", labels, sel, new[] { "a", "b", "c", "d", "e" }, null, 2); };
                c.Frame(body);
                Eq(tag + "5 short labels: segments", c.Count("sg.") == 5 && c.P.Find("sg") == null && c.P.Find("sg.1").Selected);
                Eq(tag + "default mark is in the tip", c.P.Find("sg.2").Tip.Contains("по умолчанию: 30") && c.P.Find("sg.0").Tip == "a");
                c.P.Click("sg.3"); c.Frame(body);
                Eq(tag + "a segment click selects", sel == 3);
                // the same in a narrow window: a list under the same id
                var cn = new WidgetCtx(s, 80f);
                int sn = 1;
                Action<UiKit> nbody = k => { sn = k.Segments("sg", labels, sn, null, null, 2); };
                cn.Frame(nbody);
                Eq(tag + "narrow: Dropdown instead of segments", cn.P.Find("sg") != null && cn.Count("sg.") == 0);
                cn.P.Click("sg"); cn.Frame(nbody);
                Eq(tag + "narrow: the list holds all five", cn.Count("sg.i.") == 5);
                // a grey segment gives the reason
                var cg = new WidgetCtx(s, 320f);
                var en = new[] { true, true, false, true, true };
                cg.P.Click("sg.2");
                string dc = null;
                cg.Frame(k => { k.Segments("sg", labels, 0, new[] { null, null, "Нет ГАС у бортов", null, null }, en); dc = k.DisabledClick; });
                Eq(tag + "grey segment: the reason", dc == "Нет ГАС у бортов" && !cg.P.Find("sg.2").Enabled);
                Eq(tag + "layout clean", c.P.CheckLayout(c.Width, 10000) == null);
                if (_fail != null) return _fail;
            }
            return _fail;
        }
    }
}
