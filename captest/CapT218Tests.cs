using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-218: deck members of CAP groups after a save load. The game re-creates the deck tasks with new GUIDs, so the uid of a task row of the file
    // never shows up again; the member takes a free row of its type on its ship (CapController.Restore.cs). Scenarios of `captest t218 <name>`.
    internal static class CapT218Tests
    {
        private const string ZA = "\"rect\":[10.5,11,10.83333,11.66667]";
        private const string F14 = "F-14A";
        private static readonly string[] Names =
        {
            "pure-rank", "save-fields-roundtrip", "deck-uid-changed", "deck-two-groups", "deck-held-by-other", "deck-own-key-stale",
            "deck-legacy-file", "airborne-untouched", "lists-late", "deck-strike-skipped",
        };

        private sealed class Chk
        {
            public string Err;
            public void Is(string n, bool c) { if (!c && Err == null) Err = n; }
        }

        private sealed class Rig
        {
            public CapFakeGame G;
            public AirOwnership Own = new AirOwnership();
            public CapParams P = new CapParams { Debug = true };
            public CapController C;
            public Rig(string prefix = "task:f") { Rigs.Add(this); G = new CapFakeGame { SlotHangar = true, ForeignKeyPrefix = prefix }; C = new CapController(G, P, Own); while (G.TakeOut(F14)) { } }
            public void Step(int n) { for (int i = 0; i < n; i++) { G.Advance(1); G.Tick(C); } }
            public int Jn(string prefix) { int n = 0; foreach (string j in G.Journal) if (j.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }
            public int LogN(string sub) { int n = 0; foreach (string l in G.Logs) if (l.Contains(sub)) n++; return n; }
            public FakeBird Foreign() { return G.AddForeignReady(F14, "AirToAirLongRange"); }
            /// <summary>An airborne F-14 in the zone.</summary>
            public FakeBird Air(int uid) { var b = new FakeBird { Uid = uid, Name = "Tomcat " + uid, State = "Air", Lat = 10.7, Lon = 11.3 }; G.All.Add(b); return b; }
            public List<int> Members(string group) { var l = new List<int>(); foreach (var g in C.View().Groups) if (g.Line.Group == group) foreach (var m in g.Members) l.Add(m.Uid); return l; }
        }

        private static int Uid(FakeBird b) { return AirOpsMath.TaskUid(b.Key); }
        private static Dictionary<string, object> J(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static readonly List<Rig> Rigs = new List<Rig>();

        /// <summary>CAPT218_DUMP=1: the log of the last rig of a failed scenario (debugging aid).</summary>
        public static string Run(string only = null)
        {
            string res = RunInner(only);
            if (res != null && Environment.GetEnvironmentVariable("CAPT218_DUMP") == "1" && Rigs.Count > 0)
                foreach (string l in Rigs[Rigs.Count - 1].G.Logs) Console.WriteLine(l);
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
                    case "pure-rank": e = PureRank(); break;
                    case "save-fields-roundtrip": e = SaveFields(); break;
                    case "deck-uid-changed": e = UidChanged(); break;
                    case "deck-two-groups": e = TwoGroups(); break;
                    case "deck-held-by-other": e = HeldByOther(); break;
                    case "deck-own-key-stale": e = OwnKeyStale(); break;
                    case "deck-legacy-file": e = LegacyFile(); break;
                    case "airborne-untouched": e = AirborneUntouched(); break;
                    case "lists-late": e = ListsLate(); break;
                    default: e = StrikeSkipped(); break;
                }
                if (e != null) return n + ": " + e;
            }
            return only == null || Array.IndexOf(Names, only) >= 0 ? null : "unknown scenario";
        }

        private static CapBird Row(int uid, string where, int deck = 100, string type = F14, string loadout = "AirToAirLongRange", double ready = 0)
        {
            return new CapBird { Uid = uid, DeckUid = deck, Type = type, Where = where, Loadout = loadout, ReadyInS = ready };
        }

        private static CapMemberDto Deck(int uid, string name, string where = "deck_ready", int deck = CapFakeGame.DeckUid)
        {
            return new CapMemberDto { Uid = uid, Name = name, Type = F14, Slot = -1, TaskDeck = deck, Where = where, Loadout = "AirToAirLongRange" };
        }

        private static CapMemberDto Flying(int uid) { return new CapMemberDto { Uid = uid, Name = "Tomcat " + uid, Type = F14, Slot = 0 }; }

        private static CapGroupDto Dto(string name, int seq, params CapMemberDto[] members)
        {
            return new CapGroupDto
            {
                Group = name, Seq = seq, OnStation = 1, Ready = 1, Roe = "free", Rect = new[] { 10.5, 11, 10.83333, 11.66667 }, Anchor = "geo",
                Loadouts = new Dictionary<string, string>(), P = new CapParams(), Members = new List<CapMemberDto>(members), Keys = new List<string>(),
            };
        }

        private static CapSaveData File(params CapGroupDto[] groups)
        {
            var d = new CapSaveData { MissionTimeS = 1000 };
            d.Groups.AddRange(groups);
            return d;
        }

        private static string PureRank()
        {
            var k = new Chk();
            k.Is("same state", CapMath.RestoreRowRank("deck_ready", "deck_ready") == 0 && CapMath.RestoreRowRank("hangar", "hangar") == 0);
            k.Is("ready / prep", CapMath.RestoreRowRank("deck_cool", "deck_ready") == 1 && CapMath.RestoreRowRank("deck_ready", "deck_prep") == 1 && CapMath.RestoreRowRank(null, "deck_ready") == 1);
            k.Is("cool / hangar", CapMath.RestoreRowRank("deck_ready", "deck_cool") == 2 && CapMath.RestoreRowRank("deck_ready", "hangar") == 3);
            k.Is("not allowed", CapMath.RestoreRowRank("deck_ready", "deck_launch") == -1 && CapMath.RestoreRowRank("deck_ready", "zone") == -1 && CapMath.RestoreRowRank("deck_ready", "to_zone") == -1 && CapMath.RestoreRowRank("deck_ready", null) == -1);
            Func<CapBird, bool> all = b => true;
            var rows = new List<CapBird> { Row(1, "hangar"), Row(2, "deck_ready", 101), Row(3, "deck_ready", 100), Row(4, "deck_prep", 100) };
            k.Is("own deck", CapMath.PickRestoreRow(rows, F14, 100, "deck_ready", null, all).Uid == 3);
            k.Is("other deck", CapMath.PickRestoreRow(rows, F14, 101, "deck_ready", null, all).Uid == 2);
            k.Is("any deck: lower deck uid", CapMath.PickRestoreRow(rows, F14, 0, "deck_ready", null, all).Uid == 3);
            k.Is("same state beats prep", CapMath.PickRestoreRow(rows, F14, 100, "deck_prep", null, all).Uid == 4);
            var eq = new List<CapBird> { Row(10, "deck_ready", 100, F14, "AirToAirIntercept", 5), Row(11, "deck_ready", 100, F14, "AirToAirLongRange", 50), Row(12, "deck_ready", 100, F14, "AirToAirLongRange", 20), Row(13, "deck_ready", 100, F14, "AirToAirLongRange", 20) };
            k.Is("same loadout first, then lower ready, then lower uid", CapMath.PickRestoreRow(eq, F14, 100, "deck_ready", "AirToAirLongRange", all).Uid == 12);
            k.Is("no loadout: lower ready", CapMath.PickRestoreRow(eq, F14, 100, "deck_ready", null, all).Uid == 10);
            k.Is("free=false skipped", CapMath.PickRestoreRow(eq, F14, 100, "deck_ready", "AirToAirLongRange", b => b.Uid != 12).Uid == 13);
            k.Is("other type", CapMath.PickRestoreRow(rows, "F-4", 100, "deck_ready", null, all) == null);
            k.Is("launch / air rows", CapMath.PickRestoreRow(new List<CapBird> { Row(20, "deck_launch"), Row(21, "to_zone"), Row(22, "zone") }, F14, 0, "deck_ready", null, all) == null);
            k.Is("nothing free", CapMath.PickRestoreRow(rows, F14, 100, "deck_ready", null, b => false) == null);
            return k.Err;
        }

        private static string SaveFields()
        {
            var k = new Chk(); string err;
            var d = File(Dto("ПВО-1", 1, Deck(-1236665748, "Ready 1"), Flying(30)));
            string j = CapSave.ToJson(d);
            var b = CapSave.Parse(j, out err);
            var m = err == null ? b.Groups[0].Members[0] : null;
            k.Is("fields " + err, m != null && m.TaskDeck == 100 && m.Where == "deck_ready" && m.Loadout == "AirToAirLongRange" && CapSave.ToJson(b) == j);
            k.Is("format 3", b.Format == 3 && j.Contains("\"format\":3"));
            k.Is("flying member: no keys", b.Groups[0].Members[1].TaskDeck == 0 && b.Groups[0].Members[1].Where == null && b.Groups[0].Members[1].Loadout == null);
            var old = CapSave.Parse("{\"format\":3,\"groups\":[{\"group\":\"G\",\"members\":[{\"uid\":-5,\"type\":\"F-14A\",\"slot\":-1}]}]}", out err);
            k.Is("old file", err == null && old.Groups[0].Members[0].TaskDeck == 0 && old.Groups[0].Members[0].Where == null && old.Groups[0].Members[0].Loadout == null);
            string none = CapSave.ToJson(File(Dto("ПВО-1", 1, Flying(30))));
            k.Is("no keys without fields", !none.Contains("task_deck") && !none.Contains("\"where\"") && !none.Contains("\"loadout\":\"") );
            return k.Err;
        }

        // the live case: the deck member is a game ready task; after the load the game made new tasks (other GUIDs)
        private static string UidChanged()
        {
            var k = new Chk();
            var r1 = new Rig();
            var f1 = r1.Foreign();
            r1.Air(30);
            string a = r1.C.Group(J("{\"op\":\"create\",\"uids\":[30," + Uid(f1) + "]}"));
            k.Is("create " + a, a.Contains("\"ok\":true"));
            r1.C.Order(J("{\"group\":\"ПВО-1\"," + ZA + ",\"on_station\":1,\"ready\":1}"));
            r1.Step(10);
            var saved = r1.C.Save();
            var md = saved.Groups[0].Members.Find(m => m.Uid == Uid(f1));
            if (Environment.GetEnvironmentVariable("CAPT218_DUMP") == "1") { Console.WriteLine(CapSave.ToJson(saved)); foreach (string l in r1.G.Logs) Console.WriteLine(l); }
            k.Is("saved with the ship", md != null && md.TaskDeck == CapFakeGame.DeckUid && md.Where == "deck_ready" && md.Loadout == "AirToAirLongRange");
            string json = CapSave.ToJson(saved);
            string err; var d = CapSave.Parse(json, out err);

            var r2 = new Rig("task:g");
            var g1 = r2.Foreign(); var g2 = r2.Foreign();
            r2.Air(30);
            r2.C.Load(d, r2.G.Now);
            r2.Step(25);   // longer than RestoreGraceS
            k.Is("nobody out " + r2.LogN("member out"), r2.LogN("member out") == 0);
            var members = r2.Members("ПВО-1");
            k.Is("2 members " + members.Count, members.Count == 2 && members.Contains(30));
            k.Is("new uid", members.Contains(Uid(g1)) || members.Contains(Uid(g2)));
            k.Is("log", r2.LogN("restore readopt") == 1);
            k.Is("adopt deck task", r2.LogN("adopt deck task") >= 1);
            k.Is("no order", r2.Jn("order ") == 0);
            return k.Err;
        }

        // T-325 tail: after a load a deck row with a strike loadout is not re-adopted (the only new row is a strike one: nothing is taken)
        private static string StrikeSkipped()
        {
            var k = new Chk();
            var r = new Rig("task:g");
            var bad = r.G.AddForeignReady(F14, "Strike"); bad.Weapons = new Dictionary<string, int> { { "Mk82", 6 }, { "AIM-9", 2 } };
            r.Air(30);
            r.C.Load(File(Dto("ПВО-1", 1, Flying(30), Deck(AirOpsMath.TaskUid("task:f1"), "a"))), r.G.Now);
            r.Step(10);
            k.Is("strike row not taken " + string.Join(",", r.Members("ПВО-1")), !r.Members("ПВО-1").Contains(Uid(bad)) && r.LogN("restore readopt") == 0);
            return k.Err;
        }

        private static string TwoGroups()
        {
            var k = new Chk();
            var r = new Rig("task:g");
            var g1 = r.Foreign(); var g2 = r.Foreign();
            r.Air(30); r.Air(31);
            r.C.Load(File(Dto("ПВО-1", 1, Flying(30), Deck(AirOpsMath.TaskUid("task:f1"), "a")), Dto("ПВО-2", 2, Flying(31), Deck(AirOpsMath.TaskUid("task:f2"), "b"))), r.G.Now);
            r.Step(25);
            k.Is("nobody out", r.LogN("member out") == 0);
            var a = r.Members("ПВО-1"); var b = r.Members("ПВО-2");
            k.Is("2 + 2", a.Count == 2 && b.Count == 2);
            bool a1 = a.Contains(Uid(g1)), a2 = a.Contains(Uid(g2)), b1 = b.Contains(Uid(g1)), b2 = b.Contains(Uid(g2));
            k.Is("different rows", (a1 && b2) || (a2 && b1));
            k.Is("two readopts", r.LogN("restore readopt") == 2);
            return k.Err;
        }

        // the only new row is held by the ASW window: nothing is taken, after the grace the member is lost as before
        private static string HeldByOther()
        {
            var k = new Chk();
            var r = new Rig("task:g");
            var g1 = r.Foreign();
            r.Air(30);
            r.Own.Claim(Uid(g1), "asw", "ПЛО-1");
            r.C.Load(File(Dto("ПВО-1", 1, Flying(30), Deck(AirOpsMath.TaskUid("task:f1"), "a"))), r.G.Now);
            r.Step(10);
            k.Is("not taken", r.LogN("restore readopt") == 0 && r.LogN("member out") == 0);
            r.Step(15);
            k.Is("lost after the grace", r.G.LogHas("member out uid=" + AirOpsMath.TaskUid("task:f1") + " group=ПВО-1 (lost)"));
            string w; k.Is("the row stays with ASW", r.Own.Owner(Uid(g1), out w) == "ПЛО-1");
            return k.Err;
        }

        // an own key of the file (order from the hangar) is stale: no task with it; a foreign ready row is taken, the old key is dropped without an abort
        private static string OwnKeyStale()
        {
            var k = new Chk();
            var r = new Rig("task:g");
            var g1 = r.Foreign();
            r.Air(30);
            int old = AirOpsMath.TaskUid("task:own1");
            var dto = Dto("ПВО-1", 1, Flying(30), Deck(old, "a", "deck_prep"));
            dto.Keys.Add(old + "|100|task:own1");
            r.C.Load(File(dto), r.G.Now);
            r.Step(25);
            k.Is("nobody out", r.LogN("member out") == 0);
            k.Is("readopt", r.LogN("restore readopt") == 1 && r.Members("ПВО-1").Contains(Uid(g1)));
            k.Is("no abort", r.Jn("abort ") == 0);
            k.Is("no order", r.Jn("order ") == 0);
            string json = CapSave.ToJson(r.C.Save());
            k.Is("old key not saved", !json.Contains("task:own1"));
            return k.Err;
        }

        // a 0.30.9 file: no ship / state, the uid is a task uid -> any deck
        private static string LegacyFile()
        {
            var k = new Chk();
            var r = new Rig("task:g");
            var g1 = r.Foreign();
            r.Air(30);
            int old = -1236665748;
            string json = "{\"format\":3,\"mission_time_s\":1000,\"groups\":[{\"group\":\"ПВО-1\",\"seq\":1,\"on_station\":1,\"ready\":1,\"roe\":\"free\",\"rect\":[10.5,11,10.83333,11.66667],"
                + "\"members\":[{\"uid\":30,\"name\":\"Tomcat 30\",\"type\":\"F-14A\",\"slot\":0},{\"uid\":" + old + ",\"name\":\"Ready 1\",\"type\":\"F-14A\",\"slot\":-1}],\"keys\":[]}],\"memory\":[]}";
            string err; var d = CapSave.Parse(json, out err);
            k.Is("parse " + err, err == null);
            r.C.Load(d, r.G.Now);
            r.Step(25);
            k.Is("nobody out", r.LogN("member out") == 0);
            k.Is("readopt any", r.LogN("restore deck any") == 1 && r.Members("ПВО-1").Contains(Uid(g1)));
            return k.Err;
        }

        // a flying-home member (positive uid, slot -1 in an old file) does not go to the readopt: it waits the grace and is dropped as in T-211
        private static string AirborneUntouched()
        {
            var k = new Chk();
            var r = new Rig("task:g");
            r.Foreign();
            r.Air(30);
            var home = new CapMemberDto { Uid = 6, Name = "Tomcat 6", Type = F14, Slot = -2 };
            var plain = new CapMemberDto { Uid = 7, Name = "Tomcat 7", Type = F14, Slot = -1 };
            r.C.Load(File(Dto("ПВО-1", 1, Flying(30), home, plain)), r.G.Now);
            r.Step(10);
            k.Is("no readopt", r.LogN("restore readopt") == 0);
            k.Is("waits the grace", r.G.LogHas("restore wait uid=6") && r.LogN("member out") == 0);
            r.Step(15);
            k.Is("7 lost as before (no deck task uid)", r.G.LogHas("member out uid=7 group=ПВО-1 (lost)"));
            k.Is("never took the new row", r.LogN("restore readopt") == 0);
            return k.Err;
        }

        // the lists are empty for the first ticks after the load, then the rows appear: readopt before the grace ends
        private static string ListsLate()
        {
            var k = new Chk();
            var r = new Rig("task:g");
            r.Air(30);
            r.C.Load(File(Dto("ПВО-1", 1, Flying(30), Deck(AirOpsMath.TaskUid("task:f1"), "a"))), r.G.Now);
            r.Step(5);
            k.Is("waiting, nobody out", r.LogN("member out") == 0 && r.LogN("restore readopt") == 0);
            var g1 = r.Foreign();
            r.Step(5);
            k.Is("readopt", r.LogN("restore readopt") == 1 && r.Members("ПВО-1").Contains(Uid(g1)));
            r.Step(20);
            k.Is("nobody out", r.LogN("member out") == 0);
            return k.Err;
        }
    }
}
