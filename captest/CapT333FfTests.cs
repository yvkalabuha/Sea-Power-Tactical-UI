using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-333 / ADR-0129 section 9: friendly-fire guard (CapFfGuard on a fake IFfGame). Sections of "captest t333-ff <name>".
    internal static class CapT333FfTests
    {
        private sealed class Task { public FfShot Shot; public bool Throw; public string Swapped; }

        private sealed class FakeFf : IFfGame
        {
            public double T;
            public List<FfFriend> Fr = new List<FfFriend>();
            public List<FfAlt> AltList = new List<FfAlt>();
            public bool Swappable = true, SwapFails;
            public double Now { get { return T; } }
            public bool ReadShot(object task, out FfShot shot)
            {
                var t = task as Task; shot = null;
                if (t == null) return false;
                if (t.Throw) throw new InvalidOperationException("unreadable");
                shot = t.Shot; return true;
            }
            public IList<FfFriend> Friends(double now) { return Fr; }
            public IList<FfAlt> Alts(int uid) { return AltList; }
            public bool CanSwap(object task) { return Swappable; }
            public string Swap(object task, string ammo) { if (SwapFails) return "refused"; ((Task)task).Swapped = ammo; return null; }
        }

        private sealed class Rig
        {
            public FakeFf G = new FakeFf();
            public bool On = true, Cap = true;
            public List<string> Logs = new List<string>(), Evts = new List<string>();
            public CapFfGuard Guard;
            public Rig() { Guard = new CapFfGuard(G, uid => Cap, () => On, l => Logs.Add(l), (n, j) => Evts.Add(n + " " + j)); }
        }

        private static GeoPt At(double e, double n) { return new GeoPt(10.5 + n / 60.0, 11.0 + e / 60.0); }
        private static Task Shot(string seeker, bool auto = true)
        {
            return new Task { Shot = new FfShot { Shooter = 1, Auto = auto, TargetAir = true, TargetKey = 7, From = At(0, 0), To = At(0, 10), Ammo = "aim9", Seeker = seeker } };
        }
        private static Rig WithFriend() { var r = new Rig(); r.G.Fr.Add(new FfFriend { Uid = 2, Pos = At(1.0, 11.1) }); return r; }

        public static string Run(string only = null)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("ff-hold-release") && (e = HoldRelease()) != null) return "ff-hold-release: " + e;
            if (want("ff-clear") && (e = Clear()) != null) return "ff-clear: " + e;
            if (want("ff-swap") && (e = SwapCase()) != null) return "ff-swap: " + e;
            if (want("ff-null") && (e = Null()) != null) return "ff-null: " + e;
            if (want("ff-off") && (e = Off()) != null) return "ff-off: " + e;
            if (want("ff-not-cap") && (e = NotCap()) != null) return "ff-not-cap: " + e;
            if (want("ff-allow") && (e = Allow()) != null) return "ff-allow: " + e;
            return null;
        }

        private static string HoldRelease()
        {
            var r = WithFriend(); var t = Shot("ir");
            r.G.T = 100; if (!r.Guard.Veto(t)) return "first call must hold";
            if (r.Logs.Count != 1 || !r.Logs[0].Contains("ff hold") || r.Evts.Count != 1) return "hold log/event: " + string.Join("|", r.Logs);
            r.G.T = 105; if (!r.Guard.Veto(t)) return "hold at 5 s";
            if (r.Logs.Count != 1) return "hold must log once";
            if (r.Guard.Book.Holding(105).Count != 1) return "holding list";
            r.G.T = 110.5; if (r.Guard.Veto(t)) return "release at 10.5 s";
            if (r.Logs.Count != 2 || !r.Logs[1].Contains("ff release") || !r.Logs[1].Contains("why=timeout")) return "release log: " + string.Join("|", r.Logs);
            if (r.Evts.Count != 2 || !r.Evts[1].StartsWith("cap_ff ") || !r.Evts[1].Contains("\"op\":\"release\"") || !r.Evts[1].Contains("\"why\":\"timeout\"")) return "release event: " + string.Join("|", r.Evts);
            r.G.T = 120; if (r.Guard.Veto(t)) return "cooldown allows";
            if (r.Logs.Count != 2) return "cooldown quiet";
            r.G.T = 171; if (!r.Guard.Veto(t)) return "hold again after cooldown";
            return null;
        }

        private static string Clear()
        {
            var r = WithFriend(); var t = Shot("arh");
            r.G.T = 0; if (!r.Guard.Veto(t)) return "arh holds";
            r.G.Fr[0].Pos = At(6, 11); r.G.T = 2;
            if (r.Guard.Veto(t)) return "friend left the cone: fire";
            if (r.Logs.Count != 2 || !r.Logs[1].Contains("ff release") || !r.Logs[1].Contains("why=clear")) return "clear log: " + string.Join("|", r.Logs);
            return null;
        }

        private static string SwapCase()
        {
            bool keep = CapFfMath.SwapEnabled;
            try
            {
                var r = WithFriend(); r.G.AltList.Add(new FfAlt { Ammo = "aim7", Seeker = "sarh", MaxNm = 30, Left = 2 });
                var t = Shot("ir");
                CapFfMath.SwapEnabled = false;
                if (!r.Guard.Veto(t) || t.Swapped != null) return "swap off must hold";
                CapFfMath.SwapEnabled = true;
                r = WithFriend(); r.G.AltList.Add(new FfAlt { Ammo = "aim7", Seeker = "sarh", MaxNm = 30, Left = 2 });
                t = Shot("ir");
                if (r.Guard.Veto(t)) return "swap: veto must be false";
                if (t.Swapped != "aim7") return "Swap not called";
                if (r.Logs.Count != 1 || !r.Logs[0].Contains("ff swap")) return "swap log: " + string.Join("|", r.Logs);
                r = WithFriend(); r.G.AltList.Add(new FfAlt { Ammo = "aim7", Seeker = "sarh", MaxNm = 30, Left = 2 }); r.G.Swappable = false;
                t = Shot("ir");
                if (!r.Guard.Veto(t) || t.Swapped != null) return "CanSwap=false must hold";
                r = WithFriend(); r.G.AltList.Add(new FfAlt { Ammo = "aim7", Seeker = "sarh", MaxNm = 30, Left = 2 }); r.G.SwapFails = true;
                t = Shot("ir");
                if (!r.Guard.Veto(t)) return "failed swap must hold";
                return null;
            }
            finally { CapFfMath.SwapEnabled = keep; }
        }

        private static string Null()
        {
            var r = WithFriend();
            if (r.Guard.Veto(new Task { Throw = true })) return "throwing ReadShot must allow";
            if (r.Guard.Veto(null)) return "null task";
            if (r.Guard.Veto("foreign")) return "unreadable task";
            var g = new CapFfGuard(new FakeFf(), uid => { throw new Exception("x"); }, () => true, null, null);
            if (g.Veto(Shot("ir"))) return "throwing isCapAir";
            return null;
        }

        private static string Off()
        {
            var r = WithFriend(); r.On = false;
            if (r.Guard.Veto(Shot("ir"))) return "off must allow";
            r.On = true; if (!r.Guard.Veto(Shot("ir"))) return "on holds";
            return null;
        }

        private static string NotCap()
        {
            var r = WithFriend(); r.Cap = false;
            if (r.Guard.Veto(Shot("ir"))) return "not a CAP shooter must allow";
            return null;
        }

        private static string Allow()
        {
            var r = WithFriend();
            if (r.Guard.Veto(Shot("sarh"))) return "sarh";
            if (r.Guard.Veto(Shot("cmd"))) return "cmd";
            if (r.Guard.Veto(Shot(null))) return "null seeker";
            if (r.Guard.Veto(Shot("ir", false))) return "not auto (player order)";
            r.G.Fr[0].Pos = At(2.1, 10);
            if (r.Guard.Veto(Shot("ir"))) return "2.1 nm";
            r.G.Fr[0].Pos = At(1.2, 5); var t = Shot("ir"); t.Shot.To = At(0, 5);
            if (r.Guard.Veto(t)) return "outside the cone";
            if (r.Logs.Count != 0) return "no log for allow";
            return null;
        }
    }
}
