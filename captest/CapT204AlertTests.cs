using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-204: the alert group after a landing - the machines cool down / prepare, the window says "preparing N", the raid card does not say "nobody ready".
    internal static class CapT204AlertTests
    {
        private const string F14 = "F-14A";

        /// <summary>The live mission of 0.30.6: level deck, Ready=3, every F-14 is a ready-up task of the game, no hangar rows, the deck recovers a landed unit for 40 s,
        /// the cool-down is 2 h. A raid sends two, the raid ends, they land.</summary>
        private static CapT179Tests.Rig PostLanding(CapT179Tests.Chk k, out List<int> flown)
        {
            var r = new CapT179Tests.Rig();
            r.G.SlotHangar = true; r.G.RemoveOnLand = true; r.G.RecoverGapS = 40; r.G.CoolS = 7200; r.G.NoHangar = true;
            while (r.G.TakeOut(F14)) { }
            var f1 = r.G.AddForeignReady(F14, "AirToAirLongRange"); var f2 = r.G.AddForeignReady(F14, "AirToAirLongRange"); var f3 = r.G.AddForeignReady(F14, "AirToAirLongRange");
            r.Create(AirOpsMath.TaskUid(f1.Key) + "," + AirOpsMath.TaskUid(f2.Key) + "," + AirOpsMath.TaskUid(f3.Key));
            r.Alert("ПВО-1", ",\"level\":\"deck\",\"ready\":3,\"per_raid\":2,\"after\":\"alert\"");
            r.Step(60);
            r.G.Contacts.Add(CapT179Tests.Raid(50, 70));
            r.Step(60);
            flown = new List<int>();
            foreach (var b in r.G.All) if (b.State == "Air" || b.State == "Taxi") flown.Add(b.Uid);
            k.Is("scrambled two " + flown.Count, flown.Count == 2);
            r.G.Contacts.Clear();
            for (int t = 0; t < 2500; t++)
            {
                r.Step(1);
                int cooling = 0; foreach (var b in r.G.All) if (b.State == "Cool") cooling++;
                if (cooling == 2) break;
            }
            r.Step(5);
            return r;
        }

        private static int Warns(CapT179Tests.Rig r, string code) { int n = 0; foreach (var e in r.G.Events) if (e.Key == "cap_warn" && e.Value.Contains("\"code\":\"" + code + "\"")) n++; return n; }

        internal static string AlertLandMission()
        {
            var k = new CapT179Tests.Chk();
            List<int> flown;
            var r = PostLanding(k, out flown);
            var v = r.View("ПВО-1");
            k.Is("no member out", !r.G.LogHas("member out"));
            k.Is("three members " + v.Members.Count, v.Members.Count == 3);
            int cool = 0; double minIn = 1e9, maxIn = 0;
            foreach (var m in v.Members) if (m.Where == "deck_cool") { cool++; minIn = Math.Min(minIn, m.ReadyInS); maxIn = Math.Max(maxIn, m.ReadyInS); }
            k.Is("two in deck_cool " + cool, cool == 2);
            k.Is("ready in about 2 h " + minIn + " " + maxIn, minIn > 7000 && maxIn <= 7200);
            k.Is("log of the landing", r.G.LogHas("landed uid=") && r.G.LogHas("-> deck 100 cool-down F-14A"));
            k.Is("PrepN " + v.PrepN, v.PrepN == 2 && v.PrepInS > 7000 && v.PrepInS <= 7200);
            k.Is("no not_ready warning", Warns(r, "alert_not_ready") == 0);
            // the second raid sends the one that is left; the third finds nobody ready but two preparing
            r.G.Contacts.Add(CapT179Tests.Raid(51, 60, 30));
            r.Step(30);
            k.Is("second raid: scramble of the ready one", r.Notes.Lines.Exists(l => l.Contains("|scramble|1|")));
            r.G.Contacts.Add(CapT179Tests.Raid(52, 60, -30));
            r.Step(30);
            k.Is("third raid: preparing, not 'nobody' " + string.Join(";", r.Notes.Lines), r.Notes.Lines.Exists(l => l.StartsWith("ПВО-1|52|preparing|2|")) && !r.Notes.Lines.Exists(l => l.Contains("|not_ready|")));
            return k.Err;
        }

        internal static string PrepView()
        {
            var k = new CapT179Tests.Chk();
            List<int> flown;
            var r = PostLanding(k, out flown);
            var v = r.View("ПВО-1");
            k.Is("PrepN " + v.PrepN, v.PrepN == 2);
            k.Is("PrepInS " + v.PrepInS, v.PrepInS > 7000 && v.PrepInS <= 7200);
            k.Is("ReadyNow counts the standing one only " + v.ReadyNow, v.ReadyNow == 1);
            var p = new CapT179Tests.Rig();
            p.Create("1"); p.Patrol("ПВО-1", 1, 0); p.Step(5);
            k.Is("a patrol has no preparing", p.View("ПВО-1").PrepN == 0 && p.View("ПВО-1").PrepInS < 0);
            return k.Err;
        }

        internal static string PrepLine()
        {
            var k = new CapT179Tests.Chk();
            List<int> flown;
            var r = PostLanding(k, out flown);
            var v = r.View("ПВО-1");
            string line = CapWindowModel.AlertLine(v);
            k.Is("line " + line, line == UiText.Fmt("cap.line.alert_ready", 1, 3) + " · " + UiText.Fmt("cap.line.alert_prep", 2, UiText.Duration(v.PrepInS)));
            k.Is("ru text", UiText.Fmt("cap.line.alert_prep", 2, "2 ч") == "готовятся 2 · ~2 ч");
            var none = new CapGroupView { Kind = "alert", Ordered = true, AlertSpec = new CapAlertSpec { Ready = 3 } };
            k.Is("no prep part without machines preparing", !CapWindowModel.AlertLine(none).Contains("готовятся"));
            return k.Err;
        }

        internal static string PrepScramble()
        {
            var k = new CapT179Tests.Chk();
            List<int> flown;
            var r = PostLanding(k, out flown);
            // both the ready one and the ready slot go: raid 51 takes the last ready machine, raid 52 finds the group out of ready machines
            r.G.Contacts.Add(CapT179Tests.Raid(51, 60, 30));
            r.Step(30);
            r.G.Contacts.Add(CapT179Tests.Raid(52, 60, -30));
            r.Step(30);
            k.Is("preparing line " + string.Join(";", r.Notes.Lines), r.Notes.Lines.Exists(l => l.StartsWith("ПВО-1|52|preparing|2|")));
            k.Is("warning stays in the log", Warns(r, "alert_not_ready") >= 1);
            k.Is("no empty 'nobody' line", !r.Notes.Lines.Exists(l => l.Contains("|not_ready|")));
            return k.Err;
        }
    }
}
