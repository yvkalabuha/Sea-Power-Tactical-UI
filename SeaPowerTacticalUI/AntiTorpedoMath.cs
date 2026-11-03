using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-141 (ADR-0048): pure maths of the anti-torpedo salvo fired by the own evasion before the turn.
    // No game, no Unity: compiled by the atwtest stand and the selfcheck-ui.

    /// <summary>One anti-torpedo ammo of the shooter.</summary>
    internal sealed class AtwOption
    {
        public string File, Display;
        public double Kill, MinNm, MaxNm, SpeedKn;   // SpeedKn <= 0 -> DefaultSpeedKn
        public int Count;
        public bool MountReady;                        // WeaponSelector found a ready mount with the torpedo in its arc
        public List<int> MountKeys = new List<int>();  // T-145: identities of those ready mounts; empty + MountReady = one synthetic mount per ammo
        public bool ArcBlocked;                        // T-230: the game told us no mount has the torpedo in its firing arc (MountReady=false then)
    }

    /// <summary>Own (truth) or friendly/neutral track (plotting table), surface or submarine.</summary>
    internal sealed class AtwNeighbour
    {
        public int Uid;
        public string Side;                            // own | friendly | neutral
        public double DistToTorpNm;
        public bool IsShooter;
    }

    internal sealed class AtwInput
    {
        public bool Enabled, HasTrack, GameTask;       // GameTask: shooter or own unit <= 3 nm already has an EngageTask on this torpedo
        public int TrackId;
        public double NowS, DistNm, ReactionS, SafetyNm;   // NowS = mission time
        public List<AtwOption> Options = new List<AtwOption>();
        public List<AtwNeighbour> Near = new List<AtwNeighbour>();
    }

    internal sealed class AtwDecision
    {
        public bool Fire;
        public string Reason;
        public AtwOption Ammo;
        public double THitS;
        public int BlockerUid = -1;
    }

    /// <summary>Global (whole mod, all ships) torpedo -> expected time of the last salvo's hit (mission seconds).</summary>
    internal sealed class AtwWindows
    {
        public const double ImmunityS = 7, SpreadS = 6;
        private readonly Dictionary<int, double> _last = new Dictionary<int, double>();
        private double _lastNow = double.NegativeInfinity;

        public int Count { get { return _last.Count; } }

        /// <summary>No entry, or the new hit comes at least ImmunityS + SpreadS after the last one.</summary>
        public bool Allowed(int trackId, double tHitS)
        {
            double last;
            return !_last.TryGetValue(trackId, out last) || tHitS >= last + ImmunityS + SpreadS;
        }

        public void Note(int trackId, double tHitS) { _last[trackId] = tHitS; }

        public double? Last(int trackId)
        {
            double last;
            return _last.TryGetValue(trackId, out last) ? (double?)last : null;
        }

        public void Forget(int trackId) { _last.Remove(trackId); }

        public void Clear() { _last.Clear(); _lastNow = double.NegativeInfinity; }

        /// <summary>Drops every torpedo that is not in the live list.</summary>
        public void Prune(ICollection<int> liveTrackIds)
        {
            List<int> dead = null;
            foreach (int id in _last.Keys)
                if (liveTrackIds == null || !liveTrackIds.Contains(id)) (dead ?? (dead = new List<int>())).Add(id);
            if (dead != null) foreach (int id in dead) _last.Remove(id);
        }

        /// <summary>Mission restart / new mission: the clock went back, so every window is stale.</summary>
        public void ResetIfTimeWentBack(double nowS)
        {
            if (nowS < _lastNow) _last.Clear();
            _lastNow = nowS;
        }
    }

    /// <summary>One protected ship of the shooter: itself, own (truth) or allied track; distance to one torpedo track.</summary>
    internal sealed class AtwThreatCand
    {
        public int Uid;
        public bool IsShooter;
        public double DistNm;
        public double? ClosingKn;
        /// <summary>T-193: last finite TTI of the pair when the closing got lost less than NoThreatHoldS ago (filled by the caller).</summary>
        public double? HeldTtiS;
    }

    /// <summary>
    /// T-203: robust speed of a plotted track (log only): median of the pairwise speeds of the moved positions over the last WindowS of mission time.
    /// The plotting table position jumps, a two-sample difference gives 30-72 kn for a 45 kn torpedo.
    /// </summary>
    internal sealed class TrackSpeed
    {
        public const double WindowS = 90, MinSpanS = 6, MinDtS = 0.5, MinPairDtS = 1, SameNm = 0.005;
        public const int MinSamples = 3, MaxSamples = 12;
        /// <summary>T-352: false = the flat world of stable (1 deg = 60 nm on both axes), true = the beta globe (cos lat); set by the owner every tick.</summary>
        public bool Globe;
        private sealed class Series { public readonly List<double> T = new List<double>(), Lat = new List<double>(), Lon = new List<double>(); }
        private readonly Dictionary<int, Series> _s = new Dictionary<int, Series>();

        public int Count { get { return _s.Count; } }

        public void Update(int trackId, double lat, double lon, double nowS)
        {
            Series s;
            if (!_s.TryGetValue(trackId, out s)) { s = new Series(); _s[trackId] = s; }
            int n = s.T.Count;
            if (n > 0 && nowS < s.T[n - 1]) { s.T.Clear(); s.Lat.Clear(); s.Lon.Clear(); n = 0; }   // the clock went back
            if (n > 0 && nowS - s.T[n - 1] < MinDtS) return;
            if (n > 0)
            {
                // the plotting table repeats a position between detections: only a moved position is a sample
                double dy0 = (lat - s.Lat[n - 1]) * 60.0, dx0 = (lon - s.Lon[n - 1]) * 60.0 * (Globe ? Math.Cos(lat * Math.PI / 180.0) : 1.0);
                if (Math.Sqrt(dx0 * dx0 + dy0 * dy0) <= SameNm) return;
            }
            s.T.Add(nowS); s.Lat.Add(lat); s.Lon.Add(lon);
            while (s.T.Count > 0 && (nowS - s.T[0] > WindowS || s.T.Count > MaxSamples)) { s.T.RemoveAt(0); s.Lat.RemoveAt(0); s.Lon.RemoveAt(0); }
        }

        /// <summary>Median of the speeds between all sample pairs at least MinPairDtS apart, knots; null = fewer than MinSamples or span under MinSpanS.</summary>
        public double? Kn(int trackId)
        {
            Series s;
            if (!_s.TryGetValue(trackId, out s)) return null;
            int n = s.T.Count;
            if (n < MinSamples || s.T[n - 1] - s.T[0] < MinSpanS) return null;
            var v = new List<double>();
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    double dt = s.T[j] - s.T[i];
                    if (dt < MinPairDtS) continue;
                    double dy = (s.Lat[j] - s.Lat[i]) * 60.0, dx = (s.Lon[j] - s.Lon[i]) * 60.0 * (Globe ? Math.Cos(s.Lat[i] * Math.PI / 180.0) : 1.0);
                    v.Add(Math.Sqrt(dx * dx + dy * dy) / dt * 3600.0);
                }
            if (v.Count == 0) return null;
            v.Sort();
            int m = v.Count;
            return m % 2 == 1 ? v[m / 2] : (v[m / 2 - 1] + v[m / 2]) / 2.0;
        }

        public void Prune(ICollection<int> liveTrackIds)
        {
            List<int> dead = null;
            foreach (int k in _s.Keys) if (liveTrackIds == null || !liveTrackIds.Contains(k)) (dead ?? (dead = new List<int>())).Add(k);
            if (dead != null) foreach (int k in dead) _s.Remove(k);
        }

        public void Clear() { _s.Clear(); }
    }

    internal sealed class AtwTrackIn
    {
        public int TrackId;
        public double DistNm;                                      // to the shooter
        public bool GameTask;
        public string EngagedBy;                                   // T-230: null | "own_order" | "game_task" | "game_firing"; null + GameTask=true -> "game_task"
        public List<AtwOption> Options = new List<AtwOption>();
        public List<AtwNeighbour> Near = new List<AtwNeighbour>();  // safety (own/friendly/neutral), as T-141
        public List<AtwThreatCand> Cands = new List<AtwThreatCand>();
        public int SalvosSoFar;                                     // windows noted on this track (round order tie-break)
        /// <summary>T-203: the game's detection of the track is fresh (HasLiveTrack) and its position moved within TrackFreshness.StaleS.</summary>
        public bool Fresh = true;
        /// <summary>T-203: age of the last detection, seconds (NaN = unknown). Used only when !Fresh: older than AtwStaleFireMaxS or unknown = track_stale.</summary>
        public double AgeS = double.NaN;
        // filled by Threat():
        public int ThreatUid = -1;
        public bool ThreatIsShooter;
        public double TtiS = double.PositiveInfinity;
    }

    internal sealed class AtwRoundIn
    {
        public int ShooterUid;
        public string Role;                                         // "evade" | "cover"
        public double NowS, ReactionS, SafetyNm;
        public int MaxTracks = 4;
        public double FireNm = AntiTorpedoMath.DefaultFireNm;       // T-193: no salvo at a track farther than this
        public List<AtwTrackIn> Tracks = new List<AtwTrackIn>();
    }

    internal sealed class AtwAssign
    {
        public int TrackId, ThreatUid = -1, Rank, Of, MountKey, BlockerUid = -1;
        public bool Fire;
        public string Reason;
        public AtwOption Ammo;
        public double THitS, TtiS;
    }

    /// <summary>
    /// Closing speed of (protected ship, torpedo track). T-193 (ADR-0083): Theil-Sen median over the last 8 s of mission time (TrackClosing), not two samples:
    /// the plotting-table track of a wake-homing torpedo jumps by up to 2 nm between samples. Also remembers when the pair last closed (threat hold).
    /// </summary>
    internal sealed class AtwClosing
    {
        private sealed class Last { public double S, Kn; }
        private readonly TrackClosing _tc = new TrackClosing();
        private readonly Dictionary<long, Last> _last = new Dictionary<long, Last>();

        public int Count { get { return _tc.Count; } }

        public void Update(int protUid, int trackId, double distNm, double nowS)
        {
            _tc.Update(protUid, trackId, distNm, nowS);
            double? c = _tc.ClosingKn(protUid, trackId);
            if (c.HasValue && c.Value > AntiTorpedoMath.MinClosingKn)
                _last[AntiTorpedoMath.AtwKey(protUid, trackId)] = new Last { S = nowS, Kn = c.Value };
        }

        /// <summary>Robust closing speed, knots; null = fewer than 3 samples or span under 3 s.</summary>
        public double? ClosingKn(int protUid, int trackId) { return _tc.ClosingKn(protUid, trackId); }

        /// <summary>Mission time of the last estimate above MinClosingKn; null = never.</summary>
        public double? LastClosingS(int protUid, int trackId)
        {
            Last l;
            return _last.TryGetValue(AntiTorpedoMath.AtwKey(protUid, trackId), out l) ? l.S : (double?)null;
        }

        /// <summary>Closing speed of that last estimate, knots.</summary>
        public double? LastClosingKn(int protUid, int trackId)
        {
            Last l;
            return _last.TryGetValue(AntiTorpedoMath.AtwKey(protUid, trackId), out l) ? l.Kn : (double?)null;
        }

        /// <summary>T-193 threat hold: when the closing is lost (null or at most MinClosingKn) less than NoThreatHoldS after it was last seen, the TTI of that last closing at the current distance; else null.</summary>
        public double? HeldTtiS(int protUid, int trackId, double nowS, double distNm)
        {
            double? cur = _tc.ClosingKn(protUid, trackId);
            if (cur.HasValue && cur.Value > AntiTorpedoMath.MinClosingKn) return null;
            Last l;
            if (!_last.TryGetValue(AntiTorpedoMath.AtwKey(protUid, trackId), out l)) return null;
            if (nowS - l.S > AntiTorpedoMath.NoThreatHoldS || nowS < l.S) return null;
            return distNm / l.Kn * 3600.0;
        }

        public void Prune(ICollection<int> liveTrackIds)
        {
            _tc.Prune(liveTrackIds);
            List<long> dead = null;
            foreach (long k in _last.Keys)
                if (liveTrackIds == null || !liveTrackIds.Contains((int)(uint)(k & 0xFFFFFFFFL))) (dead ?? (dead = new List<long>())).Add(k);
            if (dead != null) foreach (long k in dead) _last.Remove(k);
        }

        public void Clear() { _tc.Clear(); _last.Clear(); }
    }

    /// <summary>(shooter uid, mount key) busy until now + ReactionS + BusyExtraS; in-flight orders per (uid, ammo file).</summary>
    internal sealed class AtwMounts
    {
        private readonly Dictionary<long, double> _busy = new Dictionary<long, double>();
        private readonly Dictionary<string, List<double>> _orders = new Dictionary<string, List<double>>();

        public int Count { get { return _busy.Count; } }
        public int OrdersCount { get { int n = 0; foreach (var l in _orders.Values) n += l.Count; return n; } }

        public bool Busy(int uid, int key, double nowS)
        {
            double until;
            return _busy.TryGetValue(AntiTorpedoMath.AtwKey(uid, key), out until) && until > nowS;
        }

        public void Take(int uid, int key, string file, double untilS)
        {
            _busy[AntiTorpedoMath.AtwKey(uid, key)] = untilS;
            string k = uid + "|" + file;
            List<double> l;
            if (!_orders.TryGetValue(k, out l)) _orders[k] = l = new List<double>();
            l.Add(untilS);
        }

        /// <summary>Orders of this ammo file not yet launched (until > now).</summary>
        public int InFlight(int uid, string file, double nowS)
        {
            List<double> l;
            if (!_orders.TryGetValue(uid + "|" + file, out l)) return 0;
            int n = 0;
            foreach (double u in l) if (u > nowS) n++;
            return n;
        }

        /// <summary>The mount disappeared from the ready list (it fired or reloads): the game owns it now.</summary>
        public void Release(int uid, int key) { _busy.Remove(AntiTorpedoMath.AtwKey(uid, key)); }

        /// <summary>Releases every busy mount of the shooter that is not in the ready list any more.</summary>
        public void ReleaseMissing(int uid, ICollection<int> readyKeys)
        {
            List<long> dead = null;
            foreach (long k in _busy.Keys)
                if ((int)(k >> 32) == uid && (readyKeys == null || !readyKeys.Contains((int)(uint)(k & 0xFFFFFFFFL)))) (dead ?? (dead = new List<long>())).Add(k);
            if (dead != null) foreach (long k in dead) _busy.Remove(k);
        }

        public void Prune(double nowS)
        {
            List<long> dead = null;
            foreach (var kv in _busy) if (kv.Value <= nowS) (dead ?? (dead = new List<long>())).Add(kv.Key);
            if (dead != null) foreach (long k in dead) _busy.Remove(k);
            foreach (var l in _orders.Values) l.RemoveAll(u => u <= nowS);
        }

        public void Clear() { _busy.Clear(); _orders.Clear(); }
    }

    internal static class AntiTorpedoMath
    {
        public const double DefaultSpeedKn = 600, DefaultReactionS = 3, DefaultSafetyNm = 0.15, NeighbourTaskNm = 3, ResultAfterS = 10;
        public const int Shots = 1;   // one mount salvo, as AI.AutoAttackSalvoSize for RBU
        public static readonly string[] Reasons = { "no_track", "no_ammo", "out_of_range", "min_range", "arc_or_reload", "game_task", "window", "friendly_near", "mount_busy", "no_threat", "wait_closer", "track_stale", "no_arc", "own_order", "game_firing", "turn_back" };

        // T-145 (ADR-0053)
        public const double PrefilterNm = 4, TorpedoAssumedKn = 45, MinClosingKn = 0.5, TieS = 5, BusyExtraS = 2, CoverPeriodS = 1;
        /// <summary>T-193 (ADR-0083): salvo only at a track this close to the shooter (the game's AI fires RBU at torpedoes within 1 nm); a cover candidate keeps its last TTI this long.</summary>
        public const double DefaultFireNm = 1.5, NoThreatHoldS = 6;
        /// <summary>T-203 (ADR-0087): a frozen track (the game's detection is stale) may still be fired at when its last detection is no older than this (error about 0.4 nm at 45 kn); older: track_stale.</summary>
        public const double AtwStaleFireMaxS = 30;
        public const int MaxTracksLimit = 8;
        /// <summary>T-230 (ADR-0095): an order not launched within this many mission seconds is cancelled (fire_failed).</summary>
        public const double ConfirmS = 15;
        public const bool OneOrderPerAmmo = false;   // M1 fallback (ADR-0053 risk): true = one EngageTask per ammo file per round

        /// <summary>Expected mission time of the hit: now + reaction + distance / speed.</summary>
        public static double ExpectedHitS(double nowS, double reactionS, double distNm, double speedKn)
        {
            if (speedKn <= 0) speedKn = DefaultSpeedKn;
            return nowS + reactionS + distNm / speedKn * 3600.0;
        }

        /// <summary>Best usable option: Count > 0, MinNm <= d <= MaxNm, MountReady; max Kill, then max MaxNm. Else null + reason.</summary>
        public static AtwOption Pick(List<AtwOption> opts, double distNm, out string reason)
        {
            reason = "no_ammo";
            if (opts == null) return null;
            bool anyCount = false, anyRange = false, anyMin = false, anyArc = false;
            AtwOption best = null;
            foreach (AtwOption o in opts)
            {
                if (o == null || o.Count <= 0) continue;
                anyCount = true;
                if (distNm > o.MaxNm) continue;
                anyRange = true;
                if (distNm < o.MinNm) continue;
                anyMin = true;
                if (!o.MountReady) { if (o.ArcBlocked) anyArc = true; continue; }
                if (best == null || o.Kill > best.Kill || (o.Kill == best.Kill && o.MaxNm > best.MaxNm)) best = o;
            }
            if (best != null) { reason = null; return best; }
            reason = !anyCount ? "no_ammo" : !anyRange ? "out_of_range" : !anyMin ? "min_range" : anyArc ? "no_arc" : "arc_or_reload";
            return null;
        }

        /// <summary>An own/allied/neutral ship (not the shooter) within safetyNm of the torpedo; blockerUid = the closest one.</summary>
        public static bool FriendlyNear(List<AtwNeighbour> near, double safetyNm, out int blockerUid)
        {
            blockerUid = -1;
            if (near == null) return false;
            double best = double.MaxValue;
            bool found = false;
            foreach (AtwNeighbour n in near)
            {
                if (n == null || n.IsShooter) continue;
                if (n.DistToTorpNm <= safetyNm && n.DistToTorpNm < best) { best = n.DistToTorpNm; blockerUid = n.Uid; found = true; }
            }
            return found;
        }

        /// <summary>Order: disabled -> null decision; no_track; Pick; game_task; window; friendly_near; fire. Pure: Note() is the caller's job.</summary>
        public static AtwDecision Decide(AtwInput i, AtwWindows w)
        {
            var d = new AtwDecision();
            if (i == null || !i.Enabled) return d;
            if (!i.HasTrack) { d.Reason = "no_track"; return d; }
            string reason;
            AtwOption ammo = Pick(i.Options, i.DistNm, out reason);
            if (ammo == null) { d.Reason = reason; return d; }
            d.Ammo = ammo;
            d.THitS = ExpectedHitS(i.NowS, i.ReactionS, i.DistNm, ammo.SpeedKn);
            if (i.GameTask) { d.Reason = "game_task"; return d; }
            if (w != null && !w.Allowed(i.TrackId, d.THitS)) { d.Reason = "window"; return d; }
            int blocker;
            if (FriendlyNear(i.Near, i.SafetyNm, out blocker)) { d.Reason = "friendly_near"; d.BlockerUid = blocker; return d; }
            d.Fire = true;
            return d;
        }

        /// <summary>true when the reason for this uid differs from the last logged one (one log line per change).</summary>
        public static bool ReasonChanged(Dictionary<int, string> last, int uid, string reason)
        {
            string prev;
            if (last.TryGetValue(uid, out prev) && prev == reason) return false;
            last[uid] = reason;
            return true;
        }

        /// <summary>Relative bearing (deg, -180..180, + = starboard) inside a firing arc "from,to" read clockwise (FiringArcs of the vessel ini).</summary>
        public static bool InArc(double relDeg, double fromDeg, double toDeg)
        {
            double r = EvasionMath.Norm360(relDeg - fromDeg);
            double span = EvasionMath.Norm360(toDeg - fromDeg);
            return r <= span;
        }

        // ---- T-145 (ADR-0053): several torpedo tracks, one free mount each, cover of the neighbours ----

        public static long AtwKey(int a, int b) { return ((long)a << 32) | (uint)b; }

        /// <summary>Stable pseudo-key of "the" mount of an ammo when the game's mount identities are unknown.</summary>
        public static int SyntheticKey(string file)
        {
            unchecked
            {
                int h = 5381;
                if (file != null) foreach (char c in file) h = h * 33 + c;
                return h | 0x40000000;
            }
        }

        /// <summary>Time to impact on a protected ship: d / closing; the shooter without history is assumed to be chased at 45 kn; else +inf.</summary>
        public static double TimeToImpactS(double distNm, double? closingKn, bool isShooter)
        {
            if (closingKn.HasValue) return closingKn.Value > MinClosingKn ? distNm / closingKn.Value * 3600.0 : double.PositiveInfinity;
            return isShooter ? distNm / TorpedoAssumedKn * 3600.0 : double.PositiveInfinity;
        }

        /// <summary>The threatened ship of the track = the candidate with the smallest time to impact.</summary>
        public static void Threat(AtwTrackIn t)
        {
            t.ThreatUid = -1; t.ThreatIsShooter = false; t.TtiS = double.PositiveInfinity;
            if (t.Cands == null) return;
            foreach (AtwThreatCand c in t.Cands)
            {
                if (c == null) continue;
                double tti = TimeToImpactS(c.DistNm, c.ClosingKn, c.IsShooter);
                if (double.IsInfinity(tti) && c.HeldTtiS.HasValue) tti = c.HeldTtiS.Value;
                if (tti < t.TtiS) { t.TtiS = tti; t.ThreatUid = c.Uid; t.ThreatIsShooter = c.IsShooter; }
            }
        }

        // true when a goes strictly before b
        private static bool Before(AtwTrackIn a, AtwTrackIn b)
        {
            bool fa = !double.IsInfinity(a.TtiS), fb = !double.IsInfinity(b.TtiS);
            if (fa != fb) return fa;
            bool tie = !fa || Math.Abs(a.TtiS - b.TtiS) < TieS;
            if (!tie) return a.TtiS < b.TtiS;
            if (a.ThreatIsShooter != b.ThreatIsShooter) return a.ThreatIsShooter;
            if (a.SalvosSoFar != b.SalvosSoFar) return a.SalvosSoFar < b.SalvosSoFar;
            if (a.DistNm != b.DistNm) return a.DistNm < b.DistNm;
            return false;
        }

        /// <summary>Stable insertion sort (the 5 s tie is not transitive), cut to maxTracks (1..8). Calls Threat() for each track.</summary>
        public static List<AtwTrackIn> Rank(List<AtwTrackIn> tracks, int maxTracks)
        {
            var res = new List<AtwTrackIn>();
            if (tracks == null) return res;
            foreach (AtwTrackIn t in tracks) { if (t == null) continue; Threat(t); res.Add(t); }
            for (int i = 1; i < res.Count; i++)
                for (int j = i; j > 0 && Before(res[j], res[j - 1]); j--)
                { AtwTrackIn x = res[j]; res[j] = res[j - 1]; res[j - 1] = x; }
            int max = Math.Max(1, Math.Min(MaxTracksLimit, maxTracks));
            if (res.Count > max) res.RemoveRange(max, res.Count - max);
            return res;
        }

        private static List<int> KeysOf(AtwOption o)
        {
            if (o.MountKeys != null && o.MountKeys.Count > 0) return o.MountKeys;
            var l = new List<int>();
            if (o.MountReady) l.Add(SyntheticKey(o.File));
            return l;
        }

        private static AtwOption Clone(AtwOption o, int count, List<int> keys)
        {
            return new AtwOption { File = o.File, Display = o.Display, Kill = o.Kill, MinNm = o.MinNm, MaxNm = o.MaxNm, SpeedKn = o.SpeedKn, Count = count, MountReady = keys.Count > 0, MountKeys = keys, ArcBlocked = o.ArcBlocked && keys.Count == 0 };
        }

        /// <summary>
        /// One round of one shooter: tracks by time to impact, each gets a free mount (not busy, not taken in this round), the ammo left after
        /// the orders in flight, then game task / window / safety as in Decide. Pure: the caller does Note() and Take().
        /// </summary>
        public static List<AtwAssign> Allocate(AtwRoundIn r, AtwWindows w, AtwMounts busy)
        {
            var res = new List<AtwAssign>();
            if (r == null) return res;
            List<AtwTrackIn> ranked = Rank(r.Tracks, r.MaxTracks);
            var usedKeys = new HashSet<int>();
            var usedPerFile = new Dictionary<string, int>();
            var firedFiles = new HashSet<string>();
            for (int k = 0; k < ranked.Count; k++)
            {
                AtwTrackIn t = ranked[k];
                var a = new AtwAssign { TrackId = t.TrackId, ThreatUid = t.ThreatUid, Rank = k + 1, Of = ranked.Count, TtiS = t.TtiS };
                res.Add(a);
                if (r.Role == "cover" && double.IsInfinity(t.TtiS)) { a.Reason = "no_threat"; continue; }
                // A: ammo left, mounts "ready in the game"; B: the same minus busy / taken mounts
                var optsA = new List<AtwOption>(); var optsB = new List<AtwOption>(); var origs = new List<AtwOption>();
                foreach (AtwOption o in t.Options)
                {
                    if (o == null) continue;
                    int used; usedPerFile.TryGetValue(o.File ?? "", out used);
                    int left = o.Count - (busy == null ? 0 : busy.InFlight(r.ShooterUid, o.File, r.NowS)) - used;
                    List<int> all = KeysOf(o), free = new List<int>();
                    foreach (int key in all)
                        if (!usedKeys.Contains(key) && (busy == null || !busy.Busy(r.ShooterUid, key, r.NowS))) free.Add(key);
                    if (OneOrderPerAmmo && firedFiles.Contains(o.File ?? "")) free.Clear();
                    origs.Add(o); optsA.Add(Clone(o, left, all)); optsB.Add(Clone(o, left, free));
                }
                string reason;
                AtwOption pa = Pick(optsA, t.DistNm, out reason);
                if (pa == null) { a.Reason = reason; continue; }
                if (t.DistNm > r.FireNm) { a.Reason = "wait_closer"; continue; }   // T-193: no window Note, the salvo waits
                if (!t.Fresh && !(t.AgeS <= AtwStaleFireMaxS)) { a.Reason = "track_stale"; continue; }   // T-203: no window Note; NaN age = unknown = refuse
                AtwOption pb = Pick(optsB, t.DistNm, out reason);
                if (pb == null) { a.Reason = "mount_busy"; continue; }
                a.Ammo = origs[optsB.IndexOf(pb)];
                a.THitS = ExpectedHitS(r.NowS, r.ReactionS, t.DistNm, a.Ammo.SpeedKn);
                string eng = t.EngagedBy ?? (t.GameTask ? "game_task" : null);   // T-230: own_order | game_task | game_firing
                if (eng != null) { a.Reason = eng; continue; }
                if (w != null && !w.Allowed(t.TrackId, a.THitS)) { a.Reason = "window"; continue; }
                int blocker;
                if (FriendlyNear(t.Near, r.SafetyNm, out blocker)) { a.Reason = "friendly_near"; a.BlockerUid = blocker; continue; }
                a.MountKey = ChooseKey(pb.MountKeys, ranked, k);
                a.Fire = true;
                usedKeys.Add(a.MountKey);
                int u2; usedPerFile.TryGetValue(a.Ammo.File ?? "", out u2); usedPerFile[a.Ammo.File ?? ""] = u2 + 1;
                firedFiles.Add(a.Ammo.File ?? "");
            }
            return res;
        }

        // the free mount that fewer of the remaining tracks can use; equal -> the smaller key
        private static int ChooseKey(List<int> free, List<AtwTrackIn> ranked, int from)
        {
            int best = 0, bestVis = int.MaxValue; bool have = false;
            foreach (int key in free)
            {
                int vis = 0;
                for (int j = from + 1; j < ranked.Count; j++)
                    foreach (AtwOption o in ranked[j].Options)
                        if (o != null && KeysOf(o).Contains(key)) { vis++; break; }
                if (!have || vis < bestVis || (vis == bestVis && key < best)) { best = key; bestVis = vis; have = true; }
            }
            return best;
        }

        /// <summary>null = the cover shot may be fired; else the reason: no_capability | off | flag_off | evading.</summary>
        public static string CoverGate(bool capability, bool atwOn, bool coverOn, bool unitFlag, bool evading)
        {
            if (!capability) return "no_capability";
            if (!atwOn || !coverOn) return "off";
            if (!unitFlag) return "flag_off";
            if (evading) return "evading";
            return null;
        }

        /// <summary>true when the reason for this (uid, track) differs from the last logged one.</summary>
        public static bool ReasonChanged(Dictionary<long, string> last, int uid, int trackId, string reason)
        {
            long key = AtwKey(uid, trackId);
            string prev;
            if (last.TryGetValue(key, out prev) && prev == reason) return false;
            last[key] = reason;
            return true;
        }

        public static string SelfTest()
        {
            // T-352 worldflat: a torpedo going due east at 70N does 40 kn on the flat world and 40 cos(70) on the beta globe
            foreach (bool globe in new[] { false, true })
            {
                var tw = new TrackSpeed { Globe = globe };
                double eastDegPerS = 40.0 / 3600.0 / 60.0;
                for (int i = 0; i <= 6; i++) tw.Update(1, 70, 20 + eastDegPerS * 10 * i, 10 * i);
                double? kn = tw.Kn(1), want = globe ? 40.0 * Math.Cos(70.0 * Math.PI / 180.0) : 40.0;
                if (!kn.HasValue || Math.Abs(kn.Value - want.Value) > 0.01) return "worldflat trackspeed globe=" + globe + ": " + kn;
            }
            // expected hit time
            if (Math.Abs(ExpectedHitS(100, 3, 3, 600) - 121) > 1e-9) return "ExpectedHitS(100,3,3,600) != 121";
            if (Math.Abs(ExpectedHitS(0, 0, 1, 0) - 6) > 1e-9) return "ExpectedHitS speed 0 -> default 600 kn";

            // windows
            var w = new AtwWindows();
            if (!w.Allowed(5, 0)) return "empty window must allow";
            w.Note(5, 121);
            if (w.Allowed(5, 133.9)) return "window must refuse 133.9";
            if (!w.Allowed(5, 134)) return "window must allow 134";
            if (!w.Allowed(6, 121)) return "other track must be allowed";
            if (w.Last(5) != 121) return "Last(5)";
            w.ResetIfTimeWentBack(100);
            if (w.Count != 1) return "first ResetIfTimeWentBack must keep";
            w.ResetIfTimeWentBack(150);
            if (w.Count != 1) return "forward time must keep";
            w.ResetIfTimeWentBack(50);
            if (w.Count != 0) return "time back must clear";
            w.Note(1, 1); w.Note(2, 2);
            w.Prune(new List<int> { 2 });
            if (w.Count != 1 || w.Last(2) == null) return "Prune keeps only live";
            w.Prune(new List<int>());
            if (w.Count != 0) return "Prune({}) clears";
            w.Note(3, 3); w.Forget(3);
            if (w.Count != 0) return "Forget";

            // arcs
            if (!InArc(-50, -115, 15)) return "arc -50 in (-115,15)";
            if (InArc(90, -115, 15)) return "arc 90 not in (-115,15)";
            if (!InArc(180, 50, -50)) return "arc 180 in (50,-50)";
            if (InArc(0, 50, -50)) return "arc 0 not in (50,-50)";
            if (!InArc(-179, -179, -15)) return "arc -179 in (-179,-15)";

            // pick
            string reason;
            if (Pick(new List<AtwOption>(), 1, out reason) != null || reason != "no_ammo") return "pick: empty -> no_ammo";
            if (Pick(new List<AtwOption> { Opt("a", 0.3, 0.19, 3, 0, true) }, 1, out reason) != null || reason != "no_ammo") return "pick: count 0 -> no_ammo";
            if (Pick(new List<AtwOption> { Opt("a", 0.3, 0.19, 3, 10, true) }, 4, out reason) != null || reason != "out_of_range") return "pick: out_of_range";
            if (Pick(new List<AtwOption> { Opt("a", 0.3, 0.19, 3, 10, true) }, 0.1, out reason) != null || reason != "min_range") return "pick: min_range";
            if (Pick(new List<AtwOption> { Opt("a", 0.3, 0.19, 3, 10, false) }, 1, out reason) != null || reason != "arc_or_reload") return "pick: arc_or_reload";
            AtwOption p = Pick(new List<AtwOption> { Opt("low", 0.25, 0.19, 3, 10, true), Opt("high", 0.4, 0.05, 0.5, 10, true) }, 0.4, out reason);
            if (p == null || p.File != "high" || reason != null) return "pick: bigger kill wins";
            p = Pick(new List<AtwOption> { Opt("short", 0.3, 0.05, 0.5, 10, true), Opt("long", 0.3, 0.05, 3, 10, true) }, 0.4, out reason);
            if (p == null || p.File != "long") return "pick: equal kill -> bigger MaxNm";
            p = Pick(new List<AtwOption> { Opt("notready", 0.5, 0.05, 3, 10, false), Opt("ready", 0.1, 0.05, 3, 10, true) }, 1, out reason);
            if (p == null || p.File != "ready") return "pick: not ready skipped";

            // friendly near
            int b;
            var near = new List<AtwNeighbour> { new AtwNeighbour { Uid = 7, Side = "own", DistToTorpNm = 0.1 } };
            if (!FriendlyNear(near, 0.15, out b) || b != 7) return "friendly at 0.1 must block";
            near[0].DistToTorpNm = 0.2;
            if (FriendlyNear(near, 0.15, out b)) return "friendly at 0.2 must not block";
            near[0].Uid = -5; near[0].DistToTorpNm = 0.1;
            if (!FriendlyNear(near, 0.15, out b) || b != -5) return "track neighbour (negative uid) must block";
            near[0].Uid = 7;
            near[0].DistToTorpNm = 0.2;
            near[0].DistToTorpNm = 0.1; near[0].IsShooter = true;
            if (FriendlyNear(near, 0.15, out b)) return "shooter must be ignored";

            // decide
            var ww = new AtwWindows();
            AtwInput inp = Input();
            AtwDecision dec = Decide(inp, ww);
            if (!dec.Fire || dec.Reason != null || dec.Ammo == null) return "decide: should fire";
            if (Math.Abs(dec.THitS - ExpectedHitS(100, 3, 1.0, 600)) > 1e-9) return "decide: THitS";
            inp = Input(); inp.Enabled = false;
            dec = Decide(inp, ww);
            if (dec.Fire || dec.Reason != null) return "decide: disabled must be silent";
            inp = Input(); inp.HasTrack = false;
            dec = Decide(inp, ww);
            if (dec.Fire || dec.Reason != "no_track") return "decide: no_track";
            inp = Input(); inp.Options.Clear();
            dec = Decide(inp, ww);
            if (dec.Fire || dec.Reason != "no_ammo") return "decide: no_ammo";
            inp = Input(); inp.GameTask = true;
            dec = Decide(inp, ww);
            if (dec.Fire || dec.Reason != "game_task" || dec.THitS <= 0) return "decide: game_task with THitS";
            inp = Input(); ww.Note(inp.TrackId, 110);
            dec = Decide(inp, ww);
            if (dec.Fire || dec.Reason != "window") return "decide: window";
            inp = Input(); inp.Near.Add(new AtwNeighbour { Uid = 9, Side = "neutral", DistToTorpNm = 0.1 });
            dec = Decide(inp, new AtwWindows());
            if (dec.Fire || dec.Reason != "friendly_near" || dec.BlockerUid != 9) return "decide: friendly_near";
            inp = Input(); inp.Options[0].SpeedKn = 0;
            dec = Decide(inp, new AtwWindows());
            if (Math.Abs(dec.THitS - ExpectedHitS(100, 3, 1.0, DefaultSpeedKn)) > 1e-9) return "decide: speed 0 -> 600 kn";

            // reason log
            var last = new Dictionary<int, string>();
            if (!ReasonChanged(last, 1, "window")) return "ReasonChanged first";
            if (ReasonChanged(last, 1, "window")) return "ReasonChanged same";
            if (!ReasonChanged(last, 1, "no_ammo")) return "ReasonChanged other";
            return SelfTestMulti();
        }

        // ---- T-145 selftest ----

        private static AtwOption KOpt(string file, double kill, double min, double max, int count, params int[] keys)
        {
            var o = Opt(file, kill, min, max, count, keys.Length > 0);
            o.MountKeys.AddRange(keys);
            return o;
        }

        // a track of the shooter (uid 1): shooter candidate at the same distance with the given closing speed
        private static AtwTrackIn Trk(int id, double dist, double? closing, params AtwOption[] opts)
        {
            var t = new AtwTrackIn { TrackId = id, DistNm = dist };
            t.Cands.Add(new AtwThreatCand { Uid = 1, IsShooter = true, DistNm = dist, ClosingKn = closing });
            t.Options.AddRange(opts);
            return t;
        }

        private static AtwRoundIn Round(string role, int max, params AtwTrackIn[] tr)
        {
            var r = new AtwRoundIn { ShooterUid = 1, Role = role, NowS = 100, ReactionS = 3, SafetyNm = 0.15, MaxTracks = max, FireNm = 3 };   // S1..S5: the pre-T-193 range
            r.Tracks.AddRange(tr);
            return r;
        }

        private static string SelfTestMulti()
        {
            // TTI
            if (Math.Abs(TimeToImpactS(3, 45, false) - 240) > 1e-6) return "tti: 3 nm at 45 kn = 240 s";
            if (!double.IsInfinity(TimeToImpactS(3, 0.3, true))) return "tti: closing 0.3 -> inf";
            if (Math.Abs(TimeToImpactS(3, null, true) - 240) > 1e-6) return "tti: shooter without history = d/45";
            if (!double.IsInfinity(TimeToImpactS(3, null, false))) return "tti: neighbour without history -> inf";
            if (!double.IsInfinity(TimeToImpactS(3, -20, true))) return "tti: receding shooter -> inf";

            // closing history
            var cl = new AtwClosing();
            const double kn45 = 45.0 / 3600.0;
            cl.Update(1, 7, 3.0, 10);
            if (cl.ClosingKn(1, 7) != null) return "closing: one sample -> null";
            cl.Update(1, 7, 3.0 - kn45, 11);
            cl.Update(1, 7, 3.0 - 2 * kn45, 12);
            if (cl.ClosingKn(1, 7) != null) return "closing: span under 3 s -> null";
            cl.Update(1, 7, 3.0 - 3 * kn45, 13);
            double? ck = cl.ClosingKn(1, 7);
            if (!ck.HasValue || Math.Abs(ck.Value - 45) > 0.5) return "closing: 45 kn expected, got " + ck;
            if (cl.LastClosingS(1, 7) != 13) return "closing: LastClosingS";
            // the 7050 case: a track that jumps 2 nm for one sample must not give a tiny TTI at 0.5 nm
            var cj = new AtwClosing();
            for (int i = 0; i < 8; i++) cj.Update(1, 9, 0.5 + kn45 * (7 - i) + (i == 4 ? 2.0 : 0), 20 + i);
            double? cjk = cj.ClosingKn(1, 9);
            if (!cjk.HasValue || cjk.Value < 35 || cjk.Value > 55) return "closing: jump tolerated, got " + cjk;
            if (TimeToImpactS(0.5, cjk, true) < 30) return "closing: jump must not give tti < 30 s";
            cl.Update(2, 8, 1, 10);
            cl.Prune(new List<int> { 7 });
            if (cl.Count != 1 || cl.ClosingKn(1, 7) == null) return "closing: Prune keeps live tracks only";
            cl.Clear();
            if (cl.Count != 0) return "closing: Clear";

            // T-193 threat hold: a lost closing keeps the last TTI
            var hold = Trk(6, 2, null); hold.Cands.Clear();
            hold.Cands.Add(new AtwThreatCand { Uid = 9, DistNm = 2, ClosingKn = null, HeldTtiS = 100 });
            Threat(hold);
            if (hold.ThreatUid != 9 || Math.Abs(hold.TtiS - 100) > 1e-9) return "threat: held TTI";
            var noHold = Trk(6, 2, null); noHold.Cands.Clear();
            noHold.Cands.Add(new AtwThreatCand { Uid = 9, DistNm = 2, ClosingKn = null });
            Threat(noHold);
            if (!double.IsInfinity(noHold.TtiS)) return "threat: no hold without HeldTtiS";

            // fire range: wait_closer
            {
                var wf = new AtwWindows();
                var rf = Round("evade", 4, Trk(1, 2.9, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11)));
                rf.FireNm = DefaultFireNm;
                var af = Allocate(rf, wf, new AtwMounts());
                if (af[0].Fire || af[0].Reason != "wait_closer" || wf.Count != 0) return "fire range: 2.9 nm must wait_closer without a window";
                rf = Round("evade", 4, Trk(1, 1.4, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11)));
                rf.FireNm = DefaultFireNm;
                af = Allocate(rf, wf, new AtwMounts());
                if (!af[0].Fire) return "fire range: 1.4 nm must fire";
                rf = Round("evade", 4, Trk(1, 2.9, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11)));
                af = Allocate(rf, wf, new AtwMounts());   // FireNm = 3
                if (!af[0].Fire) return "fire range: FireNm 3 = old behaviour";
            }

            // T-203 track_stale: a frozen track fires only while its last detection is no older than AtwStaleFireMaxS
            {
                var ws = new AtwWindows();
                var ts = Trk(1, 1.2, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11));
                ts.Fresh = false; ts.AgeS = 31;
                var rs = Round("evade", 4, ts); rs.FireNm = DefaultFireNm;
                var mt = new AtwMounts();
                var asx = Allocate(rs, ws, mt);
                if (asx[0].Fire || asx[0].Reason != "track_stale" || ws.Count != 0 || mt.Count != 0) return "stale: 1.2 nm, 31 s old must be track_stale without a window or a mount";
                ts = Trk(1, 1.2, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11)); ts.Fresh = false;   // age unknown
                rs = Round("evade", 4, ts); rs.FireNm = DefaultFireNm;
                asx = Allocate(rs, ws, new AtwMounts());
                if (asx[0].Fire || asx[0].Reason != "track_stale") return "stale: unknown age must be track_stale";
                ts = Trk(1, 1.2, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11)); ts.Fresh = false; ts.AgeS = 30;
                rs = Round("evade", 4, ts); rs.FireNm = DefaultFireNm;
                asx = Allocate(rs, ws, new AtwMounts());
                if (!asx[0].Fire) return "stale: 30 s old must fire (AtwStaleFireMaxS)";
                ts = Trk(1, 2.0, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11)); ts.Fresh = false; ts.AgeS = 99;
                rs = Round("evade", 4, ts); rs.FireNm = DefaultFireNm;
                asx = Allocate(rs, new AtwWindows(), new AtwMounts());
                if (asx[0].Fire || asx[0].Reason != "wait_closer") return "stale: 2.0 nm must be wait_closer first";
                ts = Trk(1, 1.2, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11));
                rs = Round("evade", 4, ts); rs.FireNm = DefaultFireNm;
                if (!Allocate(rs, new AtwWindows(), new AtwMounts())[0].Fire) return "stale: a fresh track must fire";
            }
            // T-203 TrackSpeed: 45 kn track, updates in jumps with one 0.3 nm outlier
            {
                var tsp = new TrackSpeed();
                double lat = 10.0, kn45d = 45.0 / 3600.0 / 60.0;
                for (int i = 0; i <= 5; i++) tsp.Update(1, lat + kn45d * 20 * i + (i == 3 ? 0.3 / 60.0 : 0), 20.0, 20 * i);
                double? sp = tsp.Kn(1);
                if (!sp.HasValue || sp.Value < 40 || sp.Value > 50) return "trackspeed: 45 kn expected, got " + sp;
                var tsq = new TrackSpeed(); tsq.Update(2, 10, 20, 0); tsq.Update(2, 10.001, 20, 10);
                if (tsq.Kn(2).HasValue) return "trackspeed: 2 samples -> null";
                tsp.Update(3, 1, 1, 0);
                tsp.Prune(new List<int> { 1 });
                if (tsp.Count != 1) return "trackspeed: Prune";
                tsp.Clear();
                if (tsp.Count != 0) return "trackspeed: Clear";
            }

            // threat = min TTI over the candidates
            var th = Trk(5, 3, 45);
            th.Cands.Add(new AtwThreatCand { Uid = 9, DistNm = 1, ClosingKn = 45 });
            Threat(th);
            if (th.ThreatUid != 9 || th.ThreatIsShooter || Math.Abs(th.TtiS - 80) > 1e-6) return "threat: neighbour at 1 nm is the threatened one";
            th = Trk(5, 3, -10);
            Threat(th);
            if (th.ThreatUid != -1 || !double.IsInfinity(th.TtiS)) return "threat: nobody threatened";

            // rank
            var ranked = Rank(new List<AtwTrackIn> { Trk(1, 3, 45), Trk(2, 2, 45), Trk(3, 1, -5) }, 4);
            if (ranked.Count != 3 || ranked[0].TrackId != 2 || ranked[1].TrackId != 1 || ranked[2].TrackId != 3) return "rank: TTI up, receding last";
            // 4 s apart: threat to the shooter first
            var nb = Trk(10, 3, null); nb.Cands.Clear();
            nb.Cands.Add(new AtwThreatCand { Uid = 9, DistNm = 3, ClosingKn = 3.0 / (236.0 / 3600.0) });   // 236 s, neighbour
            var own = Trk(11, 3, 45);                                                                          // 240 s, the shooter
            ranked = Rank(new List<AtwTrackIn> { nb, own }, 4);
            if (ranked[0].TrackId != 11) return "rank: |dTTI| < 5 s -> shooter first";
            nb.Cands[0].ClosingKn = 3.0 / (234.0 / 3600.0);                                                    // 234 s: 6 s earlier
            ranked = Rank(new List<AtwTrackIn> { nb, own }, 4);
            if (ranked[0].TrackId != 10) return "rank: |dTTI| >= 5 s -> by TTI";
            var many = new List<AtwTrackIn>();
            for (int i = 0; i < 6; i++) many.Add(Trk(20 + i, 1 + i * 0.4, 45));
            if (Rank(many, 4).Count != 4 || Rank(many, 0).Count != 1 || Rank(many, 99).Count != 6) return "rank: MaxTracks cut / clamp";
            if (Rank(many, 4)[0].TrackId != 20) return "rank: nearest first";

            // allocate: two torpedoes, two ready mounts
            var w = new AtwWindows(); var bz = new AtwMounts();
            var r = Round("evade", 4, Trk(1, 3.0, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11, 12)), Trk(2, 2.5, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11, 12)));
            List<AtwAssign> al = Allocate(r, w, bz);
            if (al.Count != 2 || !al[0].Fire || !al[1].Fire) return "allocate: two mounts -> two shots";
            if (al[0].TrackId != 2 || al[1].TrackId != 1) return "allocate: urgent track first";
            if (al[0].MountKey == al[1].MountKey) return "allocate: different mounts";
            if (al[0].Rank != 1 || al[0].Of != 2 || al[1].Rank != 2) return "allocate: rank k/n";
            if (w.Count != 0 || bz.Count != 0) return "allocate: must be pure";
            // one mount
            r = Round("evade", 4, Trk(1, 3.0, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11)), Trk(2, 2.5, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11)));
            al = Allocate(r, w, bz);
            if (!al[0].Fire || al[0].TrackId != 2 || al[1].Fire || al[1].Reason != "mount_busy") return "allocate: one mount -> mount_busy";
            // mount busy by an earlier order
            bz.Take(1, 11, "rbu", 105);
            al = Allocate(Round("evade", 4, Trk(1, 3.0, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11))), w, bz);
            if (al[0].Fire || al[0].Reason != "mount_busy") return "allocate: Busy mount -> mount_busy";
            bz.Prune(106);
            if (bz.Busy(1, 11, 106) || bz.InFlight(1, "rbu", 106) != 0 || bz.Count != 0) return "mounts: Prune";
            // ammo left after the orders in flight
            bz.Take(1, 11, "rbu", 105);
            al = Allocate(Round("evade", 4, Trk(1, 3.0, 45, KOpt("rbu", 0.25, 0.19, 3, 1, 12))), w, bz);
            if (al[0].Fire || al[0].Reason != "no_ammo") return "allocate: Count 1, order in flight -> no_ammo";
            bz.Release(1, 11);
            if (bz.Busy(1, 11, 100)) return "mounts: Release";
            bz.Take(1, 11, "rbu", 105); bz.Take(1, 12, "rbu", 105); bz.Take(2, 11, "rbu", 105);
            bz.ReleaseMissing(1, new List<int> { 12 });
            if (bz.Busy(1, 11, 100) || !bz.Busy(1, 12, 100) || !bz.Busy(2, 11, 100)) return "mounts: ReleaseMissing keeps ready keys and other shooters";
            bz.Clear();
            r = Round("evade", 4, Trk(1, 3.0, 45, KOpt("rbu", 0.25, 0.19, 3, 1, 11, 12)), Trk(2, 2.5, 45, KOpt("rbu", 0.25, 0.19, 3, 1, 11, 12)));
            al = Allocate(r, w, bz);
            if (!al[0].Fire || al[1].Fire || al[1].Reason != "no_ammo") return "allocate: remainder 1 -> second no_ammo";
            // cover and no threat
            al = Allocate(Round("cover", 4, Trk(1, 2.0, -3, KOpt("rbu", 0.25, 0.19, 3, 10, 11))), w, bz);
            if (al[0].Fire || al[0].Reason != "no_threat") return "allocate: cover + inf TTI -> no_threat";
            al = Allocate(Round("evade", 4, Trk(1, 2.0, -3, KOpt("rbu", 0.25, 0.19, 3, 10, 11))), w, bz);
            if (!al[0].Fire) return "allocate: evade fires even at a receding track";
            // friendly_near on A does not block B
            var ta = Trk(1, 2.0, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11, 12)); ta.Near.Add(new AtwNeighbour { Uid = 5, Side = "own", DistToTorpNm = 0.1 });
            var tb = Trk(2, 2.5, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11, 12));
            al = Allocate(Round("evade", 4, ta, tb), w, bz);
            if (al[0].TrackId != 1 || al[0].Fire || al[0].Reason != "friendly_near" || al[0].BlockerUid != 5) return "allocate: friendly_near on A";
            if (!al[1].Fire) return "allocate: friendly_near on A must not block B";
            // window and game task
            w.Note(1, 100 + 3 + 12);
            al = Allocate(Round("evade", 4, Trk(1, 3.0, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11))), w, bz);
            if (al[0].Fire || al[0].Reason != "window") return "allocate: window";
            var tg = Trk(1, 3.0, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11)); tg.GameTask = true;
            al = Allocate(Round("evade", 4, tg), new AtwWindows(), bz);
            if (al[0].Fire || al[0].Reason != "game_task" || al[0].THitS <= 0) return "allocate: game_task";
            // key choice: the mount visible to fewer remaining tracks
            var t1 = Trk(1, 2.0, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11, 12));
            var t2 = Trk(2, 2.5, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 11));
            al = Allocate(Round("evade", 4, t1, t2), new AtwWindows(), new AtwMounts());
            if (!al[0].Fire || !al[1].Fire || al[0].MountKey != 12 || al[1].MountKey != 11) return "allocate: mount visible to fewer tracks first";
            // synthetic mount (no keys, MountReady)
            var so = Opt("rbu", 0.25, 0.19, 3, 10, true);
            al = Allocate(Round("evade", 4, Trk(1, 2.0, 45, so), Trk(2, 2.5, 45, so)), new AtwWindows(), new AtwMounts());
            if (!al[0].Fire || al[0].MountKey != SyntheticKey("rbu") || al[1].Fire || al[1].Reason != "mount_busy") return "allocate: synthetic mount = one per ammo";

            // one track + MaxTracks 1 = Decide
            string[] kinds = { "ok", "ammo0", "far", "near", "notready", "task", "window", "friend" };
            foreach (string kind in kinds)
            {
                var opt = Opt("rbu", 0.25, 0.19, 3, kind == "ammo0" ? 0 : 10, kind != "notready");
                double dist = kind == "far" ? 4 : kind == "near" ? 0.1 : 1.0;
                var wa = new AtwWindows(); var wb = new AtwWindows();
                if (kind == "window") { wa.Note(42, 110); wb.Note(42, 110); }
                var di = new AtwInput { Enabled = true, HasTrack = true, TrackId = 42, NowS = 100, DistNm = dist, ReactionS = 3, SafetyNm = 0.15, GameTask = kind == "task" };
                di.Options.Add(opt);
                if (kind == "friend") di.Near.Add(new AtwNeighbour { Uid = 9, Side = "own", DistToTorpNm = 0.1 });
                AtwDecision dd = Decide(di, wa);
                var ti = Trk(42, dist, 45, opt); ti.GameTask = di.GameTask; ti.Near.AddRange(di.Near);
                AtwAssign aa = Allocate(Round("evade", 1, ti), wb, new AtwMounts())[0];
                string ad = aa.Ammo == null ? null : aa.Ammo.File, dm = dd.Ammo == null ? null : dd.Ammo.File;
                if (aa.Fire != dd.Fire || aa.Reason != dd.Reason || ad != dm || Math.Abs(aa.THitS - dd.THitS) > 1e-9) return "allocate(1 track, max 1) != Decide: " + kind;
            }

            // cover gate
            if (CoverGate(true, true, true, true, false) != null) return "gate: all on";
            if (CoverGate(false, true, true, true, false) != "no_capability") return "gate: no_capability";
            if (CoverGate(true, false, true, true, false) != "off" || CoverGate(true, true, false, true, false) != "off") return "gate: off";
            if (CoverGate(true, true, true, false, false) != "flag_off") return "gate: flag_off";
            if (CoverGate(true, true, true, true, true) != "evading") return "gate: evading";

            // reason log per (uid, track)
            var lastL = new Dictionary<long, string>();
            if (!ReasonChanged(lastL, 1, 2, "window") || ReasonChanged(lastL, 1, 2, "window") || !ReasonChanged(lastL, 1, 3, "window") || !ReasonChanged(lastL, 1, 2, "mount_busy")) return "ReasonChanged(uid, track)";
            return SelfTestT230();
        }

        // T-230 (ADR-0095): no_arc, own_order / game_firing
        private static string SelfTestT230()
        {
            string reason;
            var blocked = Opt("a", 0.3, 0.19, 3, 10, false); blocked.ArcBlocked = true;
            if (Pick(new List<AtwOption> { blocked }, 1, out reason) != null || reason != "no_arc") return "t230 pick: arc blocked -> no_arc";
            if (Pick(new List<AtwOption> { Opt("a", 0.3, 0.19, 3, 10, false) }, 1, out reason) != null || reason != "arc_or_reload") return "t230 pick: not ready, arc unknown -> arc_or_reload";
            AtwOption ok = Pick(new List<AtwOption> { blocked, Opt("b", 0.1, 0.19, 3, 10, true) }, 1, out reason);
            if (ok == null || ok.File != "b") return "t230 pick: a ready option beats a blocked one";

            Func<string, bool, AtwAssign> alloc = (eng, game) =>
            {
                var t = Trk(7, 1.0, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 5));
                t.EngagedBy = eng; t.GameTask = game;
                return Allocate(Round("evade", 1, t), new AtwWindows(), new AtwMounts())[0];
            };
            AtwAssign x = alloc("own_order", false);
            if (x.Fire || x.Reason != "own_order") return "t230 allocate: own_order";
            x = alloc("game_firing", false);
            if (x.Fire || x.Reason != "game_firing") return "t230 allocate: game_firing";
            x = alloc(null, true);
            if (x.Fire || x.Reason != "game_task") return "t230 allocate: GameTask without EngagedBy = game_task";
            x = alloc(null, false);
            if (!x.Fire) return "t230 allocate: free track fires";
            var far = Trk(7, 2.9, 45, KOpt("rbu", 0.25, 0.19, 3, 10, 5)); far.EngagedBy = "own_order";
            if (Allocate(Round("evade", 1, far), new AtwWindows(), new AtwMounts())[0].Reason != "own_order") return "t230 allocate: own_order at 2.9 nm (FireNm 3 in Round)";
            var wc = Trk(7, 3.5, 45, KOpt("rbu", 0.25, 0.19, 5, 10, 5)); wc.EngagedBy = "own_order";
            var r3 = Round("evade", 1, wc); r3.FireNm = 1.5;
            if (Allocate(r3, new AtwWindows(), new AtwMounts())[0].Reason != "wait_closer") return "t230 allocate: wait_closer before own_order";
            var tb = Trk(7, 1.0, 45, blocked);
            AtwAssign ab = Allocate(Round("evade", 1, tb), new AtwWindows(), new AtwMounts())[0];
            if (ab.Fire || ab.Reason != "no_arc") return "t230 allocate: blocked option -> no_arc";
            return AtwOrderMath.SelfTest() == null ? AtwTurnMath.SelfTest() : "atw order: " + AtwOrderMath.SelfTest();
        }

        private static AtwOption Opt(string file, double kill, double min, double max, int count, bool ready)
        {
            return new AtwOption { File = file, Display = file, Kill = kill, MinNm = min, MaxNm = max, Count = count, MountReady = ready, SpeedKn = 600 };
        }

        private static AtwInput Input()
        {
            var i = new AtwInput { Enabled = true, HasTrack = true, TrackId = 42, NowS = 100, DistNm = 1.0, ReactionS = 3, SafetyNm = 0.15 };
            i.Options.Add(Opt("rbu", 0.25, 0.19, 3, 10, true));
            return i;
        }
    }

    /// <summary>T-230: one anti-torpedo order of the mod, followed until the launch.</summary>
    internal sealed class AtwOrder
    {
        public int Uid, TrackId, MountKey;      // MountKey: chosen by Allocate; replaced by the key of the mount that took the task
        public string File;
        public double OrderedS, THitS, TakenS = double.NaN;
        public int ShipBefore;                  // ship ammo of File at the order
        public int MountLoadedAtTake = -1;      // loaded rounds of the taking mount when first seen taken; -1 unknown
        public string Phase = "queued";         // queued | taken | launched | failed
        public string LastState;                // WeaponSystem._engageState name while taken
        public bool WasTaken;
        public int Used;                        // rounds launched (mount or ship delta)
        public string Attrib;                   // "mount" | "ship"
    }

    /// <summary>T-230: what the game shows about the order this round.</summary>
    internal sealed class AtwOrderObs
    {
        public bool InQueue;                    // still in ObjectBase._currentEngageTasks
        public bool Taken;                      // a WeaponSystem has _takenEngageTask == the order
        public int MountKey;                    // RuntimeHelpers.GetHashCode of that mount
        public string EngageState;              // its _engageState (null unknown)
        public int MountLoaded = -1;            // its getLoadedAmmoCount(File); -1 unknown
        public bool MountReloading;             // its _isReloading
        public int ShipLeft = -1;               // ship ammo of File now; -1 unknown
    }

    internal static class AtwOrderMath
    {
        /// <summary>Advances the order. null = keep following; "launched"; "failed:&lt;why&gt;" with why = dropped | timeout_&lt;state&gt; | timeout_queued.</summary>
        public static string Step(AtwOrder o, AtwOrderObs ob, double nowS)
        {
            if (o == null || ob == null) return null;
            if (ob.Taken)
            {
                if (!o.WasTaken)
                {
                    o.WasTaken = true; o.TakenS = nowS; o.MountKey = ob.MountKey; o.MountLoadedAtTake = ob.MountLoaded;
                }
                o.Phase = "taken"; o.LastState = ob.EngageState;
            }
            if (o.WasTaken && o.MountLoadedAtTake >= 0 && ob.MountLoaded >= 0 && (ob.MountLoaded < o.MountLoadedAtTake || (ob.MountReloading && !ob.Taken)))
            {
                o.Used = Math.Max(1, o.MountLoadedAtTake - ob.MountLoaded); o.Attrib = "mount"; o.Phase = "launched";
                return "launched";
            }
            if (!ob.InQueue && !ob.Taken)
            {
                if (o.WasTaken && ob.ShipLeft >= 0 && ob.ShipLeft < o.ShipBefore)
                {
                    o.Used = o.ShipBefore - ob.ShipLeft; o.Attrib = "ship"; o.Phase = "launched";
                    return "launched";
                }
                o.Phase = "failed";
                return "failed:dropped";
            }
            if (nowS - o.OrderedS >= AntiTorpedoMath.ConfirmS)
            {
                o.Phase = "failed";
                return "failed:timeout_" + (o.LastState ?? "queued");
            }
            return null;
        }

        private static AtwOrder Ord(double t) { return new AtwOrder { Uid = 1, TrackId = 9, File = "rbu", OrderedS = t, ShipBefore = 96 }; }

        public static string SelfTest()
        {
            // (1) queued -> taken -> loaded 12 -> 11
            var o = Ord(100);
            if (Step(o, new AtwOrderObs { InQueue = true }, 101) != null || o.Phase != "queued") return "1: queued keeps";
            if (Step(o, new AtwOrderObs { Taken = true, MountKey = 5, MountLoaded = 12, EngageState = "Aiming" }, 102) != null || o.Phase != "taken" || o.MountKey != 5) return "1: taken keeps";
            if (Step(o, new AtwOrderObs { Taken = true, MountKey = 5, MountLoaded = 11 }, 104) != "launched" || o.Attrib != "mount" || o.Used != 1) return "1: launch by the mount";
            // (2) stuck in the arcs state
            o = Ord(100);
            Step(o, new AtwOrderObs { Taken = true, MountKey = 5, MountLoaded = 12, EngageState = "TargetIsNotInFiringArcs" }, 101);
            if (Step(o, new AtwOrderObs { Taken = true, MountKey = 5, MountLoaded = 12, EngageState = "TargetIsNotInFiringArcs" }, 114.9) != null) return "2: before ConfirmS";
            if (Step(o, new AtwOrderObs { Taken = true, MountKey = 5, MountLoaded = 12, EngageState = "TargetIsNotInFiringArcs" }, 115) != "failed:timeout_TargetIsNotInFiringArcs") return "2: timeout with state";
            // (3) queued for 15 s
            o = Ord(100);
            if (Step(o, new AtwOrderObs { InQueue = true }, 115) != "failed:timeout_queued") return "3: timeout_queued";
            // (4) vanished without a take
            o = Ord(100);
            if (Step(o, new AtwOrderObs { ShipLeft = 96 }, 102) != "failed:dropped") return "4: dropped";
            // (5) taken, vanished, ship ammo 96 -> 84
            o = Ord(100);
            Step(o, new AtwOrderObs { Taken = true, MountKey = 5, MountLoaded = -1 }, 101);
            if (Step(o, new AtwOrderObs { ShipLeft = 84 }, 103) != "launched" || o.Attrib != "ship" || o.Used != 12) return "5: launch by the ship ammo";
            // (6) the ship ammo fell but the order was never taken: a game salvo, not ours
            o = Ord(100);
            if (Step(o, new AtwOrderObs { ShipLeft = 84 }, 103) != "failed:dropped") return "6: game salvo not counted";
            return null;
        }
    }
}
