using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-182 (ADR-0081): jamming of the radar coverage zone, the game part (main thread). Reads only what the player knows: a jammer counts when the game
    /// lists it for the unit AND it is a track of the player's own task force AND the track is identified AND it has a position estimate. The true object
    /// of a jammer is touched on exactly one line (JamTrack, marked leak-ok) to ask the game for the player's track of it; positions come from the track only.
    /// The static test tests/test_sea_power_radar_jam_leak.py keeps it so.
    /// </summary>
    internal sealed partial class GameApi
    {
        private readonly JamSignalLatch _jamLatch = new JamSignalLatch();
        private readonly Dictionary<int, object> _jamTracks = new Dictionary<int, object>();   // track id -> Vehicle, for the distance trigger
        private readonly List<int> _jamIds = new List<int>();
        private readonly List<bool> _jamIdent = new List<bool>();
        private readonly List<KeyValuePair<int, object>> _jamWatch = new List<KeyValuePair<int, object>>();   // owners under jamming (uid -> ObjectBase) for the 4 Hz bearing sample (T-190)
        private double _nextJamSample;
        private readonly Dictionary<int, double> _jamSkipAt = new Dictionary<int, double>();   // T-206: rate limit of the skip trace per track
        private readonly Dictionary<int, bool> _jamHeldLast = new Dictionary<int, bool>();
        private readonly Dictionary<int, double> _jamTraceAt = new Dictionary<int, double>();
        private int _jamTogglesLogged;
        private double _lastJamMs;
        private int _lastJamRadars;
        private bool _jamJ2SWarned;

        /// <summary>The setting is on and the game members are there (capability radar_jam).</summary>
        private bool JamReady() { return CovCfg.Jamming && Capabilities.Contains("radar_jam"); }

        /// <summary>T-190: the jam bearings of the owners in _jamWatch are read 4 times a second (the game fills them on the antenna sweep and clears them
        /// at the start of a data-link cycle; the 1 s state poll misses most fills, so the latch could not learn the real pause). Only the bearings field
        /// is read: no jammer list, no track.</summary>
        private void JamSample(double now)
        {
            if (now < _nextJamSample || _jamWatch.Count == 0) return;
            _nextJamSample = now + 0.25;
            double[] sink;
            for (int i = 0; i < _jamWatch.Count; i++)
            {
                var kv = _jamWatch[i];
                bool signal;
                try { signal = _jamLatch.Update(kv.Key, Get(kv.Value, RadarSpecs.ObjJamBearings) as IList<float>, now, out sink); }
                catch { continue; }
                if (!CovCfg.Debug) continue;
                bool was;
                if (_jamHeldLast.TryGetValue(kv.Key, out was) && was == signal) continue;
                _jamHeldLast[kv.Key] = signal;
                double last;
                if (_jamTraceAt.TryGetValue(kv.Key, out last) && now - last < 10.0) continue;
                _jamTraceAt[kv.Key] = now;
                double age, gap, hold;
                _jamLatch.Diag(kv.Key, now, out age, out gap, out hold);
                Log("radar jam: owner=" + kv.Key + " signal " + (signal ? "on" : "off") + " gap_s=" + Math.Round(gap, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " hold_s=" + Math.Round(hold, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        /// <summary>Dev channel: per owner of the latch the age of the last fill, the learned pause and the hold.</summary>
        private string JamDiagJson(double now)
        {
            var sb = new StringBuilder("[");
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            bool first = true;
            foreach (int uid in _jamLatch.Owners)
            {
                double age, gap, hold;
                if (!_jamLatch.Diag(uid, now, out age, out gap, out hold)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"uid\":").Append(uid).Append(",\"held\":").Append(Json.Bool(_jamLatch.Holds(uid, now))).Append(",\"age_s\":").Append(Math.Round(age, 1).ToString(ci))
                  .Append(",\"gap_s\":").Append(Math.Round(gap, 1).ToString(ci)).Append(",\"hold_s\":").Append(Math.Round(hold, 1).ToString(ci)).Append('}');
                if (sb.Length > 3000) break;
            }
            return sb.Append(']').ToString();
        }

        /// <summary>Dev channel: the "jam" objects of the last submitted specs (radars with a jam state other than none), at most 24.</summary>
        private string JamRadarsJson()
        {
            var specs = _lastSpecs;
            var sb = new StringBuilder("[");
            if (specs == null || !JamReady()) return "[]";
            var tiers = CoverageTiers();
            double minPower = MinPowerKw();
            int n = 0;
            foreach (var r in specs)
            {
                if (r.Jam == null || r.Jam.State == CoverageJamMath.StateNone) continue;
                if (n >= 24) break;
                string jj = JamJson(r, tiers, minPower);
                if (jj == "null") continue;
                if (n++ > 0) sb.Append(',');
                // JamJson is {"state":...}: prefix it with the identity of the radar
                sb.Append("{\"uid\":").Append(r.OwnerUid).Append(",\"unit\":").Append(Json.Str(r.OwnerName)).Append(",\"radar\":").Append(Json.Str(r.Name)).Append(',').Append(jj.Substring(1, jj.Length - 2))
                  .Append(",\"skip\":{\"ident\":").Append(r.Jam.SkipIdent).Append(",\"params\":").Append(r.Jam.SkipParams).Append(",\"fix\":").Append(r.Jam.SkipFix).Append(",\"range\":").Append(r.Jam.SkipRange).Append(",\"approx\":").Append(r.Jam.ApproxRange).Append("}}");
            }
            return sb.Append(']').ToString();
        }

        private static double JamNow() { return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency; }

        /// <summary>RCSValues._jamToSignalBurnThroughRatio (sensors.ini says 30, the code default is 3: read the real one); 30 if the member is missing (logged once).</summary>
        private double JamJ2S()
        {
            var v = D(Get(GetStatic(RadarSpecs.RcsValues), RadarSpecs.RcsJ2S));
            if (v.HasValue && v.Value > 0) return v.Value;
            if (!_jamJ2SWarned) { _jamJ2SWarned = true; Log("radar jam: J2S ratio is not readable, using " + CoverageJamMath.J2SDefault.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            return CoverageJamMath.J2SDefault;
        }

        /// <summary>The player's track of a jammer (null = unknown to the player). The only place that touches the object behind the jammer.</summary>
        private object JamTrack(object ecm, object player)
        {
            var bo = Get(ecm, RadarSpecs.EcmBaseObject);   // leak-ok: only to ask the game for the player's own track of it
            return bo == null ? null : Call(bo, RadarSpecs.ObjMapVehicleJam, player);
        }

        private bool JamTrackFix(object vehicle, out double lat, out double lon)
        {
            int sensors; double? sigma;
            return JamTrackFix(vehicle, out lat, out lon, out sensors, out sigma);
        }

        /// <summary>The estimate of the player's track plus how it is seen (T-206: sensor flags, error ellipse). Only the track is read.</summary>
        private bool JamTrackFix(object vehicle, out double lat, out double lon, out int sensors, out double? sigmaM)
        {
            lat = lon = 0; sensors = 0; sigmaM = null;
            var est = Get(vehicle, RadarSpecs.VehPosEstimate);
            if (est == null || Get(vehicle, RadarSpecs.VehBearingFan) != null) return false;
            var geo = Get(est, RadarSpecs.TupleItem1);
            var la = D(Get(geo, CoreSpecs.GeoLat)); var lo = D(Get(geo, CoreSpecs.GeoLon));
            if (!la.HasValue || !lo.HasValue) return false;
            lat = la.Value; lon = lo.Value;
            try { var ds = Get(vehicle, RadarSpecs.VehDetectingSensors); if (ds != null) sensors = Convert.ToInt32(ds); } catch { sensors = 0; }
            try { sigmaM = D(Get(Get(est, RadarSpecs.TupleItem2), RadarSpecs.EigMajorStdDev)); } catch { sigmaM = null; }
            return true;
        }

        private const int SkipIdent = 1, SkipParams = 2, SkipFix = 3, SkipRange = 4;

        /// <summary>One jammer of the list as a SI snapshot, or null when it is not known to the player (ADR-0081 section 4). Nothing of the ECM is read before the track is identified.</summary>
        private JamSource ReadKnownJammer(object vehicle, object ecm, bool identified, out int why)
        {
            why = 0;
            if (!identified) return null;
            // T-206: SensorSystemECM keeps its parameters in _ecm._ep; ECM._ep is not a member of SensorSystemECM
            var core = Get(ecm, RadarSpecs.EcmSysEcm);
            var ep = core == null ? null : Get(core, RadarSpecs.EcmEp);
            if (ep == null) { why = SkipParams; return null; }
            var typeObj = Get(ep, RadarSpecs.EpType);
            int type = typeObj == null ? -1 : Convert.ToInt32(typeObj);
            double lat, lon; int sensors; double? sigma;
            bool fix = JamTrackFix(vehicle, out lat, out lon, out sensors, out sigma);
            if (!fix) why = SkipFix;
            bool on = BoolOf(RxGet(ecm, RadarSpecs.EcmIsOn)), inop = BoolOf(RxGet(ecm, RadarSpecs.EcmInoperable)), destroyed = BoolOf(Get(ecm, RadarSpecs.EcmIsDestroyed));
            if (!CoverageJamMath.IsKnownJammer(true, type, on, inop, destroyed, true, identified, fix)) return null;
            var freqs = new List<int>();
            var fl = Get(ep, RadarSpecs.EpFrequencies) as IEnumerable;
            if (fl != null) foreach (var f in fl) freqs.Add(Convert.ToInt32(f));
            var id = Get(vehicle, RadarSpecs.VehIdJam);
            return new JamSource
            {
                Uid = id == null ? -1 : Convert.ToInt32(id), Lat = lat, Lon = lon,
                PeakKw = D(Get(ep, RadarSpecs.EpPeakPower)) ?? 0, GainLin = D(Get(ep, RadarSpecs.EpGainFactor)) ?? 1,
                Effectiveness = D(Get(ep, RadarSpecs.EpJamEffectiveness)) ?? 0, Frequencies = freqs.ToArray(),
                Sensors = sensors, SigmaM = sigma
            };
        }

        /// <summary>
        /// Main thread, inside SnapshotRaw: fills Spec.Jam for every active radar of `owner`. The game's list of jammers is copied first (the worker never sees it);
        /// jammers are filtered by the knowledge rule; the state comes from the game's bearings (latched) against the bearings of the known ones.
        /// </summary>
        private void FillJam(object owner, IList<RadarSnap> ownerSnaps, double j2s, double now)
        {
            if (!JamReady() || ownerSnaps.Count == 0) return;
            int ouid = ownerSnaps[0].Spec.OwnerUid;
            double[] bearings;
            bool signal = _jamLatch.Update(ouid, Get(owner, RadarSpecs.ObjJamBearings) as IList<float>, now, out bearings);
            if (!signal)
            {
                foreach (var sn in ownerSnaps) if (CoverageMath.Emitting(sn.Spec)) sn.Spec.Jam = new RadarJam { State = CoverageJamMath.StateNone };
                return;
            }
            var copy = new List<object>();
            try { var raw = Get(owner, RadarSpecs.ObjJammers) as IEnumerable; if (raw != null) foreach (var e in raw) copy.Add(e); }
            catch (InvalidOperationException) { }   // the list changed under the iteration: use what was copied (the next poll repeats it)
            var player = GetStatic(CoreSpecs.PlayerTaskforce);
            var known = new List<JamSource>();
            var skips = new int[5];   // T-206: only tracks of the player are counted; the number of unknown jammers is never exposed (ADR-0081 section 4)
            foreach (var ecm in copy)
            {
                if (ecm == null) continue;
                var veh = JamTrack(ecm, player);
                if (veh == null) continue;
                bool ident = BoolOf(RxGet(veh, RadarSpecs.VehIdentifiedJam));
                int why;
                var src = ReadKnownJammer(veh, ecm, ident, out why);
                if (src == null)
                {
                    if (!ident) why = SkipIdent;
                    if (why > 0) { skips[why]++; JamSkipTrace(veh, why, now); }
                    continue;
                }
                known.Add(src);
                _jamTracks[src.Uid] = veh;
            }
            double crew = 1.0;
            var crewObj = Get(owner, RadarSpecs.ObjCrew);
            if (crewObj != null) { var cf = D(Get(crewObj, RadarSpecs.CrewBurnFactor)); if (cf.HasValue && cf.Value > 0.1) crew = cf.Value; }
            foreach (var sn in ownerSnaps)
            {
                var spec = sn.Spec;
                if (!CoverageMath.Emitting(spec)) continue;
                var rp = Get(Get(sn.Radar, RadarSpecs.SysRadar), RadarSpecs.RadarRp);
                var freq = Get(rp, RadarSpecs.RpFrequency);
                var j = new RadarJam
                {
                    Frequency = freq == null ? -1 : Convert.ToInt32(freq), JamResistance = D(Get(rp, RadarSpecs.RpJamResistance)) ?? 0,
                    CrewFactor = crew, J2S = j2s, BearingsDeg = bearings
                };
                var kb = new List<double>(known.Count);
                foreach (var k in known)
                {
                    var c = k.Clone();
                    CoverageJamMath.Locate(spec, c);
                    int rc = CoverageJamMath.RangeClass(c);   // T-225 (T-206-Q2): a source with an estimated range (ESM bearings) cuts the zone too, the label gets "≈"
                    if (rc == OverviewMath.RangeNone) { j.SkipRange++; JamSkipTrace(_jamTracks.ContainsKey(k.Uid) ? _jamTracks[k.Uid] : null, SkipRange, now); continue; }
                    if (rc == OverviewMath.RangeApprox) { c.RangeApprox = true; j.ApproxRange++; }
                    j.Known.Add(c);
                    kb.Add(c.BearingDeg);
                }
                j.SkipIdent = skips[SkipIdent]; j.SkipParams = skips[SkipParams]; j.SkipFix = skips[SkipFix];
                j.State = CoverageJamMath.Classify(true, bearings, kb);
                spec.Jam = j;
            }
        }

        /// <summary>T-206: debug-only trace of why a track of the player did not become a known jammer; at most once per 30 s per track.</summary>
        private void JamSkipTrace(object veh, int why, double now)
        {
            if (!CovCfg.Debug || veh == null) return;
            var idObj = Get(veh, RadarSpecs.VehIdJam);
            int id = idObj == null ? -1 : Convert.ToInt32(idObj);
            double last;
            if (_jamSkipAt.TryGetValue(id, out last) && now - last < 30.0) return;
            _jamSkipAt[id] = now;
            Log("radar jam: track=" + id + " skip=" + (why == SkipIdent ? "ident" : why == SkipParams ? "params" : why == SkipFix ? "fix" : "range"));
        }

        /// <summary>
        /// Poll part of RadarStateKey for one unit with an "on" radar: signal flag, set of the player's tracks among the unit's jammers with their Identified flag,
        /// number of latched bearings. Cheap: two counts and no copies when nothing jams the unit.
        /// </summary>
        private string JamStateSig(object owner, double now)
        {
            if (!JamReady()) return "";
            int uid = UidOf(owner);
            var jb = Get(owner, RadarSpecs.ObjJamBearings) as ICollection;
            var jl = Get(owner, RadarSpecs.ObjJammers) as ICollection;
            if (jl != null && jl.Count > 0) WatchAdd(uid, owner); else WatchRemove(uid);
            if ((jb == null || jb.Count == 0) && (jl == null || jl.Count == 0) && !_jamLatch.Holds(uid, now)) return "j0";
            double[] bearings;
            bool signal = _jamLatch.Update(uid, jb as IList<float>, now, out bearings);
            _jamIds.Clear(); _jamIdent.Clear();
            if (signal && jl != null)
            {
                var player = GetStatic(CoreSpecs.PlayerTaskforce);
                try
                {
                    foreach (var ecm in (IEnumerable)jl)
                    {
                        var veh = ecm == null ? null : JamTrack(ecm, player);
                        if (veh == null) continue;
                        var id = Get(veh, RadarSpecs.VehIdJam);
                        _jamIds.Add(id == null ? -1 : Convert.ToInt32(id));
                        _jamIdent.Add(BoolOf(RxGet(veh, RadarSpecs.VehIdentifiedJam)));
                    }
                }
                catch (InvalidOperationException) { }
            }
            return CoverageJamMath.Signature(signal, _jamIds, _jamIdent, bearings.Length);
        }

        private void WatchAdd(int uid, object owner)
        {
            for (int i = 0; i < _jamWatch.Count; i++) if (_jamWatch[i].Key == uid) return;
            _jamWatch.Add(new KeyValuePair<int, object>(uid, owner));
        }

        private void WatchRemove(int uid)
        {
            for (int i = _jamWatch.Count - 1; i >= 0; i--) if (_jamWatch[i].Key == uid) _jamWatch.RemoveAt(i);
        }

        /// <summary>Owners that are gone leave the watch (called with the alive owners of a full snapshot).</summary>
        private void WatchForget(ICollection<int> aliveOwners)
        {
            for (int i = _jamWatch.Count - 1; i >= 0; i--) if (!aliveOwners.Contains(_jamWatch[i].Key)) _jamWatch.RemoveAt(i);
        }

        /// <summary>T-195: the track ids that the last specs still list as known jammers.</summary>
        internal static HashSet<int> JamKnownUids(List<RadarSpec> last)
        {
            var set = new HashSet<int>();
            if (last == null) return set;
            foreach (var spec in last)
                if (spec.Jam != null && spec.Jam.Known != null) foreach (var k in spec.Jam.Known) set.Add(k.Uid);
            return set;
        }

        /// <summary>T-195: drops the cached tracks that no radar lists as a known jammer any more (dead / lost / no jamming at all), not only on leaving the mission.</summary>
        private void JamTracksPrune(List<RadarSpec> last)
        {
            if (_jamTracks.Count == 0) return;
            var keep = JamKnownUids(last);
            List<int> drop = null;
            foreach (var uid in _jamTracks.Keys) if (!keep.Contains(uid)) (drop ?? (drop = new List<int>())).Add(uid);
            if (drop != null) foreach (var uid in drop) _jamTracks.Remove(uid);
        }

        /// <summary>True if the estimated distance from a radar to a known jammer changed by more than 10 % since the last submitted specs.</summary>
        private bool JamDistanceMoved(List<RadarSpec> last)
        {
            JamTracksPrune(last);
            if (last == null || _jamTracks.Count == 0) return false;
            int lastOwner = int.MinValue;
            foreach (var spec in last)
            {
                if (spec.Jam == null || spec.Jam.Known.Count == 0 || spec.OwnerUid == lastOwner) continue;
                lastOwner = spec.OwnerUid;
                foreach (var k in spec.Jam.Known)
                {
                    object veh;
                    double lat, lon;
                    if (!_jamTracks.TryGetValue(k.Uid, out veh)) continue;
                    try { if (!JamTrackFix(veh, out lat, out lon)) continue; } catch { continue; }
                    var t = k.Clone(); t.Lat = lat; t.Lon = lon;
                    CoverageJamMath.Locate(spec, t);
                    if (CoverageJamMath.DistMoved(k.DistM, t.DistM)) return true;
                }
            }
            return false;
        }

        /// <summary>The "jam" object of one radar in the radar_coverage answer: state, effective range per tier (null = not cut), game bearings, number of known jammers.</summary>
        private string JamJson(RadarSpec r, List<CoverageTier> tiers, double minPower)
        {
            var j = r.Jam;
            if (!JamReady() || j == null) return "null";
            var sb = new StringBuilder("{\"state\":").Append(Json.Str(j.State)).Append(",\"r_nm\":[");
            bool cut = CoverageJamMath.CutApplies(r);
            for (int i = 0; i < tiers.Count; i++)
            {
                if (i > 0) sb.Append(',');
                double rbt = cut ? CoverageJamMath.BurnThroughM(r, j, tiers[i].RcsM2) : double.PositiveInfinity;
                double limit = CoverageMath.RangeLimit(r, tiers[i], minPower).RangeM;
                sb.Append(rbt < limit ? Nm(rbt) : "null");
            }
            sb.Append("],\"bearings_deg\":[");
            for (int i = 0; i < j.BearingsDeg.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Json.Num(Math.Round(j.BearingsDeg[i], 1))); }
            return sb.Append("],\"known\":").Append(j.Known.Count).Append('}').ToString();
        }

        // ---------- selfcheck ----------

        private static CoverageFrame RunWorker(CoverageWorker w, List<RadarSpec> specs, List<CoverageTier> tiers, CoverageConfig cfg, out string err)
        {
            err = null;
            w.Submit(specs, tiers, 2e-14, cfg, false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CoverageFrame f = null;
            while (sw.ElapsedMilliseconds < 5000 && (f = w.TakeResult()) == null) Thread.Sleep(5);
            if (f == null) err = "worker timed out";
            else if (f.Error != null) err = f.Error;
            return err == null ? f : null;
        }

        private static long Bits(RayProfile p)
        {
            long n = 0;
            if (p.Visible == null) return 0;
            foreach (ulong w in p.Visible) { ulong v = w; while (v != 0) { n++; v &= v - 1; } }
            return n;
        }

        /// <summary>selfcheck: the worker with jammed synthetic radars (jamming on / off / unknown). Returns null or an error text.</summary>
        internal static string CoverageJamSmoke()
        {
            string err;
            var tiers = CoverageMath.ParseTiers(CoverageMath.DefaultTiers, out err);
            Func<int, RadarSpec> mk = i => new RadarSpec
            {
                Globe = true,   // T-352: the stands keep the beta (spherical) expectations; the flat world has its own sections
                OwnerUid = i, OwnerName = "U" + i, Name = "SPS-49", Type = "Search", Air = true, Surface = false, On = true,
                Lat = 15 + i * 0.6, Lon = 42 + i * 0.6, AntennaM = 30, MinRangeM = 600, MaxRangeM = 463000, MinAltM = 3, MaxAltM = 45000,
                PeakPowerKw = 280, GainFactor = Math.Pow(10, 3.9), WaveLengthM = 0.46121916, FullCircle = true, VisibleHeightM = 3, TerrainUnderAntennaM = -50
            };
            Func<bool, List<RadarSpec>> specsOf = jammed =>
            {
                var l = new List<RadarSpec> { mk(0), mk(1), mk(2) };
                if (!jammed) return l;
                var s0 = new JamSource { Uid = 5, PeakKw = 11, GainLin = Math.Pow(10, 0.5), Effectiveness = 1, Frequencies = new[] { 4 } };
                CoverageMath.Destination(l[0].Lat, l[0].Lon, 90, 50000, l[0].Globe, out s0.Lat, out s0.Lon);
                CoverageJamMath.Locate(l[0], s0);
                var j0 = new RadarJam { State = CoverageJamMath.StateKnown, Frequency = 4, J2S = 30, BearingsDeg = new double[] { 90 } };
                j0.Known.Add(s0);
                l[0].Jam = j0;
                l[1].Jam = new RadarJam { State = CoverageJamMath.StateUnknown, Frequency = 4, BearingsDeg = new double[] { 40 } };
                return l;
            };
            using (var w = new CoverageWorker())
            {
                var off = new CoverageConfig { Rays = 90, Bins = 64, Jamming = false };
                var on = new CoverageConfig { Rays = 90, Bins = 64, Jamming = true };
                var plain = RunWorker(w, specsOf(false), tiers, on, out err);
                if (plain == null) return "plain: " + err;
                var offFrame = RunWorker(w, specsOf(true), tiers, off, out err);
                if (offFrame == null) return "jamming off: " + err;
                if (!offFrame.Raster.SequenceEqual(plain.Raster) || offFrame.Segs.Count != plain.Segs.Count || offFrame.JamMarks.Count != 0)
                    return "Jamming=false is not the old frame";
                var jf = RunWorker(w, specsOf(true), tiers, on, out err);
                if (jf == null) return "jamming on: " + err;
                for (int t = 0; t < tiers.Count; t++)
                    if (Bits(plain.Profiles[t]) > 0 && Bits(jf.Profiles[t]) >= Bits(plain.Profiles[t])) return "jamming did not cut the profile of tier " + t;
                if (!jf.Segs.Any(s => s.Style == CoverageJamMath.StyleJamDashed)) return "no dashed outline (Style 3)";
                if (jf.JamMarks.Count != 2) return "marks " + jf.JamMarks.Count + " (2 expected)";
                if (jf.JamMarks.Count(m => m.State == CoverageJamMath.StateUnknown) != 1 || jf.JamMarks.Count(m => m.State == CoverageJamMath.StateKnown) != 1) return "mark states";
                if (!jf.AnyJamCut || !jf.AnyJamPartial || jf.JamRadars != 2) return "frame flags " + jf.AnyJamCut + "/" + jf.AnyJamPartial + "/" + jf.JamRadars;
                int tc = tiers.Count;
                for (int t = 0; t < tc; t++)
                {
                    if (!jf.Profiles[tc + t].Visible.SequenceEqual(plain.Profiles[tc + t].Visible)) return "unknown-jammed radar was cut (tier " + t + ")";
                    if (!jf.Profiles[2 * tc + t].Visible.SequenceEqual(plain.Profiles[2 * tc + t].Visible)) return "radar without Jam changed (tier " + t + ")";
                }
                var again = RunWorker(w, specsOf(true), tiers, on, out err);
                if (again == null) return "second run: " + err;
                if (again.Reused != again.Profiles.Count) return "profile cache not used with jamming (reused " + again.Reused + "/" + again.Profiles.Count + ")";
                // T-225: an ESM track (wide error ellipse, estimated range) cuts the zone too and the frame says "approx"
                var esm = specsOf(true);
                var es = esm[0].Jam.Known[0];
                es.Sensors = 0x200; es.SigmaM = 50000;
                if (CoverageJamMath.RangeClass(es) != OverviewMath.RangeApprox) return "esm track is not RangeApprox";
                es.RangeApprox = true;
                var ef = RunWorker(w, esm, tiers, on, out err);
                if (ef == null) return "esm track: " + err;
                if (!ef.AnyJamCut || !ef.AnyJamApprox) return "esm track did not cut / mark approx (" + ef.AnyJamCut + "/" + ef.AnyJamApprox + ")";
                if (!ef.JamMarks.Any(m => m.Approx && m.State == CoverageJamMath.StateKnown)) return "esm track: no approx mark";
                if (jf.AnyJamApprox) return "exact source flagged approx";
            }
            return null;
        }
    }
}
