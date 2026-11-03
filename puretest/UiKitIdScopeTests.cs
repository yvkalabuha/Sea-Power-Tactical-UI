// T-291: UiKit.IdScope prefixes the ids handed to the painter and to the hit zones (the cards of one toast stack pass).
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class UiKitIdScopeTests
    {
        public static string Run()
        {
            var c = new WidgetCtx(1.0, 320f);
            // 1: no scope = the ids of the windows are unchanged
            c.Frame(k => k.Button("x", "X"));
            if (c.P.Find("x") == null) return "no scope: id unchanged";
            // 2: scope
            string got = null;
            c.Frame(k => { k.IdScope = "a"; k.Button("x", "X"); got = k.Actions(new UiAction("go", "Go")); });
            if (c.P.Find("a/x") == null || c.P.Find("x") != null) return "scope: button id is a/x";
            c.P.Click("a/go"); c.Frame(k => { k.IdScope = "a"; got = k.Actions(new UiAction("go", "Go")); });
            if (got != "go") return "scope: Actions returns the local id, got " + got;
            // 3: two cards in one pass
            string r1 = null, r2 = null;
            Action<UiKit> pass = k =>
            {
                k.IdScope = "c1"; r1 = k.Actions(new UiAction("show", "S"), new UiAction("hide", "H"));
                k.IdScope = "c2"; r2 = k.Actions(new UiAction("show", "S"), new UiAction("hide", "H"));
                k.IdScope = null;
            };
            c.Frame(pass);
            var seen = new HashSet<string>();
            foreach (var b in c.P.Of("Button")) if (!seen.Add(b.Id)) return "duplicate button id " + b.Id;
            if (seen.Count != 4) return "four buttons, got " + seen.Count;
            c.P.Click("c2/hide"); c.Frame(pass);
            if (r1 != null || r2 != "hide") return "click on c2/hide: r1=" + r1 + " r2=" + r2;
            // 4: Check, Number, table row hit
            var spec = new UiTable { Id = "tbl", Columns = new[] { new UiColumn("A", 6f), new UiColumn("B", 0f, true) } };
            c.Frame(k =>
            {
                k.IdScope = "s";
                k.Check("chk", false, "Check");
                k.Number("num", "1");
                k.Table(spec, 1, (r, col) => "v", null, new HashSet<int>(), r => r, 4, null, r => { }, "tip");
            });
            if (c.P.Find("s/chk") == null || c.P.Find("chk") != null) return "Check id";
            if (c.P.Find("s/num") == null || c.P.Find("num") != null) return "Number id";
            bool rowHit = false;
            foreach (var it in c.P.Items) if (it.Id != null && it.Id.StartsWith("s/tbl.row.")) rowHit = true;
            if (!rowHit) return "table row hit id";
            // 5: hit zones of the next pass
            c.Frame(k => { k.IdScope = "a"; k.Button("x", "X"); });
            c.Frame(k => { k.IdScope = "a"; k.Button("x", "X"); });
            if (c.P.HitRects == null || !c.P.HitRects.ContainsKey("a/x") || c.P.HitRects.ContainsKey("x")) return "hit zones keyed by the scoped id";
            // 6: ScopedId
            if (UiKit.ScopedId(null, "x") != "x" || UiKit.ScopedId("", "x") != "x" || UiKit.ScopedId("k", "x") != "k/x") return "ScopedId";
            c.Frame(k => { k.IdScope = ""; k.Button("y", "Y"); });
            if (c.P.Find("y") == null) return "empty scope = none";
            // 7: End resets the scope
            c.Frame(k => { k.IdScope = "z"; k.Button("q", "Q"); });
            if (c.K.IdScope != null) return "End resets IdScope";
            return null;
        }
    }
}
