using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class AirLossTests
    {
        // ---------------- tracker on a fake game ----------------
        private sealed class FakeLossGame : IAirLossGame
        {
            public double Now { get; set; } = 1000;
            public double RealNow { get { return 5000 + Now; } }
            public List<AirOwn> Owns = new List<AirOwn>();
            public Dictionary<int, AirLossFacts> FactsOf = new Dictionary<int, AirLossFacts>();
            public Dictionary<int, int> AboardOf = new Dictionary<int, int>();
            public List<string> Events = new List<string>(), Logs = new List<string>();
            public IList<AirOwn> OwnAirborne() { return Owns; }
            public AirLossFacts Facts(int uid) { AirLossFacts f; return FactsOf.TryGetValue(uid, out f) ? f : new AirLossFacts(); }
            public int Aboard(int uid) { int a; return AboardOf.TryGetValue(uid, out a) ? a : 0; }
            public IList<string> NearHostiles(int uid, int max) { return new List<string> { "MiG-21bis@12.5", "MiG-21bis@13.1" }; }
            public string Roe(int uid) { return "tight"; }
            public void Event(string name, string json) { Events.Add(name + " " + json); }
            public void Log(string line) { Logs.Add(line); }
        }

        private sealed class FakeX1 : IAirTimeX1
        {
            public List<string> Offers = new List<string>();
            public void Offer(string kind, string key, string reason) { Offers.Add(kind + "|" + key + "|" + reason); }
        }

        private static AirOwn Bird(int uid, string name, string type, string grp = null)
        {
            return new AirOwn { Uid = uid, Name = name, TypeLabel = type, Window = grp == null ? null : "asw", Group = grp, Lat = 12.8, Lon = 43.28, SpeedKn = 220, AltM = 300 };
        }

        public static string Tracker(string only)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("loss") && (e = Loss()) != null) return "loss: " + e;
            if (want("landing") && (e = Landing()) != null) return "landing: " + e;
            if (want("alive") && (e = Alive()) != null) return "alive: " + e;
            if (want("causes") && (e = Causes()) != null) return "causes: " + e;
            return null;
        }

        private static string Loss()
        {
            var g = new FakeLossGame(); var x1 = new FakeX1();
            var t = new AirLossTracker(g) { TimeX1 = x1 };
            var lost = new List<AirLossInfo>(); t.Lost = i => lost.Add(i);
            g.Owns.Add(Bird(1, "Mad Foxes", "P-3C", "ASW-3")); g.Owns.Add(Bird(2, "Rescue", "SH-3H", "SAR-4"));
            g.AboardOf[2] = 16;
            t.Tick(g.Now);
            // the P-3C is hit by an AA-2 of a MiG-21bis and vanishes (destroyed)
            t.OnHit(1, "AA-2", "MiG-21bis", g.Now + 3);
            g.FactsOf[1] = new AirLossFacts { Destroyed = true, AltM = 250, FuelFrac = 0.6 };
            g.Owns.RemoveAll(o => o.Uid == 1);
            g.Now += 5; t.Tick(g.Now);
            if (lost.Count != 1 || lost[0].Uid != 1 || lost[0].Cause != "shot_down" || lost[0].Weapon != "AA-2" || lost[0].ByClass != "MiG-21bis" || lost[0].Group != "ASW-3") return "info " + (lost.Count > 0 ? lost[0].Cause : "none");
            if (g.Logs.Count != 1 || !g.Logs[0].StartsWith("[Loss] uid=1 name=Mad Foxes grp=ASW-3 cause=shot_down weapon=AA-2 by=MiG-21bis pos=12.8000,43.2800 alt_m=250 kn=220 mt=1005 rt=6005 hostiles=MiG-21bis@12.5,MiG-21bis@13.1 roe=tight")) return "journal: " + string.Join("|", g.Logs);
            if (g.Events.Count != 1 || !g.Events[0].StartsWith("air_lost ") || !g.Events[0].Contains("\"cause\":\"shot_down\"") || !g.Events[0].Contains("\"weapon\":\"AA-2\"") || !g.Events[0].Contains("\"by_class\":\"MiG-21bis\"")) return "event: " + string.Join("|", g.Events);
            if (x1.Offers.Count != 1 || !x1.Offers[0].StartsWith("loss|loss:1|")) return "x1 offer: " + string.Join("|", x1.Offers);
            // one card per bird: further ticks do not report it again
            for (int i = 0; i < 5; i++) { g.Now += 1; t.Tick(g.Now); }
            if (lost.Count != 1) return "reported twice";
            // the SAR helicopter with 16 aboard is shot down by a missile that was aimed at it (no hit registered)
            g.FactsOf[2] = new AirLossFacts { Destroyed = true, MissileGoneAgoS = 2, AltM = 120 };
            g.Owns.RemoveAll(o => o.Uid == 2);
            g.Now += 1; t.Tick(g.Now);
            if (lost.Count != 2 || lost[1].Cause != "shot_down" || lost[1].Weapon != null || lost[1].Aboard != 16) return "missile gone, 16 aboard";
            if (!g.Logs[1].Contains("aboard=16") || !g.Events[1].Contains("\"aboard\":16")) return "aboard in journal and event";
            return null;
        }

        private static string Landing()
        {
            var g = new FakeLossGame(); var x1 = new FakeX1();
            var t = new AirLossTracker(g) { TimeX1 = x1 };
            int n = 0; t.Lost = i => n++;
            g.Owns.Add(Bird(1, "Hawk", "E-2C"));
            t.Tick(g.Now);
            g.FactsOf[1] = new AirLossFacts { Landed = true, Alive = false };   // DeSpawn: _spawnTime = MaxValue
            g.Owns.Clear();
            g.Now += 1; t.Tick(g.Now);
            if (n != 0 || g.Logs.Count != 0 || g.Events.Count != 0 || x1.Offers.Count != 0) return "a landing is no loss";
            // a landing right after a hit (the hit was not fatal)
            t.OnHit(1, "AA-2", "MiG", g.Now);
            g.Owns.Add(Bird(1, "Hawk", "E-2C")); t.Tick(g.Now + 1);
            g.Owns.Clear(); g.Now += 2; t.Tick(g.Now);
            if (n != 0) return "landing after a hit";
            return null;
        }

        // a bird in the landing circuit leaves the "in flight" list but exists: not a loss, looked at again
        private static string Alive()
        {
            var g = new FakeLossGame();
            var t = new AirLossTracker(g);
            int n = 0; t.Lost = i => n++;
            g.Owns.Add(Bird(1, "Hawk", "E-2C"));
            t.Tick(g.Now);
            g.FactsOf[1] = new AirLossFacts { Alive = true };
            g.Owns.Clear();
            for (int i = 0; i < 20; i++) { g.Now += 1; t.Tick(g.Now); }
            if (n != 0) return "an object that still exists was reported";
            g.FactsOf[1] = new AirLossFacts { Alive = false, Destroyed = true, AltM = 2000, FuelFrac = 0.4 };
            g.Now += 1; t.Tick(g.Now);
            if (n != 1) return "reported when it is really gone";
            return null;
        }

        private static string Causes()
        {
            var cases = new[]
            {
                new { f = new AirLossFacts { Destroyed = true, AltM = 8, FuelFrac = 0.5 }, cause = "crash" },
                new { f = new AirLossFacts { Destroyed = true, AltM = 3000, FuelFrac = 0 }, cause = "fuel" },
                new { f = new AirLossFacts { Destroyed = true, AltM = 3000, FuelFrac = 0.5 }, cause = "unknown" },
            };
            foreach (var c in cases)
            {
                var g = new FakeLossGame();
                var t = new AirLossTracker(g);
                string cause = null; t.Lost = i => cause = i.Cause;
                g.Owns.Add(Bird(1, "X", "F-14A")); t.Tick(g.Now);
                g.FactsOf[1] = c.f; g.Owns.Clear(); g.Now += 1; t.Tick(g.Now);
                if (cause != c.cause) return "expected " + c.cause + " got " + cause;
            }
            // an old hit (more than 30 s) does not make a shot down
            var g2 = new FakeLossGame(); var t2 = new AirLossTracker(g2); string c2 = null; t2.Lost = i => c2 = i.Cause;
            g2.Owns.Add(Bird(1, "X", "F-14A")); t2.Tick(g2.Now);
            t2.OnHit(1, "AA-2", "MiG", g2.Now);
            g2.FactsOf[1] = new AirLossFacts { Destroyed = true, AltM = 3000, FuelFrac = 0.5 }; g2.Owns.Clear(); g2.Now += 40; t2.Tick(g2.Now);
            if (c2 != "unknown") return "old hit: " + c2;
            return null;
        }

        // ---------------- card bodies on the record painter ----------------
        private sealed class State : IUiWindowState
        {
            public readonly Dictionary<string, bool> Open = new Dictionary<string, bool>();
            public bool SectionOpen(string id, bool dflt) { bool v; return Open.TryGetValue(id, out v) ? v : dflt; }
            public void SetSectionOpen(string id, bool open) { Open[id] = open; }
        }

        /// <summary>The texts of the labels in one string (a long line is wrapped into several labels).</summary>
        private static string AllText(RecordPainter p) { return " " + string.Join(" ", p.Items.FindAll(it => it.Text != null).ConvertAll(it => it.Text).ToArray()) + " "; }

        private static AirThreatCard Card()
        {
            var c = new AirThreatCard { RaidKey = "r10", Cls = "MiG-21bis", Count = 2, Side = "hostile", CourseDeg = 183, SpeedKn = 540, TcLine = "Время ×14 → ×1: Воздушная угроза: MiG-21bis ×2, 37 миль" };
            c.Groups.Add(new AirThreatCardLine { Group = "ПЛО-3", Nm = 36.8, EtaS = 130, Action = "cover", ShipName = "Йорктаун" });
            c.Groups.Add(new AirThreatCardLine { Group = "ПЛО-2", Nm = 38.1, EtaS = 160, Action = "warn" });
            c.Groups.Add(new AirThreatCardLine { Group = "Спасатели-4", Nm = 34, EtaS = 110, Action = "land", ShipName = "Йорктаун", Aboard = 16 });
            c.Groups.Add(new AirThreatCardLine { Group = "ПЛО-5", Nm = 30, EtaS = 90, Action = "no_cover" });
            c.Groups.Add(new AirThreatCardLine { Group = "ПВО-2", Action = "scramble", Count = 2, TypeLabel = "F-14A" });   // T-179: the CAP alert group
            c.Groups.Add(new AirThreatCardLine { Group = "ПВО-3", Action = "not_ready" });
            return c;
        }

        public static string Bodies()
        {
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                {
                    string e = BodiesIn(lang);
                    if (e != null) return lang + ": " + e;
                }
            return null;
        }

        private static string BodiesIn(string lang)
        {
            foreach (double s in new[] { 1.0, 1.33 })
            {
                float w = (float)(360 * s);
                // threat card
                var p = new RecordPainter(s); var st = new State();
                var k = new UiKit(p, UiTheme.For(s, UiDensity.Compact), st, "toast");
                int shown = 0, resumed = 0;
                Action<UiKit> body = kk => AirThreatToastBody.Draw(kk, Card(), () => shown++, () => resumed++);
                p.NextFrame(); k.Begin(w); body(k); float h = k.End();
                string bad = p.CheckLayout(w, (float)(900 * s));
                if (bad != null) return "threat s=" + s + ": " + bad;
                if (k.Overflows.Count > 0) return "threat overflow s=" + s + ": " + k.Overflows[0];
                if (!AllText(p).Contains(AirThreatToastBody.RaidLine(Card()))) return "no raid line";
                foreach (var l in Card().Groups) if (!AllText(p).Contains(AirThreatToastBody.GroupLine(l))) return "no group line " + l.Action;
                if (lang == "ru" && (AirThreatToastBody.GroupLine(Card().Groups[4]) != "ПВО-2 (дежурство): взлёт 2 × F-14A" || AirThreatToastBody.GroupLine(Card().Groups[5]) != "ПВО-3 (дежурство): нет готовых")) return "alert lines " + AirThreatToastBody.GroupLine(Card().Groups[4]);
                if (AirThreatToastBody.MmSs(130) != "2:10" || AirThreatToastBody.MmSs(double.PositiveInfinity) != "-") return "MmSs";
                if (AirThreatToastBody.GroupLine(Card().Groups[2]).IndexOf("16", StringComparison.Ordinal) < 0 || AirThreatToastBody.GroupLine(Card().Groups[0]).IndexOf("Йорктаун", StringComparison.Ordinal) < 0) return "group line content";
                p.Click("show"); p.NextFrame(); k.Begin(w); body(k); k.End();
                if (shown != 1 || resumed != 0) return "show click";
                p.Click("resume"); p.NextFrame(); k.Begin(w); body(k); k.End();
                if (shown != 1 || resumed != 1) return "resume click";
                // the resumed card: one line, no buttons
                p = new RecordPainter(s); k = new UiKit(p, UiTheme.For(s, UiDensity.Compact), st, "toast");
                var rc = new AirThreatCard { Resumed = true }; rc.Groups.Add(new AirThreatCardLine { Group = "ПЛО-3", Action = "resume" });
                p.NextFrame(); k.Begin(w); AirThreatToastBody.Draw(k, rc, null, null); k.End();
                if (p.Find("show") != null || p.Find("resume") != null) return "the resumed card has buttons";
                if ((bad = p.CheckLayout(w, (float)(900 * s))) != null) return "resumed s=" + s + ": " + bad;
                // loss card
                var li = new AirLossInfo { Uid = 1, Name = "Mad Foxes", Type = "P-3C", Group = "ПЛО-3", Cause = "shot_down", Weapon = "AA-2", ByClass = "MiG-21bis", Lat = 12.8, Lon = 43.28, Aboard = 0 };
                foreach (var info in new[] { li, new AirLossInfo { Name = "Спас", Type = "SH-3H", Group = "Спасатели-4", Cause = "shot_down", Lat = 12.8, Lon = 43.28, Aboard = 16 }, new AirLossInfo { Name = "A", Type = "F-14A", Cause = "fuel", Lat = -3.5, Lon = -40.1 } })
                {
                    p = new RecordPainter(s); k = new UiKit(p, UiTheme.For(s, UiDensity.Compact), st, "toast");
                    p.NextFrame(); k.Begin(w); AirLossToastBody.Draw(k, info); k.End();
                    if ((bad = p.CheckLayout(w, (float)(900 * s))) != null) return "loss s=" + s + ": " + bad;
                    if (k.Overflows.Count > 0) return "loss overflow s=" + s;
                    if (!AllText(p).Contains(AirLossToastBody.Text(info))) return "no loss text";
                    if (info.Aboard > 0 && p.FindText(UiText.Fmt("airguard.loss.aboard", info.Aboard)) == null) return "no people line";
                }
                string txt = AirLossToastBody.Text(li);
                if (!txt.Contains("P-3C") || !txt.Contains("Mad Foxes") || !txt.Contains("ПЛО-3") || !txt.Contains("AA-2") || !txt.Contains("MiG-21bis") || !txt.Contains(lang == "ru" ? "12,8N 43,28E" : "12.8N 43.28E")) return "loss text: " + txt;
                if (AirLossToastBody.PosText(-3.5, -40.1) != (lang == "ru" ? "3,5S 40,10W" : "3.5S 40.10W")) return "position text: " + AirLossToastBody.PosText(-3.5, -40.1);
            }
            return null;
        }
    }
}
