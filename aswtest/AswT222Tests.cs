using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-222: a flapping `live` flag of a submarine track (held 90 s after the last live fix) and the reason of "no shooter". `aswtest t222 <name>`.
    internal static class AswT222Tests
    {
        private static readonly string[] Names = { "held-live-pure", "flap-assign", "flap-unknown-confirm", "no-shooter-why", "no-shooter-card", "hold-90", "fuel-pure", "fuel-soft", "fuel-return-extra" };

        private static Dictionary<string, object> P(string json) { return AswT173AttackTests.P(json); }
        private static void Run(AswController c, AswFakeGame f, int s) { AswT173AttackTests.Run(c, f, s); }
        private static int Count(AswFakeGame f, string needle) { return f.Logs.Count(l => l.Contains(needle)); }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "held-live-pure": e = HeldLivePure(); break;
                    case "flap-assign": e = FlapAssign(); break;
                    case "flap-unknown-confirm": e = FlapUnknown(); break;
                    case "no-shooter-why": e = NoShooterWhy(); break;
                    case "no-shooter-card": e = NoShooterCard(); break;
                    case "hold-90": e = Hold90(); break;
                    case "fuel-pure": e = FuelPure(); break;
                    case "fuel-soft": e = FuelSoft(); break;
                    default: e = FuelReturnExtra(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static string HeldLivePure()
        {
            if (!AswStrikeMath.HeldLive(true, -1, 100, 45)) return "live now";
            if (!AswStrikeMath.HeldLive(false, 70, 100, 45)) return "fix 30 s ago";
            if (AswStrikeMath.HeldLive(false, 54, 100, 45)) return "fix 46 s ago";
            if (AswStrikeMath.HeldLive(false, -1, 100, 45)) return "unknown fix";
            if (AswStrikeMath.HeldLive(false, 130, 100, 45)) return "fix from the future";
            return null;
        }

        private static string Hold90()
        {
            if (AswStrikeMath.StrikeLiveHoldS != 90) return "hold " + AswStrikeMath.StrikeLiveHoldS;
            if (!AswStrikeMath.HeldLive(false, 20, 100, AswStrikeMath.StrikeLiveHoldS)) return "fix 80 s ago";
            if (AswStrikeMath.HeldLive(false, 9, 100, AswStrikeMath.StrikeLiveHoldS)) return "fix 91 s ago";
            return null;
        }

        private static string FuelPure()
        {
            double r, n;
            if (!AswStrikeMath.StrikeFuelOk(0.15, 9000, 600, 11, 100, out r, out n)) return "0.15 / 11 nm: " + r + " < " + n;
            if (Math.Abs(r - 750) > 0.01 || Math.Abs(n - 456) > 0.01) return "numbers " + r + " " + n;
            if (AswStrikeMath.StrikeFuelOk(0.10, 9000, 600, 22, 100, out r, out n)) return "0.10 / 22 nm";
            if (AswStrikeMath.StrikeFuelOk(0.0667, 9000, 600, 0, 100, out r, out n)) return "empty tank";
            double r2, n2;
            AswStrikeMath.StrikeFuelOk(0.5, 9000, 600, -2, 100, out r, out n);
            AswStrikeMath.StrikeFuelOk(0.5, 9000, 600, 0, 100, out r2, out n2);
            if (n != n2) return "negative run not zero";
            if (AswStrikeMath.StrikeFuelOk(1, 9000, 0, 1, 0, out r, out n)) return "kn 0";
            var rnd = new Random(223);
            for (int i = 0; i < 50; i++)
            {
                double fr = rnd.NextDouble(), en = 3000 + rnd.NextDouble() * 9000, tr = rnd.NextDouble() * 1500, d = rnd.NextDouble() * 40, kn = 60 + rnd.NextDouble() * 80;
                bool old = Math.Max(0, (fr * en - tr) * 0.9) >= d / kn * 3600.0 + 300;
                if (old && !AswStrikeMath.StrikeFuelOk(fr, en, tr, d - 1, kn, out r, out n)) return "monotonic " + i;
            }
            return null;
        }

        private static string FuelSoft()
        {
            var f = new AswFakeGame();
            Helo(f, 7, 0.15);
            Sub(f, 7057, 12);
            var c = Strike(f);
            Run(c, f, 5);
            if (c.RunShooter(7057) == 0) return "no shooter: " + AswT173AttackTests.Dump(f);
            if (Count(f, "no shooter") != 0) return "no shooter line";
            var f2 = new AswFakeGame();
            Helo(f2, 1, 0.1);
            Sub(f2, 7, 23);
            var c2 = Strike(f2);
            Run(c2, f2, 5);
            if (Count(f2, "why=1:fuel(") != 1) return "control: " + Count(f2, "why=1:fuel(");
            return null;
        }

        /// <summary>T-223 D2: the way home from the drop point counts; a deck near the datum adds nothing.</summary>
        private static string FuelReturnExtra()
        {
            double r, n, r0, n0;
            AswStrikeMath.StrikeFuelOk(0.15, 9000, 600, 11, 100, out r0, out n0);
            if (AswStrikeMath.StrikeFuelOk(0.15, 9000, 600, 11, 11, 100, out r, out n)) return "pure: extra 11 nm passes: " + r + " vs " + n;
            if (Math.Abs(n - n0 - 396) > 0.01) return "pure numbers " + (n - n0);
            if (!AswStrikeMath.StrikeFuelOk(0.15, 9000, 600, 11, -5, 100, out r, out n) || Math.Abs(n - n0) > 0.01) return "pure: negative extra not zero";
            var f = new AswFakeGame();
            var m = Helo(f, 7, 0.15); m.ShipUid = 5000;
            f.Decks[5000] = new AswTarget { Uid = 5000, Name = "Deck", Lat = f.Tgt.Lat, Lon = f.Tgt.Lon };
            Sub(f, 7057, 12);
            var c = Strike(f);
            Run(c, f, 5);
            if (Count(f, "why=7:fuel(") != 1) return "deck behind the helo: " + AswT173AttackTests.Dump(f);
            var f2 = new AswFakeGame();
            var m2 = Helo(f2, 7, 0.15); m2.ShipUid = 5000;
            var p2 = AswMath.Offset(new GeoPt(f2.Tgt.Lat, f2.Tgt.Lon), 90, 12);
            f2.Decks[5000] = new AswTarget { Uid = 5000, Name = "Deck", Lat = p2.Lat, Lon = p2.Lon };
            Sub(f2, 7057, 12);
            var c2 = Strike(f2);
            Run(c2, f2, 5);
            if (c2.RunShooter(7057) == 0) return "deck at the datum: " + AswT173AttackTests.Dump(f2);
            return null;
        }

        private static AswContact Sub(AswFakeGame f, int id, double eastNm, string side = "hostile")
        {
            var p = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, eastNm);
            var s = new AswContact { Id = id, Name = "Sub #" + id, Lat = p.Lat, Lon = p.Lon, Source = "live", Side = side, Live = true, FixS = f.Now, ClassName = "Kilo", Classified = true, MapPosture = side == "hostile" ? "Enemy" : "Unknown" };
            f.Subs.Add(s);
            return s;
        }

        private static AswMember Helo(AswFakeGame f, int uid, double fuel = 0.9, int torps = 2)
        {
            var m = AswFakeGame.Helo(uid, "air"); m.Torpedoes = torps; m.FuelFrac = fuel; m.Lat = f.Tgt.Lat; m.Lon = f.Tgt.Lon;
            f.Mem.Add(m); f.FixPos.Add(uid);
            return m;
        }

        private static AswController Strike(AswFakeGame f)
        {
            var c = AswT173AttackTests.Make(f);
            string uids = string.Join(",", f.Mem.Select(m => m.Uid.ToString()).ToArray());
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + uids + "]}"));
            string r = c.Order(P("{\"group\":\"G\",\"order\":\"strike\"}"));
            if (AswController.IsErr(r)) throw new InvalidOperationException(r);
            return c;
        }

        /// <summary>The game's pattern: live 18 s, not live 18 s; the last live fix time follows while live.</summary>
        private static void Flap(AswController c, AswFakeGame f, AswContact sub, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                f.Advance(1);
                sub.Live = (i / 18) % 2 == 0;
                if (sub.Live) sub.FixS = f.Now;
                f.Tick(c);
            }
        }

        private static string FlapAssign()
        {
            var f = new AswFakeGame();
            Helo(f, 570);
            var sub = Sub(f, 7057, 6);
            var c = Strike(f);
            Flap(c, f, sub, 120);
            if (c.RunShooter(7057) == 0) return "no shooter: " + AswT173AttackTests.Dump(f);
            if (f.Logs.Exists(l => l.Contains("reason=track_not_live"))) return "track_not_live in the log";
            if (f.Logs.Exists(l => l.Contains("ok=0 reason=not_live"))) return "not_live in the log";
            // control: a really lost track still goes to reacquire, 90 s after the last fix
            var f2 = new AswFakeGame();
            Helo(f2, 570);
            var s2 = Sub(f2, 7057, 6);
            var c2 = Strike(f2);
            Run(c2, f2, 30);
            if (c2.RunShooter(7057) == 0) return "control: no shooter";
            s2.Live = false; s2.FixS = f2.Now;
            double last = f2.Now;
            Run(c2, f2, 80);
            if (c2.RunState(7057) == "reacquire") return "control: reacquire before the hold ended";
            Run(c2, f2, 12);
            if (c2.RunState(7057) != "reacquire" || !f2.Logs.Exists(l => l.Contains("reason=track_not_live"))) return "control: no reacquire " + (f2.Now - last) + " s after the fix: " + c2.RunState(7057);
            return null;
        }

        private static string FlapUnknown()
        {
            var f = new AswFakeGame();
            Helo(f, 570);
            var sub = Sub(f, 7057, 6, "unknown");
            var c = Strike(f);
            Flap(c, f, sub, 100);
            if (!f.Logs.Exists(l => l.Contains("target contact=7057 ok=1"))) return "never confirmed: " + string.Join(" | ", f.Logs.Where(l => l.Contains("7057")).Take(4).ToArray());
            return null;
        }

        private static string NoShooterWhy()
        {
            var f = new AswFakeGame();
            Helo(f, 1, 0.1);
            Sub(f, 7, 23);
            var c = Strike(f);
            Run(c, f, 5);
            if (Count(f, "asw strike G no shooter contact=7 why=1:fuel(") != 1) return "lines: " + Count(f, "no shooter") + " " + string.Join(" | ", f.Logs.Where(l => l.Contains("strike")).ToArray());
            Run(c, f, 30);
            if (Count(f, "no shooter") != 1) return "repeated: " + Count(f, "no shooter");
            // a second machine joins the signature (a machine without torpedoes leaves the group at the order)
            var f2 = new AswFakeGame();
            Helo(f2, 1, 0.1); Helo(f2, 2, 0.1);
            Sub(f2, 7, 23);
            var c2 = Strike(f2);
            Run(c2, f2, 5);
            if (!f2.Logs.Exists(l => l.Contains("no shooter contact=7 why=") && l.Contains("1:fuel(") && l.Contains(",2:fuel("))) return "no both: " + string.Join(" | ", f2.Logs.Where(l => l.Contains("strike")).ToArray());
            // refuelled: assigned, no new line
            f.Mem[0].FuelFrac = 0.9;
            int n = Count(f, "no shooter");
            Run(c, f, 10);
            if (c.RunShooter(7) == 0) return "not assigned after the fuel: " + AswT173AttackTests.Dump(f);
            if (Count(f, "no shooter") != n) return "line after the assignment";
            return null;
        }

        private static AswSubAlertView Card(AswController c, int id) { return c.View().SubAlerts.FirstOrDefault(a => a.ContactId == id); }

        private static string NoShooterCard()
        {
            var f = new AswFakeGame();
            Helo(f, 1, 0.1);
            Sub(f, 7, 23);
            var c = Strike(f);
            Run(c, f, 5);
            var a = Card(c, 7);
            if (a == null) return "no card";
            if (a.Line != "no_shooter" || a.LineArgs == null || a.LineArgs.Length != 1 || a.LineArgs[0] != UiText.Get("asw.toast.why.fuel")) return "line " + a.Line + " args " + (a.LineArgs == null ? "null" : string.Join(",", a.LineArgs));
            if (UiText.Get("asw.toast.no_shooter").StartsWith("asw.toast", StringComparison.Ordinal)) return "no text";
            f.AllowAttack = false;
            Run(c, f, 5);
            a = Card(c, 7);
            if (a == null || a.Line != "disabled") return "disabled: " + (a == null ? "null" : a.Line);
            f.AllowAttack = true;
            // nobody has torpedoes: the old line
            var f2 = new AswFakeGame();
            Helo(f2, 1, 0.9, 0);
            Sub(f2, 7, 6);
            var c2 = Strike(f2);
            Run(c2, f2, 5);
            a = Card(c2, 7);
            if (a == null || a.Line != "no_armed") return "no_armed: " + (a == null ? "null" : a.Line);
            return null;
        }
    }
}
