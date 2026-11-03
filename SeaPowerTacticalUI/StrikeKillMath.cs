using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>One missile type of the allocation on one target: Loss = 2*D of one hit, Sigma = the leak of one missile, Count = how many.</summary>
    internal struct StrikeKillShot { public double Loss, Sigma; public int Count; }

    /// <summary>T-342 / ADR-0133: what it takes to sink one target. H/H1 hits (without / with the margin), M missiles for the threshold (-1 = not reachable up to KillMaxM or unknown class).</summary>
    internal sealed class StrikeKillNeed
    {
        public int H = -1, H1 = -1, M = -1;
        public double P, PBreach, Sigma, Loss, Budget, BudgetLeft;
        public bool Short;
    }

    /// <summary>T-342 (ADR-0133 / design 6.1): the sinking model of the strike plan. A ship is destroyed when the sum of 2*D of the hits exceeds 5*maxI (HitEstimateMath, no copy of the formulas).
    /// Pure: no game, no Unity.</summary>
    internal static class StrikeKillMath
    {
        private static HeWarhead Wh(string w)
        {
            switch ((w ?? "").ToLowerInvariant())
            {
                case "heat": return HeWarhead.Heat;
                case "frag": return HeWarhead.Frag;
                case "cluster": return HeWarhead.Cluster;
                default: return HeWarhead.Normal;
            }
        }

        /// <summary>L = 2 * D of one missile hit on a ship.</summary>
        public static double HitLoss(StrikeMissile m, double bonus)
        {
            if (m == null) return 0;
            var a = new HeAmmo { Kind = HeAmmoKind.Missile, Warhead = Wh(m.Warhead), Power = m.WarheadPower, DudPower = m.DudPower, DamageMult = m.DamageMult > 0 ? m.DamageMult : 1 };
            return 2 * HitEstimateMath.Damage(a, HeTargetKind.Ship, bonus);
        }

        /// <summary>floor(B / L) + 1; budget &lt;= 0 -> 1; loss &lt;= 0 -> int.MaxValue.</summary>
        public static int HitsToSink(double budget, double loss)
        {
            if (loss <= 0) return int.MaxValue;
            if (budget <= 0) return 1;
            double q = Math.Floor(budget / loss + 1e-9);
            return q >= int.MaxValue - 1 ? int.MaxValue : (int)q + 1;
        }

        /// <summary>The margin for the missiles the AA gun weakens: a ship with air defence (Ksub >= 2) or a CIWS gets StrikeCalib.KillMarginAd, a weak one KillMarginWeak.</summary>
        public static int Margin(StrikeShip s)
        {
            bool ad = s != null && (s.Ad.Ksub >= 2 || s.HasCiws);
            return (int)(ad ? StrikeCalib.KillMarginAd : StrikeCalib.KillMarginWeak);
        }

        public static double BudgetLeft(double budget, double priorLoss, double weight) { return Math.Max(0, budget - weight * priorLoss); }

        /// <summary>P(Bin(m, sigma) >= h1).</summary>
        public static double PSunkOne(int m, double sigma, int h1)
        {
            if (h1 <= 0) return 1;
            if (m < h1 || sigma <= 0) return 0;
            if (sigma >= 1) return 1;
            double q = 1 - sigma, pmf = Math.Pow(q, m), below = 0;   // P(X = 0)
            for (int k = 0; k < h1; k++)
            {
                below += pmf;
                pmf *= (double)(m - k) / (k + 1) * (sigma / q);
            }
            return Math.Max(0, Math.Min(1, 1 - below));
        }

        public static double PAtLeastOne(int m, double sigma)
        {
            if (m <= 0 || sigma <= 0) return 0;
            return sigma >= 1 ? 1 : 1 - Math.Pow(1 - sigma, m);
        }

        private static double LossMin(IList<StrikeKillShot> shots)
        {
            double lm = double.MaxValue;
            if (shots != null) foreach (var s in shots) if (s.Count > 0 && s.Loss > 0 && s.Loss < lm) lm = s.Loss;
            return lm == double.MaxValue ? 0 : lm;
        }

        /// <summary>P(sum of X_i * L_i > need), X_i ~ Bernoulli(sigma_i) for every missile. One type: the binomial (exact). Mixed: a DP over the damage on a grid of L_min / 8.</summary>
        public static double PSunkMixed(IList<StrikeKillShot> shots, double need)
        {
            var act = new List<StrikeKillShot>();
            if (shots != null) foreach (var s in shots) if (s.Count > 0) act.Add(s);
            if (act.Count == 0) return need < 0 ? 1 : 0;
            if (need < 0) return 1;
            double lmin = LossMin(act);
            if (lmin <= 0) return 0;
            bool oneType = true;
            for (int i = 1; i < act.Count; i++) if (Math.Abs(act[i].Loss - act[0].Loss) > 1e-9 * act[0].Loss) oneType = false;
            if (oneType)
            {
                int m = 0; double none = 1;   // equal losses: the sum of Bernoulli with different sigma = DP over the count
                foreach (var s in act) m += s.Count;
                bool sameSigma = true;
                foreach (var s in act) if (Math.Abs(s.Sigma - act[0].Sigma) > 1e-15) sameSigma = false;
                int h = HitsToSink(need, act[0].Loss);
                if (sameSigma) return PSunkOne(m, act[0].Sigma, h);
                var dist = new double[m + 1]; dist[0] = none; int cnt = 0;
                foreach (var s in act)
                    for (int c = 0; c < s.Count; c++)
                    {
                        for (int k = cnt + 1; k >= 0; k--) dist[k] = dist[k] * (1 - Math.Min(1, Math.Max(0, s.Sigma))) + (k > 0 ? dist[k - 1] * Math.Min(1, Math.Max(0, s.Sigma)) : 0);
                        cnt++;
                    }
                double ge = 0; for (int k = Math.Min(h, m + 1); k <= m; k++) ge += dist[k];
                return Math.Max(0, Math.Min(1, ge));
            }
            double u = lmin / 8;
            int cells = (int)Math.Floor(need / u + 1e-9);   // sum of units must exceed this
            var d = new double[cells + 2]; d[0] = 1;       // d[cells + 1] = the absorbing "sunk" bucket
            foreach (var s in act)
            {
                int w = Math.Max(1, (int)Math.Round(s.Loss / u));
                double sg = Math.Min(1, Math.Max(0, s.Sigma));
                for (int c = 0; c < s.Count; c++)
                {
                    double absorbed = 0;
                    for (int j = Math.Max(0, cells + 1 - w); j <= cells; j++) absorbed += d[j] * sg;
                    for (int k = cells; k >= 0; k--) d[k] = d[k] * (1 - sg) + (k - w >= 0 ? d[k - w] * sg : 0);
                    d[cells + 1] += absorbed;
                }
            }
            return Math.Max(0, Math.Min(1, d[cells + 1]));
        }

        private static StrikeKillNeed Unknown(StrikeShip s)
        {
            return new StrikeKillNeed { Short = true, Budget = s != null ? s.KillBudget : -1 };
        }

        /// <summary>The smallest M &lt;= KillMaxM with P(sunk) >= threshold for ONE missile type; sigmaOf(M) is recomputed for every M (the leak depends on the size of the salvo).</summary>
        public static StrikeKillNeed Need(StrikeShip s, StrikeMissile m, Func<int, double> sigmaOf, double bonus, double threshold)
        {
            if (s == null || m == null || s.KillBudget < 0) return Unknown(s);
            double loss = HitLoss(m, bonus), left = BudgetLeft(s.KillBudget, s.PriorLoss, StrikeCalib.PriorHitWeight);
            var r = new StrikeKillNeed { Loss = loss, Budget = s.KillBudget, BudgetLeft = left, Short = true };
            int margin = Margin(s);
            r.H = HitsToSink(left, loss);
            if (r.H == int.MaxValue) return r;
            r.H1 = r.H + margin;
            for (int M = 1; M <= StrikeCalib.KillMaxM; M++)
            {
                if (M < r.H1) continue;
                double sg = sigmaOf(M), p = PSunkOne(M, sg, r.H1);
                if (p >= threshold) { r.M = M; r.P = p; r.Sigma = sg; r.PBreach = PAtLeastOne(M, sg); r.Short = false; return r; }
            }
            double s64 = sigmaOf(StrikeCalib.KillMaxM);
            r.Sigma = s64; r.P = PSunkOne(StrikeCalib.KillMaxM, s64, r.H1); r.PBreach = PAtLeastOne(StrikeCalib.KillMaxM, s64);
            return r;
        }

        /// <summary>P(sunk) for a given allocation (mixed types allowed). M = the number of missiles; H / H1 count the cheapest hit.</summary>
        public static StrikeKillNeed Eval(StrikeShip s, IList<StrikeKillShot> shots, double bonus, double threshold)
        {
            if (s == null || s.KillBudget < 0) return Unknown(s);
            double left = BudgetLeft(s.KillBudget, s.PriorLoss, StrikeCalib.PriorHitWeight), lmin = LossMin(shots);
            var r = new StrikeKillNeed { Budget = s.KillBudget, BudgetLeft = left, Loss = lmin };
            int margin = Margin(s), n = 0; double none = 1;
            if (shots != null) foreach (var x in shots) if (x.Count > 0) { n += x.Count; none *= Math.Pow(1 - Math.Min(1, Math.Max(0, x.Sigma)), x.Count); if (r.Sigma == 0) r.Sigma = x.Sigma; }
            r.M = n; r.PBreach = n > 0 ? 1 - none : 0;
            if (lmin <= 0) { r.Short = true; return r; }
            r.H = HitsToSink(left, lmin); r.H1 = r.H + margin;
            r.P = PSunkMixed(shots, left + margin * lmin);
            r.Short = r.P < threshold;
            return r;
        }


        // ------------------------------------------------------------------ self-test (stand sections kill-h | kill-p | kill-need | kill-mixed | kill-residual)

        // (class, displacement t, armor) and (ammo, Power): the table of docs/research/T-342-strike-v2-recon.md 2.2 (bonus 0.25), reproduced by tests/test_sea_power_strike_kill_math.py
        internal static readonly string[] GridShips = { "nimitz", "ticonderoga", "kidd", "slava", "sovremenny", "udaloy", "perry", "kynda", "krivak" };
        private static readonly double[] GridDisp = { 100000, 9600, 9630, 11490, 7940, 7570, 4100, 5500, 3190 };
        private static readonly string[] GridArmor = { "moderate", "minor", "minor", "minor", "none", "none", "none", "none", "none" };
        internal static readonly string[] GridAmmos = { "am-39", "yj-6", "harpoon", "as-4" };
        private static readonly double[] GridPower = { 39, 66, 45, 93 };
        private static readonly int[][] GridH =
        {
            new[] { 7, 4, 6, 3 }, new[] { 3, 2, 3, 2 }, new[] { 3, 2, 3, 2 }, new[] { 4, 2, 3, 2 }, new[] { 3, 2, 3, 1 }, new[] { 3, 2, 2, 1 }, new[] { 2, 2, 2, 1 }, new[] { 3, 2, 2, 1 }, new[] { 2, 2, 2, 1 },
        };

        /// <summary>The grid for the stand ("ship ammo h budget loss"): the python test recomputes it from the formulas.</summary>
        public static string Grid()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < GridShips.Length; i++)
                for (int j = 0; j < GridAmmos.Length; j++)
                {
                    double b = Budget(i), l = HitLoss(Msl(j), 0.25);
                    sb.Append(GridShips[i]).Append(' ').Append(GridAmmos[j]).Append(' ').Append(HitsToSink(b, l)).Append(' ').Append(b.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append(l.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                }
            return sb.ToString();
        }

        private static double Budget(int shipIdx) { return HitEstimateMath.Budget(new HeClass { Kind = HeTargetKind.Ship, Displacement = GridDisp[shipIdx], Armor = GridArmor[shipIdx] }); }
        private static StrikeMissile Msl(int ammoIdx) { return new StrikeMissile { Ammo = GridAmmos[ammoIdx], WarheadPower = GridPower[ammoIdx], DudPower = 0 }; }
        private static StrikeShip Ship(double budget, bool ad, bool ciws = false) { return new StrikeShip { KillBudget = budget, Ad = new StrikeAd { Ksub = ad ? 8.86 : 0 }, HasCiws = ciws }; }

        public static string SelfTest(string part = "all")
        {
            string e;
            if (part == "all" || part == "h") { e = TestH(); if (e != null) return e; }
            if (part == "all" || part == "p") { e = TestP(); if (e != null) return e; }
            if (part == "all" || part == "need") { e = TestNeed(); if (e != null) return e; }
            if (part == "all" || part == "mixed") { e = TestMixed(); if (e != null) return e; }
            if (part == "all" || part == "residual") { e = TestResidual(); if (e != null) return e; }
            return null;
        }

        private static string TestH()
        {
            for (int i = 0; i < GridShips.Length; i++)
                for (int j = 0; j < GridAmmos.Length; j++)
                {
                    int h = HitsToSink(Budget(i), HitLoss(Msl(j), 0.25));
                    if (h != GridH[i][j]) return "h " + GridShips[i] + "/" + GridAmmos[j] + " " + h + " != " + GridH[i][j];
                }
            if (HitsToSink(0, 100) != 1 || HitsToSink(-5, 100) != 1 || HitsToSink(100, 0) != int.MaxValue) return "h edges";
            if (HitsToSink(200, 100) != 3 || HitsToSink(199, 100) != 2) return "h boundary";
            // the damage multiplier and the warhead go through HitEstimateMath
            var heat = new StrikeMissile { WarheadPower = 90, Warhead = "heat" };
            if (Math.Abs(HitLoss(heat, 0) - 2 * 30 * 1.1) > 1e-9) return "heat loss";
            if (Math.Abs(HitLoss(new StrikeMissile { WarheadPower = 40, DamageMult = 1.5 }, 0.25) - 2 * 40 * 1.25 * 1.1 * 1.5) > 1e-9) return "mult loss";
            if (HitLoss(null, 0.25) != 0) return "null loss";
            // the margin: Kidd (Ksub >= 2) 1, Krivak without CIWS 0, Krivak with a CIWS 1
            if (Margin(Ship(319, true)) != 1 || Margin(Ship(184, false)) != 0 || Margin(Ship(184, false, true)) != 1) return "margin";
            if (Margin(null) != 0) return "margin null";
            if (BudgetLeft(300, 100, 0.7) != 230 || BudgetLeft(300, 1000, 0.7) != 0 || BudgetLeft(300, 0, 0.7) != 300) return "budget left";
            return null;
        }

        private static double Binom(int m, double s, int h)
        {
            double sum = 0;
            for (int k = h; k <= m; k++) { double c = 1; for (int i = 1; i <= k; i++) c = c * (m - k + i) / i; sum += c * Math.Pow(s, k) * Math.Pow(1 - s, m - k); }
            return sum;
        }

        private static string TestP()
        {
            foreach (double s in new[] { 0.2, 0.43, 0.8 })
                for (int m = 1; m <= 64; m += 3)
                    for (int h = 1; h <= 8; h++)
                        if (Math.Abs(PSunkOne(m, s, h) - Binom(m, s, h)) > 1e-9) return "binomial m=" + m + " h=" + h + " s=" + s;
            if (PSunkOne(3, 0.5, 4) != 0 || PSunkOne(5, 0.5, 0) != 1 || PSunkOne(5, 0, 1) != 0 || PSunkOne(5, 1, 5) != 1) return "binomial edges";
            // the mixed DP with one type == the binomial, for M 1..64 and h' 1..8 (loss 100: need = (h' - 1) * 100 + 40 -> h' hits)
            foreach (double s in new[] { 0.26, 0.43 })
                for (int m = 1; m <= 64; m++)
                    for (int h = 1; h <= 8; h++)
                    {
                        var one = new[] { new StrikeKillShot { Loss = 100, Sigma = s, Count = m } };
                        double a = PSunkMixed(one, (h - 1) * 100 + 40), b = PSunkOne(m, s, h);
                        if (Math.Abs(a - b) > 1e-9) return "mixed(1) == binomial m=" + m + " h=" + h + ": " + a + " vs " + b;
                        // the same type split into two entries goes through the general DP
                        int m1 = m / 2;
                        var two = new[] { new StrikeKillShot { Loss = 100, Sigma = s, Count = m1 }, new StrikeKillShot { Loss = 100.0000001, Sigma = s, Count = m - m1 } };
                        double c = PSunkMixed(two, (h - 1) * 100 + 40);
                        if (Math.Abs(c - b) > 1e-6) return "mixed(2 equal) == binomial m=" + m + " h=" + h + ": " + c + " vs " + b;
                    }
            if (Math.Abs(PAtLeastOne(5, 0.43) - (1 - Math.Pow(0.57, 5))) > 1e-12 || PAtLeastOne(0, 0.5) != 0) return "at least one";
            return null;
        }

        private static Func<int, double> Const(double s) { return m => s; }

        private static string TestNeed()
        {
            // the numbers of tools/bench/T-342-strike-v2-recon/missiles_for_kill.py (constant sigma, h + 1)
            var kidd = Ship(HitEstimateMath.Budget(new HeClass { Kind = HeTargetKind.Ship, Displacement = 9630, Armor = "minor" }), true);
            var tico = Ship(HitEstimateMath.Budget(new HeClass { Kind = HeTargetKind.Ship, Displacement = 9600, Armor = "minor" }), true);
            var harp = Msl(2); var as4 = Msl(3);
            var cases = new (StrikeShip s, StrikeMissile m, double sg, int m80, int m90)[]
            {
                (kidd, harp, 0.43, 12, 14), (kidd, as4, 0.37, 11, 13), (tico, harp, 0.26, 20, 24), (tico, harp, 0.20, 27, 32),
            };
            foreach (var c in cases)
            {
                var n8 = Need(c.s, c.m, Const(c.sg), 0.25, 0.8); var n9 = Need(c.s, c.m, Const(c.sg), 0.25, 0.9);
                if (n8.M != c.m80 || n9.M != c.m90) return "need sigma " + c.sg + ": " + n8.M + "/" + n9.M + " != " + c.m80 + "/" + c.m90;
                if (n8.P < 0.8 || n9.P < 0.9 || n8.Short || n9.Short) return "need P";
                if (n8.H1 != n8.H + 1) return "need h1";
            }
            // monotonic in the threshold
            int prev = 0;
            foreach (double th in new[] { 0.5, 0.6, 0.7, 0.8, 0.85, 0.9 }) { int m = Need(kidd, harp, Const(0.43), 0.25, th).M; if (m < prev) return "monotonic"; prev = m; }
            // sigma depends on M: a growing leak needs fewer missiles than the constant sigma of the first guess
            var nv = Need(kidd, harp, m => Math.Min(0.9, 0.2 + 0.01 * m), 0.25, 0.8);
            var nc = Need(kidd, harp, Const(0.21), 0.25, 0.8);
            if (nv.M < 0 || nc.M < 0 || nv.M >= nc.M) return "sigma(M) " + nv.M + " vs " + nc.M;
            // over KillMaxM -> -1, Short, P and PBreach at the cap
            var big = Need(tico, harp, Const(0.05), 0.25, 0.8);
            if (big.M != -1 || !big.Short || big.P >= 0.8 || big.PBreach <= 0.9) return "short " + big.M + " " + big.Short + " " + big.P;
            // an unread class: no number, Short
            var unk = Need(Ship(-1, true), harp, Const(0.5), 0.25, 0.8);
            if (unk.M != -1 || !unk.Short) return "unknown class";
            // a weak ship (no margin): h' = h
            var kriv = Ship(HitEstimateMath.Budget(new HeClass { Kind = HeTargetKind.Ship, Displacement = 3190, Armor = "none" }), false);
            var nk = Need(kriv, harp, Const(0.43), 0.25, 0.8);
            if (nk.H != 2 || nk.H1 != 2 || nk.M < 2 || nk.M >= Need(kidd, harp, Const(0.43), 0.25, 0.8).M) return "weak ship " + nk.H1 + " " + nk.M;
            // zero warhead: not reachable
            if (Need(kidd, new StrikeMissile { WarheadPower = 0 }, Const(0.5), 0.25, 0.8).M != -1) return "zero warhead";
            return null;
        }

        private static string TestMixed()
        {
            var kidd = Ship(319, false);   // no margin: the three allocations are compared on the same threshold
            double lA = HitLoss(Msl(3), 0.25), lB = lA / 3;   // AS-4-like and a weak one (AS-2 stands in at a third of the loss)
            var allA = new[] { new StrikeKillShot { Loss = lA, Sigma = 0.4, Count = 10 } };
            var allB = new[] { new StrikeKillShot { Loss = lB, Sigma = 0.4, Count = 10 } };
            var mix = new[] { new StrikeKillShot { Loss = lA, Sigma = 0.4, Count = 5 }, new StrikeKillShot { Loss = lB, Sigma = 0.4, Count = 5 } };
            double pa = Eval(kidd, allA, 0.25, 0.8).P, pb = Eval(kidd, allB, 0.25, 0.8).P, pm = Eval(kidd, mix, 0.25, 0.8).P;
            if (!(pb < pm && pm < pa)) return "mixed between: all weak " + pb + " mix " + pm + " all strong " + pa;
            // a second pass of the same inputs is the same (no hidden state)
            if (Eval(kidd, mix, 0.25, 0.8).P != pm) return "mixed repeat";
            var ev = Eval(kidd, mix, 0.25, 0.8);
            if (ev.M != 10 || ev.PBreach <= 0.99 || ev.Loss != lB) return "mixed fields " + ev.M + " " + ev.PBreach + " " + ev.Loss;
            if (Eval(kidd, null, 0.25, 0.8).P != 0 || !Eval(kidd, null, 0.25, 0.8).Short) return "mixed empty";
            if (!Eval(Ship(-1, true), mix, 0.25, 0.8).Short) return "mixed unknown";
            // three types on a Ticonderoga-sized target, M <= 64: the budget of the plan (20 ms)
            var three = new[]
            {
                new StrikeKillShot { Loss = lA, Sigma = 0.3, Count = 20 }, new StrikeKillShot { Loss = lA * 0.5, Sigma = 0.3, Count = 20 }, new StrikeKillShot { Loss = lA * 0.4, Sigma = 0.3, Count = 24 },
            };
            var tico = Ship(319, true);
            Eval(tico, three, 0.25, 0.8);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) Eval(tico, three, 0.25, 0.8);
            double ms = sw.Elapsed.TotalMilliseconds / 20;
            if (ms > 20) return "mixed budget ms=" + ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            // the probability is a probability and grows with a stronger missile
            var weaker = new[] { new StrikeKillShot { Loss = lA * 0.9, Sigma = 0.3, Count = 30 } }; var stronger = new[] { new StrikeKillShot { Loss = lA * 1.1, Sigma = 0.3, Count = 30 } };
            if (!(Eval(tico, weaker, 0.25, 0.8).P <= Eval(tico, stronger, 0.25, 0.8).P)) return "mixed monotonic in loss";
            return null;
        }

        private static string TestResidual()
        {
            // a ship hit before: the budget shrinks by weight * the loss seen, M falls
            var fresh = Ship(319, true); var hit = Ship(319, true); hit.PriorLoss = 2 * 123.75; hit.PriorHits = 2;
            var harp = Msl(2);
            var n0 = Need(fresh, harp, Const(0.43), 0.25, 0.8); var n1 = Need(hit, harp, Const(0.43), 0.25, 0.8);
            if (!(n1.BudgetLeft < n0.BudgetLeft) || Math.Abs(n1.BudgetLeft - (319 - StrikeCalib.PriorHitWeight * 247.5)) > 1e-9) return "residual budget " + n1.BudgetLeft;
            if (!(n1.M < n0.M) || n1.H >= n0.H) return "residual M " + n1.M + " vs " + n0.M + " h " + n1.H + "/" + n0.H;
            var dead = Ship(319, true); dead.PriorLoss = 10000;
            var nd = Need(dead, harp, Const(0.43), 0.25, 0.8);
            if (nd.BudgetLeft != 0 || nd.H != 1 || nd.H1 != 2 || nd.M != 6 || PSunkOne(5, 0.43, 2) >= 0.8 || PSunkOne(6, 0.43, 2) < 0.8) return "residual floor " + nd.BudgetLeft + " " + nd.H + " " + nd.H1 + " " + nd.M;
            return null;
        }
    }
}
