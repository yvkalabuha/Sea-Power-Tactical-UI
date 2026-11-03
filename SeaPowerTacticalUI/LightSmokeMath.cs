using System;
using System.Globalization;

namespace VoiceBotBridge.Ui
{
    // T-165 / ADR-0062: pure math of light smoke (no Unity, no reflection, no game).

    /// <summary>= UnityEngine.ParticleSystemCurveMode (same numeric values).</summary>
    internal enum LsCurveMode { Constant = 0, Curve = 1, TwoCurves = 2, TwoConstants = 3 }

    /// <summary>Full = emission, bursts, max particles and lifetime; KeepLifetime = the same but the lifetime stays (chaff clouds).</summary>
    internal enum LsProfile { Full, KeepLifetime }

    /// <summary>Where the system came from: spawnEffect, direct pool spawn (launcher smoke, dust, barrel smoke), chaff round.</summary>
    internal enum LsSource { Spawn, Direct, Chaff }

    /// <summary>Burst.count of one burst (MinMaxCurve): Constant / Min / Max for the constant modes, CurveMultiplier for the curve modes.</summary>
    internal struct LightSmokeBurst
    {
        public LsCurveMode Mode;
        public float Constant, Min, Max, CurveMultiplier;
    }

    /// <summary>The values of one particle system that light smoke changes. Bursts may be empty, never null.</summary>
    internal sealed class LightSmokeValues
    {
        public float RateOverTime, RateOverDistance;   // emission.rateOverTimeMultiplier / rateOverDistanceMultiplier
        public int MaxParticles;                        // main.maxParticles
        public float StartLifetime;                     // main.startLifetimeMultiplier
        public LightSmokeBurst[] Bursts = new LightSmokeBurst[0];

        public LightSmokeValues Clone()
        {
            var c = (LightSmokeValues)MemberwiseClone();
            c.Bursts = Bursts == null ? new LightSmokeBurst[0] : (LightSmokeBurst[])Bursts.Clone();
            return c;
        }

        public bool SameAs(LightSmokeValues o)
        {
            if (o == null) return false;
            if (RateOverTime != o.RateOverTime || RateOverDistance != o.RateOverDistance || MaxParticles != o.MaxParticles || StartLifetime != o.StartLifetime) return false;
            int n = Bursts == null ? 0 : Bursts.Length, m = o.Bursts == null ? 0 : o.Bursts.Length;
            if (n != m) return false;
            for (int i = 0; i < n; i++)
            {
                var a = Bursts[i]; var b = o.Bursts[i];
                if (a.Mode != b.Mode || a.Constant != b.Constant || a.Min != b.Min || a.Max != b.Max || a.CurveMultiplier != b.CurveMultiplier) return false;
            }
            return true;
        }
    }

    internal static class LightSmokeMath
    {
        public const double MinK = 0.2, MaxK = 1.0, DefaultK = 0.35;
        public static readonly string[] FactorOptions = { "0.2", "0.35", "0.5" };

        /// <summary>NaN/inf -> DefaultK (clamped=true); outside [MinK; MaxK] -> nearest bound (clamped=true).</summary>
        public static double ClampK(double k, out bool clamped)
        {
            clamped = false;
            if (double.IsNaN(k) || double.IsInfinity(k)) { clamped = true; return DefaultK; }
            if (k < MinK) { clamped = true; return MinK; }
            if (k > MaxK) { clamped = true; return MaxK; }
            return k;
        }

        /// <summary>Invariant culture; false on null/garbage (the caller keeps DefaultK).</summary>
        public static bool TryParseK(string s, out double k)
        {
            k = DefaultK;
            if (string.IsNullOrEmpty(s)) return false;
            double v;
            if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
            k = v;
            return true;
        }

        /// <summary>ceil(v*k - 1e-6), at least 1; v &lt;= 0 stays as it is.</summary>
        public static float ScaleCount(float v, double k)
        {
            if (!(v > 0)) return v;
            double r = Math.Ceiling(v * k - 1e-6);
            return (float)Math.Max(1.0, r);
        }

        public static int ScaleMax(int m, double k)
        {
            if (m <= 0) return m;
            double r = Math.Ceiling(m * k - 1e-6);
            return (int)Math.Max(1.0, r);
        }

        /// <summary>New object; original untouched. Rates x k, MaxParticles ScaleMax, StartLifetime x sqrt(k) (Full only);
        /// bursts: Constant -> ScaleCount(Constant), TwoConstants -> ScaleCount(Min), ScaleCount(Max), Curve/TwoCurves -> CurveMultiplier x k.
        /// k is clamped first.</summary>
        public static LightSmokeValues Scale(LightSmokeValues original, double k, LsProfile profile = LsProfile.Full)
        {
            bool c;
            k = ClampK(k, out c);
            var r = original.Clone();
            r.RateOverTime = (float)(original.RateOverTime * k);
            r.RateOverDistance = (float)(original.RateOverDistance * k);
            r.MaxParticles = ScaleMax(original.MaxParticles, k);
            if (profile == LsProfile.Full) r.StartLifetime = (float)(original.StartLifetime * Math.Sqrt(k));
            for (int i = 0; i < r.Bursts.Length; i++)
            {
                var b = r.Bursts[i];
                switch (b.Mode)
                {
                    case LsCurveMode.Constant: b.Constant = ScaleCount(b.Constant, k); break;
                    case LsCurveMode.TwoConstants: b.Min = ScaleCount(b.Min, k); b.Max = ScaleCount(b.Max, k); break;
                    default: b.CurveMultiplier = (float)(b.CurveMultiplier * k); break;
                }
                r.Bursts[i] = b;
            }
            return r;
        }

        public static string SelfTest()
        {
            if (ScaleCount(10, 0.35) != 4) return "ScaleCount(10,0.35)=" + ScaleCount(10, 0.35);
            if (ScaleCount(20, 0.35) != 7) return "ScaleCount(20,0.35)=" + ScaleCount(20, 0.35);
            if (ScaleCount(1, 0.2) != 1) return "ScaleCount(1,0.2)";
            if (ScaleCount(0, 0.35) != 0) return "ScaleCount(0,0.35)";
            if (ScaleMax(1000, 0.35) != 350) return "ScaleMax(1000,0.35)=" + ScaleMax(1000, 0.35);
            if (ScaleMax(1, 0.2) != 1) return "ScaleMax(1,0.2)";
            var o = new LightSmokeValues
            {
                RateOverTime = 100, RateOverDistance = 10, MaxParticles = 1000, StartLifetime = 4,
                Bursts = new[]
                {
                    new LightSmokeBurst { Mode = LsCurveMode.Constant, Constant = 20 },
                    new LightSmokeBurst { Mode = LsCurveMode.TwoConstants, Min = 0, Max = 10 },
                    new LightSmokeBurst { Mode = LsCurveMode.Curve, CurveMultiplier = 8 },
                    new LightSmokeBurst { Mode = LsCurveMode.TwoCurves, CurveMultiplier = 6 },
                },
            };
            var snap = o.Clone();
            var s = Scale(o, 0.5);
            if (!o.SameAs(snap)) return "Scale changed the original";
            if (Math.Abs(s.RateOverTime - 50) > 1e-4 || Math.Abs(s.RateOverDistance - 5) > 1e-4) return "rates";
            if (s.MaxParticles != 500) return "max " + s.MaxParticles;
            if (Math.Abs(s.StartLifetime - 4 * Math.Sqrt(0.5)) > 1e-4) return "lifetime " + s.StartLifetime;
            if (s.Bursts[0].Constant != 10) return "burst constant " + s.Bursts[0].Constant;
            if (s.Bursts[1].Min != 0 || s.Bursts[1].Max != 5) return "burst two constants " + s.Bursts[1].Min + "/" + s.Bursts[1].Max;
            if (Math.Abs(s.Bursts[2].CurveMultiplier - 4) > 1e-4 || Math.Abs(s.Bursts[3].CurveMultiplier - 3) > 1e-4) return "burst curves";
            var kl = Scale(o, 0.5, LsProfile.KeepLifetime);
            if (kl.StartLifetime != 4) return "KeepLifetime changed the lifetime";
            if (kl.MaxParticles != 500 || kl.Bursts[0].Constant != 10) return "KeepLifetime changed the rest";
            if (Scale(o, 0.05).MaxParticles != 200) return "Scale clamps k";
            bool c;
            if (ClampK(0.05, out c) != 0.2 || !c) return "ClampK low";
            if (ClampK(1.5, out c) != 1 || !c) return "ClampK high";
            if (ClampK(double.NaN, out c) != 0.35 || !c) return "ClampK NaN";
            if (ClampK(0.35, out c) != 0.35 || c) return "ClampK ok";
            double k;
            if (!TryParseK("0.35", out k) || k != 0.35) return "TryParseK 0.35";
            if (TryParseK("x", out k) || k != DefaultK) return "TryParseK garbage";
            if (TryParseK(null, out k)) return "TryParseK null";
            return null;
        }
    }
}
