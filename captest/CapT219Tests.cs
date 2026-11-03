using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-219 (ADR-0090): the alert binding of a member (state, raid, AirGuard key, last target) in the CAP file; the same wave after a load
    // (AirGuard key alias, raid place memory). Scenarios of `captest t219 <name>`; `captest alertbind` = CapAlertBind.SelfTest().
    // Written red first (plan D1): needs CapMemberDto.Bind / CapGroupDto.Memos (step B2) and the controller work (stream C) to compile and pass.
    internal static class CapT219Tests
    {
        private static readonly string[] Names =
        {
            "bind-taxi", "bind-air", "rebind-ghost", "rebind-key", "gate-key-alias", "gate-memo", "far-raid-still-scrambles",
            "stale-bind-deck", "legacy-slot3", "uid-change",
        };


        /// <summary>CAPT219_DUMP=1: the log of the last rig of a failed scenario (debugging aid).</summary>
        public static string Run(string only = null)
        {
            string res = RunInner(only);
            if (res != null && Environment.GetEnvironmentVariable("CAPT219_DUMP") == "1" && CapT179Tests.Rig.All.Count > 0)
                foreach (string l in CapT179Tests.Rig.All[CapT179Tests.Rig.All.Count - 1].G.Logs) Console.WriteLine(l);
            return res;
        }

        private static string RunInner(string only)
        {
            string e;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                switch (n)
                {
                    case "bind-taxi": e = Bind(true); break;
                    case "bind-air": e = Bind(false); break;
                    case "rebind-ghost": e = Rebind(false); break;
                    case "rebind-key": e = Rebind(true); break;
                    case "gate-key-alias": e = GateAlias(); break;
                    case "gate-memo": e = GateMemo(); break;
                    case "far-raid-still-scrambles": e = FarRaid(); break;
                    case "stale-bind-deck": e = StaleBindDeck(); break;
                    case "legacy-slot3": e = LegacySlot3(); break;
                    default: e = UidChange(); break;
                }
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario";
        }

        private static int Scrambles(CapT179Tests.Rig r)
        {
            int n = 0; foreach (string l in r.G.Logs) if (l.Contains("alert scramble group=ПВО-1 raid=")) n++; return n;
        }

        /// <summary>A rig with the "Дежурство" ПВО-1 (4 ready, 2 per raid) that scrambled a pair to the raid 7028 (50 nm north of the ship).
        /// taxiS &gt; 0: the pair taxis this long, so a save right after the scramble sees Airborne=false.</summary>
        private static CapT179Tests.Rig Scrambled(double taxiS)
        {
            var r = new CapT179Tests.Rig(null, "Cheater");
            r.G.TaxiS = taxiS;
            r.Create("1,2,3,4"); r.Alert("ПВО-1", ",\"ready\":4,\"per_raid\":2");
            r.Step(900);
            r.G.Contacts.Add(CapT179Tests.Raid(7028, 50));
            for (int i = 0; i < 30 && Scrambles(r) == 0; i++) r.Step(1);
            r.Step(3);
            return r;
        }

        private static CapSaveData Snap(CapT179Tests.Rig r)
        {
            string err; var d = CapSave.Parse(CapSave.ToJson(r.C.Save()), out err);
            if (err != null) throw new InvalidOperationException(err);
            return d;
        }

        private static CapController Reload(CapT179Tests.Rig r, CapSaveData d)
        {
            var c2 = new CapController(r.G, new CapParams(), new AirOwnership()) { AlertNotes = r.Notes };
            c2.Load(d, r.G.NowS);
            return c2;
        }

        private static void Run2(CapT179Tests.Rig r, CapController c, int n) { for (int i = 0; i < n; i++) { r.G.Advance(1); r.G.Tick(c); } }

        private static bool AnyBind(CapSaveData d) { foreach (var m in d.Groups[0].Members) if (m.Bind != null) return true; return false; }

        // the pair is saved while it taxis (taxi) / flies; after the load it keeps the intercept: no rtb_alert, a handover to the game AI near the target
        private static string Bind(bool taxi)
        {
            var k = new CapT179Tests.Chk();
            var r = Scrambled(taxi ? 60 : 0);
            if (!taxi) r.Step(120);
            var d = Snap(r);
            k.Is("binding in the file", AnyBind(d));
            var c2 = Reload(r, d);
            Run2(r, c2, 300);
            k.Is("log: bind restored", r.G.LogHas("alert bind restored"));
            k.Is("no rtb_alert while the raid is in the radius", !r.G.LogHas("-> rtb_alert"));
            r.G.Contacts.Clear(); r.G.Contacts.Add(CapT179Tests.Raid(7028, 5));
            Run2(r, c2, 200);
            k.Is("handover near the target", r.G.LogHas("alert handover"));
            return k.Err;
        }

        // the track 7028 is a ghost after the load; 7027 (another AirGuard key) near the forecast / 7031 (the same AirGuard key) far from it
        private static string Rebind(bool byKey)
        {
            var k = new CapT179Tests.Chk();
            var r = Scrambled(0);
            r.Step(20);
            r.Notes.Keys[7028] = "r7028";
            var d = Snap(r);
            var c2 = Reload(r, d);
            r.G.Contacts.Clear();
            if (byKey) { r.Notes.Keys[7031] = "r7028"; r.G.Contacts.Add(CapT179Tests.Raid(7031, 55, 45)); }
            else { r.Notes.Keys[7027] = "r7027"; r.G.Contacts.Add(CapT179Tests.Raid(7027, 50, 6)); }
            Run2(r, c2, 60);
            k.Is("rebind logged " + (byKey ? "why=key" : "why=near"), r.G.LogHas("alert rebind") && r.G.LogHas(byKey ? "why=key" : "why=near"));
            k.Is("no rtb_alert", !r.G.LogHas("-> rtb_alert"));
            k.Is("no second scramble", Scrambles(r) == 1);
            return k.Err;
        }

        // 49.7: the second pair stands; after the load the new track 7027 shares the AirGuard key of the served 7028 -> no second scramble
        private static string GateAlias()
        {
            var k = new CapT179Tests.Chk();
            var r = Scrambled(0);
            r.Notes.Keys[7028] = "r7028";
            r.Step(10);
            var d = Snap(r);
            foreach (var bm in d.Groups[0].Members) bm.Bind = null;   // the gate alone is tested: the pair is not bound (it would rebind first and take the ids itself)
            var c2 = Reload(r, d);
            r.G.Contacts.Clear();
            r.Notes.Keys[7027] = "r7027"; r.Notes.Keys[7028] = "r7027";
            r.G.Contacts.Add(CapT179Tests.Raid(7027, 60, 40));
            Run2(r, c2, 30);
            k.Is("no second scramble by the alias", Scrambles(r) == 1);
            k.Is("log: served by alias", r.G.LogHas("alert raid served") && r.G.LogHas("by=alias"));
            return k.Err;
        }

        // a new id and a new key at the forecast point of the memory: no scramble; the same after the TTL (901 s): scramble
        private static string GateMemo()
        {
            var k = new CapT179Tests.Chk();
            var r = Scrambled(0);
            r.Step(10);
            var d = Snap(r);
            k.Is("memory in the file", d.Groups[0].Memos != null && d.Groups[0].Memos.Count > 0);
            foreach (var bm in d.Groups[0].Members) bm.Bind = null;   // the gate alone is tested
            var c2 = Reload(r, d);
            r.G.Contacts.Clear();
            r.Notes.Keys[7040] = "rNew";
            r.G.Contacts.Add(CapT179Tests.Raid(7040, 50, 6));
            Run2(r, c2, 30);
            k.Is("no scramble near the memory", Scrambles(r) == 1);
            k.Is("log: served by memo", r.G.LogHas("by=memo"));
            r.G.Contacts.Clear();
            Run2(r, c2, 901);
            r.Notes.Keys[7041] = "rNew2";
            r.G.Contacts.Add(CapT179Tests.Raid(7041, 50, 6));
            Run2(r, c2, 30);
            k.Is("after the TTL the same place scrambles", Scrambles(r) == 2);
            return k.Err;
        }

        // a raid 40 nm from the memory is another wave
        private static string FarRaid()
        {
            var k = new CapT179Tests.Chk();
            var r = Scrambled(0);
            r.Step(10);
            var c2 = Reload(r, Snap(r));
            r.G.Contacts.Clear();
            r.Notes.Keys[7050] = "rFar";
            r.G.Contacts.Add(CapT179Tests.Raid(7050, 50, 40));
            Run2(r, c2, 30);
            k.Is("a far raid scrambles", Scrambles(r) == 2);
            return k.Err;
        }

        // the bound members stand on the deck after the load (the take-off never happened): after RestoreGraceS they go back to the pool, the raid is unserved
        private static string StaleBindDeck()
        {
            var k = new CapT179Tests.Chk();
            var r = Scrambled(60);
            var d = Snap(r);
            k.Is("binding in the file", AnyBind(d));
            foreach (var m in d.Groups[0].Members)
                if (m.Bind != null) { var b = r.G.Bird(m.Uid); if (b != null) { b.State = "Ready"; b.Taken = false; } }
            var c2 = Reload(r, d);
            Run2(r, c2, 40);
            k.Is("log: unbind", r.G.LogHas("alert unbind"));
            Run2(r, c2, 60);
            k.Is("the raid is scrambled again", Scrambles(r) >= 2);
            return k.Err;
        }

        // a file of an older build: slot -3 without alert_bind -> the old instant call-back (ADR-0079 section 11)
        private static string LegacySlot3()
        {
            var k = new CapT179Tests.Chk();
            var r = Scrambled(0);
            r.Step(120);
            var d = Snap(r);
            foreach (var m in d.Groups[0].Members) if (m.Bind != null) { m.Bind = null; m.Slot = -3; }
            d.Groups[0].Memos = null;
            var c2 = Reload(r, d);
            Run2(r, c2, 200);
            k.Is("old behaviour: rtb_alert", r.G.LogHas("-> rtb_alert"));
            k.Is("no bind restored", !r.G.LogHas("alert bind restored"));
            return k.Err;
        }

        // the member got another uid by the load: the binding is attached to the uid of the file record (T-218 renumbering)
        private static string UidChange()
        {
            var k = new CapT179Tests.Chk();
            var r = Scrambled(0);
            r.Step(60);
            var d = Snap(r);
            int oldUid = 0, newUid = 9100;
            foreach (var m in d.Groups[0].Members)
                if (m.Bind != null && oldUid == 0) { oldUid = m.Uid; m.Uid = newUid; }
            var b = r.G.Bird(oldUid);
            if (b != null) b.Uid = newUid;
            var c2 = Reload(r, d);
            Run2(r, c2, 60);
            k.Is("bind restored for the new uid", r.G.LogHas("alert bind restored uid=" + newUid));
            return k.Err;
        }
    }
}
