using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class IdentifyTests
    {
        public static string Store()
        {
            var s = new IdentifyStore();
            int v0 = s.Version;
            s.Record(5, IdentifyResult.None, 1);
            if (s.Get(5) != IdentifyResult.None || s.All.Count != 0 || s.Version != v0) return "none ignored";
            s.Record(5, IdentifyResult.NoLink, 10);
            if (s.Get(5) != IdentifyResult.NoLink || s.Version == v0) return "record nolink";
            int v1 = s.Version;
            s.Record(5, IdentifyResult.Answered, 20);
            if (s.Get(5) != IdentifyResult.Answered || s.Version == v1) return "nolink overwritten";
            if (s.All[5].AtS != 20) return "at";
            s.Record(5, IdentifyResult.Silent, 30);
            if (s.Get(5) != IdentifyResult.Silent) return "last wins";
            int v2 = s.Version;
            s.Record(5, IdentifyResult.Silent, 31);
            if (s.Version != v2) return "same value keeps version";
            if (s.Get(99) != IdentifyResult.None) return "unknown";
            s.Clear();
            if (s.Get(5) != IdentifyResult.None || s.All.Count != 0 || s.Version == v2) return "clear";
            return null;
        }

        private static IdentifyTemplates T()
        {
            return new IdentifyTemplates { NoLos = "Track 7: no line of contact", Ignore = "Track 7 does not respond", Refuse = "Track 7 refuses the request", Request = "Track 7: identify yourself" };
        }

        private static IdentifyResult C(bool nn, params string[] a) { return IdentifyClassify.Classify(nn, a, T()); }

        public static string Classify()
        {
            var t = T();
            if (C(true, t.Request) != IdentifyResult.Answered) return "answered";
            if (C(true) != IdentifyResult.Answered) return "answered no alerts";
            if (C(false, t.NoLos) != IdentifyResult.NoLink) return "nolink";
            if (C(false, t.Request, t.Ignore) != IdentifyResult.Silent) return "silent";
            if (C(false, t.Request, t.Refuse) != IdentifyResult.Refused) return "refused";
            if (C(false) != IdentifyResult.None) return "empty";
            if (IdentifyClassify.Classify(false, null, t) != IdentifyResult.None) return "null alerts";
            if (C(false, t.Request, "something else") != IdentifyResult.None) return "unknown second";
            if (C(false, "  TRACK 7:   No Line  of CONTACT ") != IdentifyResult.NoLink) return "nolink ws/case";
            if (C(false, "Totally localised text") != IdentifyResult.NoLink) return "single alert fallback";
            if (C(false, t.Request) != IdentifyResult.None) return "request only";
            if (C(false, "x", "y") != IdentifyResult.None) return "two unknown";
            if (IdentifyClassify.Classify(false, new List<string> { t.NoLos }, null) != IdentifyResult.NoLink) return "null templates single";
            var sub = new IdentifyTemplates { NoLos = "Track ${TrackId}: no line".Replace("${TrackId}", "42"), Ignore = "", Refuse = "", Request = "" };
            if (IdentifyClassify.Classify(false, new[] { "track 42: no line" }, sub) != IdentifyResult.NoLink) return "substituted";
            if (IdentifyClassify.Classify(false, new[] { "", "" }, sub) != IdentifyResult.None) return "empty templates must not match empty alerts";
            return null;
        }

        public static string Save()
        {
            var s = new IdentifyStore();
            s.Record(3, IdentifyResult.Refused, 100.5);
            s.Record(1, IdentifyResult.Answered, 50);
            s.Record(9, IdentifyResult.NoLink, 60);
            var d = IdentifySave.FromStore(s, 1234.5, "/saves/a \"q\".sav");
            string j = IdentifySave.ToJson(d);
            string err;
            var back = IdentifySave.Parse(j, out err);
            if (back == null) return "parse: " + err;
            if (IdentifySave.ToJson(back) != j) return "roundtrip differs";
            if (back.Items.Count != 3 || back.Items[0].Id != 1 || back.Items[1].Result != IdentifyResult.Refused || back.Items[1].AtS != 100.5) return "content";
            if (back.SavePath != "/saves/a \"q\".sav" || back.MissionTimeS != 1234.5) return "header";
            var s2 = new IdentifyStore();
            IdentifySave.ToStore(back, s2);
            if (s2.Get(9) != IdentifyResult.NoLink || s2.Get(3) != IdentifyResult.Refused) return "to store";
            // None never written
            d.Items.Add(new IdentifySaveItem { Id = 77, Result = IdentifyResult.None });
            if (IdentifySave.ToJson(d).Contains("\"id\":77")) return "none written";
            // errors
            if (IdentifySave.Parse("{bad", out err) != null || err == null) return "bad json";
            if (IdentifySave.Parse("[1]", out err) != null || err != "not_an_object") return "not object";
            if (IdentifySave.Parse(j.Replace("\"format\":1", "\"format\":9"), out err) != null || err != "unsupported_format") return "format";
            var junk = IdentifySave.Parse("{\"format\":1,\"items\":[{\"id\":1,\"result\":\"weird\"},{\"result\":\"silent\"},5]}", out err);
            if (junk == null || junk.Items.Count != 0) return "junk items skipped";
            // fresh + key
            if (!IdentifySave.Fresh(d, 1238.0) || !IdentifySave.Fresh(d, 1230.0) || IdentifySave.Fresh(d, 1240.0) || IdentifySave.Fresh(null, 0)) return "fresh";
            if (IdentifySave.Key("/A/B.sav") != AswSave.Key("/a/b.SAV") || IdentifySave.Key("/a") == IdentifySave.Key("/b")) return "key";
            return null;
        }

        public static string Text()
        {
            UiText.Reset();
            if (IdentifyText.Label(IdentifyResult.None, false) != "" || IdentifyText.Label(IdentifyResult.None, true) != "") return "none label";
            var all = new[] { IdentifyResult.Answered, IdentifyResult.Refused, IdentifyResult.Silent, IdentifyResult.NoLink, IdentifyResult.None };
            var seen = new HashSet<string>();
            foreach (var r in all)
            {
                string k = IdentifyText.Key(r);
                if (!k.StartsWith("overview.identify.") || !seen.Add(k)) return "key " + r;
            }
            UiText.LoadIni("en", "[SeaPowerTacticalUI]\noverview.identify.answered=answered\n");
            if (IdentifyText.Label(IdentifyResult.Answered, false) != "answered") return "label via UiText";
            if (IdentifyText.Label(IdentifyResult.Refused, false) != "SeaPowerTacticalUI.overview.identify.refused") return "missing key falls back to id";
            UiText.Reset();
            if (IdentifyText.Rank(IdentifyResult.None) != 0 || IdentifyText.Rank(IdentifyResult.NoLink) != 1 || IdentifyText.Rank(IdentifyResult.Silent) != 2 ||
                IdentifyText.Rank(IdentifyResult.Refused) != 3 || IdentifyText.Rank(IdentifyResult.Answered) != 4) return "rank";
            return null;
        }
    
        public static string Keeper()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "idtest-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var logs = new List<string>();
                var s = new IdentifyStore();
                var k = new IdentifyKeeper(s, dir, logs.Add);
                k.OnMissionChanged(null, 0);
                s.Record(7, IdentifyResult.Refused, 100);
                s.Record(8, IdentifyResult.NoLink, 101);
                k.OnSaveMarker("/g/save1.sav", 120);
                if (!System.IO.File.Exists(System.IO.Path.Combine(dir, IdentifySave.Key("/g/save1.sav") + ".json"))) return "file written";
                k.OnSaveMarker("/g/save1.sav", 130);   // same marker: no second write is needed
                // T-122: a new marker into the same slot keeps .bak; a broken main file is read from it
                string f1 = System.IO.Path.Combine(dir, IdentifySave.Key("/g/save1.sav") + ".json");
                k.OnSaveMarker("/g/other.sav", 125);
                k.OnSaveMarker("/g/save1.sav", 126);
                if (!System.IO.File.Exists(f1 + ".bak")) return "backup written";
                System.IO.File.WriteAllText(f1, "{oops");
                k.OnMissionChanged("/g/save1.sav", 122);
                if (s.Get(7) != IdentifyResult.Refused) return "restore from backup";
                // nothing to keep for a new marker: the slot file goes (R8)
                s.Clear();
                k.OnSaveMarker("/g/save1.sav", 140);
                k.OnSaveMarker("/g/zzz.sav", 141);
                k.OnSaveMarker("/g/save1.sav", 142);
                if (System.IO.File.Exists(f1) || System.IO.File.Exists(f1 + ".bak")) return "empty store clears the slot";
                s.Record(7, IdentifyResult.Refused, 100);
                s.Record(8, IdentifyResult.NoLink, 101);
                k.OnSaveMarker("/g/other2.sav", 119);
                k.OnSaveMarker("/g/save1.sav", 120);
                // T-130: leaving the mission empties the store
                k.OnMissionLeft();
                if (s.All.Count != 0 || k.LastSave != null) return "OnMissionLeft clears";
                s.Record(7, IdentifyResult.Refused, 100);
                s.Record(8, IdentifyResult.NoLink, 101);
                // mission change clears
                k.OnMissionChanged(null, 0);
                if (s.All.Count != 0) return "mission change clears";
                // load from the save: fresh file restores
                k.OnMissionChanged("/g/save1.sav", 121);
                if (s.Get(7) != IdentifyResult.Refused || s.Get(8) != IdentifyResult.NoLink) return "restore fresh";
                // old file (mission time far away) is ignored
                k.OnMissionChanged("/g/save1.sav", 5000);
                if (s.All.Count != 0) return "stale ignored";
                // bad file ignored
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, IdentifySave.Key("/g/bad.sav") + ".json"), "{oops");
                k.OnMissionChanged("/g/bad.sav", 1);
                if (s.All.Count != 0) return "bad ignored";
                // empty store writes nothing
                k.OnSaveMarker("/g/empty.sav", 2);
                if (System.IO.File.Exists(System.IO.Path.Combine(dir, IdentifySave.Key("/g/empty.sav") + ".json"))) return "empty not written";
                string e198 = Keeper198(dir, logs);
                if (e198 != null) return "t198: " + e198;
                return null;
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        // T-198 (ADR-0086): forced write to the same path, stamp-based restore, the time base moves, the hook flag silences the marker observer
        private static string Keeper198(string dir, List<string> logs)
        {
            var utc = new System.DateTime(2026, 10, 23, 2, 7, 37, System.DateTimeKind.Utc);
            var s = new IdentifyStore();
            var k = new IdentifyKeeper(s, dir, logs.Add);
            k.OnMissionChanged(null, 0);
            s.Record(7, IdentifyResult.Refused, 2400);
            string f = System.IO.Path.Combine(dir, IdentifySave.Key("/g/q.sav") + ".json");
            k.OnSaveMarker("/g/q.sav", 2426, SavStamp.Now(2425.6, utc, "hook"), true);
            if (!System.IO.File.Exists(f) || !System.IO.File.ReadAllText(f).Contains("\"sav\"")) return "stamped file";
            s.Record(8, IdentifyResult.NoLink, 2410);
            k.OnSaveMarker("/g/q.sav", 2430, SavStamp.Now(2429.6, utc.AddSeconds(4), "hook"), true);
            if (!System.IO.File.ReadAllText(f).Contains("2429.6")) return "forced write to the same path";
            k.OnSaveMarker("/g/q.sav", 2440);
            if (!System.IO.File.ReadAllText(f).Contains("2429.6")) return "plain marker with the same path must not write";
            // load: stamp matches while the clock restarted
            k.OnMissionChanged("/g/q.sav", 3, new SavStamp { ElapsedS = 2429.6, Created = "2026-10-23T02:07:41Z", Src = "sav" });
            if (s.Get(7) != IdentifyResult.Refused || s.Get(8) != IdentifyResult.NoLink) return "restore by stamp";
            double at = s.All[7].AtS;
            if (System.Math.Abs(at - (2400 + 3 - 2430)) > 1e-9) return "time shifted: " + at;
            // another save loaded
            k.OnMissionChanged("/g/q.sav", 3, new SavStamp { ElapsedS = 3100.2, Src = "sav" });
            if (s.All.Count != 0 || !logs.Contains("identify restore: file is stale (stamp_mismatch)")) return "stamp_mismatch";
            // hook active: the marker observer is silent
            k.SaveHookActive = true;
            s.Record(9, IdentifyResult.Silent, 1);
            k.OnSaveMarker("/g/hooked.sav", 5);
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, IdentifySave.Key("/g/hooked.sav") + ".json"))) return "marker wrote while the hook is active";
            k.OnSaveMarker("/g/hooked.sav", 5, SavStamp.Now(5, utc, "hook"), true);
            if (!System.IO.File.Exists(System.IO.Path.Combine(dir, IdentifySave.Key("/g/hooked.sav") + ".json"))) return "hook write";
            return null;
        }

        public static string Capture()
        {
            var t = new IdentifyTemplates { NoLos = "Track 5: no link", Ignore = "Track 5: no reply", Refuse = "Track 5: refuses", Request = "Track 5: identify" };
            int id; IdentifyResult r;
            if (IdentifyCapture.End(true, t, out id, out r)) return "end without begin";
            IdentifyCapture.Alert("stray");   // outside a scope: ignored
            IdentifyCapture.Begin(5);
            IdentifyCapture.Alert("Track 5: identify"); IdentifyCapture.Alert("Track 5: refuses");
            if (!IdentifyCapture.End(false, t, out id, out r) || id != 5 || r != IdentifyResult.Refused) return "refused";
            if (IdentifyCapture.Open) return "closed after end";
            IdentifyCapture.Begin(6); IdentifyCapture.Alert("Track 5: no link");
            if (!IdentifyCapture.End(false, t, out id, out r) || id != 6 || r != IdentifyResult.NoLink) return "nolink";
            // T-329: a colour tag around an alert text (message colouring) must not change the classification
            IdentifyCapture.Begin(5); IdentifyCapture.Alert("<color=red>Track 5: refuses</color>");
            if (!IdentifyCapture.End(false, t, out id, out r) || r != IdentifyResult.Refused) return "refused with a colour tag";
            IdentifyCapture.Begin(5); IdentifyCapture.Alert("<color=red>Track 5: no link</color>"); IdentifyCapture.Alert("<color=red>Track 5: no link</color>");
            if (!IdentifyCapture.End(false, t, out id, out r) || r != IdentifyResult.NoLink) return "nolink with a colour tag";
            IdentifyCapture.Begin(7);
            if (!IdentifyCapture.End(true, t, out id, out r) || r != IdentifyResult.Answered) return "answered";
            IdentifyCapture.Begin(8); IdentifyCapture.Begin(9);   // a stale scope is replaced
            if (!IdentifyCapture.End(false, t, out id, out r) || id != 9 || r != IdentifyResult.None) return "replace";
            // finalizer body: an exception in the original must not leave the scope open
            IdentifyCapture.Begin(9); IdentifyCapture.Alert("x");
            var ex = new System.InvalidOperationException("boom");
            if (!object.ReferenceEquals(IdentifyCapture.AfterRequest(ex), ex)) return "finalizer returns same exception";
            if (IdentifyCapture.Open) return "finalizer closes scope";
            IdentifyCapture.Alert("late");
            if (IdentifyCapture.End(false, t, out id, out r)) return "late alert ignored";
            if (IdentifyCapture.AfterRequest(null) != null || IdentifyCapture.Open) return "finalizer null";
            return null;
        }
}
}
