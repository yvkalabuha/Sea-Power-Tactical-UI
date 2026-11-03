// T-178 Ke1 (ADR-0077 §6.2): units with the Russian declension, one place for the windows of the mod. ru: "1 миля", "4,3 мили", "25 миль", "500 фт", "12 уз";
// en: "1 nm" (no declension). The settings [ContactInfo] Units (km) are not applied by the windows of the mod (as before).
using System;
using System.Globalization;

namespace VoiceBotBridge
{
    internal static class UnitFmt
    {
        private static bool Ru { get { return UiText.Lang == "ru"; } }
        private const string Dash = "—";

        private static bool Bad(double v) { return double.IsNaN(v) || double.IsInfinity(v); }

        /// <summary>A number in the language's notation (ru: comma).</summary>
        private static string Num(double v, int decimals)
        {
            string s = Math.Round(v, Math.Max(0, decimals)).ToString("0." + new string('#', Math.Max(0, decimals)), CultureInfo.InvariantCulture);
            if (s == "-0") s = "0";
            return Ru ? s.Replace('.', ',') : s;
        }

        public static string Nm(double v, int decimals = 1)
        {
            if (Bad(v)) return Dash;
            double r = Math.Round(v, Math.Max(0, decimals));
            bool whole = Math.Abs(r - Math.Round(r)) < 1e-9;
            string unit;
            if (!Ru) unit = UiText.Get("unit.nm.one");
            else if (whole) unit = UiText.PluralRu((long)Math.Round(r), UiText.Get("unit.nm.one"), UiText.Get("unit.nm.few"), UiText.Get("unit.nm.many"));
            else unit = UiText.Get("unit.nm.frac");
            return Num(r, decimals) + " " + unit;
        }

        public static string Ft(double v) { return Bad(v) ? Dash : Num(v, 0) + " " + UiText.Get("unit.ft"); }
        public static string Kn(double v) { return Bad(v) ? Dash : Num(v, 0) + " " + UiText.Get("unit.kn"); }
        public static string Deg(double v) { return Bad(v) ? Dash : Num(v, 0) + "°"; }

        /// <summary>"45 с", "26 мин" (rounded up), "1 ч 05 мин".</summary>
        public static string Dur(double seconds)
        {
            if (Bad(seconds) || seconds < 0) return Dash;
            int sec = (int)Math.Ceiling(seconds);   // whole seconds first: 59.5 s is 60 s = "1 min", never "60 s"
            if (sec < 60) return sec + " " + UiText.Get("unit.s");
            int m = (int)Math.Ceiling(sec / 60.0);
            if (m < 60) return m + " " + UiText.Get("unit.min");
            int mm = m % 60;
            return (m / 60) + " " + UiText.Get("unit.h") + (mm > 0 ? " " + mm.ToString("00", CultureInfo.InvariantCulture) + " " + UiText.Get("unit.min") : "");
        }

        public static string SelfTest()
        {
            if (UiText.Lang != "ru") return "UnitFmt self-test needs the ru texts (lang=" + UiText.Lang + ")";
            var cases = new[]
            {
                new[] { "0", "0 миль" }, new[] { "1", "1 миля" }, new[] { "2", "2 мили" }, new[] { "4", "4 мили" }, new[] { "5", "5 миль" },
                new[] { "11", "11 миль" }, new[] { "12", "12 миль" }, new[] { "14", "14 миль" }, new[] { "21", "21 миля" }, new[] { "22", "22 мили" },
                new[] { "25", "25 миль" }, new[] { "101", "101 миля" }, new[] { "111", "111 миль" }, new[] { "4.3", "4,3 мили" }, new[] { "1.5", "1,5 мили" },
                new[] { "0.5", "0,5 мили" }, new[] { "4.96", "5 миль" }, new[] { "21.04", "21 миля" },
            };
            foreach (var c in cases)
            {
                string got = Nm(double.Parse(c[0], CultureInfo.InvariantCulture));
                if (got != c[1]) return "ru Nm(" + c[0] + ") = '" + got + "', expected '" + c[1] + "'";
            }
            if (Nm(double.NaN) != Dash || Nm(double.PositiveInfinity) != Dash) return "Nm of a bad value is a dash";
            if (Ft(500) != "500 фт" || Kn(12.4) != "12 уз" || Deg(90) != "90°") return "ft/kn/deg: " + Ft(500) + "," + Kn(12.4) + "," + Deg(90);
            if (Dur(45) != "45 с" || Dur(1560) != "26 мин" || Dur(3900) != "1 ч 05 мин" || Dur(7200) != "2 ч" || Dur(59.5) != "1 мин" || Dur(59) != "59 с" || Dur(59.0001) != "1 мин" || Dur(3599.5) != "1 ч" || Dur(-1) != Dash) return "Dur: " + Dur(45) + "," + Dur(1560) + "," + Dur(3900) + "," + Dur(7200);
            if (UiText.PluralRu(1, "a", "b", "c") != "a" || UiText.PluralRu(112, "a", "b", "c") != "c" || UiText.PluralRu(-3, "a", "b", "c") != "b") return "PluralRu";
            using (UiText.Push("en"))
            {
                if (Nm(1) != "1 nm" || Nm(2) != "2 nm" || Nm(4.3) != "4.3 nm" || Nm(25) != "25 nm") return "en Nm has no declension: " + Nm(1) + "," + Nm(2) + "," + Nm(4.3);
                if (Ft(500) != "500 ft" || Kn(12) != "12 kn" || Dur(1560) != "26 min") return "en units: " + Ft(500) + "," + Kn(12) + "," + Dur(1560);
            }
            return null;
        }
    }
}
