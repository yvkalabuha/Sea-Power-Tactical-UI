using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-288 stand. Usage: datumtest [section|all]. One line per case: "datum &lt;section&gt; &lt;case&gt; selftest ok|FAILED: ...".</summary>
    internal static class Program
    {
        private static KeyValuePair<string, Func<string>> C(string name, Func<string> f) { return new KeyValuePair<string, Func<string>>(name, f); }

        private static readonly List<KeyValuePair<string, Func<string>>> Cases = new List<KeyValuePair<string, Func<string>>>
        {
            C("geo roundtrip", SubDatumMathTests.GeoRoundtrip),
            C("geo worldflat", SubDatumMathTests.WorldFlat),
            C("geo sector", SubDatumMathTests.SectorShape),
            C("geo hull", SubDatumMathTests.HullConvex),
            C("geo inflate", SubDatumMathTests.InflateSquare),
            C("geo clip", SubDatumMathTests.ClipSectors),
            C("geo contains", SubDatumMathTests.ContainsCases),
            C("axis vel", SubDatumMathTests.AxisVel),
            C("axis fixes", SubDatumMathTests.AxisFixes),
            C("axis los", SubDatumMathTests.AxisLos),
            C("axis none", SubDatumMathTests.AxisNone),
            C("run identified", SubDatumMathTests.RunIdentified),
            C("run maxlaunch", SubDatumMathTests.RunMaxLaunch),
            C("run unknown", SubDatumMathTests.RunUnknown),
            C("dtgt ahead", SubDatumMathTests.DtgtAhead),
            C("dtgt behind", SubDatumMathTests.DtgtBehind),
            C("dtgt aside", SubDatumMathTests.DtgtAside),
            C("rmax clamp", SubDatumMathTests.RmaxClamp),
            C("rect core", SubDatumMathTests.RectCore),
            C("rect big", SubDatumMathTests.RectBig),
            C("rect geo", SubDatumMathTests.RectGeoOrder),
            C("pick skip", SubDatumMathTests.PickSkip),
            C("pick order", SubDatumMathTests.PickOrder),
            C("pick usable", SubDatumMathTests.PickUsable),
            C("math smoke", SubDatumMath.SelfTest),
            C("card group", SubDatumCardTests.WithGroup),
            C("card nogroup", SubDatumCardTests.NoGroup),
            C("card replace", SubDatumCardTests.Replace),
            C("card closed", SubDatumCardTests.Closed),
            C("card auto", SubDatumCardTests.Auto),
            C("book new", SubDatumBookTests.New),
            C("book worldflat", SubDatumBookTests.WorldFlat),
            C("book skip", SubDatumBookTests.Skip),
            C("book refine", SubDatumBookTests.Refine),
            C("book merge", SubDatumBookTests.Merge),
            C("book nomerge", SubDatumBookTests.NoMerge),
            C("book maxlive", SubDatumBookTests.MaxLive),
            C("book ttl", SubDatumBookTests.Ttl),
            C("book found", SubDatumBookTests.Found),
            C("book dismiss", SubDatumBookTests.Dismiss),
            C("book inflated", SubDatumBookTests.InflatedGrows),
            C("book orderrect", SubDatumBookTests.OrderRect),
            C("book json", SubDatumBookTests.JsonOut),
            C("book merge_found", SubDatumBookTests.MergeFound),
            C("book merge_prefers_live", SubDatumBookTests.MergePrefersLive),
            C("book merge_found_window", SubDatumBookTests.MergeFoundWindow),
            C("book nomerge_dismissed", SubDatumBookTests.NoMergeDismissed),
            C("book merge_recent", SubDatumBookTests.MergeRecent),
            C("book receding", SubDatumBookTests.Receding),
            C("book revision", SubDatumBookTests.Revision),
        };

        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            string a = args.Length > 0 ? args[0] : "all";
            int bad = 0, ran = 0;
            foreach (var c in Cases)
            {
                if (a != "all" && a != c.Key && !c.Key.StartsWith(a + " ", StringComparison.Ordinal)) continue;
                ran++;
                string err;
                try { err = c.Value(); }
                catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
                Console.WriteLine("datum " + c.Key + " selftest " + (err == null ? "ok" : "FAILED: " + err));
                if (err != null) bad++;
            }
            if (ran == 0) { Console.WriteLine("datum " + a + " selftest FAILED: unknown section"); return 1; }
            return bad == 0 ? 0 : 1;
        }
    }
}
