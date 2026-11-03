// T-178 Kb3 (ADR-0077 §2.2): UiKit.Compass: a round selector of a bearing. A 64 px x s disc (north up, clockwise), ticks every 45 degrees, a ray and a handle, a triangle for the
// course of the reference ship; a click or a drag sets the angle (the drag goes on outside the disc), snapped to `step`; ◀ ▶ change by 1 degree; 8 preset buttons; the mode
// [Abs.] [From course] only changes the way the angle is entered and shown, the result is always the absolute bearing. The mode is remembered in IUiWindowValues
// (compass.<id> = abs|rel) or, without it, in this kit for the session.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class UiKit
    {
        private readonly Dictionary<string, string> _compassMode = new Dictionary<string, string>();
        private static readonly string[] CompassDirKeys = { "n", "ne", "e", "se", "s", "sw", "w", "nw" };

        private static string SignedDeg(double v)
        {
            int n = (int)Math.Round(v);
            return n > 0 ? "+" + n : n < 0 ? "−" + (-n) : "0";
        }

        public double Compass(string id, double absDeg, double? courseDeg, string tip, bool enabled = true, double step = 5)
        {
            double cur = CompassMath.Norm(absDeg), result = cur;
            bool canRel = courseDeg.HasValue;
            string key = "compass." + id;
            var vals = _state as IUiWindowValues;
            string mode;
            if (vals != null) mode = vals.Value(key, "abs");
            else if (!_compassMode.TryGetValue(key, out mode)) mode = "abs";
            bool rel = mode == "rel" && canRel;

            // mode
            Wrap();
            string relTip = canRel ? UiText.Get("ui.compass.mode.rel.tip") : UiText.Get("ui.compass.rel_off");
            int m = Radio(id + ".mode", new[] { UiText.Get("ui.compass.mode.abs"), UiText.Get("ui.compass.mode.rel") }, rel ? 1 : 0,
                new[] { UiText.Get("ui.compass.mode.abs.tip"), relTip }, new[] { enabled, enabled && canRel });
            if (m != (rel ? 1 : 0))
            {
                rel = m == 1;
                if (vals != null) vals.SetValue(key, rel ? "rel" : "abs"); else _compassMode[key] = rel ? "rel" : "abs";
            }
            Wrap();

            // disc
            float D = Math.Min((float)Math.Round(64 * _t.S), Math.Max(40f, _right - _x));
            float x0 = _x, y0 = _y + 1f;
            var disc = new UiRect(x0, y0, D, D);
            float cx = x0 + D / 2f, cy = y0 + D / 2f, rad = D / 2f - 1f;
            _p.Ring(Loc(disc), UiColorRole.Divider, 1f);
            for (int i = 0; i < 8; i++)
            {
                double a = i * 45 * Math.PI / 180.0;
                float len = (i % 2 == 0 ? 5f : 3f) * _t.S;
                _p.Line(cx + (float)Math.Sin(a) * rad, cy - (float)Math.Cos(a) * rad, cx + (float)Math.Sin(a) * (rad - len), cy - (float)Math.Cos(a) * (rad - len), UiColorRole.Divider, 1f);
            }
            float lw = Math.Max(10f, M("W", UiFont.Small)) + 2f, lh = _t.TableRowH;
            string[] cardinal = { UiText.Get("ui.compass.n"), UiText.Get("ui.compass.e"), UiText.Get("ui.compass.s"), UiText.Get("ui.compass.w") };
            _p.Label(Loc(new UiRect(cx - lw / 2f, y0 + 6f * _t.S, lw, lh)), cardinal[0], UiFont.Small, UiTone.Dim, null);
            _p.Label(Loc(new UiRect(x0 + D - lw - 6f * _t.S, cy - lh / 2f, lw, lh)), cardinal[1], UiFont.Small, UiTone.Dim, null);
            _p.Label(Loc(new UiRect(cx - lw / 2f, y0 + D - lh - 6f * _t.S, lw, lh)), cardinal[2], UiFont.Small, UiTone.Dim, null);
            _p.Label(Loc(new UiRect(x0 + 6f * _t.S, cy - lh / 2f, lw, lh)), cardinal[3], UiFont.Small, UiTone.Dim, null);
            if (courseDeg.HasValue)
            {
                double ca = courseDeg.Value * Math.PI / 180.0;
                float tw = 10f * _t.S;
                _p.Icon(Loc(new UiRect(cx + (float)Math.Sin(ca) * (rad - tw / 2f) - tw / 2f, cy - (float)Math.Cos(ca) * (rad - tw / 2f) - tw / 2f, tw, tw)), "glyph:△", UiTone.Dim);
            }
            string discTip = (string.IsNullOrEmpty(tip) ? "" : tip + "\n") + UiText.Get("ui.compass.disc.tip");
            if (enabled)
            {
                var ld = Loc(disc);
                float mx, my; double deg;
                if (_p.Drag(ld, Sid(id + ".disc"), out mx, out my) && CompassMath.AngleAt(ld.X + ld.W / 2f, ld.Y + ld.H / 2f, ld.W / 2f - 1f, mx, my, out deg))
                    result = CompassMath.Snap(deg, step);
            }
            // ray and handle follow the value shown in this pass
            double shown = result;
            double sa = shown * Math.PI / 180.0;
            float hw = Math.Max(14f * _t.S, 8f), hr = Math.Max(1f, rad - hw / 2f);
            float hx = cx + (float)Math.Sin(sa) * hr, hy = cy - (float)Math.Cos(sa) * hr;
            _p.Line(cx, cy, hx, hy, enabled ? UiColorRole.Accent : UiColorRole.Divider, Math.Max(1f, 1.5f * _t.S));
            _p.Fill(Loc(new UiRect(hx - hw / 2f, hy - hw / 2f, hw, hw)), enabled ? UiColorRole.Accent : UiColorRole.Divider);
            _p.Label(Loc(disc), "", UiFont.Body, UiTone.Normal, discTip);

            // value and the 1 degree buttons to the right of the disc
            float x1 = x0 + D + _t.Gap, w1 = Math.Max(0f, _right - x1);
            string text = rel && courseDeg.HasValue
                ? UiText.Fmt("ui.compass.value.rel", SignedDeg(CompassMath.ToRel(shown, courseDeg.Value)), (int)Math.Round(shown))
                : UiText.Fmt("ui.compass.value.abs", (int)Math.Round(shown));
            string vs = Cut(text, UiFont.Body, w1);
            _p.Label(Loc(new UiRect(x1, y0, w1, _t.RowH)), vs, UiFont.Body, enabled ? UiTone.Normal : UiTone.Dim, FullTip(text, tip, vs != text));
            float bw = Math.Max(_t.CtlH, M(UiText.Get("ui.compass.dec"), UiFont.Body) + 2 * _t.BtnPad);
            float by = y0 + _t.RowH + 2f;
            if (w1 >= 2 * bw + _t.Gap)
            {
                if (Btn(new UiRect(x1, by, bw, _t.CtlH), id + ".dec", UiText.Get("ui.compass.dec"), UiText.Get("ui.compass.dec.tip"), enabled, false, UiTone.Normal)) result = CompassMath.Nudge(shown, -1);
                if (Btn(new UiRect(x1 + bw + _t.Gap, by, bw, _t.CtlH), id + ".inc", UiText.Get("ui.compass.inc"), UiText.Get("ui.compass.inc.tip"), enabled, false, UiTone.Normal)) result = CompassMath.Nudge(shown, 1);
            }
            _y = y0 + D + 1f; _x = _lineStart; _lineH = 0; _labelBottom = 0;

            // presets
            double[] set = rel ? CompassMath.PresetsRel : CompassMath.PresetsAbs;
            for (int i = 0; i < 8; i++)
            {
                double target = rel ? CompassMath.ToAbs(set[i], courseDeg.Value) : set[i];
                string label = rel ? (set[i] == 0 || set[i] == 180 ? ((int)set[i]).ToString() : SignedDeg(set[i])) : UiText.Get("ui.compass." + CompassDirKeys[i]);
                string ptip = rel ? UiText.Get("ui.compass.preset." + i) + " (" + SignedDeg(set[i]) + "°)" : label + " (" + (int)set[i] + "°)";
                var slot = Place(BtnWidth(label, 0.3f * (_right - _lineStart)));
                bool on = Math.Abs(CompassMath.ToRel(target, shown)) < 0.5;
                if (Btn(CtlRect(slot), id + ".p." + i, label, ptip, enabled, on && enabled, UiTone.Normal)) result = target;
            }
            return CompassMath.Norm(result);
        }
    }
}
