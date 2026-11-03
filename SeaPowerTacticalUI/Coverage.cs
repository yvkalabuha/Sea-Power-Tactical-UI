using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        public CoverageConfig CovCfg = new CoverageConfig();
        public Func<bool> MapFound = () => false;
        private List<CoverageTier> _covTiers;
        private CoverageWorker _worker;
        private TerrainBroker _broker;   // T-225: the worker's only way to the game's terrain, served in PumpCoverage on the main thread
        private int _mainTid;
        private CoverageFrame _frame;
        private bool _overlayOn;
        private bool _covForce = true;
        private List<RadarSpec> _lastSpecs;
        private List<RadarSnap> _lastSnaps;
        private string _covWarn;
        private CoverageSchedule _sched;
        private string _stateKey, _legendKey;
        private int _covDirty;

        internal CoverageStats CovStats { get; } = new CoverageStats { GcCount = () => GC.CollectionCount(0) };   // T-232: the GC base is there from the first Due()
        /// <summary>Set by UiPlugin after the layer init: "noesis" | "imgui".</summary>
        internal string CoverageRenderMode = "imgui";
        internal Func<bool> NoesisAttached = () => false;
        /// <summary>Bumps on every new frame and when the legend content changes (clutter flag, language).</summary>
        internal int CoverageDirtyVersion { get { return _covDirty; } }

        internal bool OverlayOn { get { return _overlayOn; } }
        internal CoverageFrame LatestFrame { get { return _frame; } }

        // ---------- self-check (members: RadarSpecs, registry) ----------

        public int CoverageMembers;

        /// <summary>
        /// T-090: offline (selfcheck) the Noesis assembly is not loaded yet; the registry would not find the Noesis types. Loads it from the game's Managed folder
        /// before the registry runs (in the game it is loaded already and nothing happens).
        /// </summary>
        internal void LoadNoesisOffline()
        {
            try
            {
                var image = RadarSpecs.ImageType;
                if (FindType(image.Type) != null) return;
                var anchor = TypeOf(CoreSpecs.ObjectBaseType);
                string dir = anchor == null ? null : System.IO.Path.GetDirectoryName(anchor.Assembly.Location);
                string dll = dir == null ? null : System.IO.Path.Combine(dir, RadarSpecs.NoesisAssembly);
                if (dll != null && System.IO.File.Exists(dll)) { Assembly.LoadFrom(dll); _types.Remove(image.Type); }
            }
            catch { }
        }

        /// <summary>T-144 L3: a RadarCoverage setting changed at run time. recompute = the zone itself changes (tiers, radar set, schedule): drop the parsed tiers and the schedule, force a build;
        /// otherwise only the drawing changes (fog, flip, segment cap, small-window rule): one redraw.</summary>
        internal void CoverageConfigChanged(bool recompute)
        {
            if (recompute) { _covTiers = null; _sched = null; _covForce = true; }
            _covDirty++;
        }

        internal List<CoverageTier> CoverageTiers()
        {
            if (_covTiers != null) return _covTiers;
            string err;
            _covTiers = CoverageMath.ParseTiers(CovCfg.Tiers, out err);
            if (_covTiers == null)
            {
                Log("RadarCoverage.Tiers invalid (" + err + "), using defaults");
                _covTiers = CoverageMath.ParseTiers(CoverageMath.DefaultTiers, out err);
            }
            return _covTiers;
        }

        // ---------- reading the game ----------

        private sealed class RadarSnap { public RadarSpec Spec; public object Radar, Owner; }

        private double MinPowerKw()
        {
            var rv = GetStatic(RadarSpecs.RcsValues);
            return D(Get(rv, RadarSpecs.RcsMinPower)) ?? 2e-14;
        }

        private int SeaStateNow()
        {
            var env = SingletonInstance(RadarSpecs.EnvType);
            var s = D(Get(env, RadarSpecs.EnvSeaState));
            return s.HasValue ? (int)s.Value : 0;
        }

        private double WaveHeightM(int seaState)
        {
            var arr = GetStatic(RadarSpecs.WaveHeights) as float[];
            if (arr == null || arr.Length == 0) return 0;
            int i = Math.Max(0, Math.Min(arr.Length - 1, seaState));
            return arr[i] * CoverageMath.MPerUnity;
        }

        private static UnityEngine.Transform TransformOf(object o)
        {
            var go = o as UnityEngine.GameObject;
            if (go != null) return go.transform;
            var c = o as UnityEngine.Component;
            return c == null ? null : c.transform;
        }

        private static bool Vec2(object o, out double x, out double y)
        {
            x = y = 0;
            if (!(o is UnityEngine.Vector2)) return false;
            var v = (UnityEngine.Vector2)o; x = v.x; y = v.y;
            return true;
        }

        private List<RadarSnap> SnapshotRaw(int? uid)
        {
            var res = new List<RadarSnap>();
            if (!Frame.InMission) return res;
            int ss = SeaStateNow();
            double wave = WaveHeightM(ss);
            var sampler = TerrainSampler(false);
            bool jam = JamReady();
            double jamMs = 0; int jamRadars = 0;
            double jamNow = jam ? JamNow() : 0, j2s = jam ? JamJ2S() : 0;
            var aliveOwners = jam ? new HashSet<int>() : null;
            foreach (var u in OwnUnits())
            {
                if (!IsAlive(u) || BoolOf(Get(u, RadarSpecs.ObjIsDestroyed))) continue;
                string kind = UnitKindOfObj(u);
                if (kind == null) continue;
                int ouid = UidOf(u);
                if (uid.HasValue && ouid != uid.Value) continue;
                bool air = IsAirUnit(u);
                if (air && !InFlight(u)) continue;
                if (!air && BoolOf(RxGet(u, RadarSpecs.IsSubmerged))) continue;
                double lat, lon, hh;
                if (!OwnPos(u, out lat, out lon, out hh)) continue;
                var obp = Get(u, RadarSpecs.ObjObp);
                double visibleU = D(Get(obp, RadarSpecs.ParamsVisibleHeight)) ?? 0.046;
                bool emcon = BoolOf(RxGet(u, RadarSpecs.ObjEmcon));
                double terr = sampler == null ? double.NaN : sampler(lat, lon);
                string uname = NameOf(u);
                int firstSnap = res.Count;
                var lists = new List<IEnumerable>();
                lists.Add(Get(obp, RadarSpecs.ParamsSearch) as IEnumerable);
                if (CovCfg.IncludeTargeting) lists.Add(Get(obp, RadarSpecs.ParamsTargeting) as IEnumerable);
                foreach (var list in lists)
                {
                    if (list == null) continue;
                    foreach (var rs in list)
                    {
                        var snap = SnapOne(rs, u, uname, ouid, lat, lon, air, emcon, visibleU, ss, wave, double.IsNaN(terr) ? -1 : terr);
                        if (snap != null) res.Add(snap);
                    }
                }
                if (jam && res.Count > firstSnap)
                {
                    // T-182 (ADR-0081): the jamming input of this unit's radars, on the main thread, before the worker sees the specs
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    aliveOwners.Add(ouid);
                    var ownerSnaps = res.GetRange(firstSnap, res.Count - firstSnap);
                    FillJam(u, ownerSnaps, j2s, jamNow);
                    foreach (var os in ownerSnaps) if (os.Spec.Jam != null && os.Spec.Jam.State != CoverageJamMath.StateNone) jamRadars++;
                    jamMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                }
            }
            if (jam && !uid.HasValue) { _jamLatch.Forget(aliveOwners); WatchForget(aliveOwners); }
            _lastJamMs = jamMs; _lastJamRadars = jamRadars;
            return res;
        }

        private RadarSnap SnapOne(object rs, object owner, string ownerName, int ownerUid, double lat, double lon, bool airborne, bool emcon,
                                  double visibleU, int seaState, double waveM, double terrainM)
        {
            if (rs == null || !IsAlive(rs)) return null;
            var radar = Get(rs, RadarSpecs.SysRadar);
            var rp = Get(radar, RadarSpecs.RadarRp);
            if (rp == null) return null;
            string type = (Get(rs, RadarSpecs.SysType) ?? "").ToString();
            if (type != "Search" && type != "DirectedSearch" && !(CovCfg.IncludeTargeting && type == "Targeting")) return null;
            var pos = Get(rs, RadarSpecs.SysPosition);
            double antennaU = pos is UnityEngine.Vector3 ? ((UnityEngine.Vector3)pos).y : 0;
            double fx = 0, fz = 1;
            var tr = TransformOf(Get(rs, RadarSpecs.SysMount));
            if (tr != null) { var f = tr.forward; fx = f.x; fz = f.z; }
            double hMin = -181, hMax = 181, vMin = -91, vMax = 91;
            Vec2(Get(rs, RadarSpecs.SysHArc), out hMin, out hMax);
            Vec2(Get(rs, RadarSpecs.SysVArc), out vMin, out vMax);
            var roleObj = Get(rp, RadarSpecs.RpRole);
            int role = roleObj == null ? 0 : Convert.ToInt32(roleObj);
            var spec = CoverageMath.SpecFromRaw(
                (Get(rp, RadarSpecs.RpSystemName) as string) ?? "radar", type, role, BoolOf(Get(rp, RadarSpecs.RpRotatable)),
                D(Get(rp, RadarSpecs.RpMinRange)) ?? 0, D(Get(rp, RadarSpecs.RpMaxRange)) ?? 0, D(Get(rp, RadarSpecs.RpMinAltitude)) ?? -1, D(Get(rp, RadarSpecs.RpMaxAltitude)) ?? -1,
                D(Get(rp, RadarSpecs.RpPeakPower)) ?? 0, D(Get(rp, RadarSpecs.RpGainFactor)) ?? 1, D(Get(rp, RadarSpecs.RpWaveLength)) ?? 0,
                D(Get(rs, RadarSpecs.SysLookDownMul)) ?? 1, D(Get(rs, RadarSpecs.SysLookDownRange)) ?? 0, hMin, hMax, vMin, vMax, fx, fz, antennaU, visibleU);
            spec.OwnerUid = ownerUid; spec.OwnerName = ownerName; spec.Lat = lat; spec.Lon = lon; spec.Globe = AswMath.WorldGlobe;   // T-352: main thread
            spec.Airborne = airborne; spec.DataLink = BoolOf(Get(rp, RadarSpecs.RpHasDataLink));
            spec.On = BoolOf(RxGet(rs, RadarSpecs.SysIsOn)); spec.Inoperable = BoolOf(RxGet(rs, RadarSpecs.SysInoperable)); spec.Emcon = emcon;
            spec.SeaState = seaState; spec.SeaWaveM = waveM; spec.TerrainUnderAntennaM = terrainM;
            return new RadarSnap { Spec = spec, Radar = rs, Owner = owner };
        }

        /// <summary>Own search radars as SI snapshots (main thread; own task force only, ADR-0024).</summary>
        internal List<RadarSpec> SnapshotRadars(int? uid)
        {
            var res = new List<RadarSpec>();
            foreach (var s in SnapshotRaw(uid)) res.Add(s.Spec);
            return res;
        }

        /// <summary>Terrain height in metres ASL (NaN = unknown). All reflection handles are resolved here, on the main thread.
        /// T-128 (checked against the game's code): the game's isCalledFromJob flag of GetRealHeightInMetersAt is NOT a thread-safety guarantee.
        /// It only forbids synchronous DEM loads; the call still reads TerrainManager.TerrainChunkDictionary (a plain Dictionary the main thread
        /// changes without the game's _lockGetHeight), so a read from another thread can race an Add/Remove and spin inside the lock the game's main thread also takes.
        /// T-225: BOTH modes are main-thread only. The coverage worker no longer calls the game at all: it sends its cells to the TerrainBroker and ServiceTerrain answers them here.
        /// job=true (the broker's answers): only the DEM cache path, GetDEMHeightAt (TerrainLoader._lock, no loading) + Utils.unityHeightToMeter; NaN where the chunk is not in the cache.
        /// A call from any other thread is refused (NaN, TerrainOffMain++). job=false (snapshots): GetRealHeightInMetersAt(gp, false). Both go through TerrainCall (compiled, no per-call allocations).
        /// One delegate per mode is cached.</summary>
        internal Func<double, double, double> TerrainSampler(bool job)
        {
            var cached = job ? _terrainJob : _terrainMain;
            if (cached != null) return cached;
            var gt = TypeOf(CoreSpecs.GeoType);
            var real = Member(RadarSpecs.TerrainReal) as MethodInfo;
            var dem = Member(RadarSpecs.TerrainDem) as MethodInfo;
            var toM = Member(RadarSpecs.UnityHeightToMeter) as MethodInfo;
            if (gt == null || (real == null && dem == null)) return null;
            var fla = Member(CoreSpecs.GeoLat) as FieldInfo; var flo = Member(CoreSpecs.GeoLon) as FieldInfo; var fh = Member(CoreSpecs.GeoHeight) as FieldInfo;
            if (fla == null || flo == null) return null;
            // T-202: the "terrain on" flag is NOT read per point (reflection + boxing, 400 million calls per session); the main thread reads it once per job (TerrainEnabledNow)
            Func<double, double, double> inner;
            string mode, path;
            if (job && dem != null && toM != null)
            {
                inner = TerrainCall.Build(dem, gt, fla, flo, fh, toM, null, null, out mode);
                path = "dem";
            }
            else
            {
                if (real == null) return null;
                if (job) Log("coverage terrain: worker uses GetRealHeightInMetersAt (DEM path missing)");
                inner = TerrainCall.Build(real, gt, fla, flo, fh, null, job, null, out mode);
                path = "real";
            }
            if (inner == null) return null;
            if (mode != "compiled") { path = "reflect"; Log("coverage terrain: reflection fallback (" + (job ? "worker" : "main") + ")"); }
            TerrainPath = path;
            Func<double, double, double> f = (lat, lon) =>
            {
                if (job && (_mainTid == 0 || System.Threading.Thread.CurrentThread.ManagedThreadId != _mainTid))   // T-225 guard: never reach the game from another thread
                {
                    System.Threading.Interlocked.Increment(ref TerrainOffMain);
                    return double.NaN;
                }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                System.Threading.Interlocked.Increment(ref TerrainCalls);
                try { return inner(lat, lon); }
                finally { System.Threading.Interlocked.Add(ref TerrainTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
            };
            if (job) _terrainJob = f; else _terrainMain = f;
            return f;
        }

        private Func<double, double, double> _terrainJob, _terrainMain;
        private Func<bool> _terrainFlag;
        private bool _terrainFlagInit;

        /// <summary>T-202: the game's "terrain loaded / enabled" flag (compiled static field read, no boxing); true when the flag is unknown. Main thread.</summary>
        internal bool TerrainEnabledNow()
        {
            if (!_terrainFlagInit)
            {
                _terrainFlagInit = true;
                _terrainFlag = TerrainCall.StaticBool(Member(RadarSpecs.TerrainEnabled) as FieldInfo);
            }
            if (_terrainFlag == null) return true;
            try { return _terrainFlag(); } catch { return true; }
        }

        /// <summary>T-202: worker breadcrumb sink (set by HeartbeatFeature; null = off).</summary>
        internal ICoverageProbe CoverageProbe;
        /// <summary>T-128 diagnostics: "dem" | "real" | "reflect" (the last built sampler).</summary>
        internal static string TerrainPath = "";

        /// <summary>T-151 memory counters of the terrain sampler (Interlocked): game calls, their Stopwatch ticks, memo size and clears.</summary>
        internal static long TerrainCalls, TerrainTicks, MemoEntries, MemoClears;
        /// <summary>T-225: terrain calls refused because they came from a thread other than the main one (must stay 0; stat terrain_offmain).</summary>
        internal static long TerrainOffMain;

        // ---------- helpers for CoverageOverlay (reflection access that stays inside GameApi) ----------

        internal double D2(object o) { return D(o) ?? 0.0; }

        internal static bool BoolOf2(object o) { return o is bool && (bool)o; }

        internal object GeoObj(double lat, double lon) { return MakeGeo(lat, lon, 0.0); }

        internal bool TypingActive()
        {
            try { return BoolOf(Get(SingletonInstance(CoreSpecs.InputType), RadarSpecs.TypingActive)); } catch { return false; }
        }

        /// <summary>T-125: the game's UI scale of this frame (read once per frame).</summary>
        internal double UiScale() { return Frame.UiScale; }

        private double ReadUiScale()
        {
            try { return D(Get(SingletonInstance(RadarSpecs.OptionsType), RadarSpecs.UiScaleProp)) ?? 1.0; } catch { return 1.0; }
        }

        // ---------- overlay state, background recalculation ----------

        internal void SetOverlay(bool on, string source)
        {
            if (_overlayOn == on) return;
            _overlayOn = on;
            if (!on && _broker != null) _broker.Cancel();   // T-225: a build waiting for terrain is dropped
            _covForce = true;
            _covDirty++;
            _stateKey = null;
            if (_sched != null) _sched.Reset();
            if (EventsAvailable) EvLine("overlay_changed", "{\"name\":\"radar_coverage\",\"on\":" + Json.Bool(on) + ",\"source\":" + Json.Str(source) + "}");
            Log("radar coverage overlay " + (on ? "on" : "off") + " (" + source + ")");
            SyncCoverageLayer();
        }

        /// <summary>T-190: set by RadarFeature, the state of the Noesis layer for ui_state radar.layer.</summary>
        internal Ui.ICoverageLayerDiag CoverageLayerDiag;

        internal string OverlayJson()
        {
            var f = _frame;
            return "{\"radar_coverage\":" + Json.Bool(_overlayOn) + ",\"radars\":" + (f == null ? 0 : f.Radars) + ",\"frame_version\":" + (f == null ? 0 : f.Version) +
                   ",\"build_ms\":" + Json.Num(f == null ? 0 : Math.Round(f.BuildMs, 1)) + ",\"map_found\":" + Json.Bool(MapFound()) +
                   ",\"render\":" + Json.Str(CoverageRenderMode) + ",\"noesis_attached\":" + Json.Bool(NoesisAttached()) + ",\"jam_radars\":" + (f == null ? 0 : f.JamRadars) +
                   ",\"layer\":" + SafeJson(() => { var d = CoverageLayerDiag; return d != null ? d.DiagJson() : "null"; }) + ",\"jam\":" + SafeJson(JamRadarsJson) + ",\"jam_latch\":" + SafeJson(() => JamDiagJson(JamNow())) +
                   ",\"units\":" + SafeJson(() => CoverageDiag.UnitsJson(_lastSpecs, f, 64)) + ",\"rebuild\":" + SafeJson(() => CoverageDiag.RebuildJson(f)) + "}";
        }

        private static string SafeJson(Func<string> f)
        {
            try { var t = f == null ? null : f(); return string.IsNullOrEmpty(t) ? "null" : t; } catch { return "null"; }
        }

        public void DisposeCoverage()
        {
            if (_worker != null) { _worker.Dispose(); _worker = null; }
            if (_broker != null) _broker.Cancel();
        }

        /// <summary>Cheap poll: own units (uid, alive, in flight, submerged, EMCON), their radars (IsOn, Inoperable), sea state, clutter option.</summary>
        private string RadarStateKey()
        {
            var sb = new StringBuilder(256);
            bool jam = JamReady();
            double jamNow = jam ? JamNow() : 0;
            sb.Append(SeaStateNow()).Append('|').Append(BoolOf(GetStatic(RadarSpecs.LowAltClutter)) ? '1' : '0');
            foreach (var u in OwnUnits())
            {
                bool alive = IsAlive(u) && !BoolOf(Get(u, RadarSpecs.ObjIsDestroyed));
                sb.Append(';').Append(UidOf(u)).Append(alive ? '1' : '0');
                if (!alive) continue;
                sb.Append(InFlight(u) ? '1' : '0').Append(BoolOf(RxGet(u, RadarSpecs.IsSubmerged)) ? '1' : '0').Append(BoolOf(RxGet(u, RadarSpecs.ObjEmcon)) ? '1' : '0');
                var obp = Get(u, RadarSpecs.ObjObp);
                var lists = new List<IEnumerable> { Get(obp, RadarSpecs.ParamsSearch) as IEnumerable };
                if (CovCfg.IncludeTargeting) lists.Add(Get(obp, RadarSpecs.ParamsTargeting) as IEnumerable);
                bool anyOn = false;
                foreach (var list in lists)
                {
                    if (list == null) continue;
                    foreach (var rs in list)
                    {
                        bool on = rs != null && IsAlive(rs) && BoolOf(RxGet(rs, RadarSpecs.SysIsOn));
                        if (on) anyOn = true;
                        sb.Append(rs != null && IsAlive(rs) ? (on ? 'o' : 'f') : 'x').Append(rs != null && IsAlive(rs) && BoolOf(RxGet(rs, RadarSpecs.SysInoperable)) ? 'i' : '.');
                    }
                }
                if (anyOn && jam) sb.Append(JamStateSig(u, jamNow));   // T-182: the state of the jamming of the owner (signal, known tracks, bearings)
            }
            return sb.ToString();
        }

        /// <summary>Cheap: current position of each radar's owner (and the mount forward of sector radars) against the last snapshot, with the CoverageMath.Moved thresholds.</summary>
        private bool AnyUnitMoved(List<RadarSpec> last)
        {
            var snaps = _lastSnaps;
            if (last == null || snaps == null || snaps.Count != last.Count) return false;
            var pos = new Dictionary<int, double[]>();
            for (int i = 0; i < snaps.Count; i++)
            {
                var snap = snaps[i];
                var old = last[i];
                if (!IsAlive(snap.Owner) || !IsAlive(snap.Radar)) continue;
                double[] p;
                if (!pos.TryGetValue(old.OwnerUid, out p))
                {
                    double la, lo, hh;
                    p = OwnPos(snap.Owner, out la, out lo, out hh) ? new[] { la, lo } : null;
                    pos[old.OwnerUid] = p;
                }
                if (p == null) continue;
                var cur = new RadarSpec { Globe = AswMath.WorldGlobe, Lat = p[0], Lon = p[1], AntennaM = old.AntennaM, AxisDeg = old.AxisDeg, FullCircle = old.FullCircle };
                var sp = Get(snap.Radar, RadarSpecs.SysPosition);
                if (sp is UnityEngine.Vector3) cur.AntennaM = ((UnityEngine.Vector3)sp).y * CoverageMath.MPerUnity;
                if (!old.FullCircle)
                {
                    var tr = TransformOf(Get(snap.Radar, RadarSpecs.SysMount));
                    if (tr != null) { var f = tr.forward; cur.AxisDeg = (Math.Atan2(f.x, f.z) * 180.0 / Math.PI + 360.0) % 360.0; }
                }
                if (CoverageMath.Moved(old, cur)) return true;
            }
            return false;
        }

        /// <summary>Plugin.Update (T-090): the scheduler decides, the worker computes. Poll radar state every StateCheckSeconds, check for motion every RecomputeSeconds,
        /// never blocks (the heavy work is on the worker thread). Nothing runs while the overlay is off or the fleet stands still with unchanged radars.</summary>
        public void PumpCoverage(double realNow)
        {
            if (_worker != null)
            {
                var done = _worker.TakeResult();
                if (done != null)
                {
                    _frame = done;
                    _covDirty++;
                    if (done.Error != null && done.Error != _covWarn) { _covWarn = done.Error; Log("coverage worker: " + done.Error); }
                }
            }
            ServiceTerrain();
            if (!_overlayOn) return;
            if (GameMenuHidden) return;   // a game menu (pause / After Action Report) covers the map: no new coverage work until it closes
            if (CovCfg.Debug && CovStats.Due(realNow))
            {
                if (_broker != null)
                {
                    long bp; int bf; double bs, bm, bw;
                    _broker.Drain(out bp, out bf, out bs, out bm, out bw);
                    CovStats.AddBroker(bp, bf, bs, bm, bw, _worker == null ? 0 : _worker.TerrainLate, System.Threading.Interlocked.Exchange(ref TerrainOffMain, 0));
                }
                Log(CovStats.TakeLine(realNow, CoverageRenderMode, _frame == null ? 0 : _frame.Radars) + " jam_toggles=" + (_jamLatch.Toggles - _jamTogglesLogged)); _jamTogglesLogged = _jamLatch.Toggles;
            }
            if (!Capabilities.Contains("radar_coverage")) return;
            if (_sched == null) _sched = new CoverageSchedule(CovCfg.RecomputeSeconds, CovCfg.StateCheckSeconds);
            if (!Frame.InMission)
            {
                if (_broker != null) _broker.Cancel();   // T-225: left the mission with a build waiting for terrain
                if (_frame != null) { _frame = null; _covDirty++; }
                _lastSpecs = null; _lastSnaps = null; _stateKey = null; _covForce = true;
                _jamLatch.Reset(); _jamTracks.Clear(); _jamWatch.Clear();
                _sched.Reset();
                return;
            }
            if (JamReady()) JamSample(JamNow());   // T-190: 4 Hz bearing sample of the jammed owners feeds the adaptive latch
            bool stateChanged = false, moved = false;
            if (_sched.PollDue(realNow))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int gcPoll = GC.CollectionCount(0);
                string key = RadarStateKey();
                bool jamMoved = JamReady() && JamDistanceMoved(_lastSpecs);   // T-182: a known jammer moved by > 10 % of its distance
                CovStats.AddPoll(sw.Elapsed.TotalMilliseconds, GC.CollectionCount(0) != gcPoll);
                if (_stateKey != null && (key != _stateKey || jamMoved)) stateChanged = true;
                _stateKey = key;
                string lk = UiText.Lang + "#" + UiText.Revision + (BoolOf(GetStatic(RadarSpecs.LowAltClutter)) ? "+clutter" : "");
                if (lk != _legendKey) { if (_legendKey != null) _covDirty++; _legendKey = lk; }
            }
            if (_sched.MotionDue(realNow)) moved = AnyUnitMoved(_lastSpecs);
            var trig = _sched.Decide(realNow, _covForce, stateChanged, moved);
            if (trig == CoverageTrigger.None) return;
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            int gcSnap = GC.CollectionCount(0);
            var snaps = SnapshotRaw(null);
            var specs = new List<RadarSpec>(snaps.Count);
            foreach (var sn in snaps) specs.Add(sn.Spec);
            bool gcDuring = GC.CollectionCount(0) != gcSnap;   // T-202: a GC pause inside the measured span
            CovStats.AddSnapshot(sw2.Elapsed.TotalMilliseconds, gcDuring);
            if (JamReady()) CovStats.AddJam(_lastJamMs, _lastJamRadars, gcDuring);
            _covForce = false;
            _lastSpecs = specs; _lastSnaps = snaps;
            if (_worker == null)
            {
                _broker = new TerrainBroker();
                _worker = new CoverageWorker(CovStats, CoverageProbe, () => GC.CollectionCount(0), null,
                                             n => System.Threading.Interlocked.Exchange(ref MemoEntries, n), true, _broker);
            }
            bool terrainOn = CovCfg.TerrainShadows && TerrainEnabledNow();   // read here, on the main thread, once per job
            _broker.Arm();
            _worker.Submit(specs, CoverageTiers(), MinPowerKw(), CovCfg, terrainOn);
            _sched.Submitted(realNow, trig);
        }

        /// <summary>
        /// T-225 (ADR-0092): answers the terrain cells the coverage worker asked for, on the main thread, within TerrainBroker.BudgetMs per frame (BurstBudgetMs in the first build after the
        /// layer was switched on). Nothing happens while no request waits, the overlay is off, the player is not in a mission or a game menu covers the map (the worker waits).
        /// </summary>
        private void ServiceTerrain()
        {
            if (_broker == null || !_broker.Pending) return;
            if (!_overlayOn || !Frame.InMission || GameMenuHidden) return;
            _mainTid = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var game = TerrainSampler(true);
            if (game == null) { _broker.Cancel(); return; }   // no terrain API: release the worker (its build is dropped)
            double spent;
            _broker.Service(game, out spent);
        }

        /// <summary>selfcheck: 8 synthetic radars + synthetic terrain through the real worker. Returns null or an error text.</summary>
        internal static string CoverageSmoke(out double ms)
        {
            ms = 0;
            var cfg = new CoverageConfig { Rays = 180, Bins = 64 };
            string err;
            var tiers = CoverageMath.ParseTiers(CoverageMath.DefaultTiers, out err);
            var specs = new List<RadarSpec>();
            for (int i = 0; i < 8; i++)
            {
                var r = new RadarSpec
                {
                    OwnerUid = i, OwnerName = "U" + i, Name = "SPS-49", Type = "Search", Air = true, Surface = i % 2 == 0, On = i != 5, Emcon = i == 6, Airborne = i == 7,
                    Globe = AswMath.WorldGlobe, Lat = 15 + i * 0.03, Lon = 42 + i * 0.03, AntennaM = i == 7 ? 7600 : 30, MinRangeM = 600, MaxRangeM = 463000, MinAltM = 3, MaxAltM = 45000,
                    PeakPowerKw = 280, GainFactor = Math.Pow(10, 3.9), WaveLengthM = 0.46121916, FullCircle = true, VisibleHeightM = 3, TerrainUnderAntennaM = -50
                };
                specs.Add(r);
            }
            Func<double, double, double> terrain = (la, lo) => (la > 15.4 && la < 15.5 && lo > 42.0 && lo < 42.2) ? 250.0 : -50.0;
            var broker = new TerrainBroker();
            using (var w = new CoverageWorker(null, null, null, null, null, true, broker))
            {
                double spent;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                w.Submit(specs, tiers, 2e-14, cfg, true);
                CoverageFrame f = null;
                while (sw.ElapsedMilliseconds < 5000 && (f = w.TakeResult()) == null) { if (broker.Service(terrain, out spent) == 0) Thread.Sleep(5); }   // this thread plays the main thread
                ms = sw.Elapsed.TotalMilliseconds;
                if (f == null) return "worker timed out";
                if (f.Error != null) return f.Error;
                if (f.Radars != 8 || f.Profiles.Count != 8 * tiers.Count || f.Segs.Count == 0 || f.Raster.Length != f.W * f.H) return "unexpected frame";
                // same specs again: the worker must take the profiles from its cache
                w.Submit(specs, tiers, 2e-14, cfg, true);
                CoverageFrame f2 = null;
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                while (sw2.ElapsedMilliseconds < 5000 && (f2 = w.TakeResult()) == null) { if (broker.Service(terrain, out spent) == 0) Thread.Sleep(5); }
                if (f2 == null) return "worker timed out (second job)";
                if (f2.Error != null) return f2.Error;
                if (f2.Reused <= 0 || f2.Reused < f2.Profiles.Count) return "worker cache not used (reused " + f2.Reused + ")";
            }
            return null;
        }

        // ---------- commands ----------

        private string RadarOverlayCmd(string id, Dictionary<string, object> cmd)
        {
            bool on = !_overlayOn;
            if (cmd.ContainsKey("on") && cmd["on"] != null)
            {
                if (!(cmd["on"] is bool)) return Err(id, "bad_args", "on (true/false) optional");
                on = (bool)cmd["on"];
            }
            Need("radar_overlay");
            bool previous = _overlayOn;
            SetOverlay(on, "bot");
            return Ok(id, "\"on\":" + Json.Bool(_overlayOn) + ",\"previous\":" + Json.Bool(previous));
        }

        private static string Nm(double m) { return Json.Num(Math.Round(m / CoverageMath.MPerNm, 2)); }

        private string RadarCoverageCmd(string id, Dictionary<string, object> cmd)
        {
            List<CoverageTier> tiers = CoverageTiers();
            if (cmd.ContainsKey("tiers") && cmd["tiers"] != null)
            {
                string spec = cmd["tiers"] as string, terr;
                tiers = spec == null ? null : CoverageMath.ParseTiers(spec, out terr);
                if (tiers == null) { string e; CoverageMath.ParseTiers(spec ?? "", out e); return Err(id, "bad_args", e ?? "tiers must be a string"); }
            }
            int? uid = null;
            if (cmd.ContainsKey("uid") && cmd["uid"] != null)
            {
                var d = D(cmd["uid"]);
                if (!d.HasValue) return Err(id, "bad_args", "uid must be a number");
                uid = (int)d.Value;
            }
            int? checkUid = null;
            if (cmd.ContainsKey("check_uid") && cmd["check_uid"] != null)
            {
                var d = D(cmd["check_uid"]);
                if (!d.HasValue || cmd["check_uid"] is bool || cmd["check_uid"] is string) return Err(id, "bad_args", "check_uid must be a number");
                checkUid = (int)d.Value;
            }
            bool terrain = true;
            if (cmd.ContainsKey("terrain") && cmd["terrain"] != null)
            {
                if (!(cmd["terrain"] is bool)) return Err(id, "bad_args", "terrain (true/false) optional");
                terrain = (bool)cmd["terrain"];
            }
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            Need("radar_coverage");
            object target = null;
            if (checkUid.HasValue)
            {
                target = FindOwnUnit(checkUid.Value);
                if (target == null) return Err(id, "bad_args", "check_uid must be one of your own units (own sensors only)");
            }
            if (uid.HasValue && FindOwnUnit(uid.Value) == null) return Err(id, "unit_not_found", null);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double minPower = MinPowerKw();
            var snaps = SnapshotRaw(uid);
            var sampler = terrain ? TerrainSampler(false) : null;
            var cached = sampler == null ? null : CoverageMath.CachedSampler(sampler, 0.0005);
            var sb = new StringBuilder();
            sb.Append("\"tiers\":[");
            for (int i = 0; i < tiers.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var kinds = new List<string>();
                if (tiers[i].Air) kinds.Add("air");
                if (tiers[i].Surface) kinds.Add("surface");
                sb.Append("{\"height_m\":").Append(Json.Num(tiers[i].HeightM)).Append(",\"rcs_m2\":").Append(Json.Num(tiers[i].RcsM2)).Append(",\"kinds\":[");
                for (int k = 0; k < kinds.Count; k++) { if (k > 0) sb.Append(','); sb.Append(Json.Str(kinds[k])); }
                sb.Append("]}");
            }
            sb.Append("],\"sea_state\":").Append(SeaStateNow());
            sb.Append(",\"clutter_option\":").Append(Json.Bool(BoolOf(GetStatic(RadarSpecs.LowAltClutter))));
            sb.Append(",\"terrain\":").Append(Json.Bool(cached != null));
            sb.Append(",\"units\":[");
            int lastUid = int.MinValue; bool firstUnit = true, firstRadar = true;
            foreach (var s in snaps)
            {
                var r = s.Spec;
                if (r.OwnerUid != lastUid)
                {
                    if (!firstUnit) sb.Append("]}");
                    if (!firstUnit) sb.Append(',');
                    firstUnit = false; firstRadar = true; lastUid = r.OwnerUid;
                    sb.Append("{\"uid\":").Append(r.OwnerUid).Append(",\"name\":").Append(Json.Str(r.OwnerName)).Append(",\"kind\":").Append(Json.Str(UnitKindOfObj(s.Owner)));
                    sb.Append(",\"lat\":").Append(Json.Num(Math.Round(r.Lat, 5))).Append(",\"lon\":").Append(Json.Num(Math.Round(r.Lon, 5))).Append(",\"radars\":[");
                }
                if (!firstRadar) sb.Append(',');
                firstRadar = false;
                sb.Append("{\"name\":").Append(Json.Str(r.Name)).Append(",\"type\":").Append(Json.Str(r.Type));
                sb.Append(",\"role\":").Append(Json.Str(r.Air && r.Surface ? "air+surface" : r.Air ? "air" : r.Surface ? "surface" : "undefined"));
                sb.Append(",\"on\":").Append(Json.Bool(r.On)).Append(",\"inoperable\":").Append(Json.Bool(r.Inoperable)).Append(",\"emcon\":").Append(Json.Bool(r.Emcon));
                sb.Append(",\"airborne\":").Append(Json.Bool(r.Airborne)).Append(",\"datalink\":").Append(Json.Bool(r.DataLink));
                sb.Append(",\"antenna_m\":").Append(Json.Num(Math.Round(r.AntennaM, 1))).Append(",\"min_range_nm\":").Append(Nm(r.MinRangeM)).Append(",\"max_range_nm\":").Append(Nm(r.MaxRangeM));
                sb.Append(",\"full_circle\":").Append(Json.Bool(r.FullCircle));
                if (r.FullCircle) sb.Append(",\"arc\":null");
                else sb.Append(",\"arc\":{\"h\":[").Append(Json.Num(r.HArcMin)).Append(',').Append(Json.Num(r.HArcMax)).Append("],\"v\":[").Append(Json.Num(r.VArcMin)).Append(',').Append(Json.Num(r.VArcMax))
                       .Append("],\"axis_deg\":").Append(Json.Num(Math.Round(r.AxisDeg, 1))).Append('}');
                sb.Append(",\"tiers\":[");
                for (int i = 0; i < tiers.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var lim = CoverageMath.RangeLimit(r, tiers[i], minPower);
                    double shadow = 0;
                    if (cached != null && lim.RangeM > 0)
                        shadow = CoverageMath.BuildProfile(r, tiers[i], i, minPower, cached, 72, 64).ShadowPct;
                    sb.Append("{\"range_nm\":").Append(Nm(lim.RangeM)).Append(",\"limit\":").Append(Json.Str(lim.Limit)).Append(",\"shadow_pct\":").Append(Json.Num(Math.Round(shadow, 1))).Append('}');
                }
                sb.Append("],\"jam\":").Append(JamJson(r, tiers, minPower)).Append('}');
            }
            if (!firstUnit) sb.Append("]}");
            sb.Append(']');
            if (target != null) sb.Append(",\"check\":").Append(CheckJson(target, snaps, minPower));
            sb.Append(",\"build_ms\":").Append(Json.Num(Math.Round(sw.Elapsed.TotalMilliseconds, 1)));
            return Ok(id, sb.ToString());
        }

        /// <summary>Own target vs the game's own SensorSystemRadar.GetRange for every own radar (honest: only own objects).</summary>
        private string CheckJson(object target, List<RadarSnap> snaps, double minPower)
        {
            var tr = TransformOf(target);
            double altM = tr == null ? 0 : tr.position.y * CoverageMath.MPerUnity;
            bool air = IsAirUnit(target);
            var sd = Get(target, RadarSpecs.ObjSensorData);
            double rcs = D(Get(sd, RadarSpecs.DataMaxRcs)) ?? 1.0;
            double visM = (D(Get(Get(target, RadarSpecs.ObjObp), RadarSpecs.ParamsVisibleHeight)) ?? 0.046) * CoverageMath.MPerUnity;
            var tier = new CoverageTier { HeightM = altM, RcsM2 = rcs, Air = air, Surface = !air };
            var sb = new StringBuilder("{\"target_uid\":" + UidOf(target) + ",\"target_alt_m\":" + Json.Num(Math.Round(altM, 1)) + ",\"target_rcs_m2\":" + Json.Num(rcs) + ",\"rows\":[");
            bool first = true;
            foreach (var s in snaps)
            {
                if (ReferenceEquals(s.Owner, target)) continue;
                var gr = Member(RadarSpecs.SysGetRange) as MethodInfo;
                if (gr == null) continue;
                object res;
                try { res = gr.Invoke(s.Radar, new[] { target }); } catch { continue; }
                double gameU = D(Get(res, RadarSpecs.DistDistance)) ?? 0;
                string issue = (Get(res, RadarSpecs.DistIssue) ?? "").ToString();
                var spec = s.Spec;
                spec.VisibleHeightM = visM;
                double ours = CoverageMath.RangeLimit(spec, tier, minPower).RangeM;
                double gameM = gameU * CoverageMath.MPerUnity;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"uid\":").Append(spec.OwnerUid).Append(",\"radar\":").Append(Json.Str(spec.Name)).Append(",\"ours_nm\":").Append(Nm(ours)).Append(",\"game_nm\":").Append(Nm(gameM));
                sb.Append(",\"diff_pct\":").Append(gameM > 1 ? Json.Num(Math.Round((ours - gameM) / gameM * 100.0, 1)) : "null").Append(",\"game_issue\":").Append(Json.Str(issue)).Append('}');
            }
            return sb.Append("]}").ToString();
        }
    }
}
