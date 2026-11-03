using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge.Dev
{
    /// <summary>What a fire command is about; filled by FireGate and used for the [Dev][FIRE] line.</summary>
    internal sealed class FireInfo
    {
        public string Cmd;
        public int Shooter = -1, Target = -1, Uid = -1;
        public bool MapHostile;   // 0.27.10: the main map paints the target red (diag "map_hostile")
        public string ShooterName = "?", Class = "?", Rel = "none", Weapon = "-", Salvo = "-", Ammo = "-", Proposal = "-", Roe = "-", Scope = "unit";
    }

    /// <summary>ADR-0046: fire commands only at hostile targets, only while [Dev] FireCommands is on, always logged.
    /// "hostile" is the player's knowledge (PlottingTable track), answered by the diag slot: relationship hostile, or relationship unknown while
    /// the main map paints the track red (AttackHostility, the same rule as the ASW proposal and attack). Neutral / friendly: never.</summary>
    internal static class FireGate
    {
        public static int ToInt(object o, int def)
        {
            return o is double ? (int)(double)o : def;
        }

        private static Dictionary<string, object> Ask(IDiagPort diag, string json)
        {
            if (diag == null) return null;
            try { return Json.Parse(diag.Query(json)) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        private static string Str(Dictionary<string, object> d, string k, string def)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v is string ? (string)v : def;
        }

        private static void FillTarget(FireInfo info, IDiagPort diag, int id)
        {
            info.Target = id;
            var d = Ask(diag, "{\"q\":\"relation\",\"id\":" + id.ToString(CultureInfo.InvariantCulture) + "}");
            info.Rel = Str(d, "rel", "none");
            info.Class = Str(d, "class", "?");
            info.MapHostile = d != null && d.ContainsKey("map_hostile") && d["map_hostile"] is bool && (bool)d["map_hostile"];
        }

        private static void FillShooter(FireInfo info, IDiagPort diag, Dictionary<string, object> a)
        {
            info.Shooter = ToInt(a.ContainsKey("shooter") ? a["shooter"] : null, -1);
            var d = Ask(diag, "{\"q\":\"unit\",\"uid\":" + info.Shooter.ToString(CultureInfo.InvariantCulture) + "}");
            info.ShooterName = Str(d, "name", "?");
        }

        private static string Opt(Dictionary<string, object> a, string k)
        {
            object v;
            if (!a.TryGetValue(k, out v) || v == null) return "-";
            return v is double ? ((double)v).ToString(CultureInfo.InvariantCulture) : v.ToString();
        }

        /// <summary>null = allowed; otherwise "fire_disabled" | "target_not_hostile" | "no_proposal". `info` is always filled as far as known.</summary>
        public static string Check(string cmd, Dictionary<string, object> a, bool fireOn, IDiagPort diag, out FireInfo info)
        {
            info = new FireInfo { Cmd = cmd };
            switch (cmd)
            {
                case "attack":
                case "smart_attack":
                    info.Weapon = Opt(a, "weapon"); info.Salvo = Opt(a, "salvo"); info.Ammo = Opt(a, "ammo");
                    FillShooter(info, diag, a);
                    break;
                case "asw_attack":
                    info.Proposal = Opt(a, "proposal_id");
                    break;
                case "asw_strike":
                case "asw_strike_resume":
                    info.Proposal = Opt(a, "group") + "/" + Opt(a, "contact");   // T-173: no track of its own: the strike group decides the targets (eligibility in the mod)
                    break;
                case "strike_launch":   // T-341 C: the launch of a strike group; no target of its own: the plan holds only hostile identified ships (checked by the mod)
                    info.Proposal = Opt(a, "group") + "/" + Opt(a, "variant");
                    break;
                case "strike_probe":    // T-341 A: the probe ops; fire is a launch at a hostile identified ship, checked by the mod
                    info.Proposal = Opt(a, "op") + "/" + Opt(a, "uid") + "/" + Opt(a, "target_id");
                    break;
                case "set_roe":
                    info.Roe = Opt(a, "roe");
                    if (a.ContainsKey("formation")) { info.Uid = ToInt(a["formation"], -1); info.Scope = "formation"; }
                    else info.Uid = ToInt(a.ContainsKey("uid") ? a["uid"] : null, -1);
                    break;
            }
            if (!fireOn) return "fire_disabled";
            if (cmd == "set_roe") return null;
            if (cmd == "asw_strike" || cmd == "asw_strike_resume" || cmd == "strike_launch" || cmd == "strike_probe") return null;
            int target = -1;
            if (cmd == "asw_attack")
            {
                var st = Ask(diag, "{\"q\":\"state\",\"sections\":[\"asw\"],\"limit\":100}");
                var secs = st != null && st.ContainsKey("sections") ? st["sections"] as Dictionary<string, object> : null;
                var asw = secs != null && secs.ContainsKey("asw") ? secs["asw"] as Dictionary<string, object> : null;
                var props = asw != null && asw.ContainsKey("proposals") ? asw["proposals"] as List<object> : null;
                bool found = false;
                if (props != null)
                    foreach (object o in props)
                    {
                        var p = o as Dictionary<string, object>;
                        if (p != null && Str(p, "proposal_id", null) == Str(a, "proposal_id", "")) { found = true; target = ToInt(p.ContainsKey("contact_id") ? p["contact_id"] : null, -1); }
                    }
                if (!found) return diag == null ? "target_not_hostile" : "no_proposal";
            }
            else target = ToInt(a.ContainsKey("target") ? a["target"] : null, -1);
            FillTarget(info, diag, target);
            return AttackHostility.AttackOk(info.Rel, info.MapHostile) ? null : "target_not_hostile";
        }

        /// <summary>The exact journal line (tests rely on this format).</summary>
        public static string FireLine(FireInfo i, string result)
        {
            string head = "[Dev][FIRE] " + i.Cmd + " ";
            string tail = " → " + result;
            switch (i.Cmd)
            {
                case "attack":
                    return head + "shooter=" + i.Shooter + " " + i.ShooterName + " target=" + i.Target + " " + i.Class + " rel=" + i.Rel + " weapon=" + i.Weapon + " salvo=" + i.Salvo + tail;
                case "smart_attack":
                    return head + "shooter=" + i.Shooter + " " + i.ShooterName + " target=" + i.Target + " " + i.Class + " rel=" + i.Rel + " ammo=" + i.Ammo + tail;
                case "asw_strike":
                case "asw_strike_resume":
                case "strike_launch":
                case "strike_probe":
                    return head + "args=" + i.Proposal + tail;
                case "asw_attack":
                    return head + "proposal=" + i.Proposal + " target=" + i.Target + " rel=" + i.Rel + (i.MapHostile && i.Rel != "hostile" ? " map=hostile" : "") + tail;
                default:
                    return head + "uid=" + i.Uid + " roe=" + i.Roe + " scope=" + i.Scope + tail;
            }
        }
    }
}
