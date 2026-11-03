using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class Program
    {
        // T-113 B1: World View item recognition and retry back-off
        private static string MathChecks()
        {
            if (!MapMenuMath.IsWorldViewItem("World View: Open", "World View: Open", null)) return "header match";
            if (!MapMenuMath.IsWorldViewItem(null, null, "DataContext.WorldViewOpen")) return "binding path match";
            if (!MapMenuMath.IsWorldViewItem("x", "y", "DataContext.WorldViewOpen")) return "binding path beats header";
            if (MapMenuMath.IsWorldViewItem("x", null, null)) return "header without button name";
            if (MapMenuMath.IsWorldViewItem("", "", null)) return "empty headers";
            if (MapMenuMath.IsWorldViewItem("x", "y", "DataContext.Other")) return "other path";
            double[] want = { 3, 6, 12, 24, 48, 60, 60 };
            for (int i = 0; i < want.Length; i++)
                if (MapMenuMath.RetryDelayS(i) != want[i]) return "RetryDelayS " + i + "=" + MapMenuMath.RetryDelayS(i);
            if (!MapMenuMath.GiveUp(6, 6)) return "GiveUp 6/6";
            if (MapMenuMath.GiveUp(5, 6)) return "GiveUp 5/6";
            return null;
        }

        // the game side reads the Tag of a real MenuItem through the registry; here the fake items have a property of that name
        private static object TagOf(object item) { var p = item.GetType().GetProperty("Tag"); return p == null ? null : p.GetValue(item, null); }

        private sealed class FakeItem { public object Tag { get; set; } }
        private sealed class OddTag { private readonly string _s; public OddTag(string s) { _s = s; } public override string ToString() { return _s; } }
        private sealed class NoTag { }
        private sealed class EqItem { public override bool Equals(object o) { return o is EqItem; } public override int GetHashCode() { return 1; } }

        // T-143 A1
        private static string ActionChecks()
        {
            foreach (bool c in new[] { false, true }) foreach (bool l in new[] { false, true }) foreach (bool a in new[] { false, true }) foreach (bool f in new[] { false, true })
                if (MapMenuMath.NextAction(false, c, l, a, f, 100, 0) != MenuAction.None) return "not in mission";
            if (MapMenuMath.NextAction(true, true, true, true, true, 0, 99) != MenuAction.None) return "all members forced";
            if (MapMenuMath.NextAction(true, true, true, false, false, 1, 99) != MenuAction.Insert) return "insert before nextScan";
            if (MapMenuMath.NextAction(true, true, false, false, true, 1, 99) != MenuAction.Scan) return "unloaded forced";
            if (MapMenuMath.NextAction(true, true, false, false, false, 1, 99) != MenuAction.None) return "unloaded early";
            if (MapMenuMath.NextAction(true, true, false, false, false, 99, 99) != MenuAction.Scan) return "unloaded due";
            if (MapMenuMath.NextAction(true, false, true, true, true, 1, 99) != MenuAction.Scan) return "no cache forced";
            if (MapMenuMath.NextAction(true, false, true, true, false, 1, 99) != MenuAction.None) return "no cache early";
            if (MapMenuMath.NextAction(true, false, true, true, false, 99, 99) != MenuAction.Scan) return "no cache due";
            if (MapMenuMath.MinRescanS != 5) return "MinRescanS";
            return null;
        }

        // T-143 A2
        private static string ItemChecks()
        {
            if (!MapMenuMath.TagIs("VB_ASW", "VB_ASW")) return "TagIs string";
            if (!MapMenuMath.TagIs(new OddTag("VB_ASW"), "VB_ASW")) return "TagIs odd";
            if (MapMenuMath.TagIs(null, "VB_ASW")) return "TagIs null";
            if (MapMenuMath.TagIs("VB_CAP", "VB_ASW")) return "TagIs other";
            var a = new FakeItem { Tag = "VB_ASW" };
            var foreign = new FakeItem { Tag = "x" };
            var outside = new FakeItem { Tag = "VB_ASW" };
            var list = new List<object> { null, "text", new NoTag(), foreign, a };
            if (!ReferenceEquals(MapMenuMath.FindOwn(list, "VB_ASW", foreign, TagOf), foreign)) return "FindOwn known wins";
            if (!ReferenceEquals(MapMenuMath.FindOwn(list, "VB_ASW", outside, TagOf), a)) return "FindOwn by tag";
            if (!ReferenceEquals(MapMenuMath.FindOwn(list, "VB_ASW", null, TagOf), a)) return "FindOwn no known";
            if (MapMenuMath.FindOwn(null, "VB_ASW", a, TagOf) != null) return "FindOwn null list";
            if (MapMenuMath.FindOwn(new List<object> { foreign, null }, "VB_ASW", null, TagOf) != null) return "FindOwn none";
            var b = new FakeItem { Tag = new OddTag("VB_CAP") };
            var items = new List<object> { foreign, a, b };
            if (!MapMenuMath.AllMembers(items, new List<object> { a, b })) return "AllMembers all";
            if (MapMenuMath.AllMembers(items, new List<object> { a, outside })) return "AllMembers missing";
            if (MapMenuMath.AllMembers(items, new List<object>())) return "AllMembers empty";
            if (MapMenuMath.AllMembers(items, new List<object> { a, null })) return "AllMembers null element";
            if (MapMenuMath.AllMembers(null, new List<object> { a })) return "AllMembers null items";
            if (MapMenuMath.AllMembers(new List<object> { new EqItem() }, new List<object> { new EqItem() })) return "AllMembers by reference";
            if (MapMenuMath.Count(null) != 0 || MapMenuMath.Count(items) != 3) return "Count";
            if (MapMenuMath.TagTypeName(null, TagOf) != "null" || MapMenuMath.TagTypeName(new FakeItem(), TagOf) != "null") return "TagTypeName null";
            if (MapMenuMath.TagTypeName(a, TagOf) != "String") return "TagTypeName string";
            if (MapMenuMath.TagTypeName(b, TagOf) != "OddTag") return "TagTypeName odd";
            return null;
        }

        // T-143 A3
        private static string GateChecks()
        {
            var g = new MapMenuLogGate();
            if (!g.FirstFor(5) || g.FirstFor(5) || !g.FirstFor(6)) return "FirstFor";
            for (int i = 100; i <= 100 + MapMenuLogGate.MaxRoots; i++) g.FirstFor(i);
            if (!g.FirstFor(100)) return "roots cleared";
            g.Reset();
            int s, r, n;
            if (g.SummaryDue(0, out s, out r, out n)) return "first call arms";
            if (g.SummaryDue(60, out s, out r, out n)) return "zero counters";
            g.FirstFor(1); g.FirstFor(2);
            g.NoteScan(); g.NoteScan(); g.NoteReinsert();
            if (g.SummaryDue(100, out s, out r, out n)) return "summary early";
            if (!g.SummaryDue(120, out s, out r, out n) || s != 2 || r != 1 || n != 2) return "summary values " + s + "/" + r + "/" + n;
            if (g.SummaryDue(121, out s, out r, out n)) return "summary again";
            g.NoteScan(); g.Reset();
            if (!g.FirstFor(1)) return "Reset roots";
            if (g.SummaryDue(500, out s, out r, out n)) return "Reset arms";
            return null;
        }

        // T-154 F3
        private static string OpenedChecks()
        {
            var other = new FakeItem { Tag = "x" };
            var wv = new FakeItem { Tag = "wv" };
            var items = new List<object> { null, "text", other, wv };
            int idx;
            Func<object, bool> isItem = o => o is FakeItem;
            Func<object, string> head = o => o == wv ? "World View" : "Other";
            Func<object, string> path = o => null;
            var r = MapMenuMath.FindWorldViewIn(items, "World View", isItem, head, path, out idx);
            if (!ReferenceEquals(r, wv) || idx != 3) return "FindWorldViewIn by header " + idx;
            Func<object, string> pathWv = o => o == wv ? MapMenuMath.WorldViewPath : null;
            r = MapMenuMath.FindWorldViewIn(items, null, isItem, o => "x", pathWv, out idx);
            if (!ReferenceEquals(r, wv)) return "FindWorldViewIn by path";
            if (MapMenuMath.FindWorldViewIn(items, "Nope", isItem, o => "x", path, out idx) != null || idx != -1) return "FindWorldViewIn miss";
            if (MapMenuMath.FindWorldViewIn(null, "World View", isItem, head, path, out idx) != null) return "FindWorldViewIn null";
            var g = new MapMenuLogGate();
            int logged = 0;
            for (int i = 0; i < 50; i++) if (g.ShouldLogRoot(1000 + i)) logged++;
            if (logged != 1) return "ShouldLogRoot lines " + logged;
            int s, ri, n;
            g.SummaryDue(0, out s, out ri, out n);
            if (!g.SummaryDue(60, out s, out ri, out n) || n != 50) return "one summary for 50 roots " + n;
            if (g.SummaryDue(61, out s, out ri, out n)) return "no second summary";
            return null;
        }

        private static int Main()
        {
            string err = OverviewDebugGate.SelfTest() ?? MathChecks() ?? ActionChecks() ?? ItemChecks() ?? GateChecks() ?? OpenedChecks();
            Console.WriteLine("map menu sync + overview debug gate selftest " + (err == null ? "ok" : "FAILED: " + err));
            string lerr = LegendSwitch.SelfTest();
            Console.WriteLine("legend switch selftest " + (lerr == null ? "ok" : "FAILED: " + lerr));
            return err == null && lerr == null ? 0 : 1;
        }
    }
}
