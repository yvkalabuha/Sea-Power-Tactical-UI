using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-288 / ADR-0115: the likely sub launch area from the back bearing of a hostile torpedo track. Polls the torpedo tracks once a second (SubDatumGame),
    /// draws the sector under the game's interface (SubDatumOverlay), shows a card with "Search: &lt;group&gt;" (the usual asw_search_area on the core of the sector).
    /// Auto=idle sends a group without an order by itself. Never fires: the strike on a found contact stays with ADR-0074.</summary>
    internal sealed class SubDatumFeature : UiFeatureBase, IUiDiag, IUiSettingsSection
    {
        public override string Id { get { return "sub_datum"; } }
        public override string[] DependsOn { get { return new[] { "radar", "toast", "asw" }; } }
        public int SettingsOrder { get { return 41; } }
        public override void SelfCheck(ISelfCheck check) { check.Specs(SubDatumSpecs.Set); }

        private readonly GameApi _api;
        private readonly SubDatumParams _p = new SubDatumParams();
        private SubDatumBook _book;
        private SubDatumOverlay _ov;
        private IUiToasts _toasts;
        private IAswWindow _aswWin;
        private readonly UiUndoSlot _undo = new UiUndoSlot();
        private int _undoFor;                                         // the sector of the snapshot in the "datum" slot
        private double _nextPoll = double.NegativeInfinity;
        private int _lastRev = -1;

        private sealed class Snap { public int Merges; public double Sigma; public string Src, State, Group; }
        private sealed class GroupCache { public double At = double.NegativeInfinity; public List<SubDatumGroupIn> Groups; public int Pick = -1; }
        private readonly Dictionary<int, Snap> _seen = new Dictionary<int, Snap>();
        private readonly Dictionary<int, GroupCache> _groups = new Dictionary<int, GroupCache>();
        private readonly HashSet<int> _auto = new HashSet<int>(), _replaced = new HashSet<int>();

        public SubDatumFeature(UiContext ctx) : base(ctx) { _api = ctx.Api; }

        private sealed class HostService : GameApi.ISubDatumHost
        {
            private readonly SubDatumFeature _f;
            public HostService(SubDatumFeature f) { _f = f; }
            public List<SubDatumGroupIn> Groups(double lat, double lon) { return _f._api.Asw == null ? null : _f._api.Asw.DatumGroups(lat, lon); }
            public string Search(int id, string group) { return _f.Search(id, group, false); }
        }

        // ---- IUiDiag (ADR-0046)
        public string DiagSection { get { return "sub_datum"; } }

        public string DiagJson(int limit)
        {
            var b = _book;
            if (b == null) return "[]";
            double t = _api.CiMissionTimeS();
            return b.Json(limit, double.IsNaN(t) ? 0 : t);
        }

        public override void Bind(IUiConfig cfg)
        {
            const string S = "SubDatum";
            _p.Enabled = cfg.Bind(S, "Enabled", true, "Likely sub launch area from the back bearing of a hostile torpedo: a sector on the map and a card with a search order for an ASW group", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Restart }).Value;
            var map = cfg.Bind(S, "Map", true, "Draw the sector (core, far sector, buffer, axis) on the tactical map", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Check, Apply = UiApply.Live });
            var auto = cfg.Bind(S, "Auto", "off", "off | idle: off = only the card with a button, idle = an ASW group without an order starts the search at once", UiKeyClass.Basic, new UiSettingMeta { Control = UiControl.Choice, Apply = UiApply.Live, Options = new[] { "off", "idle" } });
            var trigger = cfg.Bind(S, "TriggerNm", 12.0, "A torpedo first seen farther than this (nm) from every own ship or submarine gives no sector", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var core = cfg.Bind(S, "CoreNm", 6.0, "Length of the core of the sector (nm from the detection point) that goes into the search order", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var maxLen = cfg.Bind(S, "MaxLenNm", 15.0, "The longest far sector (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var unknownRun = cfg.Bind(S, "UnknownRunNm", 12.0, "Run of a torpedo that is not identified (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var subKn = cfg.Bind(S, "SubKn", 5.0, "Speed of the submarine for the growth of the buffer (kn)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var ttl = cfg.Bind(S, "TtlS", 1800.0, "A sector expires after this many seconds of mission time", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var merge = cfg.Bind(S, "MergeS", 600.0, "A new torpedo within this time and an overlapping sector is merged into the old sector (seconds)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var areaMax = cfg.Bind(S, "AreaMaxNm", 10.0, "The longest side of the search area in the order (nm)", UiKeyClass.Advanced, new UiSettingMeta { Apply = UiApply.Live });
            var refine = cfg.Bind(S, "RefineS", 60.0, "The axis of a sector is refined by the track for this long (seconds)", UiKeyClass.Internal);
            var airNm = cfg.Bind(S, "AirSkipNm", 3.0, "A torpedo first seen this close (nm) to a hostile air track gives no sector (air-launched)", UiKeyClass.Internal);
            var airS = cfg.Bind(S, "AirSkipS", 60.0, "Memory of the air tracks for that rule (seconds)", UiKeyClass.Internal);
            var maxLive = cfg.Bind(S, "MaxLive", 3, "Live sectors at once (the oldest is taken off)", UiKeyClass.Internal);
            _p.Map = map.Value; _p.Auto = AutoOf(auto.Value);
            _p.TriggerNm = Math.Max(1, trigger.Value); _p.CoreNm = Math.Max(1, Math.Min(10, core.Value)); _p.MaxLenNm = Math.Max(2, maxLen.Value);
            _p.UnknownRunNm = Math.Max(2, unknownRun.Value); _p.SubKn = Math.Max(0, subKn.Value); _p.TtlS = Math.Max(60, ttl.Value);
            _p.MergeS = Math.Max(0, merge.Value); _p.AreaMaxNm = Math.Max(2, areaMax.Value);
            _p.RefineS = Math.Max(0, refine.Value); _p.AirSkipNm = Math.Max(0, airNm.Value); _p.AirSkipS = Math.Max(0, airS.Value); _p.MaxLive = Math.Max(1, Math.Min(10, maxLive.Value));
            OnChanged(map, v => { _p.Map = v; if (_ov != null) _ov.RequestDraw(); });
            OnChanged(auto, v => _p.Auto = AutoOf(v));
            OnChanged(trigger, v => _p.TriggerNm = Math.Max(1, v));
            OnChanged(core, v => _p.CoreNm = Math.Max(1, Math.Min(10, v)));
            OnChanged(maxLen, v => _p.MaxLenNm = Math.Max(2, v));
            OnChanged(unknownRun, v => _p.UnknownRunNm = Math.Max(2, v));
            OnChanged(subKn, v => _p.SubKn = Math.Max(0, v));
            OnChanged(ttl, v => _p.TtlS = Math.Max(60, v));
            OnChanged(merge, v => _p.MergeS = Math.Max(0, v));
            OnChanged(areaMax, v => _p.AreaMaxNm = Math.Max(2, v));
        }

        internal static string AutoOf(string v) { return (v ?? "off").Trim().ToLowerInvariant() == "idle" ? "idle" : "off"; }

        public override void Init(UiContext ctx)
        {
            try
            {
                if (!_p.Enabled) { ctx.Host.LogInfo("sub_datum off ([SubDatum] Enabled=false)"); return; }
                if (!_api.Capabilities.Contains("sub_datum")) { ctx.Host.LogWarning("sub_datum disabled: members of the game are missing"); return; }
                var host = ctx.Host;
                _book = new SubDatumBook(_p, m => host.LogInfo(m));   // the lines carry their own "[SubDatum] " prefix
                _api.SubDatum = _book;
                _api.SubDatumHost = new HostService(this);
                _undo.Log = m => host.LogInfo("[SubDatum] " + m);
                _toasts = ctx.Get<IUiToasts>();
                _aswWin = ctx.Get<IAswWindow>();
                var radar = ctx.Get<IRadarView>();
                _ov = new SubDatumOverlay(() => _book, () => { var t = _api.CiMissionTimeS(); return double.IsNaN(t) ? 0 : t; }, () => _p.Enabled && _p.Map, m => host.LogInfo(m));
                if (radar != null && radar.Layer != null) radar.Layer.Extra.Add(_ov);
                ctx.Mission.Subscribe(70, "sub_datum", null, why => LeaveMission());
            }
            catch (Exception e)
            {
                ctx.Host.LogError("sub_datum init crashed: " + e);
                _book = null; _api.SubDatum = null; _api.SubDatumHost = null; _ov = null;
            }
        }

        private void LeaveMission()
        {
            if (_book == null) return;
            foreach (int id in new List<int>(_seen.Keys)) CloseCard(id);
            _seen.Clear(); _groups.Clear(); _auto.Clear(); _replaced.Clear(); _undo.Reset(); _undoFor = 0;
            _api.SubDatumLeaveMission();   // also clears the book
            _lastRev = -1; _nextPoll = double.NegativeInfinity;
            if (_ov != null) _ov.RequestDraw();
        }

        private bool _worldSeen;   // T-356: AswMath.WorldGlobe as the live datums know it

        public override void Update(UiFrame frame)
        {
            var book = _book;
            if (book == null || !_api.Frame.InMission) return;
            if (_worldSeen != AswMath.WorldGlobe) { _worldSeen = AswMath.WorldGlobe; book.SetWorld(_worldSeen); }   // T-356
            if (frame.RealTime >= _nextPoll)
            {
                _nextPoll = frame.RealTime + 1.0;
                double t = _api.CiMissionTimeS();
                if (!double.IsNaN(t)) _api.SubDatumPoll(t);
            }
            _undo.Tick(frame.RealTime);
            if (book.Revision != _lastRev)
            {
                _lastRev = book.Revision;
                try { Sync(book, frame.RealTime); }
                catch (Exception e) { Ctx.Host.LogWarning("[SubDatum] cards failed: " + e.GetType().Name + ": " + e.Message); }
                if (_ov != null) _ov.RequestDraw();
            }
        }

        public override void Dispose()
        {
            try { if (_ov != null) _ov.Dispose(); } catch (Exception) { }
        }

        // ---- cards and events

        private void Sync(SubDatumBook book, double real)
        {
            var present = new HashSet<int>();
            foreach (SubDatum d in new List<SubDatum>(book.All))
            {
                present.Add(d.Id);
                Snap s;
                if (!_seen.TryGetValue(d.Id, out s))
                {
                    _seen[d.Id] = Snapshot(d);
                    ShowCard(d.Id, real);
                    Event("new", d);
                    if (_p.Auto == "idle" && d.State == "live") AutoSearch(d);
                    continue;
                }
                string kind = null;
                if (d.State != s.State && d.State != "live") kind = "closed";
                else if (d.Merges != s.Merges) kind = "merge";
                else if (d.SearchGroup != s.Group && d.SearchGroup != null) kind = "search";
                else if (d.Src != s.Src || Math.Abs(d.SigmaDeg - s.Sigma) > 1e-9) kind = "refine";
                _seen[d.Id] = Snapshot(d);
                if (kind == null) continue;
                if (kind == "merge" && d.State == "live") ShowCard(d.Id, real);
                else if (_toasts != null) _toasts.Update(Key(d.Id), BodyOf(d.Id));
                Event(kind, d);
            }
            foreach (int id in new List<int>(_seen.Keys))
                if (!present.Contains(id)) { CloseCard(id); _seen.Remove(id); _groups.Remove(id); _auto.Remove(id); _replaced.Remove(id); }
        }

        private static Snap Snapshot(SubDatum d) { return new Snap { Merges = d.Merges, Sigma = d.SigmaDeg, Src = d.Src, State = d.State, Group = d.SearchGroup }; }

        private static string Key(int id) { return "datum:" + id; }

        private void ShowCard(int id, double real)
        {
            if (_toasts != null) _toasts.Show(Key(id), "warn", BodyOf(id), real, 0, true);
        }

        private void CloseCard(int id)
        {
            if (_toasts != null) _toasts.Close(Key(id));
        }

        private Action<UiKit> BodyOf(int id)
        {
            return k =>
            {
                var d = _book == null ? null : _book.Find(id);
                if (d == null) return;
                SubDatumCard.Draw(k, ModelOf(d), ActionsOf(id));
            };
        }

        private void Event(string kind, SubDatum d)
        {
            _api.SubDatumEvent("{\"kind\":" + Json.Str(kind) + ",\"id\":" + d.Id + ",\"trk\":" + d.TrackId + ",\"state\":" + Json.Str(d.State) + ",\"brg\":" + Json.Num(Math.Round(d.ThetaDeg, 1))
                + ",\"sigma\":" + Json.Num(d.SigmaDeg) + ",\"src\":" + Json.Str(d.Src) + ",\"r\":" + Json.Num(Math.Round(d.RmaxNm, 2)) + ",\"group\":" + Json.Str(d.SearchGroup) + "}");
        }

        private double NowS() { double t = _api.CiMissionTimeS(); return double.IsNaN(t) ? 0 : t; }

        /// <summary>Centre of the core, geo.</summary>
        private static void CenterOf(SubDatum d, out double lat, out double lon)
        {
            SubDatumMath.ToGeo(d.Lat0, d.Lon0, SubDatumMath.Centroid(d.Core), d.Globe, out lat, out lon);
        }

        private GroupCache GroupsFor(SubDatum d, double real)
        {
            GroupCache c;
            if (!_groups.TryGetValue(d.Id, out c)) _groups[d.Id] = c = new GroupCache();
            if (real - c.At < 2.0) return c;
            c.At = real;
            double la, lo; CenterOf(d, out la, out lo);
            c.Groups = _api.Asw == null ? null : _api.Asw.DatumGroups(la, lo);
            c.Pick = c.Groups == null ? -1 : SubDatumMath.PickGroup(c.Groups);
            return c;
        }

        private SubDatumCardModel ModelOf(SubDatum d)
        {
            double now = NowS(), real = Ctx.Host.RealTime;
            var m = new SubDatumCardModel
            {
                Id = d.Id, BrgDeg = d.ThetaDeg, RNm = d.RmaxNm, CoreNm = d.CoreLenNm, SigmaDeg = d.SigmaDeg, Src = d.Src, Ammo = d.Ammo, State = d.State,
                AgeS = Math.Max(0, (d.State == "live" || d.ClosedAtS < 0 ? now : d.ClosedAtS) - d.T0S)
            };
            if (d.State != "live") return m;
            if (d.SearchGroup != null)
            {
                m.Searching = d.SearchGroup; m.AutoStarted = _auto.Contains(d.Id); m.ReplacesArea = _replaced.Contains(d.Id);
                m.UndoLeftS = _undoFor == d.Id ? _undo.Left("datum", real) : 0;
                return m;
            }
            var c = GroupsFor(d, real);
            if (c.Pick >= 0)
            {
                var g = c.Groups[c.Pick];
                m.Group = g.Name; m.ReplacesArea = g.Order == "area";
                m.GroupEtaS = double.IsInfinity(g.EtaS) ? -1 : g.EtaS;
            }
            return m;
        }

        private SubDatumCardActions ActionsOf(int id)
        {
            return new SubDatumCardActions
            {
                Search = () => { string e = Search(id, null, false); if (e != null) Ctx.Host.LogInfo("[SubDatum] search id=" + id + " failed: " + e); },
                OpenWindow = () => OpenWindow(id),
                Show = () => ShowOnMap(id),
                Hide = () => Hide(id),
                Undo = () => UndoSearch(id)
            };
        }

        // ---- orders

        private static List<object> Nums(double[] a)
        {
            var l = new List<object>(a.Length);
            foreach (double v in a) l.Add(v);
            return l;
        }

        /// <summary>(id, group?) -> null | error code. The order is the usual asw_search_area on the core of the sector. A group with an "in area" order is replaced (10 s of Undo).</summary>
        private string Search(int id, string group, bool auto)
        {
            var book = _book;
            var d = book == null ? null : book.Find(id);
            if (d == null) return "no_datum";
            if (d.State != "live") return "closed";
            double real = Ctx.Host.RealTime, now = NowS();
            double la, lo; CenterOf(d, out la, out lo);
            var groups = _api.Asw == null ? null : _api.Asw.DatumGroups(la, lo);
            if (groups == null) return "no_group";
            int idx = -1;
            if (group != null)
            {
                for (int i = 0; i < groups.Count; i++)
                    if (groups[i].Name == group && groups[i].Usable >= 1 && (groups[i].Order == "none" || groups[i].Order == "area")) { idx = i; break; }
            }
            else idx = SubDatumMath.PickGroup(groups);
            if (idx < 0) return "no_group";
            var g = groups[idx];
            double[] rect = book.OrderRect(d, now);
            var cmd = new Dictionary<string, object> { { "group", g.Name }, { "rect", Nums(rect) } };
            string err = Ack(_api.AswCmd("0", "asw_search_area", cmd));
            if (err != null) return err;
            bool replaced = g.Order == "area" && g.Rect != null && g.Rect.Length >= 8;
            _replaced.Remove(id);
            if (replaced)
            {
                _replaced.Add(id);
                var old = g;
                _undo.Take("datum", old.Name, 1, () => RestoreArea(old), real);
                _undoFor = id;
            }
            if (auto) _auto.Add(id); else _auto.Remove(id);
            book.NoteSearch(id, g.Name, rect);
            return null;
        }

        private UiUndoResult RestoreArea(SubDatumGroupIn g)
        {
            var cmd = new Dictionary<string, object> { { "group", g.Name }, { "rect", Nums(g.Rect) } };
            if (g.Tactics != null && g.Tactics.Count > 0) { var t = new List<object>(); foreach (string s in g.Tactics) t.Add(s); cmd["tactics"] = t; }
            if (!string.IsNullOrEmpty(g.Depth)) cmd["depth"] = g.Depth;
            var r = new UiUndoResult { Groups = 1 };
            string err = Ack(_api.AswCmd("0", "asw_search_area", cmd));
            if (err == null) r.Restored = 1; else r.Skipped.Add(g.Name + ": " + err);
            return r;
        }

        /// <summary>null = the ack is ok; else the error code.</summary>
        private static string Ack(string line)
        {
            try
            {
                var a = Json.Parse(line) as Dictionary<string, object>;
                if (a == null) return "bad_ack";
                object e;
                if (a.TryGetValue("error", out e) && e is string) return (string)e;
                object ok;
                return a.TryGetValue("ok", out ok) && ok is bool && !(bool)ok ? "failed" : null;
            }
            catch (Exception) { return "bad_ack"; }
        }

        private void AutoSearch(SubDatum d)
        {
            double la, lo; CenterOf(d, out la, out lo);
            var groups = _api.Asw == null ? null : _api.Asw.DatumGroups(la, lo);
            if (groups == null) return;
            int idx = SubDatumMath.PickGroup(groups);
            if (idx < 0 || groups[idx].Order != "none") return;   // Auto=idle takes only a group without an order
            string err = Search(d.Id, groups[idx].Name, true);
            if (err != null) Ctx.Host.LogInfo("[SubDatum] auto id=" + d.Id + " failed: " + err);
        }

        private void UndoSearch(int id)
        {
            var d = _book == null ? null : _book.Find(id);
            if (d == null || d.SearchGroup == null) return;
            if (_auto.Contains(id))
            {
                _api.AswCmd("0", "asw_stop", new Dictionary<string, object> { { "group", d.SearchGroup } });
                _auto.Remove(id);
            }
            else if (_undoFor == id) _undo.Restore("datum", Ctx.Host.RealTime);
            d.SearchGroup = null; d.SearchRect = null; _replaced.Remove(id);
            _lastRev = -1;   // redraw the card and the layer
        }

        private void OpenWindow(int id)
        {
            var d = _book == null ? null : _book.Find(id);
            if (d == null || _aswWin == null) return;
            _aswWin.OpenAreaDraft(_book.OrderRect(d, NowS()));
        }

        private void ShowOnMap(int id)
        {
            var d = _book == null ? null : _book.Find(id);
            if (d == null) return;
            string err;
            _api.CenterMap(null, null, d.Lat0, d.Lon0, "sub_datum", out err);
            if (err != null) Ctx.Host.LogInfo("[SubDatum] show id=" + id + " failed: " + err);
        }

        private void Hide(int id)
        {
            if (_book == null) return;
            _book.Dismiss(id, NowS());
            CloseCard(id);
        }
    }
}
