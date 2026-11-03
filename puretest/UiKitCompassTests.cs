// T-178 Kb3: click tests of UiKit.Compass on the RecordPainter.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class ValState : IUiWindowState, IUiWindowValues
    {
        public readonly Dictionary<string, string> Vals = new Dictionary<string, string>();
        public bool SectionOpen(string id, bool dflt) { return dflt; }
        public void SetSectionOpen(string id, bool open) { }
        public string Value(string id, string dflt) { string v; return Vals.TryGetValue(id, out v) ? v : dflt; }
        public void SetValue(string id, string v) { Vals[id] = v; }
    }

    internal static class UiKitCompassTests
    {
        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }

        public static string Run()
        {
            _fail = null;
            foreach (double s in new[] { 1.0, 0.85, 1.33 })
            {
                string tag = "compass@" + s + ": ";
                Clicks(s, tag); Modes(s, tag); Memory(s, tag);
                if (_fail != null) return _fail;
            }
            return _fail;
        }

        private static void Disc(RecordPainter p, out float cx, out float cy, out float r)
        {
            var d = p.Find("c.disc").R;
            cx = d.X + d.W / 2f; cy = d.Y + d.H / 2f; r = d.W / 2f;
        }

        private static void Clicks(double s, string tag)
        {
            var c = new WidgetCtx(s);
            double ang = 0; double? course = null;
            Action<UiKit> body = k => { k.Row("Пеленг"); ang = k.Compass("c", ang, course, "Пеленг зоны"); };
            c.Frame(body);
            float cx, cy, r; Disc(c.P, out cx, out cy, out r);
            var disc = c.P.Find("c.disc").R;
            Eq(tag + "disc >= 54 px, square", disc.W >= 54f - 0.5f && Math.Abs(disc.W - disc.H) < 0.01f);
            Eq(tag + "layout clean, no overflow", c.P.CheckLayout(c.Width, 10000) == null && c.K.Overflows.Count == 0);
            Eq(tag + "ring, 8 ticks and a ray are drawn", c.P.Of("ring").Count == 1 && c.P.Of("line").Count == 9);
            Eq(tag + "N E S W labels", c.P.FindText("С") != null && c.P.FindText("В") != null && c.P.FindText("Ю") != null && c.P.FindText("З") != null);
            Eq(tag + "no course: no triangle", c.P.Of("icon").Count == 0);
            // click: east
            c.P.ClickAt(cx + r * 0.7f, cy); c.Frame(body);
            Eq(tag + "click on the east side -> 90", Math.Abs(ang - 90) < 0.01);
            c.P.ClickAt(cx + r * 0.6f, cy - r * 0.6f * 1.0f); c.Frame(body);
            Eq(tag + "click north-east -> 45", Math.Abs(ang - 45) < 0.01);
            c.P.ClickAt(cx, cy + r * 0.7f); c.Frame(body);
            Eq(tag + "click south -> 180", Math.Abs(ang - 180) < 0.01);
            c.P.ClickAt(cx - r * 0.7f, cy); c.Frame(body);
            Eq(tag + "click west -> 270", Math.Abs(ang - 270) < 0.01);
            // the dead zone changes nothing
            c.P.ClickAt(cx + 1, cy + 1); c.Frame(body);
            Eq(tag + "dead zone: unchanged", Math.Abs(ang - 270) < 0.01);
            // snapping: 92 degrees -> 90
            double a92 = 92 * Math.PI / 180.0;
            c.P.ClickAt(cx + (float)Math.Sin(a92) * r * 0.7f, cy - (float)Math.Cos(a92) * r * 0.7f); c.Frame(body);
            Eq(tag + "92 degrees snaps to 90", Math.Abs(ang - 90) < 0.01);
            // the drag goes on outside the disc
            c.P.DragTo("c.disc", cx + 400, cy); c.Frame(body);
            Eq(tag + "drag far outside the disc keeps the angle (east)", Math.Abs(ang - 90) < 0.01);
            c.P.DragTo("c.disc", cx + 400, cy + 400); c.Frame(body);
            Eq(tag + "drag far outside, south-east -> 135", Math.Abs(ang - 135) < 0.01);
            c.P.Release();
            // presets
            c.P.Click("c.p.2"); c.Frame(body);
            Eq(tag + "preset East -> 90", Math.Abs(ang - 90) < 0.01);
            c.Frame(body);
            Eq(tag + "the preset of the value is lit", c.P.Find("c.p.2").Selected && !c.P.Find("c.p.0").Selected);
            c.P.Click("c.p.7"); c.Frame(body);
            Eq(tag + "preset NW -> 315", Math.Abs(ang - 315) < 0.01);
            Eq(tag + "all presets have a tip", c.P.Items.TrueForAll(i => i.Id == null || !i.Id.StartsWith("c.p.") || !string.IsNullOrEmpty(i.Tip)));
            // arrows through 0
            ang = 0; c.P.Click("c.dec"); c.Frame(body);
            Eq(tag + "◀ from 0 -> 359", Math.Abs(ang - 359) < 0.01);
            c.P.Click("c.inc"); c.Frame(body);
            Eq(tag + "▶ from 359 -> 0", Math.Abs(ang - 0) < 0.01);
            ang = 90; c.P.Click("c.inc"); c.Frame(body);
            Eq(tag + "▶ = +1 degree, no snapping", Math.Abs(ang - 91) < 0.01);
            Eq(tag + "value text", c.P.FindText("91°") != null || c.P.FindText("90°") != null);
            // grey compass: nothing moves
            var c2 = new WidgetCtx(s);
            double a2 = 45;
            Action<UiKit> b2 = k => { k.Row("Пеленг"); a2 = k.Compass("c", a2, null, "Нет зоны", false); };
            c2.Frame(b2);
            float cx2 = cx, cy2 = cy, r2 = r;   // same layout; a grey compass records no Drag
            c2.P.ClickAt(cx2 + r2 * 0.7f, cy2); c2.P.Click("c.p.2"); c2.P.Click("c.dec"); c2.Frame(b2);
            Eq(tag + "a grey compass does not change", Math.Abs(a2 - 45) < 0.01 && !c2.P.Find("c.p.2").Enabled && !c2.P.Find("c.dec").Enabled);
        }

        private static void Modes(double s, string tag)
        {
            var c = new WidgetCtx(s);
            double ang = 0; double? course = null;
            Action<UiKit> body = k => { k.Row("Пеленг"); ang = k.Compass("c", ang, course, "Пеленг зоны"); };
            c.Frame(body);
            var rel = c.P.Find("c.mode.1");
            Eq(tag + "no course: From course is grey with the reason", !rel.Enabled && rel.Tip.Contains("курса") && c.P.Find("c.mode.0").Enabled);
            string dc = null;
            c.P.Click("c.mode.1");
            c.Frame(k => { body(k); dc = k.DisabledClick; });
            Eq(tag + "no course: a click on the grey mode gives the reason", dc != null && dc.Contains("Нет курса"));
            // with a course of 145
            course = 145;
            c.Frame(body);
            Eq(tag + "course known: both modes work, a triangle is drawn", c.P.Find("c.mode.1").Enabled && c.P.Of("icon").Count == 1);
            c.P.Click("c.mode.1"); c.Frame(body); c.Frame(body);
            Eq(tag + "mode From course is selected", c.P.Find("c.mode.1").Selected);
            c.P.Click("c.p.2"); c.Frame(body);   // +90
            Eq(tag + "From course +90 at course 145 -> 235 absolute", Math.Abs(ang - 235) < 0.01);
            c.Frame(body);
            Eq(tag + "double caption: +90° from course (235°)", c.P.FindText("+90° от курса (235°)") != null);
            c.P.Click("c.p.6"); c.Frame(body);   // -90
            Eq(tag + "From course -90 -> 55", Math.Abs(ang - 55) < 0.01);
            c.Frame(body);
            Eq(tag + "caption with a minus", c.P.FindText("−90° от курса (55°)") != null);
            Eq(tag + "relative presets have names in the tips", c.P.Find("c.p.0").Tip.StartsWith("Вперёд") && c.P.Find("c.p.2").Tip.StartsWith("Правый траверз") && c.P.Find("c.p.4").Tip.StartsWith("Корма"));
            // the course disappears: the mode falls back to absolute
            course = null; c.Frame(body);
            Eq(tag + "no course any more: absolute mode again", c.P.FindText("55°") != null && c.P.Find("c.mode.0").Selected);
            Eq(tag + "layout clean in both modes", c.P.CheckLayout(c.Width, 10000) == null);
        }

        private static void Memory(double s, string tag)
        {
            // without IUiWindowValues: kept in the kit for the session
            var c = new WidgetCtx(s);
            double ang = 10;
            Action<UiKit> body = k => { k.Row("Пеленг"); ang = k.Compass("c", ang, 90.0, "t"); };
            c.Frame(body); c.P.Click("c.mode.1"); c.Frame(body); c.Frame(body);
            Eq(tag + "session memory without a store", c.P.Find("c.mode.1").Selected);
            // with the store: written as compass.<id> and restored by a new kit
            var vs = new ValState();
            var p1 = new RecordPainter(s); var t = UiTheme.For(s);
            var k1 = new UiKit(p1, t, vs, "w");
            Action<UiKit> b1 = k => { k.Row("x"); k.Compass("c", 0, 90.0, "t"); };
            p1.NextFrame(); k1.Begin(300f * (float)s); b1(k1); k1.End();
            p1.Click("c.mode.1");
            p1.NextFrame(); k1.Begin(300f * (float)s); b1(k1); k1.End();
            Eq(tag + "the mode is written to the window values", vs.Vals.ContainsKey("compass.c") && vs.Vals["compass.c"] == "rel");
            var p2 = new RecordPainter(s);
            var k2 = new UiKit(p2, t, vs, "w");
            p2.NextFrame(); k2.Begin(300f * (float)s); b1(k2); k2.End();
            Eq(tag + "a new kit starts in the remembered mode", p2.Find("c.mode.1").Selected);
        }
    }
}
