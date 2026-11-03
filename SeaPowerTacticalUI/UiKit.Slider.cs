// T-178 Ka4 (ADR-0077 §2.3, ADR-0076 §7): UiKit.Slider and AutoSlider. "◀ [====o----|--] ▶ value": the whole track is clickable and draggable (Drag), the handle is
// at least 14 px x s, a tick marks the default value, ◀ ▶ change by one step, the mouse wheel does nothing. The value is snapped to the step and clamped.
using System;

namespace VoiceBotBridge
{
    internal sealed partial class UiKit
    {
        public static double SnapValue(double v, double min, double max, double step)
        {
            if (max < min) max = min;
            if (step > 0) v = min + Math.Round((v - min) / step) * step;
            if (v < min) v = min;
            if (v > max) v = max;
            return Math.Round(v, 6);
        }

        public double Slider(string id, double min, double max, double step, double value, Func<double, string> format, string tip, double? defaultValue = null, bool enabled = true)
        {
            if (format == null) format = v => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            double cur = SnapValue(value, min, max, 0);
            float btn = _t.CtlH;
            float valW = Math.Max(M(format(min), UiFont.Body), Math.Max(M(format(max), UiFont.Body), M(format(cur), UiFont.Body)));
            float trackW = (float)Math.Round(96 * _t.S);
            float total = 2 * btn + trackW + valW + 3 * _t.Gap;
            if (total > _right - _x && _x > _lineStart + 0.01f) NextLine();
            trackW = Math.Max(Math.Min(trackW, _right - _x - 2 * btn - valW - 3 * _t.Gap), (float)Math.Round(40 * _t.S));
            string tipDec = UiText.Get("ui.slider.dec"), tipInc = UiText.Get("ui.slider.inc");
            string fullTip = tip;
            if (defaultValue.HasValue) fullTip = DefaultTip(tip, format(defaultValue.Value));
            double result = cur;

            var s1 = Place(btn);
            if (Btn(CtlRect(s1), Cat(id, ".dec"), "◀", string.IsNullOrEmpty(fullTip) ? tipDec : Cat(Cat(tipDec, "\n"), fullTip), enabled, false, UiTone.Normal))
                result = SnapValue(cur - (step > 0 ? step : (max - min) / 20), min, max, step);

            var s2 = Place(trackW);
            var tr = CtlRect(s2);
            float hw = Math.Max(14f * _t.S, 8f);
            float span = Math.Max(1f, tr.W - hw);
            float frac = max > min ? (float)((cur - min) / (max - min)) : 0f;
            float barH = Math.Max(3f, (float)Math.Round(4 * _t.S));
            var bar = new UiRect(tr.X, tr.Y + (tr.H - barH) / 2f, tr.W, barH);
            _p.Fill(Loc(bar), UiColorRole.Separator);
            float hx = tr.X + frac * span;
            if (hx > tr.X) _p.Fill(Loc(new UiRect(tr.X, bar.Y, hx - tr.X + hw / 2f, barH)), UiColorRole.Selected);
            if (defaultValue.HasValue && max > min)
            {
                float dx = tr.X + hw / 2f + (float)((SnapValue(defaultValue.Value, min, max, 0) - min) / (max - min)) * span;
                var l = Loc(new UiRect(dx, tr.Y, 0, tr.H));
                _p.Line(l.X, l.Y, l.X, l.Y + tr.H, UiColorRole.Divider, 1f);
            }
            _p.Fill(Loc(new UiRect(hx, tr.Y, hw, tr.H)), enabled ? UiColorRole.Accent : UiColorRole.Divider);
            _p.Label(Loc(tr), "", UiFont.Body, UiTone.Normal, fullTip);   // the tooltip of the whole track
            if (enabled)
            {
                var lt = Loc(tr);
                float mx, my;
                if (_p.Drag(lt, Sid(Cat(id, ".trk")), out mx, out my))
                {
                    float f = Math.Max(0f, Math.Min(1f, (mx - lt.X - hw / 2f) / span));
                    result = SnapValue(min + f * (max - min), min, max, step);
                }
            }

            var s3 = Place(btn);
            if (Btn(CtlRect(s3), Cat(id, ".inc"), "▶", string.IsNullOrEmpty(fullTip) ? tipInc : Cat(Cat(tipInc, "\n"), fullTip), enabled, false, UiTone.Normal))
                result = SnapValue(cur + (step > 0 ? step : (max - min) / 20), min, max, step);

            var s4 = Place(valW);
            _p.Label(Loc(TextRect(s4)), format(enabled ? result : cur), UiFont.Body, enabled ? UiTone.Normal : UiTone.Dim, fullTip);
            return result;
        }

        /// <summary>A "Auto" flag in front of the slider: while it is on, the slider is grey and shows autoValue. Returns the slider value (unchanged while Auto is on).</summary>
        public double AutoSlider(string id, ref bool auto, double autoValue, double min, double max, double step, double value, Func<double, string> format, string tip, double? defaultValue = null)
        {
            auto = Check(Cat(id, ".auto"), auto, UiText.Get("ui.auto"), UiText.Get("ui.auto.tip"));
            if (auto) { Slider(id, min, max, step, autoValue, format, tip, defaultValue, false); return value; }
            return Slider(id, min, max, step, value, format, tip, defaultValue, true);
        }
    }
}
