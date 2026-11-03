using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (UiText.LoadForTests("ru") < 2) { Console.WriteLine("menu model stand FAILED: language tables not found"); return 1; }
            if (args.Length >= 2 && args[0] == "sheet")
            {
                int zoom = 3;
                if (args.Length >= 3 && !int.TryParse(args[2], out zoom)) zoom = 3;
                string perr = RadialGlyphs.SelfTest();
                if (perr != null) { Console.WriteLine("RadialGlyphs selftest FAILED: " + perr); return 1; }
                return PngSheet.Write(args[1], zoom);
            }
            string err = RadialText.SelfTest();
            Console.WriteLine("RadialText " + (err == null ? "ok" : "FAILED: " + err));
            string err2 = MenuModel.SelfTest();
            Console.WriteLine("MenuModel " + (err2 == null ? "ok" : "FAILED: " + err2));
            string err3 = RadialGlyphs.SelfTest();
            Console.WriteLine("RadialGlyphs selftest " + (err3 == null ? "ok" : "FAILED: " + err3));
            string err4 = Coverage();
            Console.WriteLine("radial glyph coverage selftest " + (err4 == null ? "ok" : "FAILED: " + err4));
            string err5 = FormationMath.SelfTest();
            Console.WriteLine("FormationMath selftest " + (err5 == null ? "ok" : "FAILED: " + err5));
            string err6 = RingGroups.SelfTest();
            Console.WriteLine("RingGroups " + (err6 == null ? "ok" : "FAILED: " + err6));   // T-328
            return err == null && err2 == null && err3 == null && err4 == null && err5 == null && err6 == null ? 0 : 1;
        }

        private static string Missing(IEnumerable<MenuNode> nodes, string where)
        {
            foreach (var n in nodes)
            {
                if (n == null || n.Kind == "value") continue;
                if (RadialGlyphs.KeyFor(n) == null) return where + ": no glyph for " + n.Id;
                string e = Missing(n.Children, where + "/" + n.Id);
                if (e != null) return e;
            }
            return null;
        }

        private static UnitView View(string kind)
        {
            var u = new UnitView { Uid = 1, Name = "U", Kind = kind, AllowAttack = true, WiredTorpedoes = true };
            foreach (var c in new[] { "unit_flags", "auto_evade", "post_launch", "fire_veto", "smart_attack", "air_defense_ready", "radar_overlay", "overview", "standing_orders", "air_evasion" }) u.Caps.Add(c);
            return u;
        }

        private static string Coverage()
        {
            string err;
            var text = ActionRegistry.LoadEmbedded();
            var defs = ActionRegistry.Parse(text, out err);
            if (defs == null) return "registry: " + err;
            var layout = ActionRegistry.ParseRadial(text, defs, out err);
            if (layout == null) return "radial: " + err;
            foreach (var kind in new[] { "ship", "sub", "air", "helo" })
            {
                foreach (bool so in new[] { false, true })
                {
                    var u = View(kind);
                    if (!so) u.Caps.Remove("standing_orders");
                    string e = Missing(MenuModel.Ring(defs, layout, u), kind);
                    if (e != null) return e;
                }
            }
            // "More" with every action: no place, no layout
            foreach (var d in defs) d.Place = null;
            foreach (var kind in new[] { "ship", "sub", "air", "helo" })
            {
                var u = View(kind); u.Caps.Remove("standing_orders");
                var ring = MenuModel.Ring(defs, null, u);
                var more = ring.FirstOrDefault(n => n != null && n.Id == "more");
                if (more == null) return kind + ": no More group";
                if (RadialGlyphs.KeyFor(more) != "more") return "More group key";
                string e = Missing(ring, kind + "/more");
                if (e != null) return e;
            }
            if (RadialGlyphs.KeyFor(new MenuNode { Id = "modes", Kind = "group" }) != "group") return "modes group key";
            if (RadialGlyphs.KeyFor(new MenuNode { Id = "post_launch=evade", Kind = "value" }) != null) return "value node has a glyph";
            if (RadialGlyphs.KeyFor(new MenuNode { Id = "x", Kind = "action", Icon = "decoy_salvo" }) != "decoy_salvo") return "icon key";
            if (RadialGlyphs.KeyFor(new MenuNode { Id = "cease_all", Kind = "action", Icon = "nope" }) != "cease_all") return "icon fallback to id";
            if (RadialGlyphs.KeyFor(new MenuNode { Id = "unknown_action", Kind = "action" }) != null) return "unknown action has a glyph";
            if (RadialGlyphs.KeyFor(null) != null) return "null node";
            return null;
        }
    }
}
