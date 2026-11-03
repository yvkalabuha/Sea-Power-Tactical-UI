using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-192: dispatcher of the cases (retreat_hold, hangar_text, deck_label, air_label, rafts_idle).</summary>
    internal static class AswT192Tests
    {
        private static readonly string[] Names = { "retreat_hold", "hangar_text", "deck_label", "air_label", "rafts_idle" };

        public static string Run(string only)
        {
            foreach (var n in Names)
            {
                if (only != null && only != n) continue;
                string e = Case(n);
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static string Case(string n)
        {
            switch (n)
            {
                case "retreat_hold": return AswT192Retreat.Run();
                case "rafts_idle": return AswT192Rafts.Run();
                case "hangar_text": return HangarText();
                case "deck_label": return DeckLabel();
                default: return AirLabel();
            }
        }

        private static string HangarText()
        {
            var m = AswMath.DeckToMember(new AswMath.DeckEntry { Key = "hangar:usn_sh-3h#2", Type = "usn_sh-3h", Label = "SH-3H", State = "ready", HangarPrepS = 900, Helicopter = true, Slot = 2 });
            if (m.Where != "deck_ready" || m.HangarPrepS != 900) return "member " + m.Where + "/" + m.HangarPrepS;
            if (!AswMath.IsHangarRow("hangar:usn_sh-3h#2") || AswMath.IsHangarRow("task:7") || AswMath.IsHangarRow(null)) return "IsHangarRow";
            string t = AswWindowModel.StateText(new AswRow { State = "deck_ready", DeckKey = "hangar:usn_sh-3h#2", HangarPrepS = 900 });
            if (t != "в ангаре, подготовка ~15 мин") return "prep text: " + t;
            t = AswWindowModel.StateText(new AswRow { State = "deck_ready", DeckKey = "hangar:usn_sh-3h#2", HangarPrepS = 0 });
            if (t != "в ангаре: готов к заказу") return "ready text: " + t;
            t = AswWindowModel.StateText(new AswRow { State = "deck_ready", DeckKey = "task:t1" });
            if (!t.StartsWith("на палубе", System.StringComparison.Ordinal)) return "task row text: " + t;
            t = AswWindowModel.StateText(new AswRow { State = "deck_service", DeckKey = "hangar:usn_sh-3h#1", HangarPrepS = 600 });
            if (t != "в ангаре, подготовка ~10 мин") return "service text: " + t;
            // ui_state: hangar_prep_s per row
            var f = new AswFakeGame();
            var h = AswFakeGame.Helo(-5, "deck_ready"); h.Type = "usn_sh-3h"; h.DeckKey = "hangar:usn_sh-3h#1"; h.HangarPrepS = 900; h.DeckUid = 9000; f.Mem.Add(h);
            var k = AswFakeGame.Helo(-6, "deck_ready"); k.Type = "usn_sh-3h"; k.DeckKey = "task:t1"; k.DeckUid = 9000; f.Mem.Add(k);
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            for (int i = 0; i < 3; i++) { f.Advance(1); f.Tick(c); }
            string js = c.StateJson();
            if (!js.Contains("\"hangar_prep_s\":900")) return "ui_state lacks hangar_prep_s=900: " + js;
            if (!js.Contains("\"hangar_prep_s\":-1")) return "ui_state lacks hangar_prep_s=-1";
            return null;
        }

        private static string DeckLabel()
        {
            var m = AswMath.DeckToMember(new AswMath.DeckEntry { Key = "unit:77", Type = "usn_sh-3h", Label = "SH-3H", UnitName = "Griffin 1", State = "launching", Uid = 77, Helicopter = true });
            if (m.Name != "Griffin 1" || m.TypeLabel != "SH-3H") return "unit row " + m.Name + "/" + m.TypeLabel;
            m = AswMath.DeckToMember(new AswMath.DeckEntry { Key = "task:t1", Type = "usn_sh-3h", Label = "SH-3H", State = "ready", Helicopter = true });
            if (m.Name != "SH-3H" || m.TypeLabel != "SH-3H") return "task row " + m.Name + "/" + m.TypeLabel;
            // the controller: the type stays the type on the deck, while launching and in the air
            var f = new AswFakeGame();
            var h = AswFakeGame.Helo(77, "deck_launching"); h.Name = "Griffin 1"; h.Type = "usn_sh-3h"; h.TypeLabel = "SH-3H"; h.DeckKey = "unit:77"; h.DeckUid = 9000; f.Mem.Add(h);
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            foreach (string where in new[] { "deck_launching", "air" })
            {
                h.Where = where;
                for (int i = 0; i < 3; i++) { f.Advance(1); f.Tick(c); }
                var row = c.View().Aircraft.Find(r => r.Uid == 77);
                if (row == null || row.Type != "SH-3H" || row.Name != "Griffin 1") return where + ": row " + (row == null ? "null" : row.Type + "/" + row.Name);
                if (!c.StateJson().Contains("\"type\":\"SH-3H\"")) return where + ": ui_state type";
            }
            return null;
        }

        private static string AirLabel()
        {
            if (AswMath.TypeLabelOf("SH-3H", "Griffin 1", "Griffin 1") != "SH-3H") return "deck label wins";
            if (AswMath.TypeLabelOf(null, "Griffin 1", "Griffin 1") != null) return "ClassName = callsign must not be the type";
            if (AswMath.TypeLabelOf(null, "SH-3H", "Griffin 1") != "SH-3H") return "ClassName is the type";
            if (AswMath.TypeLabelOf(null, null, "Griffin 1") != null) return "no ClassName";
            return null;
        }
    }
}
