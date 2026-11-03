using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-137 C: an extra consumer of the coverage layer's render texture (range rings, T-138 intercept course, ...). No Unity here, so the
    /// helpers below run in the light stands. Implementations must never throw out of HasContent/RefPoint; DrawGeo is guarded by the layer.
    /// </summary>
    internal interface IRtOverlay
    {
        string Id { get; }
        bool HasContent();
        /// <summary>Projection anchor used when neither radar nor ASW/CAP provide one.</summary>
        bool RefPoint(out double lat, out double lon);
        /// <summary>Unscaled time of the next required redraw; +inf = only on an event (forced redraw).</summary>
        double DueAt { get; }
        /// <summary>T-309: px maps geo to render-texture pixels (exact globe projector, or the old affine fit on a flat map).</summary>
        void DrawGeo(MapPx px, int rw, int rh);
    }

    /// <summary>T-171 / ADR-0068: a line of the legend of an external layer (range rings): colour (Argb, 0 = none), dashed sample, bold title.</summary>
    internal struct RtLegendLine { public string Text; public uint Argb; public bool Dashed, Bold; }

    /// <summary>T-171: an overlay that also gives lines to the legend box above the radar legend. null / empty = no box. Must not throw.</summary>
    internal interface IRtLegend
    {
        IList<RtLegendLine> LegendLines();
    }

    /// <summary>T-171: a text the layer draws itself at a geo point (range-ring labels): no dependency on the contact_info label canvas.</summary>
    internal struct RtTextItem { public double Lat, Lon; public string Text; public uint Argb; public float DyPx; /* T-321: extra pixels down (stacked lines) */ }

    /// <summary>T-171: an overlay that gives texts to draw next to its geometry. null / empty = none. Must not throw.</summary>
    internal interface IRtTexts
    {
        IList<RtTextItem> TextItems();
    }

    internal static class RtOverlays
    {
        public static bool AnyContent(IList<IRtOverlay> l)
        {
            if (l == null) return false;
            for (int i = 0; i < l.Count; i++)
            {
                try { if (l[i] != null && l[i].HasContent()) return true; } catch { }
            }
            return false;
        }

        /// <summary>Earliest DueAt among overlays that have content; +inf when none.</summary>
        public static double MinDue(IList<IRtOverlay> l)
        {
            double m = double.PositiveInfinity;
            if (l == null) return m;
            for (int i = 0; i < l.Count; i++)
            {
                try
                {
                    var o = l[i];
                    if (o == null || !o.HasContent()) continue;
                    double d = o.DueAt;
                    if (!double.IsNaN(d) && d < m) m = d;
                }
                catch { }
            }
            return m;
        }

        public static bool FirstRef(IList<IRtOverlay> l, out double lat, out double lon)
        {
            lat = lon = 0;
            if (l == null) return false;
            for (int i = 0; i < l.Count; i++)
            {
                try
                {
                    var o = l[i];
                    if (o == null || !o.HasContent()) continue;
                    double a, b;
                    if (o.RefPoint(out a, out b) && !double.IsNaN(a) && !double.IsNaN(b)) { lat = a; lon = b; return true; }
                }
                catch { }
            }
            return false;
        }

        private sealed class Fake : IRtOverlay
        {
            public bool Content, HasRef, Throws; public double Due, Lat, Lon;
            public string Id { get { return "fake"; } }
            public bool HasContent() { if (Throws) throw new InvalidOperationException(); return Content; }
            public bool RefPoint(out double lat, out double lon) { lat = Lat; lon = Lon; return HasRef; }
            public double DueAt { get { return Due; } }
            public void DrawGeo(MapPx px, int rw, int rh) { }
        }

        public static string SelfTest()
        {
            var l = new List<IRtOverlay>();
            if (AnyContent(l) || AnyContent(null)) return "empty list has content";
            if (!double.IsPositiveInfinity(MinDue(l))) return "MinDue of empty is not +inf";
            double la, lo;
            if (FirstRef(l, out la, out lo)) return "FirstRef of empty";
            var a = new Fake { Content = false, Due = 1, HasRef = true, Lat = 1, Lon = 2 };
            var b = new Fake { Content = true, Due = 5, HasRef = false };
            var c = new Fake { Content = true, Due = 3, HasRef = true, Lat = 10, Lon = 20 };
            l.Add(a);
            if (AnyContent(l) || !double.IsPositiveInfinity(MinDue(l)) || FirstRef(l, out la, out lo)) return "overlay without content counted";
            l.Add(b); l.Add(c);
            if (!AnyContent(l)) return "AnyContent false";
            if (MinDue(l) != 3) return "MinDue " + MinDue(l);
            if (!FirstRef(l, out la, out lo) || la != 10 || lo != 20) return "FirstRef skips overlays without ref";
            c.Due = double.PositiveInfinity;
            if (MinDue(l) != 5) return "MinDue with +inf";
            var t = new Fake { Throws = true };
            l.Insert(0, t);
            if (!AnyContent(l) || MinDue(l) != 5) return "throwing overlay breaks helpers";
            var lines = new List<RtLegendLine> { new RtLegendLine { Text = "t", Bold = true }, new RtLegendLine { Text = "x", Argb = 0xFF112233, Dashed = true } };
            if (lines[1].Argb != 0xFF112233 || !lines[1].Dashed || !lines[0].Bold || lines[0].Argb != 0) return "legend line";
            return null;
        }
    }
}
