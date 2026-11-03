// Reference: a literal transcription of the beta formulas (MapControl.MapGeoMath, OrthographicProjection, MapViewport, ViewTransform + Noesis.Matrix in float).
// Independent of MapProjector.cs on purpose.
using System;

namespace VoiceBotBridge.ProjTest
{
    internal struct D3
    {
        public double X, Y, Z;
        public D3(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    internal static class GameRef
    {
        public const double R = 6378137.0;
        public const double MeterPerDegree = 111319.49079327358;

        public struct OrthoFrame { public double SinLat0, CosLat0, Lon0Rad, SinLon0, CosLon0; }

        private static double Radians(double d) { return d * 0.017453292519943295; }
        private static double Degrees(double r) { return r * 57.295779513082323; }

        public static OrthoFrame FromCenter(double latDeg, double lonDeg)
        {
            double lat = Radians(latDeg), lon = Radians(lonDeg);
            return new OrthoFrame { SinLat0 = Math.Sin(lat), CosLat0 = Math.Cos(lat), Lon0Rad = lon, SinLon0 = Math.Sin(lon), CosLon0 = Math.Cos(lon) };
        }

        public static D3 GeoToViewFrame(double latDeg, double lonDeg, OrthoFrame f)
        {
            double s = Math.Sin(Radians(latDeg)), c = Math.Cos(Radians(latDeg));
            double s2 = Math.Sin(Radians(lonDeg) - f.Lon0Rad), c2 = Math.Cos(Radians(lonDeg) - f.Lon0Rad);
            return new D3(c * s2, f.CosLat0 * s - f.SinLat0 * c * c2, f.SinLat0 * s + f.CosLat0 * c * c2);
        }

        public static D3 UnitToViewFrame(D3 u, OrthoFrame f)
        {
            double x = u.Y * f.CosLon0 - u.X * f.SinLon0;
            double n = u.X * f.CosLon0 + u.Y * f.SinLon0;
            return new D3(x, f.CosLat0 * u.Z - f.SinLat0 * n, f.SinLat0 * u.Z + f.CosLat0 * n);
        }

        public static D3 ViewFrameToUnit(D3 v, OrthoFrame f)
        {
            double z = f.CosLat0 * v.Y + f.SinLat0 * v.Z;
            double n = f.CosLat0 * v.Z - f.SinLat0 * v.Y;
            return new D3(n * f.CosLon0 - v.X * f.SinLon0, n * f.SinLon0 + v.X * f.CosLon0, z);
        }

        public static D3 GeoToUnit(double latDeg, double lonDeg)
        {
            double s = Math.Sin(Radians(latDeg)), c = Math.Cos(Radians(latDeg)), s2 = Math.Sin(Radians(lonDeg)), c2 = Math.Cos(Radians(lonDeg));
            return new D3(c * c2, c * s2, s);
        }

        public static void UnitToGeo(D3 v, out double latDeg, out double lonDeg)
        {
            latDeg = Degrees(Math.Atan2(v.Z, Math.Sqrt(v.X * v.X + v.Y * v.Y)));
            lonDeg = Degrees(Math.Atan2(v.Y, v.X));
        }

        /// <summary>OrthographicProjection.Inverse (map metres -> geo); false = off disc (clamped to the limb).</summary>
        public static bool Inverse(double mapX, double mapY, OrthoFrame f, out double lat, out double lon)
        {
            double a = mapX / R, b = mapY / R, r2 = a * a + b * b;
            bool on = r2 <= 1.0;
            if (!on) { double k = 0.999999999 / Math.Sqrt(r2); a *= k; b *= k; r2 = 0.9999999980000001; }
            D3 v2 = ViewFrameToUnit(new D3(a, b, Math.Sqrt(Math.Max(0.0, 1.0 - r2))), f);
            UnitToGeo(v2, out lat, out lon);
            return on;
        }

        public static double SphericalDistanceM(double lat0, double lon0, double lat1, double lon1)
        {
            D3 a = GeoToUnit(lat0, lon0), b = GeoToUnit(lat1, lon1);
            double cx = a.Y * b.Z - a.Z * b.Y, cy = a.Z * b.X - a.X * b.Z, cz = a.X * b.Y - a.Y * b.X;
            return R * Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), a.X * b.X + a.Y * b.Y + a.Z * b.Z);
        }

        public static double ZoomLevelToScale(float zoom) { return 256.0 * Math.Pow(2.0, zoom) / (Math.PI * 12756274.0); }
        public static float ScaleToZoomLevel(double scale) { return (float)Math.Log(scale * 360.0 * MeterPerDegree / 256.0, 2.0); }

        /// <summary>ViewTransform.SetTransform with Noesis.Matrix in float (Rotate in degrees, appended: M * Rot).</summary>
        public sealed class FloatView
        {
            public float M11, M12, M21, M22, OffX, OffY;
            public double Scale, Rotation;
            public static FloatView Set(float mapCx, float mapCy, float viewCx, float viewCy, double scale, double rotation)
            {
                var v = new FloatView { Scale = scale, Rotation = rotation };
                float offX = 0f - (float)(scale * mapCx), offY = (float)(scale * mapCy);
                float m11 = (float)scale, m12 = 0f, m21 = 0f, m22 = 0f - (float)scale;
                float ang = (float)(rotation * Math.PI / 180.0);
                float c = (float)Math.Cos(ang), s = (float)Math.Sin(ang);
                float n11 = m11 * c - m12 * s, n12 = m11 * s + m12 * c, n21 = m21 * c - m22 * s, n22 = m21 * s + m22 * c;
                float nox = offX * c - offY * s, noy = offX * s + offY * c;
                v.M11 = n11; v.M12 = n12; v.M21 = n21; v.M22 = n22; v.OffX = nox + viewCx; v.OffY = noy + viewCy;
                return v;
            }
            public void Transform(float x, float y, out float ox, out float oy)
            {
                ox = x * M11 + y * M21 + OffX;
                oy = x * M12 + y * M22 + OffY;
            }
        }

        /// <summary>A whole reference view: the float ViewTransform of the game and the double MapViewport of the ECS.</summary>
        public sealed class View
        {
            public double ProjLat, ProjLon, CenterLat, CenterLon, Scale, Rot;
            public OrthoFrame Frame;
            public FloatView Fv;
            public float MapRefX, MapRefY, ViewRefX, ViewRefY;   // MapViewport.MapRefPoint / ViewRefPoint (floats widened)
            public double M00, M01, M10, M11;

            public static View Make(double projLat, double projLon, double centerLat, double centerLon, double scale, double rotDeg, float viewCx, float viewCy)
            {
                var v = new View { ProjLat = projLat, ProjLon = projLon, CenterLat = centerLat, CenterLon = centerLon, Scale = scale, Rot = rotDeg };
                v.Frame = FromCenter(projLat, projLon);
                float mx, my;
                v.MapF(centerLat, centerLon, out mx, out my);
                v.Fv = FloatView.Set(mx, my, viewCx, viewCy, scale, rotDeg);
                float vx, vy;
                v.Fv.Transform(mx, my, out vx, out vy);
                v.MapRefX = mx; v.MapRefY = my; v.ViewRefX = vx; v.ViewRefY = vy;
                double c = Math.Cos(Radians(rotDeg)), s = Math.Sin(Radians(rotDeg));
                v.M00 = c * scale; v.M01 = s * scale; v.M10 = s * scale; v.M11 = -c * scale;
                return v;
            }

            /// <summary>LocationToMap: a float Point.</summary>
            public void MapF(double lat, double lon, out float x, out float y)
            {
                D3 f = GeoToViewFrame(lat, lon, Frame);
                x = (float)(f.X * R); y = (float)(f.Y * R);
            }

            /// <summary>MapBase.LocationToView (float).</summary>
            public void LocationToView(double lat, double lon, out float x, out float y, out bool visible)
            {
                float mx, my;
                MapF(lat, lon, out mx, out my);
                Fv.Transform(mx, my, out x, out y);
                visible = GeoToViewFrame(lat, lon, Frame).Z >= 0;
            }

            /// <summary>The ECS route: double map point through MapViewport (double2x2).</summary>
            public void EcsView(double lat, double lon, out double x, out double y, out bool visible)
            {
                D3 f = GeoToViewFrame(lat, lon, Frame);
                double dx = f.X * R - MapRefX, dy = f.Y * R - MapRefY;
                x = M00 * dx + M01 * dy + ViewRefX;
                y = M10 * dx + M11 * dy + ViewRefY;
                visible = f.Z >= 0;
            }

            /// <summary>ViewToMap (double inverse) then OrthographicProjection.Inverse.</summary>
            public bool EcsInverse(double x, double y, out double lat, out double lon)
            {
                double s2 = Scale * Scale;
                double vx = x - ViewRefX, vy = y - ViewRefY;
                double mx = (M00 * vx + M01 * vy) / s2 + MapRefX, my = (M10 * vx + M11 * vy) / s2 + MapRefY;
                return Inverse(mx, my, Frame, out lat, out lon);
            }
        }
    }
}
