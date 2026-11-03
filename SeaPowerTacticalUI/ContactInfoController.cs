using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-136 / ADR-0045: orchestrator of the contact_info module and the IContactInfo service. No Unity, no game types:
    /// everything comes through IContactSource / IContactLabels / IModProbe, so the stand runs it on a fake game.
    /// A snapshot is built round-robin over the frames (PerFrame tracks per Tick) and published as a whole.
    /// Foreign units are described by their track only; noise exists for the player's own units only.
    /// </summary>
    internal sealed class ContactInfoController : IContactInfo, IMapExtraLabels, IContactMissionHooks
    {
        private const double MaxS = 1e9;
        private readonly ContactInfoSettings _s;
        private readonly IContactSource _game;
        private readonly IContactLabels _labels;
        private readonly IModProbe _probe;
        private readonly Action<string> _logInfo, _logWarn;
        private readonly HotkeyBinding _masterKey;
        private readonly HotkeyBinding[] _layerKeys = new HotkeyBinding[7];
        private CiUnits _units;
        private readonly int[] _layerNumbers;
        private bool[] _dupOff = new bool[7];
        private bool _master = false;   // T-337 (ADR-0131 note): contact labels are off until the player switches them on (hotkey / map layer)

        // published snapshot
        private Dictionary<int, ContactInfoRow> _tracks = new Dictionary<int, ContactInfoRow>();
        private Dictionary<int, ContactInfoRow> _own = new Dictionary<int, ContactInfoRow>();
        private List<int> _ownKeys = new List<int>();
        private CiWaypoint _waypoint;
        private int _version;

        // pass in progress
        private bool _passOn;
        private IList<int> _ids;
        private int _cursor;
        private IList<CiOwn> _ownList;
        private Dictionary<int, CiOwnWeapon> _ownWeapons;
        private Dictionary<int, ContactInfoRow> _building;
        private int _selUid;
        private double _missionT, _passStart = double.NaN;
        private bool _globe;   // T-352: the world of the game (false = flat, stable), read once per pass
        private int _lastReads;
        private int _labelsVersion = -1;
        private bool _hidden;
        private readonly Stopwatch _sw = new Stopwatch();   // reused: no per-frame allocation
        private int _labelsMask = -1;
        private int _pubRev;
        private const int MaxExtraPerOwner = 24;
        private readonly SortedDictionary<string, List<LabelItem>> _extra = new SortedDictionary<string, List<LabelItem>>(StringComparer.Ordinal);

        public ContactInfoController(ContactInfoSettings s, IContactSource game, IContactLabels labels, IModProbe probe,
                                     HotkeyRegistry hotkeys, Action<string> logInfo, Action<string> logWarn)
        {
            _s = s; _game = game; _labels = labels; _probe = probe; _logInfo = logInfo; _logWarn = logWarn;
            Budget = new CiBudget(0.4, 10.0, 4.0);
            string err;
            _units = ContactInfoMath.ParseUnits(s.Units, out err);
            if (err != null && _logWarn != null) _logWarn("contact_info: " + err);
            _layerNumbers = ContactInfoMath.PickLayerNumbers(ContactInfoMath.LayerPreferred, SafeTaken(), ContactInfoMath.LayerFirst, ContactInfoMath.LayerLast);
            _masterKey = new HotkeyBinding("contact_info", "ContactInfo.Hotkey", s.Hotkey, hotkeys, logInfo, logWarn);
            for (int i = 0; i < 7; i++)
            {
                string spec = s.LayerHotkeys != null && i < s.LayerHotkeys.Length ? s.LayerHotkeys[i] : "";
                _layerKeys[i] = new HotkeyBinding("contact_info", "ContactInfo.Hotkey." + (CiLayer)i, spec, hotkeys, logInfo, logWarn);
            }
        }

        private ICollection<int> SafeTaken()
        {
            try { return _game.TakenLayerNumbers(); } catch (Exception) { return new List<int>(); }
        }

        public CiBudget Budget { get; private set; }
        /// <summary>Layer numbers chosen for the 7 map layers (index = CiLayer); -1 = none free.</summary>
        public int[] LayerNumbers { get { return (int[])_layerNumbers.Clone(); } }
        /// <summary>The module feature writes the Show.&lt;Layer&gt; setting here.</summary>
        public Action<CiLayer, bool> PersistShow;

        public bool Master
        {
            get { return _master; }
            set { if (_master != value) { _master = value; _labelsVersion = -1; } }
        }

        /// <summary>Effective: Show[i] and not switched off as a duplicate of another mod, and the master switch is on.</summary>
        public bool[] LayerOn
        {
            get
            {
                var r = new bool[7];
                for (int i = 0; i < 7; i++) r[i] = _master && _s.Show[i] && !_dupOff[i];
                return r;
            }
        }

        /// <summary>T-144: Show.&lt;Layer&gt; changed in the settings window or the file; no write back (the setting already has the value).</summary>
        public void ApplyShow(CiLayer l, bool on)
        {
            int i = (int)l;
            if (_s.Show[i] != on) { _s.Show[i] = on; _labelsVersion = -1; }
        }

        /// <summary>ADR-0045 Supplement 1: ContactInfo.OwnSpeedNoise changed (settings window / file).</summary>
        public void SetOwnSpeedNoise(bool on)
        {
            if (_s.OwnSpeedNoise != on) { _s.OwnSpeedNoise = on; _labelsVersion = -1; }
        }

        /// <summary>T-144: Units changed at run time; a bad string gives the defaults and a warning, like at start.</summary>
        public void SetUnits(string spec)
        {
            string err;
            _s.Units = spec;
            _units = ContactInfoMath.ParseUnits(spec, out err);
            if (err != null && _logWarn != null) _logWarn("contact_info: " + err);
            _labelsVersion = -1;
        }

        /// <summary>T-144: a setting read at every use (Waypoint, AgeShowS, MaxLabels, thresholds) changed: lay the labels out again.</summary>
        public void Refresh() { _labelsVersion = -1; }

        public void RespecMaster(string spec) { _s.Hotkey = spec; _masterKey.Respec(spec); }

        public void RespecLayer(CiLayer l, string spec)
        {
            int i = (int)l;
            if (_s.LayerHotkeys != null && i < _s.LayerHotkeys.Length) _s.LayerHotkeys[i] = spec;
            _layerKeys[i].Respec(spec);
        }

        public bool SetLayer(CiLayer l, bool on)
        {
            int i = (int)l;
            if (_s.Show[i] != on)
            {
                _s.Show[i] = on; _labelsVersion = -1;
                if (PersistShow != null) PersistShow(l, on);
            }
            return _s.Show[i];
        }

        // ---- IMapExtraLabels (T-137 D) ----
        public void SetExtraLabels(string owner, IList<LabelItem> items)
        {
            if (string.IsNullOrEmpty(owner)) return;
            if (items == null || items.Count == 0)
            {
                if (_extra.Remove(owner)) _labelsVersion = -1;
                return;
            }
            var l = new List<LabelItem>(Math.Min(items.Count, MaxExtraPerOwner));
            for (int i = 0; i < items.Count && l.Count < MaxExtraPerOwner; i++) if (items[i] != null) l.Add(items[i]);
            if (l.Count == 0) { if (_extra.Remove(owner)) _labelsVersion = -1; return; }
            _extra[owner] = l;
            _labelsVersion = -1;
        }

        public string FmtDistance(double nm) { return ContactInfoMath.FmtDist(nm, _units); }

        // ---- IContactInfo ----
        public ContactInfoRow Get(int trackId) { ContactInfoRow r; return _tracks.TryGetValue(trackId, out r) ? r : null; }
        public ContactInfoRow GetOwn(int uniqueId) { ContactInfoRow r; return _own.TryGetValue(uniqueId, out r) ? r : null; }
        public IList<int> OwnKeys { get { return _ownKeys; } }
        /// <summary>Track ids of the last published snapshot (diag section).</summary>
        public IList<int> TrackKeys { get { return new List<int>(_tracks.Keys); } }
        public int Version { get { return _version; } }

        /// <summary>Reads of tracks done by the last Tick (stand check of the frame budget).</summary>
        public int LastReads { get { return _lastReads; } }

        public string StatsLine()
        {
            return "contact_info: n=" + _tracks.Count + " period=" + (Budget.Period > 0 ? Budget.Period : _s.PeriodS).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                   + " doublings=" + Budget.Doublings + " labels=" + _labels.Mode;
        }

        // ---------------------------------------------------------------- tick

        public void Tick(double realTimeS, double dtS, IKeyInput keys, IHotkeyGame keyGame)
        {
            _lastReads = 0;
            bool inMission = _game.InMission;
            if (keys != null && keyGame != null) PumpHotkeys(realTimeS, inMission, keys, keyGame);
            if (!_s.Enabled || !inMission)
            {
                if (!_hidden) { _labels.Hide(); _hidden = true; _labelsVersion = -1; }
                if (!inMission) _passOn = false;
                return;
            }
            _sw.Restart();
            Step(realTimeS, dtS);   // the service (Overview columns, ASW rows) keeps running while only the labels are switched off
            if (_master || _extra.Count > 0) PublishLabels();
            else if (!_hidden) { _labels.Hide(); _hidden = true; _labelsVersion = -1; }
            if (Budget.Feed(realTimeS, _sw.Elapsed.TotalMilliseconds, _s.PeriodS) && _logInfo != null)
                _logInfo("contact_info: over budget, period x2 = " + Budget.Period.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s");
        }

        private void PumpHotkeys(double realTimeS, bool inMission, IKeyInput keys, IHotkeyGame keyGame)
        {
            _masterKey.Resolve(realTimeS, inMission, keyGame);
            if (_masterKey.Pressed(keys)) Master = !Master;
            for (int i = 0; i < 7; i++)
            {
                _layerKeys[i].Resolve(realTimeS, inMission, keyGame);
                if (_layerKeys[i].Pressed(keys)) SetLayer((CiLayer)i, !_s.Show[i]);
            }
        }

        /// <summary>T-130 / T-157: the mission was left (MissionLifecycle, order 65): drop everything that belongs to it, extra labels included.</summary>
        public void OnMissionLeft()
        {
            _passOn = false; _passStart = double.NaN; _labelsVersion = -1;
            _tracks = new Dictionary<int, ContactInfoRow>(); _own = new Dictionary<int, ContactInfoRow>(); _ownKeys = new List<int>();
            _extra.Clear();
        }

        /// <summary>Mission entry / save load (MissionLifecycle, order 65): look for Better TacMap and Vessel Noise Display (ADR-0045 6, 6a). No later probing.</summary>
        public void OnMissionEntered()
        {
            bool btm = false, nd = false, ndAsm = false;
            try { btm = _probe.HarmonyHas(ContactInfoMath.BtmHarmonyId); } catch (Exception) { }
            try { nd = _probe.HarmonyHas(ContactInfoMath.NdHarmonyId); } catch (Exception) { }
            try { ndAsm = _probe.AssemblyLoaded(ContactInfoMath.NdAssembly); } catch (Exception) { }
            _dupOff = ContactInfoMath.DuplicatesOff(_s.BetterTacMap, btm, _s.NoiseDisplay, nd);
            if (_logInfo != null)
            {
                if (btm) _logInfo(_dupOff[(int)CiLayer.Depth] ? "Better TacMap detected — duplicate map labels off (ContactInfo.BetterTacMap=on to force)"
                                                              : "Better TacMap detected — ContactInfo.BetterTacMap=on, our labels stay on");
                if (nd) _logInfo(_dupOff[(int)CiLayer.Noise] ? "Vessel Noise Display detected — duplicate dB label off (ContactInfo.NoiseDisplay=on to force)"
                                                             : "Vessel Noise Display detected — ContactInfo.NoiseDisplay=on, our dB label stays on");
                else if (ndAsm) _logInfo("Vessel Noise Display assembly found, no patches yet — our dB label stays on");
            }
            OnMissionLeft();   // a fresh start for the new mission
        }

        private double Period { get { return Budget.Period > 0 ? Budget.Period : _s.PeriodS; } }

        private void Step(double realTimeS, double dtS)
        {
            if (!_passOn)
            {
                if (!double.IsNaN(_passStart) && realTimeS < _passStart + Period) return;
                BeginPass(realTimeS);
            }
            int n = _ids.Count;
            int k = n == 0 ? 0 : ContactInfoMath.PerFrame(n, dtS, Period);
            double reach = _s.CpaMaxMin * 60.0;
            for (int j = 0; j < k && _cursor < n; j++, _cursor++)
            {
                _lastReads++;
                CiTrack t = null;
                try { t = _game.ReadTrack(_ids[_cursor]); } catch (Exception) { }
                if (t != null) _building[t.Id] = BuildTrackRow(t, reach);
            }
            if (_cursor >= n) Publish();
        }

        private void BeginPass(double realTimeS)
        {
            _passOn = true; _passStart = realTimeS; _cursor = 0;
            _ids = SafeIds();
            _ownList = SafeOwn();
            _ownWeapons = new Dictionary<int, CiOwnWeapon>();
            try { foreach (var w in _game.ReadOwnWeapons()) if (w != null && w.TrackId >= 0) _ownWeapons[w.TrackId] = w; } catch (Exception) { }
            _selUid = 0;
            try { _selUid = _game.SelectedOwnUid; } catch (Exception) { }
            _missionT = double.NaN;
            try { _missionT = _game.MissionTimeS; } catch (Exception) { }
            try { _globe = _game.WorldGlobe; } catch (Exception) { }
            _building = new Dictionary<int, ContactInfoRow>();
        }

        private IList<int> SafeIds() { try { return _game.TrackIds() ?? new List<int>(); } catch (Exception) { return new List<int>(); } }
        private IList<CiOwn> SafeOwn() { try { return _game.ReadOwn() ?? new List<CiOwn>(); } catch (Exception) { return new List<CiOwn>(); } }

        private ContactInfoRow BuildTrackRow(CiTrack t, double reachS)
        {
            var r = new ContactInfoRow { Key = t.Id, Own = false, Lat = t.Lat, Lon = t.Lon };
            if (t.HasVel) { r.SpeedKn = Math.Sqrt(t.VxKn * t.VxKn + t.VzKn * t.VzKn); r.SpeedEstimated = true; }
            r.AgeS = ContactInfoMath.Age(_missionT, t.EpochS);
            if (double.IsNaN(r.AgeS) && t.Live) r.AgeS = 0;
            r.Stale = ContactInfoMath.IsStale(r.AgeS, _s.StaleS) || (!t.Live && double.IsNaN(r.AgeS));
            if (t.Subsurface)
            {
                double off;
                r.LayerSide = ContactInfoMath.LayerSide(t.HasDepthSolution, t.DepthFt, t.LayerDepthFt, out off);
                r.LayerOffsetFt = off;
                if (t.HasDepthSolution) { r.DepthFt = t.DepthFt; r.DepthErrFt = t.DepthErrFt; }
            }
            CiOwnWeapon ow;
            if (_ownWeapons.TryGetValue(t.Id, out ow))
            {
                r.TtgS = ContactInfoMath.TtgOwn(ow.RangeNm, ow.SpeedKn);
                r.TtgExact = true; r.TtgTargetKey = ow.TargetKey; r.TtgTargetName = ow.TargetName;
            }
            else if (t.IsWeapon && t.HasVel)
            {
                double ttg;
                int idx = ContactInfoMath.TtgForeign(t, _ownList, _s.TtgHitNm, reachS, _globe, out ttg);
                if (idx >= 0) { r.TtgS = ttg; r.TtgTargetKey = _ownList[idx].Uid; r.TtgTargetName = _ownList[idx].Name; }
            }
            if (t.HasVel && t.Live) FillCpa(r, t, reachS);
            return r;
        }

        private void FillCpa(ContactInfoRow r, CiTrack t, double reachS)
        {
            double cpa, tcpa; int refIdx;
            if (_s.CpaTarget == "group")
            {
                if (ContactInfoMath.CpaGroup(_ownList, t, reachS, _globe, out cpa, out tcpa, out refIdx))
                { r.CpaNm = cpa; r.TcpaS = tcpa; r.CpaRefName = _ownList[refIdx].Name; }
                return;
            }
            CiOwn sel = null;
            foreach (var o in _ownList) if (o.Uid == _selUid && _selUid != 0) { sel = o; break; }
            if (sel == null) return;
            var one = new List<CiOwn> { sel };
            if (ContactInfoMath.CpaGroup(one, t, reachS, _globe, out cpa, out tcpa, out refIdx)) { r.CpaNm = cpa; r.TcpaS = tcpa; }
        }

        private void Publish()
        {
            var own = new Dictionary<int, ContactInfoRow>();
            var keys = new List<int>();
            foreach (var o in _ownList)
            {
                var r = new ContactInfoRow { Key = o.Uid, Own = true, Air = o.IsAir, Name = o.Name, Lat = o.Lat, Lon = o.Lon, SpeedKn = o.SpeedKn, AgeS = 0 };
                if (o.IsSub && !double.IsNaN(o.DepthFt)) r.DepthFt = o.DepthFt;
                r.NoiseDb = o.NoiseDb; r.CavOnsetKn = o.CavOnsetKn; r.Cavitating = o.Cavitating;
                r.CavState = ContactInfoMath.CavState(o.SpeedKn, o.CavOnsetKn, o.Cavitating, _s.CavMarginKn);
                own[o.Uid] = r; keys.Add(o.Uid);
            }
            _tracks = _building; _own = own; _ownKeys = keys;
            _waypoint = null;
            if (_selUid != 0) { try { _waypoint = _game.ReadWaypoint(_selUid); } catch (Exception) { } }
            _version++;
            _passOn = false;
        }

        // ---------------------------------------------------------------- labels

        private void PublishLabels()
        {
            int mask = 0;   // allocation-free change detection (runs every frame)
            for (int i = 0; i < 7; i++) if (_master && _s.Show[i] && !_dupOff[i]) mask |= 1 << i;
            if (_s.OwnSpeedNoise) mask |= 1 << 7;
            if (_version == _labelsVersion && mask == _labelsMask && !_hidden) return;
            if (_version == 0 && _extra.Count == 0) return;
            var on = LayerOn;
            _labelsVersion = _version; _labelsMask = mask; _hidden = false;
            bool cavOnly = _dupOff[(int)CiLayer.Noise] && _s.Show[(int)CiLayer.Noise];
            double rLat = double.NaN, rLon = double.NaN;
            ContactInfoRow selRow = null;
            if (_selUid != 0 && _own.TryGetValue(_selUid, out selRow)) { rLat = selRow.Lat; rLon = selRow.Lon; }
            else if (_ownKeys.Count > 0) { var f = _own[_ownKeys[0]]; rLat = f.Lat; rLon = f.Lon; }
            var cand = new List<KeyValuePair<double, LabelItem>>();
            Action<ContactInfoRow> add = r =>
            {
                bool ownLine = r.Own && !r.Air && _s.OwnSpeedNoise;   // ADR-0045 Supplement 1: one line, left of the icon, coloured by the cavitation margin
                string text = ownLine ? ContactInfoMath.OwnLine(r, on[(int)CiLayer.Depth], !_dupOff[(int)CiLayer.Noise], _units)
                                      : ContactInfoMath.LabelText(r, on, _s.AgeShowS, _units, cavOnly);
                if (string.IsNullOrEmpty(text)) return;
                double x = 0, y = 0;
                if (!double.IsNaN(rLat)) ContactInfoMath.ToLocalNm(rLat, rLon, r.Lat, r.Lon, _globe, out x, out y);
                var item = new LabelItem { Key = r.Key, Lat = r.Lat, Lon = r.Lon, Text = text, Dim = r.Stale, Argb = ownLine ? ContactInfoMath.OwnArgb(r.CavState) : r.Own ? ContactInfoMath.OwnArgbOk : (r.Stale ? 0x99C0C0C0u : 0xFFFFFFFFu), LeftOfIcon = ownLine };
                cand.Add(new KeyValuePair<double, LabelItem>(x * x + y * y, item));
            };
            if (_master)
            {
                foreach (var r in _tracks.Values) add(r);
                foreach (var r in _own.Values) add(r);
            }
            cand.Sort((a, b) => a.Key.CompareTo(b.Key));
            var items = new List<LabelItem>();
            for (int i = 0; i < cand.Count && items.Count < _s.MaxLabels; i++) items.Add(cand[i].Value);
            if (_master && on[(int)CiLayer.Waypoint] && _waypoint != null)
            {
                string wt = ContactInfoMath.FmtWaypoint(_waypoint, _s.Waypoint, _units);
                if (wt.Length > 0) items.Add(new LabelItem { Key = -1000000 - _waypoint.OwnUid, Lat = _waypoint.LastLat, Lon = _waypoint.LastLon, Text = wt, Argb = 0xFFB0F0B0u });
            }
            foreach (var kv in _extra) items.AddRange(kv.Value);
            _labels.Show(items, ++_pubRev);   // every publish is a new layout (extra labels change without a snapshot)
        }
    }
}
