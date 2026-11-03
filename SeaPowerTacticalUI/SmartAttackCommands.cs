using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-102 / ADR-0038 section 10: parsing of smart_attack / smart_attack_cancel / smart_attack_status. Pure (the dispatch lives in SmartAttackGame.cs).

    internal sealed class SmartAttackArgs { public int Uid; public int? TargetId; public string Ammo; public bool ConfirmNeutral, Pick; }

    internal static class SmartAttackCommandsParse
    {
        private static bool Int(Dictionary<string, object> c, string k, out int v, out bool present)
        {
            v = 0; present = false;
            object o;
            if (c == null || !c.TryGetValue(k, out o) || o == null) return true;
            present = true;
            if (!(o is double)) return false;
            double d = (double)o;
            if (d != Math.Floor(d) || Math.Abs(d) > int.MaxValue) return false;
            v = (int)d;
            return true;
        }

        private static bool Str(Dictionary<string, object> c, string k, out string v)
        {
            v = null;
            object o;
            if (c == null || !c.TryGetValue(k, out o) || o == null) return true;
            v = o as string;
            return v != null;
        }

        private static bool Flag(Dictionary<string, object> c, string k, out bool v)
        {
            v = false;
            object o;
            if (c == null || !c.TryGetValue(k, out o) || o == null) return true;
            if (!(o is bool)) return false;
            v = (bool)o;
            return true;
        }

        /// <summary>smart_attack {uid, target_id?, ammo?, confirm_neutral?, pick?}; err = bad_args.</summary>
        public static SmartAttackArgs Attack(Dictionary<string, object> cmd, out string err)
        {
            err = null;
            var a = new SmartAttackArgs();
            int uid, tid; bool hasUid, hasTid;
            if (!Int(cmd, "uid", out uid, out hasUid) || !hasUid) { err = "bad_args"; return null; }
            if (!Int(cmd, "target_id", out tid, out hasTid)) { err = "bad_args"; return null; }
            string ammo;
            if (!Str(cmd, "ammo", out ammo)) { err = "bad_args"; return null; }
            bool confirm, pick;
            if (!Flag(cmd, "confirm_neutral", out confirm) || !Flag(cmd, "pick", out pick)) { err = "bad_args"; return null; }
            a.Uid = uid; a.TargetId = hasTid ? (int?)tid : null; a.Ammo = ammo; a.ConfirmNeutral = confirm; a.Pick = pick;
            return a;
        }

        /// <summary>smart_attack_cancel {uid | attack_id}; at least one of them.</summary>
        public static bool Cancel(Dictionary<string, object> cmd, out int? uid, out string attackId, out string err)
        {
            err = null; uid = null; attackId = null;
            int u; bool hasUid;
            if (!Int(cmd, "uid", out u, out hasUid) || !Str(cmd, "attack_id", out attackId)) { err = "bad_args"; return false; }
            if (hasUid) uid = u;
            if (!uid.HasValue && attackId == null) { err = "bad_args"; return false; }
            return true;
        }

        /// <summary>smart_attack_status {uid?}; null = all.</summary>
        public static int? Status(Dictionary<string, object> cmd, out string err)
        {
            err = null;
            int u; bool has;
            if (!Int(cmd, "uid", out u, out has)) { err = "bad_args"; return null; }
            return has ? (int?)u : null;
        }

        private static Dictionary<string, object> C(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static string SelfTest()
        {
            string err;
            var a = Attack(C("uid", 5.0, "target_id", 12.0), out err);
            if (a == null || err != null || a.Uid != 5 || a.TargetId != 12 || a.Pick || a.ConfirmNeutral || a.Ammo != null) return "attack ok";
            a = Attack(C("uid", 5.0, "pick", true), out err);
            if (a == null || !a.Pick || a.TargetId.HasValue) return "pick without target";
            a = Attack(C("uid", 5.0, "target_id", 3.0, "ammo", "x", "confirm_neutral", true), out err);
            if (a == null || a.Ammo != "x" || !a.ConfirmNeutral) return "ammo/confirm";
            if (Attack(C("target_id", 3.0), out err) != null || err != "bad_args") return "uid missing";
            if (Attack(C("uid", 5.0, "target_id", "x"), out err) != null || err != "bad_args") return "target_id text";
            if (Attack(C("uid", 5.0, "ammo", 5.0), out err) != null || err != "bad_args") return "ammo number";
            if (Attack(C("uid", 5.0, "confirm_neutral", "yes"), out err) != null || err != "bad_args") return "confirm_neutral type";
            if (Attack(C("uid", "5"), out err) != null || err != "bad_args") return "uid text";
            if (Attack(C("uid", 5.5), out err) != null || err != "bad_args") return "uid fractional";
            int? uid; string aid;
            if (!Cancel(C("uid", 7.0), out uid, out aid, out err) || uid != 7 || aid != null) return "cancel uid";
            if (!Cancel(C("attack_id", "sa-3"), out uid, out aid, out err) || uid.HasValue || aid != "sa-3") return "cancel attack_id";
            if (Cancel(C(), out uid, out aid, out err) || err != "bad_args") return "cancel nothing";
            if (Cancel(C("uid", "x"), out uid, out aid, out err) || err != "bad_args") return "cancel bad uid";
            if (Status(C(), out err) != null || err != null) return "status all";
            if (Status(C("uid", 4.0), out err) != 4) return "status uid";
            Status(C("uid", "x"), out err);
            if (err != "bad_args") return "status bad";
            return null;
        }
    }
}
