// T-309 / ADR-0119: pure orthographic map projector of the beta (formulas of MapControl.MapGeoMath, OrthographicProjection, MapViewport). No Unity, no game.
using System;

namespace VoiceBotBridge
{
    /// <summary>Affine = the old 4-point fit (flat maps, geo -> local units); Ortho = exact orthographic globe; Reflect = Ortho geometry, pixels asked from the game.</summary>
    internal enum MapProjKind { Affine = 0, Ortho = 1, Reflect = 2 }

    internal struct V3
    {
        public double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    /// <summary>Immutable view snapshot, built once per frame. Local units = the units of MapBase (y down). Map -> view: M * (map - mapRef) + viewRef.</summary>
    internal struct MapViewSnap
    {
        public MapProjKind Kind;
        public double Lat0, Lon0, SinLat0, CosLat0, Lon0Rad, SinLon0, CosLon0;   // projection centre (OrthoFrame)
        public double Scale, RotationDeg;                                       // ViewTransform.Scale (px/m), .Rotation (deg)
        public double M00, M01, M10, M11;                                       // map -> view: S * [[c, s], [s, -c]]
        public double MapRefX, MapRefY, ViewRefX, ViewRefY;                    // mapRef = float(R * frame(Center).xy), viewRef = LocationToView(Center)
        public double MapW, MapH;                                               // local size of the MapBase
        public Affine2 GeoToLocal;                                              // Kind = Affine only

        /// <summary>Like MapViewport.From: mapRef is the float-rounded projection of the view centre (LocationToMap returns a float Point).</summary>
        public static MapViewSnap Ortho(double projLat0, double projLon0, double scale, double rotationDeg,
                                        double centerLat, double centerLon, double viewRefX, double viewRefY, double mapW, double mapH)
        {
            var s = new MapViewSnap { Kind = MapProjKind.Ortho };
            const double rad = Math.PI / 180.0;
            s.Lat0 = projLat0; s.Lon0 = projLon0;
            s.SinLat0 = Math.Sin(projLat0 * rad); s.CosLat0 = Math.Cos(projLat0 * rad);
            s.Lon0Rad = projLon0 * rad; s.SinLon0 = Math.Sin(s.Lon0Rad); s.CosLon0 = Math.Cos(s.Lon0Rad);
            s.Scale = scale; s.RotationDeg = rotationDeg;
            double c = Math.Cos(rotationDeg * rad), sn = Math.Sin(rotationDeg * rad);
            s.M00 = c * scale; s.M01 = sn * scale; s.M10 = sn * scale; s.M11 = -c * scale;
            s.ViewRefX = viewRefX; s.ViewRefY = viewRefY;
            s.MapW = mapW; s.MapH = mapH;
            V3 f = MapProjector.GeoToFrame(in s, centerLat, centerLon);
            s.MapRefX = (float)(f.X * MapProjector.R);
            s.MapRefY = (float)(f.Y * MapProjector.R);
            return s;
        }

        public static MapViewSnap Affine(Affine2 geoToLocal, double mapW, double mapH)
        {
            return new MapViewSnap { Kind = MapProjKind.Affine, GeoToLocal = geoToLocal, MapW = mapW, MapH = mapH };
        }

        private static bool Fin(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        public bool Valid
        {
            get
            {
                if (!(MapW >= 2) || !(MapH >= 2) || !Fin(MapW) || !Fin(MapH)) return false;
                if (Kind == MapProjKind.Affine)
                    return Fin(GeoToLocal.A) && Fin(GeoToLocal.B) && Fin(GeoToLocal.C) && Fin(GeoToLocal.D) && Fin(GeoToLocal.E) && Fin(GeoToLocal.F);
                return Fin(Lat0) && Fin(Lon0) && Fin(Scale) && Scale > 0 && Fin(RotationDeg) && Fin(MapRefX) && Fin(MapRefY) && Fin(ViewRefX) && Fin(ViewRefY);
            }
        }
    }

    internal static class MapProjector
    {
        public const double R = 6378137.0;       // MapGeoMath.EquatorialRadius
        public const int MaxSteps = 256;         // MapGeoMath.MaxGreatCircleSteps
        public const double TolPx = 0.5;
        private const double Rad = Math.PI / 180.0, Deg = 180.0 / Math.PI;

        public static V3 GeoToUnit(double latDeg, double lonDeg)
        {
            double s = Math.Sin(latDeg * Rad), c = Math.Cos(latDeg * Rad), s2 = Math.Sin(lonDeg * Rad), c2 = Math.Cos(lonDeg * Rad);
            return new V3(c * c2, c * s2, s);
        }

        public static void UnitToGeo(V3 u, out double latDeg, out double lonDeg)
        {
            latDeg = Math.Atan2(u.Z, Math.Sqrt(u.X * u.X + u.Y * u.Y)) * Deg;
            lonDeg = Math.Atan2(u.Y, u.X) * Deg;
        }

        public static V3 UnitToFrame(in MapViewSnap s, V3 u)
        {
            double x = u.Y * s.CosLon0 - u.X * s.SinLon0;
            double n = u.X * s.CosLon0 + u.Y * s.SinLon0;
            return new V3(x, s.CosLat0 * u.Z - s.SinLat0 * n, s.SinLat0 * u.Z + s.CosLat0 * n);
        }

        public static V3 FrameToUnit(in MapViewSnap s, V3 v)
        {
            double z = s.CosLat0 * v.Y + s.SinLat0 * v.Z;
            double n = s.CosLat0 * v.Z - s.SinLat0 * v.Y;
            return new V3(n * s.CosLon0 - v.X * s.SinLon0, n * s.SinLon0 + v.X * s.CosLon0, z);
        }

        public static V3 GeoToFrame(in MapViewSnap s, double latDeg, double lonDeg)
        {
            double sn = Math.Sin(latDeg * Rad), c = Math.Cos(latDeg * Rad);
            double s2 = Math.Sin(lonDeg * Rad - s.Lon0Rad), c2 = Math.Cos(lonDeg * Rad - s.Lon0Rad);
            return new V3(c * s2, s.CosLat0 * sn - s.SinLat0 * c * c2, s.SinLat0 * sn + s.CosLat0 * c * c2);
        }

        /// <summary>R * v.xy (map metres) -> local view units.</summary>
        public static void FrameToView(in MapViewSnap s, V3 v, out double x, out double y)
        {
            double dx = v.X * R - s.MapRefX, dy = v.Y * R - s.MapRefY;
            x = s.M00 * dx + s.M01 * dy + s.ViewRefX;
            y = s.M10 * dx + s.M11 * dy + s.ViewRefY;
        }

        /// <summary>true = above the horizon (z &gt;= 0). Kind = Affine: the old fit, always true. x, y are set also when false.</summary>
        public static bool GeoToView(in MapViewSnap s, double latDeg, double lonDeg, out double x, out double y)
        {
            if (s.Kind == MapProjKind.Affine) { s.GeoToLocal.Apply(latDeg, lonDeg, out x, out y); return true; }
            V3 f = GeoToFrame(in s, latDeg, lonDeg);
            FrameToView(in s, f, out x, out y);
            return f.Z >= 0;
        }

        /// <summary>Inverse (M^-1: the matrix [[c, s], [s, -c]] * S is its own inverse up to 1 / S^2). |u|^2 &gt; 1 = off the disc = false.</summary>
        public static bool ViewToGeo(in MapViewSnap s, double x, double y, out double latDeg, out double lonDeg)
        {
            latDeg = 0; lonDeg = 0;
            if (s.Kind == MapProjKind.Affine)
            {
                Affine2 g = s.GeoToLocal;
                double det = g.A * g.E - g.B * g.D;
                if (!(Math.Abs(det) > 1e-300)) return false;
                double rx = x - g.C, ry = y - g.F;
                latDeg = (g.E * rx - g.B * ry) / det;
                lonDeg = (-g.D * rx + g.A * ry) / det;
                return !double.IsNaN(latDeg) && !double.IsNaN(lonDeg);
            }
            double s2 = s.Scale * s.Scale;
            double vx = x - s.ViewRefX, vy = y - s.ViewRefY;
            double mx = (s.M00 * vx + s.M01 * vy) / s2 + s.MapRefX;
            double my = (s.M10 * vx + s.M11 * vy) / s2 + s.MapRefY;
            double ux = mx / R, uy = my / R, r2 = ux * ux + uy * uy;
            if (!(r2 <= 1.0)) return false;
            V3 u = FrameToUnit(in s, new V3(ux, uy, Math.Sqrt(1.0 - r2)));
            UnitToGeo(u, out latDeg, out lonDeg);
            return true;
        }

        public static V3 Slerp(V3 a, V3 b, double t, double theta)
        {
            double sn = Math.Sin(theta);
            if (sn >= 1e-12)
            {
                double k0 = Math.Sin((1.0 - t) * theta) / sn, k1 = Math.Sin(t * theta) / sn;
                return new V3(k0 * a.X + k1 * b.X, k0 * a.Y + k1 * b.Y, k0 * a.Z + k1 * b.Z);
            }
            if (theta < 1.0) return Normalize(new V3(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), a.Z + t * (b.Z - a.Z)));
            V3 y = Math.Abs(a.Z) < 0.9 ? new V3(0, 0, 1) : new V3(1, 0, 0);
            V3 d = Normalize(Cross(a, y));
            double s = Math.Sin(theta * t), c = Math.Cos(theta * t);
            return new V3(a.X * c + d.X * s, a.Y * c + d.Y * s, a.Z * c + d.Z * s);
        }

        private static V3 Cross(V3 a, V3 b) { return new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
        private static V3 Normalize(V3 v)
        {
            double l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return l > 0 ? new V3(v.X / l, v.Y / l, v.Z / l) : v;
        }

        public static double Angle(V3 a, V3 b)
        {
            V3 c = Cross(a, b);
            return Math.Atan2(Math.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z), a.X * b.X + a.Y * b.Y + a.Z * b.Z);
        }

        /// <summary>LocalFrame.At(lat, lon) + OffsetOnSphere(bearing clockwise from north, angular distance in radians) -> unit vector.</summary>
        public static V3 OffsetOnSphere(double latDeg, double lonDeg, double bearingDeg, double angRad)
        {
            double s = Math.Sin(latDeg * Rad), c = Math.Cos(latDeg * Rad), s2 = Math.Sin(lonDeg * Rad), c2 = Math.Cos(lonDeg * Rad);
            double sb = Math.Sin(bearingDeg * Rad), cb = Math.Cos(bearingDeg * Rad);
            double sa = Math.Sin(angRad), ca = Math.Cos(angRad);
            // East = (-s2, c2, 0), North = (-s*c2, -s*s2, c), Origin = (c*c2, c*s2, s)
            double dx = -s2 * sb + (-s * c2) * cb, dy = c2 * sb + (-s * s2) * cb, dz = c * cb;
            return new V3(c * c2 * ca + dx * sa, c * s2 * ca + dy * sa, s * ca + dz * sa);
        }

        /// <summary>MapGeoMath.TryHorizonCrossing on view-frame vectors: the point of the great-circle arc a-b with z = 0.</summary>
        public static bool HorizonCrossing(V3 a, V3 b, out V3 c)
        {
            double x = b.Z * a.X - a.Z * b.X, y = b.Z * a.Y - a.Z * b.Y, z = b.Z * a.Z - a.Z * b.Z;
            if (a.Z > b.Z) { x = -x; y = -y; z = -z; }
            double l2 = x * x + y * y + z * z;
            if (!(l2 >= 1e-12)) { c = new V3(); return false; }
            double k = 1.0 / Math.Sqrt(l2);
            c = new V3(x * k, y * k, z * k);
            return true;
        }

        /// <summary>A hidden point pressed to the limb: (xy / |xy|, 0).</summary>
        public static V3 ClampToLimb(V3 frame)
        {
            double l = Math.Sqrt(frame.X * frame.X + frame.Y * frame.Y);
            if (!(l > 1e-12)) return new V3(1, 0, 0);
            return new V3(frame.X / l, frame.Y / l, 0);
        }

        /// <summary>Pieces of a great-circle of angle theta so that the chord sag is at most tolPx: theta_max = sqrt(8 tol / (R * pxPerM)); 1..256.</summary>
        public static int StepsFor(double thetaRad, double pxPerM, double tolPx = TolPx)
        {
            if (!(thetaRad > 1e-12) || !(pxPerM > 0) || double.IsInfinity(pxPerM)) return 1;
            double max = Math.Sqrt(8.0 * tolPx / (R * pxPerM));
            double n = Math.Ceiling(thetaRad / max);
            if (double.IsNaN(n) || n < 1) return 1;
            return n > MaxSteps ? MaxSteps : (int)n;
        }

        /// <summary>Pieces of an azimuth sweep of a small circle of angular radius angRadius: px radius R * sin(alpha) * S.</summary>
        public static int StepsForArc(double sweepRad, double angRadius, double pxPerM, double tolPx = TolPx)
        {
            if (!(sweepRad > 1e-12) || !(pxPerM > 0) || double.IsInfinity(pxPerM)) return 1;
            double rPx = R * Math.Abs(Math.Sin(angRadius > Math.PI ? Math.PI : angRadius)) * pxPerM;
            if (!(rPx > 0)) return 1;
            double max = Math.Sqrt(8.0 * tolPx / rPx);
            double n = Math.Ceiling(sweepRad / max);
            if (double.IsNaN(n) || n < 1) return 1;
            return n > MaxSteps ? MaxSteps : (int)n;
        }

        /// <summary>A copy of RangeRingMath.Offset (the flat offset of the Affine path): cos(lat0) floored at 0.05.</summary>
        public static void FlatOffset(double lat0, double lon0, double bearingDeg, double nm, out double lat, out double lon)
        {
            double b = bearingDeg * Math.PI / 180.0;
            double cl = Math.Cos(lat0 * Math.PI / 180.0);
            if (!(cl >= 0.05)) cl = 0.05;
            lat = lat0 + nm / 60.0 * Math.Cos(b);
            lon = lon0 + nm / 60.0 * Math.Sin(b) / cl;
        }
    }
}
