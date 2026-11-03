// T-178 Kd3 (ADR-0077 §2.6): hit zones. A control reacts to its visible rectangle plus max(2, round(2 * s)) px on every side, but never beyond the middle of the gap
// to a neighbour: the zones do not overlap. UiKit.End() collects the rectangles of the pass, Begin() hands the zones of the previous pass to the painter.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class UiHit
    {
        public static float Slop(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s) || s <= 0) s = 1;
            return (float)Math.Max(2.0, Math.Round(2.0 * s, MidpointRounding.AwayFromZero));
        }

        private static bool Meet(float a0, float a1, float b0, float b1) { return a0 < b1 - 0.001f && b0 < a1 - 0.001f; }

        public static UiRect[] Inflate(IList<UiRect> rects, float slop)
        {
            int n = rects == null ? 0 : rects.Count;
            var res = new UiRect[n];
            InflateInto(rects, slop, res);
            return res;
        }

        /// <summary>T-239: the same into a caller-owned buffer (res.Length &gt;= rects.Count; the entries past Count are left alone), no allocation.</summary>
        public static void InflateInto(IList<UiRect> rects, float slop, UiRect[] res)
        {
            int n = rects == null ? 0 : rects.Count;
            for (int i = 0; i < n; i++)
            {
                var r = rects[i];
                float l = slop, rt = slop, up = slop, dn = slop;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    var o = rects[j];
                    if (Meet(r.Y, r.Y + r.H, o.Y - slop, o.Y + o.H + slop))
                    {
                        if (o.X >= r.X + r.W - 0.001f) rt = Math.Min(rt, Math.Max(0f, (o.X - (r.X + r.W)) / 2f));
                        else if (o.X + o.W <= r.X + 0.001f) l = Math.Min(l, Math.Max(0f, (r.X - (o.X + o.W)) / 2f));
                    }
                    if (Meet(r.X, r.X + r.W, o.X - slop, o.X + o.W + slop))
                    {
                        if (o.Y >= r.Y + r.H - 0.001f) dn = Math.Min(dn, Math.Max(0f, (o.Y - (r.Y + r.H)) / 2f));
                        else if (o.Y + o.H <= r.Y + 0.001f) up = Math.Min(up, Math.Max(0f, (r.Y - (o.Y + o.H)) / 2f));
                    }
                }
                res[i] = new UiRect(r.X - l, r.Y - up, r.W + l + rt, r.H + up + dn);
            }
            // diagonal neighbours: their zones may still meet at the corner; trim the horizontal part of the zone that is smaller in the overlap
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (!res[i].Overlaps(res[j])) continue;
                    float ox = Math.Min(res[i].X + res[i].W, res[j].X + res[j].W) - Math.Max(res[i].X, res[j].X);
                    float oy = Math.Min(res[i].Y + res[i].H, res[j].Y + res[j].H) - Math.Max(res[i].Y, res[j].Y);
                    bool iLeft = rects[i].X + rects[i].W / 2f <= rects[j].X + rects[j].W / 2f, iTop = rects[i].Y + rects[i].H / 2f <= rects[j].Y + rects[j].H / 2f;
                    if (ox <= oy) TrimX(res, i, j, ox, iLeft); else TrimY(res, i, j, oy, iTop);
                }
        }

        private static void TrimX(UiRect[] a, int i, int j, float o, bool iLeft)
        {
            float h = o / 2f;
            if (iLeft) { a[i].W -= h; a[j].X += h; a[j].W -= h; } else { a[j].W -= h; a[i].X += h; a[i].W -= h; }
        }

        private static void TrimY(UiRect[] a, int i, int j, float o, bool iTop)
        {
            float h = o / 2f;
            if (iTop) { a[i].H -= h; a[j].Y += h; a[j].H -= h; } else { a[j].H -= h; a[i].Y += h; a[i].H -= h; }
        }

        public static string SelfTest()
        {
            if (Slop(1) != 2 || Slop(0.85) != 2 || Slop(1.33) != 3 || Slop(2) != 4 || Slop(double.NaN) != 2) return "slop: " + Slop(1) + "," + Slop(1.33);
            var one = Inflate(new[] { new UiRect(10, 10, 30, 18) }, 2);
            if (one[0].X != 8 || one[0].Y != 8 || one[0].W != 34 || one[0].H != 22) return "single control +2 px on every side";
            var s3 = Inflate(new[] { new UiRect(10, 10, 30, 18) }, Slop(1.33));
            if (s3[0].X != 7 || s3[0].W != 36) return "s 1.33: +3 px";
            // neighbours with a 3 px gap: zones meet in the middle and do not overlap
            var two = Inflate(new[] { new UiRect(10, 10, 30, 18), new UiRect(43, 10, 30, 18) }, 2);
            if (two[0].Overlaps(two[1])) return "neighbours with a 3 px gap: zones overlap";
            if (Math.Abs(two[0].X + two[0].W - 41.5f) > 0.01f || Math.Abs(two[1].X - 41.5f) > 0.01f) return "the zones meet in the middle of the gap: " + (two[0].X + two[0].W) + "," + two[1].X;
            if (two[0].X != 8 || two[1].X + two[1].W != 75) return "outer sides still +2";
            // touching controls: no growth between them
            var touch = Inflate(new[] { new UiRect(0, 0, 20, 18), new UiRect(20, 0, 20, 18) }, 2);
            if (touch[0].X + touch[0].W != 20 || touch[1].X != 20) return "touching controls";
            // stacked rows with a 1 px gap
            var rows = Inflate(new[] { new UiRect(0, 0, 50, 16), new UiRect(0, 17, 50, 16) }, 2);
            if (rows[0].Overlaps(rows[1])) return "rows with a 1 px gap overlap";
            // a far neighbour changes nothing
            var far = Inflate(new[] { new UiRect(0, 0, 20, 18), new UiRect(200, 0, 20, 18) }, 2);
            if (far[0].W != 24 || far[1].X != 198) return "a far neighbour";
            // diagonal neighbours never overlap
            var diag = Inflate(new[] { new UiRect(0, 0, 20, 18), new UiRect(22, 19, 20, 18) }, 2);
            if (diag[0].Overlaps(diag[1])) return "diagonal neighbours overlap";
            // a grid of controls
            var grid = new List<UiRect>();
            for (int y = 0; y < 3; y++) for (int x = 0; x < 5; x++) grid.Add(new UiRect(x * 23, y * 21, 20, 18));
            var g = Inflate(grid, 2);
            for (int i = 0; i < g.Length; i++) for (int j = i + 1; j < g.Length; j++) if (g[i].Overlaps(g[j])) return "grid zones overlap: " + i + "," + j;
            if (Inflate(null, 2).Length != 0) return "null list";
            return null;
        }
    }

    /// <summary>T-239: a quick LMB click whose hot control the game lost between MouseDown and MouseUp still counts once (was private to UnityPainter.Button since 0.8.7).
    /// Pure state machine: the painter feeds it the raw mouse events of one control id and reads the verdict.</summary>
    internal sealed class UiClickLatch
    {
        private string _down;

        public string DownId { get { return _down; } }

        /// <summary>MouseDown (raw) inside the zone of id: remember id.</summary>
        public void Down(string id, bool insideZone)
        {
            if (id != null && insideZone) _down = id;
        }

        /// <summary>The normal path consumed the click (IMGUI returned true on its own): the latch of this id is dropped.</summary>
        public void Consumed(string id)
        {
            if (id != null && _down == id) _down = null;
        }

        /// <summary>MouseUp (raw) for id. normalHit = what IMGUI returned. Returns true when the click must count: normalHit, or (no normalHit and the down was on this id
        /// and the up is inside the zone). The latch of this id is cleared either way.</summary>
        public bool Up(string id, bool insideZone, bool normalHit)
        {
            bool mine = id != null && _down == id;
            if (mine) _down = null;
            return normalHit || (mine && insideZone);
        }

        public static string SelfTest()
        {
            var l = new UiClickLatch();
            l.Down("a", true);
            if (!l.Up("a", true, false)) return "latch: down + up inside on the same id counts";
            if (l.Up("a", true, false)) return "latch: a repeated up does not count";
            if (l.DownId != null) return "latch: cleared after the up";
            if (l.Up("a", true, false)) return "latch: up without a down";
            l.Down("a", true);
            if (l.Up("b", true, false) || l.DownId != "a") return "latch: another id does not count and keeps the latch";
            if (l.Up("a", false, false)) return "latch: up outside the zone";
            if (l.DownId != null) return "latch: cleared by an up outside";
            l.Down("a", false);
            if (l.DownId != null || l.Up("a", true, false)) return "latch: a down outside the zone is not remembered";
            l.Down("a", true);
            if (!l.Up("a", true, true) || l.DownId != null) return "latch: a normal hit counts and leaves the latch empty";
            l.Down("a", true);
            l.Consumed("a");
            if (l.DownId != null || l.Up("a", true, false)) return "latch: Consumed drops the click (no double toggle)";
            l.Down("a", true); l.Consumed("b");
            if (l.DownId != "a") return "latch: Consumed of another id keeps the latch";
            l.Down(null, true);
            if (l.DownId != "a") return "latch: a null id is ignored";
            return null;
        }
    }
}
