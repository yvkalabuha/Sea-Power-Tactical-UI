// T-309 / ADR-0119: reads the live MapBase into a MapViewSnap (once per frame) and checks the projector against the game. Game layer: reflection through GameApi.
using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed class MapSnapReader
    {
        private readonly GameApi _api;
        private readonly Action<string> _log;
        private readonly MapProjectorCheck _check = new MapProjectorCheck();
        private readonly double[] _pLat = new double[3], _pLon = new double[3];
        private readonly object[] _viewArgs = new object[1];   // LocationToView(GeoPosition), once per frame
        private int _frame = -1; private object _map; private bool _ok; private MapViewSnap _snap;
        private string _mode = "affine";
        private GeoToLocalFn _reflectFn; private object _reflectMap; private readonly object[] _reflectArgs = new object[2];
        private bool _loggedReflect;

        public MapSnapReader(GameApi api, Action<string> log) { _api = api; _log = log ?? delegate { }; }

        /// <summary>true = the slow exact mode is on (the formulas disagreed with the game three checks in a row).</summary>
        public bool Reflect { get { return _check.Reflect; } }

        private static bool IsOrtho(object proj, string crs)
        {
            if (crs == "EPSG:9840") return true;
            for (Type t = proj == null ? null : proj.GetType(); t != null; t = t.BaseType)
                if (t.FullName == "MapControl.OrthographicProjection") return true;
            return false;
        }

        /// <summary>The snapshot of this frame (the same answer for the same map and frame). Ortho map: the exact snapshot; else the affine fallback (null = false).</summary>
        public bool Read(object mapBase, int frame, Func<Affine2?> affineFallback, out MapViewSnap snap)
        {
            if (mapBase != null && frame == _frame && ReferenceEquals(mapBase, _map)) { snap = _snap; return _ok; }
            _frame = frame; _map = mapBase; _ok = false; _snap = default(MapViewSnap);
            snap = _snap;
            if (mapBase == null) return false;
            try
            {
                double w = _api.D2(_api.Get(mapBase, RadarSpecs.ElementWidth)), h = _api.D2(_api.Get(mapBase, RadarSpecs.ElementHeight));
                if (ReadOrtho(mapBase, w, h, out _snap)) { _ok = true; _mode = _check.Reflect ? "reflect" : "ortho"; }
                else
                {
                    Affine2? a = affineFallback == null ? null : affineFallback();
                    if (a.HasValue) { _snap = MapViewSnap.Affine(a.Value, w, h); _ok = _snap.Valid; }
                    _mode = "affine";
                }
            }
            catch { _ok = false; }
            snap = _snap;
            return _ok;
        }

        private bool ReadOrtho(object map, double w, double h, out MapViewSnap s)
        {
            s = default(MapViewSnap);
            object proj = _api.Get(map, MapProjectorSpecs.MapProjection), vt = _api.Get(map, MapProjectorSpecs.ViewTransform);
            if (proj == null || vt == null) return false;
            string crs = _api.Get(proj, MapProjectorSpecs.CrsId) as string;
            if (!IsOrtho(proj, crs)) return false;
            object pc = _api.Get(proj, MapProjectorSpecs.ProjectionCenter), c = _api.Get(map, MapProjectorSpecs.Center);
            if (pc == null || c == null) return false;
            double pLat = _api.D2(_api.Get(pc, MapProjectorSpecs.LocLat)), pLon = _api.D2(_api.Get(pc, MapProjectorSpecs.LocLon));
            double cLat = _api.D2(_api.Get(c, MapProjectorSpecs.LocLat)), cLon = _api.D2(_api.Get(c, MapProjectorSpecs.LocLon));
            double scale = _api.D2(_api.Get(vt, MapProjectorSpecs.Scale)), rot = _api.D2(_api.Get(vt, MapProjectorSpecs.Rotation));
            MethodInfo l2v = _api.Member(CoreSpecs.LocationToView) as MethodInfo;
            if (l2v == null) return false;
            _viewArgs[0] = _api.GeoObj(cLat, cLon);
            object vp = l2v.Invoke(map, _viewArgs);
            if (vp == null) return false;
            double vx = _api.D2(_api.Get(vp, RadialSpecs.PointX)), vy = _api.D2(_api.Get(vp, RadialSpecs.PointY));
            s = MapViewSnap.Ortho(pLat, pLon, scale, rot, cLat, cLon, vx, vy, w, h);
            return s.Valid;
        }

        /// <summary>The game's own placement of a point in local map units (MapBase.TryLocationToView); for MapPx.Reflect. null when the member is missing.</summary>
        public GeoToLocalFn ReflectFn(object mapBase)
        {
            if (mapBase == null) return null;
            if (_reflectFn != null && ReferenceEquals(_reflectMap, mapBase)) return _reflectFn;
            MethodInfo m = _api.Member(MapProjectorSpecs.TryLocationToView) as MethodInfo;
            if (m == null) return null;
            _reflectMap = mapBase;
            _reflectFn = delegate (double lat, double lon, out double x, out double y)
            {
                return Ask(m, mapBase, lat, lon, out x, out y);
            };
            return _reflectFn;
        }

        private bool Ask(MethodInfo m, object map, double lat, double lon, out double x, out double y)
        {
            x = 0; y = 0;
            try
            {
                _reflectArgs[0] = _api.GeoObj(lat, lon); _reflectArgs[1] = null;
                object ok = m.Invoke(map, _reflectArgs);
                object pt = _reflectArgs[1];
                if (!(ok is bool) || !(bool)ok || pt == null) return false;
                x = _api.D2(_api.Get(pt, RadialSpecs.PointX)); y = _api.D2(_api.Get(pt, RadialSpecs.PointY));
                return !double.IsNaN(x) && !double.IsNaN(y);
            }
            catch { return false; }
        }

        /// <summary>The check (MapProjectorCheck policy: first snapshot, then at most every 2 s on a changed key): three probes through TryLocationToView.</summary>
        public void Verify(object mapBase, in MapViewSnap snap, double now)
        {
            if (mapBase == null || snap.Kind != MapProjKind.Ortho) return;
            var key = new MapPxKey { Kind = MapProjKind.Ortho, Snap = snap, Out0 = 1, Out4 = 1, OutW = (int)snap.MapW, OutH = (int)snap.MapH };
            if (!_check.DueSettled(now, in key, false)) return;
            MethodInfo m = _api.Member(MapProjectorSpecs.TryLocationToView) as MethodInfo;
            if (m == null) return;
            // T-312: the cached snapshot may be a zoom-animation step behind the game; re-read right before probing and postpone if the view moved
            MapViewSnap fresh;
            if (!ReadOrtho(mapBase, snap.MapW, snap.MapH, out fresh)) { _check.Defer(); return; }
            var freshKey = new MapPxKey { Kind = MapProjKind.Ortho, Snap = fresh, Out0 = 1, Out4 = 1, OutW = (int)fresh.MapW, OutH = (int)fresh.MapH };
            if (MapPx.KeyShiftPx(in key, in freshKey) > MapProjectorCheck.StillPx) { _check.Defer(); return; }
            int n = MapProjectorCheck.Probes(in fresh, _pLat, _pLon);
            double worst = 0; int used = 0;
            for (int i = 0; i < n; i++)
            {
                double gx, gy, ox, oy;
                if (!Ask(m, mapBase, _pLat[i], _pLon[i], out gx, out gy)) continue;
                if (!MapProjector.GeoToView(in fresh, _pLat[i], _pLon[i], out ox, out oy)) continue;
                double d = Math.Sqrt((ox - gx) * (ox - gx) + (oy - gy) * (oy - gy));
                if (double.IsNaN(d)) d = double.PositiveInfinity;
                if (d > worst) worst = d;
                used++;
            }
            if (used == 0) return;
            bool wasReflect = _check.Reflect;
            _check.Report(now, worst);
            if (worst > MapProjectorCheck.TolPx || double.IsNaN(worst))
            {
                try { _log(_check.LogLine(in fresh, worst) + (_check.LogOnly ? " (log only)" : "")); } catch { }
            }
            if (!wasReflect && _check.Reflect && !_loggedReflect)
            {
                _loggedReflect = true;
                try { _log("map projector: switching to reflect (TryLocationToView per vertex)"); } catch { }
            }
        }

        /// <summary>",\"projector\":\"ortho|affine|reflect\",\"projector_dpx\":..,\"projector_mismatch\":.." for CoverageLayer.DiagJson (leading comma).</summary>
        public string DiagJson()
        {
            var sb = new StringBuilder(160);
            sb.Append(",\"projector\":\"").Append(_check.Reflect ? "reflect" : _mode).Append('"');
            sb.Append(",\"projector_dpx\":").Append(Num(_check.LastDpx));
            sb.Append(",\"projector_mismatch\":").Append(_check.Mismatches.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"projector_checks\":").Append(_check.Checks.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"projector_skips\":").Append(_check.Skips.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"projector_dpx_max\":").Append(Num(_check.MaxDpx));
            return sb.ToString();
        }

        private static string Num(double v)
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? "-1" : v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>The MapBase changed: forget the checks (Reflect included) and the frame cache.</summary>
        public void OnMapChanged(object mapBase)
        {
            _check.Reset(); _map = null; _frame = -1; _reflectFn = null; _reflectMap = null; _loggedReflect = false;
        }
    }
}
