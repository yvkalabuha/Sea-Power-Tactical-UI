using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-145 stand (ADR-0053): several torpedoes, several ships, AntiTorpedoMath.Allocate with the shared windows, the mount busy map and
    /// the closing history, one round per ship per second. Ships stand still (the course is not the subject), torpedoes run at 45 kn.
    /// A mount launches 3 s after the order, then reloads; "the game" fires the mount the order names (M2 is a live check).
    /// </summary>
    internal static class AtwMultiSim
    {
        private const double TorpKn = 45, ReactionS = 3, SafetyNm = 0.15;

        private sealed class Mount { public int Key; public string Type; public double From, To, ReadyAtS; }
        private sealed class Launch { public double AtS; public int Key; public string File; }

        private sealed class Ship
        {
            public int Uid; public double X, Y; public string Role = "evade"; public bool Shoots = true, FlagOn = true;
            public readonly List<Mount> Mounts = new List<Mount>();
            public readonly Dictionary<string, int> Ammo = new Dictionary<string, int>();
            public readonly List<Launch> Pending = new List<Launch>();
        }

        private sealed class Torp
        {
            public int Id; public bool Jitter; public double StaleFromS = double.NaN, StaleToS, StaleAge0; public double X, Y, Vx, Vy, KillAtS = double.PositiveInfinity; public Ship Target; public bool Alive = true;
        }

        private sealed class Ev { public double T, THit; public int Uid, Track, Threat, Rank, Of, Key; public string Role, File; }
        private sealed class Skip { public double T; public int Uid, Track; public string Reason; }

        private sealed class World
        {
            public readonly List<Ship> Ships = new List<Ship>();
            public readonly List<Torp> Torps = new List<Torp>();
            public readonly List<Ev> Events = new List<Ev>();
            public readonly List<Skip> Skips = new List<Skip>();
            public double CoverNm = 4, RoundsFromS = 0, DurationS = 10;
            public bool CoverOn = true, KillOnHit;
            public int MaxTracks = 4;
            public double FireNm = 3;   // S1..S12: the pre-T-193 range
            public readonly AtwWindows Windows = new AtwWindows();
            public readonly AtwMounts Busy = new AtwMounts();
            public readonly AtwClosing Closing = new AtwClosing();
            public readonly Dictionary<long, string> Last = new Dictionary<long, string>();
            public readonly Dictionary<int, int> Salvos = new Dictionary<int, int>();
        }

        private static readonly Dictionary<string, double> Reload = new Dictionary<string, double> { { "rbu6000", 60 }, { "rbu1000", 64 } };
        private static readonly Dictionary<string, double> Kill = new Dictionary<string, double> { { "rbu6000", 0.25 }, { "rbu1000", 0.40 } };
        private static readonly Dictionary<string, double> MinNm = new Dictionary<string, double> { { "rbu6000", 0.19 }, { "rbu1000", 0.054 } };
        private static readonly Dictionary<string, double> MaxNm = new Dictionary<string, double> { { "rbu6000", 3.0 }, { "rbu1000", 0.54 } };

        private static void AddMount(Ship s, int key, string type, double from, double to)
        {
            s.Mounts.Add(new Mount { Key = key, Type = type, From = from, To = to });
            if (!s.Ammo.ContainsKey(type)) s.Ammo[type] = 100;
        }

        private static Ship Kresta(int uid, double x, double y, string role, bool shoots = true)
        {
            var s = new Ship { Uid = uid, X = x, Y = y, Role = role, Shoots = shoots };
            AddMount(s, 1, "rbu6000", -115, 15); AddMount(s, 2, "rbu6000", -35, 137);
            AddMount(s, 3, "rbu1000", -179, -15); AddMount(s, 4, "rbu1000", 15, 179);
            return s;
        }

        private static Ship Full(int uid, double x, double y, string role, bool shoots = true)
        {
            var s = new Ship { Uid = uid, X = x, Y = y, Role = role, Shoots = shoots };
            AddMount(s, 1, "rbu6000", -180, 179.9); AddMount(s, 2, "rbu6000", -180, 179.9);
            return s;
        }

        private static Torp Pursuit(int id, Ship target, double brgDeg, double distNm)
        {
            double r = brgDeg * Math.PI / 180.0;
            return new Torp { Id = id, X = target.X + distNm * Math.Sin(r), Y = target.Y + distNm * Math.Cos(r), Target = target };
        }

        private static Torp PursuitAt(int id, Ship target, double x, double y) { return new Torp { Id = id, X = x, Y = y, Target = target }; }

        private static Torp Drift(int id, double x, double y, double brgDeg)
        {
            double r = brgDeg * Math.PI / 180.0;
            return new Torp { Id = id, X = x, Y = y, Vx = Math.Sin(r) * TorpKn / 3600.0, Vy = Math.Cos(r) * TorpKn / 3600.0 };
        }

        private static double Dist(double ax, double ay, double bx, double by) { return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)); }

        private static void Run(World w)
        {
            for (int t = 0; t <= (int)w.DurationS; t++)
            {
                foreach (Ship s in w.Ships)
                    for (int i = s.Pending.Count - 1; i >= 0; i--)
                    {
                        Launch l = s.Pending[i];
                        if (l.AtS > t) continue;
                        s.Pending.RemoveAt(i);
                        foreach (Mount m in s.Mounts) if (m.Key == l.Key) m.ReadyAtS = l.AtS + Reload[m.Type];
                        s.Ammo[l.File]--;
                    }
                foreach (Torp q in w.Torps) if (q.Alive && q.KillAtS <= t) q.Alive = false;
                var live = new List<int>();
                foreach (Torp q in w.Torps)
                {
                    if (!q.Alive) continue;
                    live.Add(q.Id);
                    foreach (Ship s in w.Ships)
                    {
                        double d = Dist(s.X, s.Y, q.X, q.Y);
                        if (d <= AntiTorpedoMath.PrefilterNm + 10) w.Closing.Update(s.Uid, q.Id, q.Jitter && t % 2 == 0 ? d + 1.5 : d, t);   // Jitter: the track jumps 1.5 nm every other second
                    }
                }
                w.Windows.Prune(live); w.Closing.Prune(live); w.Busy.Prune(t);
                if (t >= w.RoundsFromS)
                    foreach (Ship s in w.Ships) if (s.Shoots) Round(w, s, t);
                foreach (Torp q in w.Torps)
                {
                    if (!q.Alive) continue;
                    if (q.Target != null)
                    {
                        double d = Dist(q.Target.X, q.Target.Y, q.X, q.Y);
                        if (d < 0.03) { q.Alive = false; continue; }
                        double st = TorpKn / 3600.0;
                        q.X += (q.Target.X - q.X) / d * st; q.Y += (q.Target.Y - q.Y) / d * st;
                    }
                    else { q.X += q.Vx; q.Y += q.Vy; }
                }
            }
        }

        private static AtwOption OptionFor(Ship s, string type, double rel, double t)
        {
            var o = new AtwOption { File = type, Display = type, Kill = Kill[type], MinNm = MinNm[type], MaxNm = MaxNm[type], SpeedKn = 600, Count = s.Ammo[type] };
            foreach (Mount m in s.Mounts)
                if (m.Type == type && m.ReadyAtS <= t && AntiTorpedoMath.InArc(rel, m.From, m.To)) o.MountKeys.Add(m.Key);
            o.MountReady = o.MountKeys.Count > 0;
            return o;
        }

        private static void Round(World w, Ship s, double t)
        {
            if (s.Role == "cover" && AntiTorpedoMath.CoverGate(true, true, w.CoverOn, s.FlagOn, false) != null) return;
            foreach (Mount m in s.Mounts) if (m.ReadyAtS > t) w.Busy.Release(s.Uid, m.Key);
            var round = new AtwRoundIn { ShooterUid = s.Uid, Role = s.Role, NowS = t, ReactionS = ReactionS, SafetyNm = SafetyNm, MaxTracks = w.MaxTracks, FireNm = w.FireNm };
            var types = new List<string>();
            foreach (Mount m in s.Mounts) if (!types.Contains(m.Type)) types.Add(m.Type);
            foreach (Torp q in w.Torps)
            {
                if (!q.Alive) continue;
                double d = Dist(s.X, s.Y, q.X, q.Y);
                if (d > AntiTorpedoMath.PrefilterNm) continue;
                double brg = EvasionMath.Norm360(Math.Atan2(q.X - s.X, q.Y - s.Y) * 180.0 / Math.PI);
                double rel = EvasionMath.Diff(0, brg);
                var tr = new AtwTrackIn { TrackId = q.Id, DistNm = d };
                int salvos; w.Salvos.TryGetValue(q.Id, out salvos); tr.SalvosSoFar = salvos;
                if (!double.IsNaN(q.StaleFromS) && t >= q.StaleFromS && t < q.StaleToS) { tr.Fresh = false; tr.AgeS = q.StaleAge0 + (t - q.StaleFromS); }   // T-203: the game's detection of the track is stale
                foreach (string type in types) tr.Options.Add(OptionFor(s, type, rel, t));
                tr.Cands.Add(new AtwThreatCand { Uid = s.Uid, IsShooter = true, DistNm = d, ClosingKn = w.Closing.ClosingKn(s.Uid, q.Id), HeldTtiS = w.Closing.HeldTtiS(s.Uid, q.Id, t, d) });
                foreach (Ship o in w.Ships)
                {
                    if (ReferenceEquals(o, s)) continue;
                    double dt = Dist(o.X, o.Y, q.X, q.Y);
                    tr.Near.Add(new AtwNeighbour { Uid = o.Uid, Side = "own", DistToTorpNm = dt });
                    if (w.CoverOn && Dist(s.X, s.Y, o.X, o.Y) <= w.CoverNm)
                        tr.Cands.Add(new AtwThreatCand { Uid = o.Uid, DistNm = dt, ClosingKn = w.Closing.ClosingKn(o.Uid, q.Id), HeldTtiS = w.Closing.HeldTtiS(o.Uid, q.Id, t, dt) });
                }
                round.Tracks.Add(tr);
            }
            if (round.Tracks.Count == 0) return;
            foreach (AtwAssign a in AntiTorpedoMath.Allocate(round, w.Windows, w.Busy))
            {
                if (!a.Fire)
                {
                    if (a.Reason != null && AntiTorpedoMath.ReasonChanged(w.Last, s.Uid, a.TrackId, a.Reason))
                        w.Skips.Add(new Skip { T = t, Uid = s.Uid, Track = a.TrackId, Reason = a.Reason });
                    continue;
                }
                w.Windows.Note(a.TrackId, a.THitS);
                w.Busy.Take(s.Uid, a.MountKey, a.Ammo.File, t + ReactionS + AntiTorpedoMath.BusyExtraS);
                s.Pending.Add(new Launch { AtS = t + ReactionS, Key = a.MountKey, File = a.Ammo.File });
                int n; w.Salvos.TryGetValue(a.TrackId, out n); w.Salvos[a.TrackId] = n + 1;
                w.Events.Add(new Ev { T = t, THit = a.THitS, Uid = s.Uid, Track = a.TrackId, Threat = a.ThreatUid, Rank = a.Rank, Of = a.Of, Key = a.MountKey, Role = s.Role, File = a.Ammo.File });
                if (w.KillOnHit) foreach (Torp q in w.Torps) if (q.Id == a.TrackId) q.KillAtS = a.THitS;
            }
        }

        // ---- checks ----

        private static string Gaps(World w)
        {
            var byTrack = new Dictionary<int, double>();
            foreach (Ev e in w.Events)
            {
                double last;
                if (byTrack.TryGetValue(e.Track, out last) && e.THit - last < AtwWindows.ImmunityS + AtwWindows.SpreadS - 1e-9)
                    return "two salvos on track " + e.Track + " with T_hit " + (e.THit - last) + " s apart";
                byTrack[e.Track] = e.THit;
            }
            return null;
        }

        private static string Reason(World w, int uid, int track)
        {
            string r = null;
            foreach (Skip s in w.Skips) if (s.Uid == uid && s.Track == track) r = s.Reason;
            return r;
        }

        private static bool HasSkip(World w, int uid, int track, string reason)
        {
            foreach (Skip s in w.Skips) if (s.Uid == uid && s.Track == track && s.Reason == reason) return true;
            return false;
        }

        private static string S1()
        {
            var w = new World { DurationS = 2 };
            Ship a = Kresta(1, 0, 0, "evade"); w.Ships.Add(a);
            w.Torps.Add(Pursuit(1, a, 0, 3.0)); w.Torps.Add(Pursuit(2, a, 5, 2.5));
            Run(w);
            if (w.Events.Count != 2 || w.Events[0].T != 0 || w.Events[1].T != 0) return "S1: two salvos in the first tick, got " + w.Events.Count;
            if (w.Events[0].Track != 2 || w.Events[1].Track != 1) return "S1: the nearer torpedo first";
            if (w.Events[0].Key == w.Events[1].Key) return "S1: different mounts";
            if (w.Events[0].Rank != 1 || w.Events[1].Rank != 2 || w.Events[0].Of != 2) return "S1: rank k/n";
            return Gaps(w);
        }

        private static string S2()
        {
            var w = new World { DurationS = 140, KillOnHit = true };
            var a = new Ship { Uid = 1, Role = "evade" }; AddMount(a, 1, "rbu6000", -115, 15); w.Ships.Add(a);
            w.Torps.Add(Pursuit(1, a, 0, 3.0)); w.Torps.Add(Pursuit(2, a, 3, 2.7)); w.Torps.Add(Pursuit(3, a, 6, 2.4));
            Run(w);
            if (w.Events.Count != 3) return "S2: expected 3 salvos in 140 s, got " + w.Events.Count;
            int[] tr = { 3, 2, 1 }; double[] at = { 0, 63, 126 };
            for (int i = 0; i < 3; i++)
                if (w.Events[i].Track != tr[i] || w.Events[i].T != at[i]) return "S2: salvo " + i + " expected track " + tr[i] + " at " + at[i] + ", got " + w.Events[i].Track + " at " + w.Events[i].T;
            if (!HasSkip(w, 1, 2, "mount_busy") || !HasSkip(w, 1, 1, "mount_busy")) return "S2: the other tracks must be mount_busy";
            return Gaps(w);
        }

        private static string S3()
        {
            var w = new World { DurationS = 40 };
            Ship a = Kresta(1, 0, 0, "evade"); w.Ships.Add(a);
            w.Torps.Add(Pursuit(1, a, 0, 3.0));
            Run(w);
            if (w.Events.Count != 2) return "S3: expected 2 salvos, got " + w.Events.Count;
            if (w.Events[0].T != 0 || w.Events[1].T < 14) return "S3: the second salvo before the window ends: " + w.Events[1].T;
            if (w.Events[0].Key == w.Events[1].Key) return "S3: the second salvo from the other mount";
            if (!HasSkip(w, 1, 1, "window")) return "S3: reason window expected";
            return Gaps(w);
        }

        private static string S4()
        {
            for (int mounts = 1; mounts <= 2; mounts++)
            {
                var w = new World { DurationS = 4, RoundsFromS = 3 };   // T-193: the closing estimate needs 3 s of history
                var a = new Ship { Uid = 1, Role = "evade" };
                AddMount(a, 1, "rbu6000", -115, 15); if (mounts == 2) AddMount(a, 2, "rbu6000", -35, 137);
                w.Ships.Add(a);
                w.Torps.Add(Pursuit(1, a, 0, 3.0)); w.Torps.Add(Drift(2, 0.087, 0.996, 5));   // 1 nm, bearing 5, running away
                Run(w);
                if (w.Events.Count == 0 || w.Events[0].Track != 1) return "S4: the approaching torpedo first (" + mounts + " mounts)";
                if (mounts == 1 && (w.Events.Count != 1 || Reason(w, 1, 2) != "mount_busy")) return "S4: one mount -> the receding torpedo waits";
                if (mounts == 2 && (w.Events.Count != 2 || w.Events[1].Track != 2 || w.Events[1].Rank != 2)) return "S4: two mounts -> the receding torpedo last";
            }
            return null;
        }

        private static string S5()
        {
            var w = new World { DurationS = 8 };
            var a = new Ship { Uid = 1, Role = "evade" }; AddMount(a, 1, "rbu6000", -115, 15); AddMount(a, 2, "rbu6000", -35, 137); a.Ammo["rbu6000"] = 1;
            w.Ships.Add(a);
            w.Torps.Add(Pursuit(1, a, 0, 3.0)); w.Torps.Add(Pursuit(2, a, 5, 2.5));
            Run(w);
            if (w.Events.Count != 1 || w.Events[0].Track != 2) return "S5: one salvo, at the urgent track";
            int n = 0; foreach (Skip s in w.Skips) if (s.Reason == "no_ammo" && s.Track == 1) n++;
            if (n != 1 || !HasSkip(w, 1, 1, "no_ammo")) return "S5: no_ammo logged once, got " + n;
            return null;
        }

        private static string S6()
        {
            var w = new World { DurationS = 1 };
            Ship a = Kresta(1, 0, 0, "evade"); Ship o = Kresta(2, 0.1, 2.5, "evade", false); w.Ships.Add(a); w.Ships.Add(o);
            w.Torps.Add(Pursuit(1, a, 0, 2.5)); w.Torps.Add(Pursuit(2, a, 10, 2.8));
            Run(w);
            if (w.Events.Count != 1 || w.Events[0].Track != 2) return "S6: salvo at B only, got " + w.Events.Count;
            if (!HasSkip(w, 1, 1, "friendly_near")) return "S6: friendly_near at A";
            return null;
        }

        private static string S7()
        {
            var w = new World { DurationS = 80 };
            Ship a = Kresta(1, 0, 0, "evade"); Ship b = Kresta(2, 0.4, 0, "evade"); w.Ships.Add(a); w.Ships.Add(b);
            w.Torps.Add(Pursuit(1, a, 0, 2.5)); w.Torps.Add(Pursuit(2, a, 5, 2.7)); w.Torps.Add(Pursuit(3, b, 355, 2.9));
            Run(w);
            var tracks = new HashSet<int>(); foreach (Ev e in w.Events) tracks.Add(e.Track);
            if (tracks.Count < 3) return "S7: every track gets a salvo, got " + tracks.Count;
            return Gaps(w);
        }

        // T-193 B2: the neighbour's track jumps by 1.5 nm every other second; the cover ship must still fire once the track is inside FireNm
        private static string S13()
        {
            var w = new World { DurationS = 200, RoundsFromS = 1, FireNm = AntiTorpedoMath.DefaultFireNm };
            Ship a = Full(1, 0, 0, "evade", false), b = Full(2, 0, 2.0, "cover"); w.Ships.Add(a); w.Ships.Add(b);
            Torp q = Pursuit(1, a, 0, 3.0); q.Jitter = true; w.Torps.Add(q);   // runs south at A, passing B first at 1 nm
            Run(w);
            bool cover = false;
            foreach (Ev e in w.Events) if (e.Role == "cover" && e.Uid == 2) cover = true;
            if (!cover) return "S13: the cover ship must fire at the jumping track";
            return Gaps(w);
        }

        // T-193 B3: a torpedo chases the shooter from 3 nm at 45 kn, two mounts, reload 60 s: the first salvo from <= 1.5 nm, two salvos before 0.19 nm
        private static string S14()
        {
            var w = new World { DurationS = 300, RoundsFromS = 1, FireNm = AntiTorpedoMath.DefaultFireNm };
            Ship a = Full(1, 0, 0, "evade"); w.Ships.Add(a);
            w.Torps.Add(Pursuit(1, a, 0, 3.0));
            Run(w);
            if (w.Events.Count < 2) return "S14: at least 2 salvos, got " + w.Events.Count;
            double d0 = 3.0 - TorpKn / 3600.0 * w.Events[0].T;
            if (d0 > AntiTorpedoMath.DefaultFireNm + 0.05) return "S14: the first salvo from " + d0 + " nm";
            double d1 = 3.0 - TorpKn / 3600.0 * w.Events[1].T;
            if (d1 < 0.19) return "S14: the second salvo inside the minimum range";
            if (!HasSkip(w, 1, 1, "wait_closer")) return "S14: wait_closer expected before the salvo";
            if (w.Events[1].THit - w.Events[0].THit < 13 - 1e-9) return "S14: expected hits less than 13 s apart";
            return Gaps(w);
        }

        // T-203 (ADR-0087): a frozen track whose last detection is older than AtwStaleFireMaxS gets no salvo (track_stale), a younger one does; a fresh one always
        private static string S15()
        {
            var w = new World { DurationS = 120, RoundsFromS = 4, FireNm = AntiTorpedoMath.DefaultFireNm };
            Ship a = Full(1, 0, 0, "evade"); w.Ships.Add(a);
            Torp q = Pursuit(1, a, 0, 1.4); q.StaleFromS = 0; q.StaleToS = 20; q.StaleAge0 = 40; w.Torps.Add(q);
            Run(w);
            foreach (Ev e in w.Events) if (e.T < 20) return "S15: a salvo at t=" + e.T + " on a track whose detection is 40+ s old";
            if (!HasSkip(w, 1, 1, "track_stale")) return "S15: track_stale expected";
            if (w.Events.Count < 1) return "S15: once fresh, the track must get a salvo";
            w = new World { DurationS = 60, RoundsFromS = 4, FireNm = AntiTorpedoMath.DefaultFireNm };
            a = Full(1, 0, 0, "evade"); w.Ships.Add(a);
            q = Pursuit(1, a, 0, 1.4); q.StaleFromS = 0; q.StaleToS = 50; q.StaleAge0 = 5; w.Torps.Add(q);
            Run(w);
            if (w.Events.Count < 1 || w.Events[0].T > 10) return "S15: a track last detected 9 s ago must get a salvo at once";
            return Gaps(w);
        }

        private static string S8() { return AtwSim.CompareAllocateWithDecide(); }

        private static World CoverWorld(double durationS, bool shooterA, bool order)
        {
            var w = new World { DurationS = durationS + 2, RoundsFromS = 3 };   // T-193: 3 s of history first
            Ship a = Full(1, 0, 0, "evade", shooterA), b = Full(2, 0, 2.5, "cover");
            if (order) { w.Ships.Add(a); w.Ships.Add(b); } else { w.Ships.Add(b); w.Ships.Add(a); }
            w.Torps.Add(Pursuit(1, a, 0, 1.0));   // runs south at A, away from B
            return w;
        }

        private static string S9()
        {
            World w = CoverWorld(10, false, true);
            Run(w);
            if (w.Events.Count == 0) return "S9: the neighbour must fire";
            Ev e = w.Events[0];
            if (e.Role != "cover" || e.Uid != 2 || e.Threat != 1) return "S9: role=cover threat=A expected, got " + e.Role + " uid=" + e.Uid + " threat=" + e.Threat;
            if (e.T < 1) return "S9: not before the second sample of the track";
            return Gaps(w);
        }

        private static string S10()
        {
            foreach (bool order in new[] { true, false })
            {
                World w = CoverWorld(40, true, order);
                Run(w);
                int n = 0; foreach (Ev e in w.Events) if (e.T <= 10) n++;
                if (n != 1) { string dump = ""; foreach (Ev e in w.Events) dump += " [t=" + e.T + " uid=" + e.Uid + " trk=" + e.Track + " thit=" + e.THit + "]"; return "S10: exactly one salvo until the window opens (T_hit gap 13 s), got " + n + " (order " + order + ")" + dump; }
                string g = Gaps(w);
                if (g != null) return "S10: " + g;
            }
            return null;
        }

        private static string S11()
        {
            foreach (double nm in new[] { 4.0, 6.0 })
            {
                var w = new World { DurationS = 5, RoundsFromS = 3, CoverNm = nm };
                Ship a = Full(1, 5, 0, "evade", false), b = Full(2, 0, 0, "cover"); w.Ships.Add(a); w.Ships.Add(b);
                w.Torps.Add(PursuitAt(1, a, 2, 2));
                Run(w);
                if (nm == 4 && (w.Events.Count != 0 || !HasSkip(w, 2, 1, "no_threat"))) return "S11: A 5 nm away with CoverNm 4: no salvo (no_threat)";
                if (nm == 6 && (w.Events.Count == 0 || w.Events[0].Threat != 1)) return "S11: CoverNm 6: salvo with threat=A";
            }
            foreach (int mode in new[] { 1, 2 })
            {
                var w = new World { DurationS = 5, RoundsFromS = 3, CoverNm = 6, CoverOn = mode != 1 };
                Ship a = Full(1, 5, 0, "evade", false), b = Full(2, 0, 0, "cover"); b.FlagOn = mode != 2; w.Ships.Add(a); w.Ships.Add(b);
                w.Torps.Add(PursuitAt(1, a, 2, 2));
                Run(w);
                if (w.Events.Count != 0) return "S11: Cover off / auto_evade flag off: no salvo";
            }
            return null;
        }

        private static string S12()
        {
            var w = new World { DurationS = 5, RoundsFromS = 3 };
            Ship a = Full(1, 1, 0, "evade", false), b = Full(2, 0, 0, "cover"); w.Ships.Add(a); w.Ships.Add(b);
            w.Torps.Add(Drift(1, 0, 2, 0));   // runs north, away from both
            Run(w);
            if (w.Events.Count != 0 || !HasSkip(w, 2, 1, "no_threat")) return "S12: no_threat expected";
            w = new World { DurationS = 4, RoundsFromS = 3 };
            a = Full(1, 0, 0, "evade", false); b = Full(2, 0, 1.8, "cover"); w.Ships.Add(a); w.Ships.Add(b);
            w.Torps.Add(Pursuit(1, a, 0, 0.1));
            Run(w);
            if (w.Events.Count != 0 || !HasSkip(w, 2, 1, "friendly_near")) return "S12: friendly_near expected";
            return null;
        }

        public static string SelfTest()
        {
            var cases = new List<KeyValuePair<string, Func<string>>>
            {
                new KeyValuePair<string, Func<string>>("S1", S1), new KeyValuePair<string, Func<string>>("S2", S2),
                new KeyValuePair<string, Func<string>>("S3", S3), new KeyValuePair<string, Func<string>>("S4", S4),
                new KeyValuePair<string, Func<string>>("S5", S5), new KeyValuePair<string, Func<string>>("S6", S6),
                new KeyValuePair<string, Func<string>>("S7", S7), new KeyValuePair<string, Func<string>>("S8", S8),
                new KeyValuePair<string, Func<string>>("S9", S9), new KeyValuePair<string, Func<string>>("S10", S10),
                new KeyValuePair<string, Func<string>>("S11", S11), new KeyValuePair<string, Func<string>>("S12", S12),
                new KeyValuePair<string, Func<string>>("S13", S13), new KeyValuePair<string, Func<string>>("S14", S14), new KeyValuePair<string, Func<string>>("S15", S15),
            };
            foreach (var c in cases)
            {
                string err = c.Value();
                if (err != null) return err;
                Console.WriteLine("atw multi " + c.Key + " ok");
            }
            return null;
        }
    }
}
