// T-291: two cards of one stack pass must not share button ids: a click on the lower card acts on it only.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class ToastIdsTests
    {
        private sealed class Ctx
        {
            public RecordPainter P = new RecordPainter(1.0);
            public UiKit K; public UiToastStackModel M = new UiToastStackModel();
            public List<string> Logs = new List<string>();
            public Ctx() { K = new UiKit(P, UiTheme.For(1.0), null, "toast"); }
            public void Frame() { P.NextFrame(); K.Begin(360f); UiToastCards.Draw(K, M, Logs.Add); K.End(); }
        }

        private sealed class Counts
        {
            public int Search, Window, Show, Hide, Undo;
            public SubDatumCardActions A() { return new SubDatumCardActions { Search = () => Search++, OpenWindow = () => Window++, Show = () => Show++, Hide = () => Hide++, Undo = () => Undo++ }; }
            public string Sum() { return Search + "," + Window + "," + Show + "," + Hide + "," + Undo; }
        }

        private static SubDatumCardModel Model(int id, bool undo)
        {
            var m = new SubDatumCardModel { Id = id, BrgDeg = 40, RNm = 10, CoreNm = 3, SigmaDeg = 5, AgeS = 20, Src = "los", Ammo = "ASROC", State = "live", Group = "ASW-3" };
            if (undo) { m.Searching = "ASW-3"; m.ReplacesArea = true; m.UndoLeftS = 9; }
            return m;
        }

        public static string TwoDatum()
        {
            foreach (string btn in new[] { "search", "window", "show", "hide", "undo" })
                foreach (string which in new[] { "datum:7", "datum:8" })
                {
                    bool undo = btn == "undo";
                    var c8 = new Counts(); var c7 = new Counts();
                    var c = new Ctx();
                    c.M.Show("datum:8", "warn", k => SubDatumCard.Draw(k, Model(8, undo), c8.A()), 0, 0, true);
                    c.M.Show("datum:7", "warn", k => SubDatumCard.Draw(k, Model(7, undo), c7.A()), 0, 0, true);
                    c.Frame();
                    c.P.Click(UiKit.ScopedId(which, btn));
                    c.Frame();
                    var hit = which == "datum:7" ? c7 : c8; var other = which == "datum:7" ? c8 : c7;
                    int n = btn == "search" ? hit.Search : btn == "window" ? hit.Window : btn == "show" ? hit.Show : btn == "hide" ? hit.Hide : hit.Undo;
                    if (n != 1 || other.Sum() != "0,0,0,0,0") return btn + " on " + which + ": target=" + hit.Sum() + " other=" + other.Sum();
                }
            return null;
        }

        private static AirThreatCard Raid(string key)
        {
            var c = new AirThreatCard { RaidKey = key, Cls = "MiG-21bis", Side = "hostile", Count = 2, CourseDeg = 90, SpeedKn = 400 };
            c.Groups.Add(new AirThreatCardLine { Group = "CAP-1", Nm = 20, EtaS = 60, Action = "warn" });
            return c;
        }

        public static string TwoAirThreat()
        {
            foreach (string btn in new[] { "show", "resume" })
                foreach (string which in new[] { "r1", "r2" })
                {
                    var n = new Dictionary<string, int> { { "r1show", 0 }, { "r1resume", 0 }, { "r2show", 0 }, { "r2resume", 0 } };
                    var c = new Ctx();
                    foreach (string r in new[] { "r1", "r2" })
                    {
                        string rr = r;
                        c.M.Show("air_threat:" + rr, "warn", k => AirThreatToastBody.Draw(k, Raid(rr), () => n[rr + "show"]++, () => n[rr + "resume"]++), 0, 0, true);
                    }
                    c.Frame();
                    c.P.Click(UiKit.ScopedId("air_threat:" + which, btn));
                    c.Frame();
                    foreach (var kv in n)
                    {
                        int want = kv.Key == which + btn ? 1 : 0;
                        if (kv.Value != want) return btn + " on " + which + ": " + kv.Key + "=" + kv.Value;
                    }
                }
            return null;
        }

        public static string TwoUndo()
        {
            foreach (string which in new[] { "undo:datum", "undo:asw" })
            {
                int a = 0, b = 0;
                var c = new Ctx();
                c.M.Show("undo:datum", "undo", UiUndoCard.Body("T1", () => 9, () => a++, () => null), 0, 0, true);
                c.M.Show("undo:asw", "undo", UiUndoCard.Body("T2", () => 9, () => b++, () => null), 0, 0, true);
                c.Frame();
                c.P.Click(UiKit.ScopedId(which, UiUndoCard.ButtonId));
                c.Frame();
                int wa = which == "undo:datum" ? 1 : 0, wb = 1 - wa;
                if (a != wa || b != wb) return which + ": datum=" + a + " asw=" + b;
            }
            return null;
        }

        public static string CloseIds()
        {
            var c = new Ctx();
            var c8 = new Counts(); var c7 = new Counts();
            c.M.Show("datum:8", "warn", k => SubDatumCard.Draw(k, Model(8, false), c8.A()), 0, 0, true);
            c.M.Show("datum:7", "warn", k => SubDatumCard.Draw(k, Model(7, false), c7.A()), 0, 0, true);
            c.Frame();
            if (c.P.Find("toast.close.datum:7") == null || c.P.Find("toast.close.datum:8") == null) return "close buttons keep their unscoped ids";
            c.P.Click("toast.close.datum:7");
            c.Frame();
            if (c.M.Has("datum:7") || !c.M.Has("datum:8")) return "close [x] of 7 closes only 7";
            return null;
        }

        public static string NoDuplicateIds()
        {
            var c = new Ctx();
            var cn = new Counts();
            c.M.Show("datum:8", "warn", k => SubDatumCard.Draw(k, Model(8, true), cn.A()), 0, 0, true);
            c.M.Show("datum:7", "warn", k => SubDatumCard.Draw(k, Model(7, false), cn.A()), 0, 0, true);
            c.M.Show("air_threat:r1", "warn", k => AirThreatToastBody.Draw(k, Raid("r1"), null, null), 0, 0, true);
            c.M.Show("air_threat:r2", "warn", k => AirThreatToastBody.Draw(k, Raid("r2"), null, null), 0, 0, true);
            c.M.Show("undo:datum", "undo", UiUndoCard.Body("T1", () => 9, () => { }, () => null), 0, 0, true);
            c.M.Show("undo:asw", "undo", UiUndoCard.Body("T2", () => 9, () => { }, () => null), 0, 0, true);
            // all six one after another: every pass shows the newest MaxVisible; close the top to reveal the next ones
            var seen = new HashSet<string>();
            for (int pass = 0; pass < 6; pass++)
            {
                c.Frame();
                var ids = new HashSet<string>();
                foreach (var b in c.P.Of("Button")) if (!ids.Add(b.Id)) return "duplicate button id " + b.Id;
                foreach (var b in ids) seen.Add(b);
                c.M.Close(c.M.Visible()[0].Key);
            }
            if (seen.Count < 12) return "expected many distinct ids, got " + seen.Count;
            return null;
        }
    }
}
