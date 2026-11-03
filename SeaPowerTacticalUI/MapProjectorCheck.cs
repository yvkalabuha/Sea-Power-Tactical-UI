// T-309 / ADR-0119 section 4: the run-time check of the projector against the game (pure policy; the probes themselves live in MapSnapReader).
using System;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>
    /// A check is due on the first snapshot and when the key changed and at least PeriodS passed. Three checks in a row with a difference above TolPx
    /// switch to the slow exact mode ("reflect") until Reset (the MapBase changed). From zoom NoReflectZoom on (Q3, T-309) the game's own float
    /// matrix is as noisy as the tolerance, so a mismatch is only logged there and never switches the mode.
    /// </summary>
    internal sealed class MapProjectorCheck
    {
        public const double TolPx = 1.0, PeriodS = 2.0;
        public const int Strikes = 3;
        public const double NoReflectZoom = 18.0;

        private bool _have; private MapPxKey _last; private double _lastAt = double.NegativeInfinity;
        private int _strikes; private double _zoom;
        // T-312: the previous frame's key (settled-view test) and the state before the last Due (for Defer)
        private bool _frameHave; private MapPxKey _framePrev; private bool _inRun;
        private bool _uHave; private MapPxKey _uLast; private double _uLastAt, _uZoom;
        public const double StillPx = 0.5;

        public bool Reflect { get; private set; }
        public double LastDpx { get; private set; }
        public int Mismatches { get; private set; }
        /// <summary>The last mismatch was only logged (zoom &gt;= NoReflectZoom).</summary>
        public bool LogOnly { get; private set; }
        /// <summary>Report calls since Reset.</summary>
        public int Checks { get; private set; }
        /// <summary>Checks deferred because the view was moving (one per unsettled run + every Defer).</summary>
        public int Skips { get; private set; }
        /// <summary>Largest finite dpx reported since Reset (NaN ignored).</summary>
        public double MaxDpx { get; private set; }

        /// <summary>ViewTransform.ScaleToZoomLevel.</summary>
        public static double ZoomOf(double scale)
        {
            return scale > 0 ? Math.Log(scale * 360.0 * 111319.49079327358 / 256.0, 2.0) : 0.0;
        }

        /// <summary>Is a check needed now: the first snapshot, or a changed key after PeriodS. Remembers the key when it answers true. Never after the switch to reflect.</summary>
        public bool Due(double now, in MapPxKey key)
        {
            if (!WouldBeDue(now, in key)) return false;
            _uHave = _have; _uLast = _last; _uLastAt = _lastAt; _uZoom = _zoom;
            _have = true; _last = key; _lastAt = now; _zoom = ZoomOf(key.Snap.Scale);
            return true;
        }

        private bool WouldBeDue(double now, in MapPxKey key)
        {
            if (Reflect) return false;
            if (_have)
            {
                if (now - _lastAt < PeriodS) return false;
                if (MapPx.KeyShiftPx(in _last, in key) == 0) return false;
            }
            return true;
        }

        /// <summary>Once per frame: remembers the key; true = there was a previous key, it moved at most StillPx and moving == false.</summary>
        public bool Settled(in MapPxKey key, bool moving)
        {
            bool still = _frameHave && !moving && MapPx.KeyShiftPx(in _framePrev, in key) <= StillPx;
            _frameHave = true; _framePrev = key;
            return still;
        }

        /// <summary>Settled + Due: on an unsettled view a check that would be due is not consumed and counts one skip per unsettled run.</summary>
        public bool DueSettled(double now, in MapPxKey key, bool moving)
        {
            if (!Settled(in key, moving))
            {
                if (WouldBeDue(now, in key) && !_inRun) { Skips++; _inRun = true; }
                return false;
            }
            _inRun = false;
            return Due(now, in key);
        }

        /// <summary>Undo the last Due/DueSettled that answered true (the reader saw the view move before probing).</summary>
        public void Defer()
        {
            _have = _uHave; _last = _uLast; _lastAt = _uLastAt; _zoom = _uZoom;
            Skips++;
        }

        /// <summary>The view centre and two points at 40 % of the view radius (in local units, through ViewToGeo). Returns how many probes are usable (3 normally).</summary>
        public static int Probes(in MapViewSnap s, double[] lat, double[] lon)
        {
            int n = 0;
            double cx = s.MapW / 2.0, cy = s.MapH / 2.0, r = 0.4 * Math.Min(s.MapW, s.MapH) / 2.0;
            double[] dx = { 0.0, r * 0.8660254037844386, -r * 0.8660254037844386 };
            double[] dy = { 0.0, r * 0.5, -r * 0.5 };
            for (int i = 0; i < 3 && n < lat.Length && n < lon.Length; i++)
            {
                double la, lo;
                if (!MapProjector.ViewToGeo(in s, cx + dx[i], cy + dy[i], out la, out lo)) continue;
                lat[n] = la; lon[n] = lo; n++;
            }
            return n;
        }

        /// <summary>The result of a check: the largest |ours - game| in px. NaN counts as a mismatch.</summary>
        public void Report(double now, double dpx)
        {
            LastDpx = dpx;
            Checks++;
            if (!double.IsNaN(dpx) && !double.IsInfinity(dpx) && dpx > MaxDpx) MaxDpx = dpx;
            if (double.IsNaN(dpx) || dpx > TolPx)
            {
                Mismatches++;
                _strikes++;
                if (_strikes >= Strikes)
                {
                    if (_zoom >= NoReflectZoom) LogOnly = true;
                    else Reflect = true;
                }
            }
            else { _strikes = 0; LogOnly = false; }
        }

        public void Reset()
        {
            _have = false; _lastAt = double.NegativeInfinity; _strikes = 0; Reflect = false; LogOnly = false; Mismatches = 0; LastDpx = 0;
            Checks = 0; Skips = 0; MaxDpx = 0; _frameHave = false; _inRun = false;
        }

        public string LogLine(in MapViewSnap s, double dpx)
        {
            string kind = s.Kind == MapProjKind.Affine ? "affine" : Reflect ? "reflect" : "ortho";
            return string.Format(CultureInfo.InvariantCulture, "map projector mismatch kind={0} dpx={1:0.###} zoom={2:0.##} rot={3:0.#}", kind, dpx, ZoomOf(s.Scale), s.RotationDeg);
        }
    }
}
