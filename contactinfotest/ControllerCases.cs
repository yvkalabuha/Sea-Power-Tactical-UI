using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-136 stream C: ContactInfoController on a fake game (plan C1 cases a-k).</summary>
    internal static class ContactInfoControllerCases
    {
        private sealed class FakeSource : IContactSource
        {
            public bool InMissionNow = true;
            public bool WorldGlobe { get; set; }
            public double TimeS = 100;
            public int SelUid;
            public readonly Dictionary<int, CiTrack> Tracks = new Dictionary<int, CiTrack>();
            public readonly List<CiOwn> OwnList = new List<CiOwn>();
            public readonly List<CiOwnWeapon> Weapons = new List<CiOwnWeapon>();
            public CiWaypoint Wp;
            public int TrackReads;
            public bool InMission { get { return InMissionNow; } }
            public double MissionTimeS { get { return TimeS; } }
            public int SelectedOwnUid { get { return SelUid; } }
            public IList<int> TrackIds() { return Tracks.Keys.ToList(); }
            public CiTrack ReadTrack(int id) { TrackReads++; CiTrack t; return Tracks.TryGetValue(id, out t) ? t : null; }
            public IList<CiOwn> ReadOwn() { return OwnList; }
            public IList<CiOwnWeapon> ReadOwnWeapons() { return Weapons; }
            public CiWaypoint ReadWaypoint(int ownUid) { return Wp; }
            public IList<int> TakenLayerNumbers() { return new List<int> { 212 }; }
        }

        private sealed class FakeLabels : IContactLabels
        {
            public List<LabelItem> Last = new List<LabelItem>();
            public int Shows, Hides, LastVersion;
            public bool Ready { get { return true; } }
            public string Mode { get { return "fake"; } }
            public void Show(IList<LabelItem> items, int version) { Shows++; LastVersion = version; Last = new List<LabelItem>(items); }
            public void Hide() { Hides++; Last = new List<LabelItem>(); }
            public LabelItem Find(int key) { return Last.FirstOrDefault(i => i.Key == key); }
        }

        private sealed class FakeProbe : IModProbe
        {
            public readonly HashSet<string> Patches = new HashSet<string>(), Assemblies = new HashSet<string>();
            public bool HarmonyHas(string id) { return Patches.Contains(id); }
            public bool AssemblyLoaded(string name) { return Assemblies.Contains(name); }
        }

        private sealed class FakeKeys : IKeyInput
        {
            public readonly HashSet<string> HeldKeys = new HashSet<string>(), DownKeys = new HashSet<string>();
            public bool Held(string k) { return HeldKeys.Contains(k); }
            public bool Down(string k) { return DownKeys.Contains(k); }
            public bool Typing { get { return false; } }
        }

        private sealed class FreeKeyGame : IHotkeyGame
        {
            public string Check(string[] keys, out string conflict) { conflict = null; return "free"; }
        }

        private sealed class Rig
        {
            public readonly FakeSource Game = new FakeSource();
            public readonly FakeLabels Labels = new FakeLabels();
            public readonly FakeProbe Probe = new FakeProbe();
            public readonly FakeKeys Keys = new FakeKeys();
            public readonly List<string> Info = new List<string>(), Warn = new List<string>();
            public readonly ContactInfoSettings S = new ContactInfoSettings();
            public ContactInfoController Ctl;
            public double T;
            public int MaxReads;
            public Rig Make() { Ctl = new ContactInfoController(S, Game, Labels, Probe, new HotkeyRegistry(), Info.Add, Warn.Add); Ctl.Master = true; Ctl.OnMissionEntered(); return this; }
            public void Run(int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    T += 1.0 / 60;
                    int before = Game.TrackReads;
                    Ctl.Tick(T, 1.0 / 60, Keys, new FreeKeyGame());
                    MaxReads = Math.Max(MaxReads, Game.TrackReads - before);
                }
            }
        }

        private static CiTrack Trk(int id, double lat, double lon, double vx, double vz, string kind = "Surface")
        {
            return new CiTrack { Id = id, Kind = kind, Live = true, HasVel = true, Lat = lat, Lon = lon, VxKn = vx, VzKn = vz,
                                 IsWeapon = kind == "Torpedo" || kind == "Missile", Subsurface = kind == "Submarine" || kind == "Torpedo" };
        }

        private static CiOwn Own(int uid, string name, double lat, double lon)
        {
            return new CiOwn { Uid = uid, Name = name, Lat = lat, Lon = lon, IsShip = true, SpeedKn = 10 };
        }

        public static string SelfTest()
        {
            try
            {
                using (UiText.Push("en"))
                {
                    string e = null;
                    e = e ?? CaseNoSolution();
                    e = e ?? CaseForeignTorpedo();
                    e = e ?? CaseSchedule();
                    e = e ?? CaseBetterTacMap();
                    e = e ?? CaseNoiseDisplay();
                    e = e ?? CaseDefaults();
                    e = e ?? CaseHotkeys();
                    e = e ?? CaseCpa();
                    e = e ?? CaseCpaWorld();
                    e = e ?? CaseStale();
                    e = e ?? CaseOwnNoise();
                    e = e ?? CaseOwnLine();
                    e = e ?? CaseMaxLabels();
                    e = e ?? CaseWaypoint();
                    e = e ?? CaseSingleLayer();
                    e = e ?? CaseLiveSettings();
                    e = e ?? CaseExtraLabels();
                    e = e ?? CaseMasterDefaultOff();
                    return e;
                }
            }
            catch (Exception ex) { return "controller: " + ex.GetType().Name + ": " + ex.Message; }
        }

        // (a) the enemy is really at 400 ft, the track has no depth solution
        private static string CaseNoSolution()
        {
            var r = new Rig().Make();
            r.Game.Tracks[7] = Trk(7, 0.1, 0.1, 5, 0, "Submarine");
            r.Run(130);
            var row = r.Ctl.Get(7);
            if (row == null || row.LayerSide != "nosol") return "controller a: LayerSide " + (row == null ? "no row" : row.LayerSide);
            if (!double.IsNaN(row.DepthFt)) return "controller a: depth leaked without a solution";
            var item = r.Labels.Find(7);
            if (item == null || !item.Text.Contains(UiText.Get("ci.depth.nosol"))) return "controller a: label text '" + (item == null ? "none" : item.Text) + "'";
            return null;
        }

        // (b) a foreign torpedo runs at own B, own A is aside
        private static string CaseForeignTorpedo()
        {
            var r = new Rig().Make();
            r.Game.OwnList.Add(Own(1, "A", 0.05, 0)); r.Game.OwnList.Add(Own(2, "B", 0, 0));
            r.Game.Tracks[9] = Trk(9, 0, 0.05, -40, 0, "Torpedo");
            r.Run(130);
            var row = r.Ctl.Get(9);
            if (row == null || row.TtgTargetKey != 2 || double.IsNaN(row.TtgS) || Math.Abs(row.TtgS - 270) > 2) return "controller b: ttg " + (row == null ? "no row" : row.TtgTargetKey + "/" + row.TtgS);
            if (row.TtgExact) return "controller b: foreign TTG must not be exact";
            return null;
        }

        // (c) 100 tracks: whole snapshot in <= 61 frames, <= PerFrame reads per frame
        private static string CaseSchedule()
        {
            var r = new Rig().Make();
            for (int i = 1; i <= 100; i++) r.Game.Tracks[i] = Trk(i, i * 0.001, 0, 5, 0);
            int frames = 0;
            while (r.Ctl.Version < 1 && frames < 200) { r.Run(1); frames++; }
            if (frames > 61) return "controller c: first snapshot after " + frames + " frames";
            if (r.MaxReads > ContactInfoMath.PerFrame(100, 1.0 / 60, 1.0)) return "controller c: reads per frame " + r.MaxReads;
            if (r.Ctl.Version != 1) return "controller c: version " + r.Ctl.Version;
            return null;
        }

        private static Rig WithBtm(string mode)
        {
            var r = new Rig();
            r.S.BetterTacMap = mode;
            r.Probe.Patches.Add(ContactInfoMath.BtmHarmonyId);
            r.S.Show[(int)CiLayer.Speed] = true;
            return r.Make();
        }

        // (d) Better TacMap
        private static string CaseBetterTacMap()
        {
            var r = WithBtm("auto");
            r.Run(200);
            var on = r.Ctl.LayerOn;
            for (int i = 0; i <= (int)CiLayer.Age; i++) if (on[i]) return "controller d: layer " + i + " must be off with BTM";
            if (!on[(int)CiLayer.Noise] || !on[(int)CiLayer.Waypoint]) return "controller d: noise/waypoint must stay";
            if (r.Info.Count(s => s == "Better TacMap detected — duplicate map labels off (ContactInfo.BetterTacMap=on to force)") != 1) return "controller d: log lines " + string.Join("|", r.Info.ToArray());
            var f = WithBtm("on");
            f.Run(5);
            if (!f.Ctl.LayerOn[(int)CiLayer.Depth] || !f.Ctl.LayerOn[(int)CiLayer.Speed]) return "controller d: on must force our labels";
            return null;
        }

        // (e) Vessel Noise Display
        private static string CaseNoiseDisplay()
        {
            var r = new Rig(); r.Probe.Patches.Add(ContactInfoMath.NdHarmonyId); r.S.OwnSpeedNoise = false;   // the classic multi-line label
            r.Make();
            var sub = Own(5, "S", 0, 0); sub.IsShip = false; sub.IsSub = true; sub.DepthFt = 200; sub.NoiseDb = 118; sub.CavOnsetKn = 12; sub.SpeedKn = 11.5;
            r.Game.OwnList.Add(sub);
            r.Run(130);
            var on = r.Ctl.LayerOn;
            if (on[(int)CiLayer.Noise]) return "controller e: dB layer must be off with ND";
            if (!on[(int)CiLayer.Depth] || !on[(int)CiLayer.Ttg]) return "controller e: other layers must stay";
            var item = r.Labels.Find(5);
            if (item == null || !item.Text.Contains(UiText.Get("ci.cav.near"))) return "controller e: near cavitation line must stay: '" + (item == null ? "none" : item.Text) + "'";
            if (item.Text.Contains("dB")) return "controller e: dB text must be off";
            r.Ctl.SetOwnSpeedNoise(true); r.Run(70);   // the one-line own label drops the dB part as well, the margin stays
            var one = r.Labels.Find(5);
            if (one == null || one.Text != "200 ft · 12 kn · margin 1 kn" || !one.LeftOfIcon) return "controller e: own line with ND: '" + (one == null ? "none" : one.Text) + "'";
            if (r.Info.Count(s => s.StartsWith("Vessel Noise Display detected")) != 1) return "controller e: log lines " + string.Join("|", r.Info.ToArray());
            // the assembly alone (no patches) keeps ours on
            var a = new Rig(); a.Probe.Assemblies.Add(ContactInfoMath.NdAssembly); a.Make(); a.Run(5);
            if (!a.Ctl.LayerOn[(int)CiLayer.Noise]) return "controller e: assembly without patches must keep dB on";
            // patches that appear after the mission entry count from the next entry
            var late = new Rig().Make(); late.Run(5);
            late.Probe.Patches.Add(ContactInfoMath.NdHarmonyId); late.Run(60);
            if (!late.Ctl.LayerOn[(int)CiLayer.Noise]) return "controller e: late patches must not switch within the mission";
            late.Ctl.OnMissionLeft(); late.Ctl.OnMissionEntered(); late.Run(5);
            if (late.Ctl.LayerOn[(int)CiLayer.Noise]) return "controller e: a new mission entry must pick the patches up";
            var force = new Rig(); force.S.NoiseDisplay = "on"; force.Probe.Patches.Add(ContactInfoMath.NdHarmonyId); force.Make(); force.Run(5);
            if (!force.Ctl.LayerOn[(int)CiLayer.Noise]) return "controller e: on must force dB";
            return null;
        }

        // (f)
        // T-337: contact labels are off until the player switches them on; extra labels (CAP zone names, intercept) draw anyway; the switch works
        private static string CaseMasterDefaultOff()
        {
            var r = new Rig();
            r.Ctl = new ContactInfoController(r.S, r.Game, r.Labels, r.Probe, new HotkeyRegistry(), r.Info.Add, r.Warn.Add);
            if (r.Ctl.Master) return "controller master: default must be off";
            r.Ctl.OnMissionEntered();
            ((IMapExtraLabels)r.Ctl).SetExtraLabels("cap", new List<LabelItem> { Extra(-7, "zone") });
            r.Run(130);
            if (r.Labels.Find(-7) == null) return "controller master: extra label must draw with the default Master off";
            r.Ctl.Master = true; r.Run(2);
            if (!r.Ctl.Master || r.Labels.Find(-7) == null) return "controller master: toggle on keeps extras";
            return null;
        }

        private static string CaseDefaults()
        {
            if (new ContactInfoSettings().Waypoint != "dist") return "controller f: waypoint default must be dist";
            return null;
        }

        // (m) T-154 / ADR-0051: one map-layer entry = master switch; the per-label settings stay independent
        private static string CaseSingleLayer()
        {
            if (ContactInfoMath.LayerNames.Length != 1 || ContactInfoMath.LayerNames[0] != "VOICEBOT_CONTACT_INFO") return "controller m: one layer name";
            var r = new Rig().Make();
            r.Game.Tracks[1] = Trk(1, 0.1, 0, 5, 0);
            r.Run(3);
            if (!r.Ctl.Master) return "controller m: master default on";
            bool depthBefore = r.Ctl.LayerOn[(int)CiLayer.Depth];
            r.Ctl.Master = false;   // what the map-layer checkbox does (SetOpen)
            foreach (bool on in r.Ctl.LayerOn) if (on) return "controller m: master off must switch every label layer off";
            r.Ctl.Master = true;
            if (r.Ctl.LayerOn[(int)CiLayer.Depth] != depthBefore) return "controller m: Show.* must survive the master switch";
            r.Ctl.SetLayer(CiLayer.Depth, false);
            r.Ctl.Master = false; r.Ctl.Master = true;
            if (r.Ctl.LayerOn[(int)CiLayer.Depth]) return "controller m: an individually disabled label must stay off";
            if (r.Ctl.LayerNumbers[0] != 210) return "controller m: layer number";
            return null;
        }

        // (g) hotkeys
        private static string CaseHotkeys()
        {
            var r = new Rig().Make();
            r.Game.Tracks[1] = Trk(1, 0.1, 0, 5, 0);
            var persisted = new List<string>();
            r.Ctl.PersistShow = (l, on) => persisted.Add(l + "=" + on);
            r.Run(3);
            r.Keys.HeldKeys.Add("LeftAlt"); r.Keys.HeldKeys.Add("LeftShift"); r.Keys.DownKeys.Add("Keypad1");
            r.Run(1);
            r.Keys.DownKeys.Clear();
            if (r.Ctl.LayerOn[(int)CiLayer.Depth]) return "controller g: hotkey must switch the depth layer off";
            if (persisted.Count != 1 || persisted[0] != "Depth=False") return "controller g: persist " + string.Join("|", persisted.ToArray());
            r.Keys.HeldKeys.Remove("LeftShift"); r.Keys.DownKeys.Add("N");
            int hides = r.Labels.Hides;
            r.Run(1);
            r.Keys.DownKeys.Clear();
            if (r.Ctl.Master || r.Labels.Hides <= hides) return "controller g: master off must hide the labels";
            if (r.Ctl.SetLayer(CiLayer.Speed, true) != true || persisted.Count != 2) return "controller g: SetLayer";
            return null;
        }

        // (h) CPA to the group / to the selected unit
        private static string CaseCpa()
        {
            var r = new Rig(); r.S.CpaTarget = "group"; r.Make();
            r.Game.OwnList.Add(Own(1, "A", 0, 0)); r.Game.OwnList.Add(Own(2, "B", 0.2, 0));
            r.Game.Tracks[3] = Trk(3, 0.2, 0.1, -20, 0);
            r.Run(130);
            var row = r.Ctl.Get(3);
            if (row == null || row.CpaRefName != "B" || double.IsNaN(row.CpaNm) || row.CpaNm > 0.2) return "controller h: group cpa " + (row == null ? "no row" : row.CpaRefName + "/" + row.CpaNm);
            var s = new Rig().Make();
            s.Game.OwnList.Add(Own(1, "A", 0, 0)); s.Game.OwnList.Add(Own(2, "B", 0.2, 0));
            s.Game.Tracks[3] = Trk(3, 0.2, 0.1, -20, 0);
            s.Run(130);
            if (!double.IsNaN(s.Ctl.Get(3).CpaNm)) return "controller h: selected without a selected own unit must give NaN";
            s.Game.SelUid = 1; s.Run(130);
            var sr = s.Ctl.Get(3);
            if (double.IsNaN(sr.CpaNm) || Math.Abs(sr.CpaNm - 12) > 0.2) return "controller h: selected cpa " + sr.CpaNm;
            return null;
        }

        // T-352: the controller passes the world of the game to CpaGroup (60N, 0.1 deg east, going west at 20 kn: TCPA 1080 s flat, 540 s on the globe)
        private static string CaseCpaWorld()
        {
            foreach (bool globe in new[] { false, true })
            {
                var r = new Rig(); r.S.CpaTarget = "group"; r.Game.WorldGlobe = globe; r.Make();
                r.Game.OwnList.Add(Own(1, "A", 60, 10));
                r.Game.Tracks[3] = Trk(3, 60, 10.1, -20, 0);
                r.Run(130);
                var row = r.Ctl.Get(3);
                double want = globe ? 540 : 1080;
                if (row == null || double.IsNaN(row.TcpaS) || Math.Abs(row.TcpaS - want) > 2) return "controller worldflat: globe=" + globe + " tcpa " + (row == null ? "no row" : row.TcpaS.ToString());
            }
            return null;
        }

        // (i) stale track
        private static string CaseStale()
        {
            var r = new Rig(); r.S.Show[(int)CiLayer.Speed] = true; r.Make();
            var t = Trk(4, 0.1, 0.1, 6, 0); t.Live = false; t.EpochS = 0;
            r.Game.Tracks[4] = t; r.Game.TimeS = 100;
            r.Run(130);
            var row = r.Ctl.Get(4);
            if (row == null || !row.Stale) return "controller i: row must be stale";
            var item = r.Labels.Find(4);
            if (item == null || !item.Dim || !item.Text.Contains("~")) return "controller i: dim label with ~ speed, got '" + (item == null ? "none" : item.Text) + "'";
            return null;
        }

        // (j) noise belongs to own units only
        private static string CaseOwnNoise()
        {
            var r = new Rig().Make();
            var o = Own(1, "O", 0, 0); o.NoiseDb = 110; o.CavOnsetKn = 15;
            r.Game.OwnList.Add(o);
            r.Game.Tracks[2] = Trk(2, 0.1, 0.1, 5, 0);
            r.Run(130);
            if (r.Ctl.GetOwn(1) == null || r.Ctl.GetOwn(1).NoiseDb != 110) return "controller j: own noise missing";
            if (!double.IsNaN(r.Ctl.Get(2).NoiseDb)) return "controller j: a track must have no noise";
            if (r.Ctl.OwnKeys.Count != 1 || r.Ctl.OwnKeys[0] != 1) return "controller j: OwnKeys";
            return null;
        }

        // (n) ADR-0045 Supplement 1: one speed + noise line at own ships and submarines, left of the icon, coloured by the cavitation margin
        private static string CaseOwnLine()
        {
            var r = new Rig().Make();
            var ship = Own(1, "Yorktown", 0, 0); ship.SpeedKn = 12.2; ship.NoiseDb = 157.6; ship.CavOnsetKn = 25;
            var sub = Own(2, "Dallas", 0.1, 0); sub.IsShip = false; sub.IsSub = true; sub.DepthFt = 300; sub.SpeedKn = 18; sub.NoiseDb = 120; sub.CavOnsetKn = 18.5;
            var cav = Own(3, "Fast", 0.2, 0); cav.SpeedKn = 30; cav.NoiseDb = 150; cav.Cavitating = true;
            var air = Own(4, "Hawk", 0.3, 0); air.IsShip = false; air.IsAir = true; air.SpeedKn = 120;
            r.Game.OwnList.AddRange(new[] { ship, sub, cav, air });
            r.Game.Tracks[9] = Trk(9, 0.5, 0.5, 5, 0);
            r.Run(130);
            var a = r.Labels.Find(1); var b = r.Labels.Find(2); var c = r.Labels.Find(3);
            if (a == null || a.Text != "12 kn · 158 dB" || !a.LeftOfIcon || a.Argb != ContactInfoMath.OwnArgbOk) return "controller n: ship '" + (a == null ? "none" : a.Text) + "'";
            if (b == null || b.Text != "300 ft · 18 kn · 120 dB · margin 1 kn" || b.Argb != ContactInfoMath.OwnArgbNear) return "controller n: sub '" + (b == null ? "none" : b.Text) + "'";
            if (c == null || c.Text != "30 kn · 150 dB · CAV" || c.Argb != ContactInfoMath.OwnArgbCav) return "controller n: cav '" + (c == null ? "none" : c.Text) + "'";
            if (r.Labels.Find(4) != null) return "controller n: an own aircraft gets no speed+noise line (Show.Speed is off)";
            var t = r.Labels.Find(9);
            if (t != null && t.LeftOfIcon) return "controller n: a track label stays right of the icon";
            // ~1 Hz: the text follows the next snapshot
            ship.SpeedKn = 20; ship.NoiseDb = 165;
            r.Run(70);
            if (r.Labels.Find(1).Text != "20 kn · 165 dB") return "controller n: update '" + r.Labels.Find(1).Text + "'";
            // master off hides it with every other label
            r.Ctl.Master = false; r.Run(2);
            if (r.Labels.Find(1) != null) return "controller n: master off must hide the own line";
            r.Ctl.Master = true;
            // the setting off: back to the classic layers (noise line on, speed layer off by default), right of the icon
            r.Ctl.SetOwnSpeedNoise(false); r.Run(2);
            var off = r.Labels.Find(1);
            if (off == null || off.LeftOfIcon || off.Text != "165 dB (game model)") return "controller n: setting off '" + (off == null ? "none" : off.Text) + "'";
            r.Ctl.SetOwnSpeedNoise(true); r.Run(2);
            if (!r.Labels.Find(1).LeftOfIcon) return "controller n: setting on again";
            return null;
        }

        // (k) label cap
        private static string CaseMaxLabels()
        {
            var r = new Rig(); r.S.Show[(int)CiLayer.Speed] = true; r.Make();
            for (int i = 1; i <= 200; i++) r.Game.Tracks[i] = Trk(i, i * 0.001, 0, 10, 0);
            r.Run(130);
            if (r.Labels.Last.Count != 80) return "controller k: labels " + r.Labels.Last.Count;
            if (!r.Ctl.StatsLine().Contains("n=200")) return "controller k: stats " + r.Ctl.StatsLine();
            int[] nums = r.Ctl.LayerNumbers;
            if (nums[2] != 217 || nums[0] != 210) return "controller k: layer numbers " + string.Join(",", nums.Select(x => x.ToString()).ToArray());
            return null;
        }

        // (l) the waypoint label of the selected unit
        private static string CaseWaypoint()
        {
            var r = new Rig().Make();
            r.Game.OwnList.Add(Own(1, "A", 0, 0)); r.Game.SelUid = 1;
            r.Game.Wp = new CiWaypoint { OwnUid = 1, RouteNm = 5, SpeedKn = 10, LastLat = 0.05, LastLon = 0 };
            r.Run(130);
            var item = r.Labels.Find(-1000000 - 1);
            string expect = ContactInfoMath.FmtWaypoint(r.Game.Wp, "dist", new CiUnits());
            if (item == null || expect.Length == 0 || item.Text != expect) return "controller l: waypoint label '" + (item == null ? "none" : item.Text) + "'";
            return null;
        }

        private static LabelItem Extra(int key, string text)
        {
            return new LabelItem { Key = key, Lat = 0.01 * -key, Lon = 0, Text = text, Argb = 0xFFFFFFFFu };
        }

        // (o) T-137 D: IMapExtraLabels
        private static string CaseExtraLabels()
        {
            var r = new Rig(); r.S.MaxLabels = 5; r.Make();
            var ex = (IMapExtraLabels)r.Ctl;
            r.Ctl.Master = false;
            r.Run(130);
            ex.SetExtraLabels("range_rings", new List<LabelItem> { Extra(-5, "ring") });
            r.Run(2);
            if (r.Labels.Find(-5) == null) return "controller o: extra label must show with Master off";
            int v1 = r.Labels.LastVersion;
            ex.SetExtraLabels("range_rings", new List<LabelItem> { Extra(-5, "ring"), Extra(-6, "ring2") });
            r.Run(2);
            if (r.Labels.Find(-6) == null || r.Labels.LastVersion == v1) return "controller o: a change must give a new Show version";
            ex.SetExtraLabels("range_rings", null);
            r.Run(2);
            if (r.Labels.Last.Count != 0) return "controller o: null must remove the labels";
            var many = new List<LabelItem>();
            for (int i = 1; i <= 40; i++) many.Add(Extra(-100 - i, "m" + i));
            ex.SetExtraLabels("range_rings", many);
            ex.SetExtraLabels("intercept", new List<LabelItem> { Extra(-500, "icpt") });
            r.Run(2);
            if (r.Labels.Last.Count != 25 || r.Labels.Find(-500) == null) return "controller o: cap 24 per owner, owners independent: " + r.Labels.Last.Count;
            ex.SetExtraLabels("range_rings", null); ex.SetExtraLabels("intercept", null);
            // Master on: extras come together with the contact labels and are not cut by MaxLabels
            r.Ctl.Master = true;
            for (int i = 1; i <= 20; i++) r.Game.Tracks[i] = Trk(i, i * 0.001, 0, 10, 0);
            r.S.Show[(int)CiLayer.Speed] = true;
            r.Run(130);
            int plain = r.Labels.Last.Count;
            if (plain != 5) return "controller o: MaxLabels " + plain;
            ex.SetExtraLabels("range_rings", new List<LabelItem> { Extra(-7, "a"), Extra(-8, "b"), Extra(-9, "c") });
            r.Run(2);
            if (r.Labels.Last.Count != 8 || r.Labels.Find(-9) == null) return "controller o: extras with Master on: " + r.Labels.Last.Count;
            // mission edge clears them
            r.Ctl.OnMissionLeft(); r.Game.Tracks.Clear(); r.Ctl.Master = false;
            r.Run(130);
            if (r.Labels.Last.Count != 0) return "controller o: leaving the mission must clear extras";
            // distance formatting equals the module's own
            if (ex.FmtDistance(25) != ContactInfoMath.FmtDist(25, new CiUnits())) return "controller o: FmtDistance nm";
            r.Ctl.SetUnits("km,kmh,m");
            string km = ex.FmtDistance(25);
            string err;
            if (km != ContactInfoMath.FmtDist(25, ContactInfoMath.ParseUnits("km,kmh,m", out err)) || !km.Contains("km")) return "controller o: FmtDistance km '" + km + "'";
            return null;
        }

        // (n) T-144 L4: settings changed at run time
        private static string CaseLiveSettings()
        {
            var r = new Rig().Make();
            r.Game.OwnList.Add(Own(1, "A", 0, 0)); r.Game.SelUid = 1;
            r.Game.Wp = new CiWaypoint { OwnUid = 1, RouteNm = 5, SpeedKn = 10, LastLat = 0.05, LastLon = 0 };
            r.Run(130);
            var wp = r.Labels.Find(-1000000 - 1);
            if (wp == null) return "controller n: waypoint label before";
            string nm = wp.Text;
            r.S.Waypoint = "off"; r.Ctl.Refresh(); r.Run(2);
            if (r.Labels.Find(-1000000 - 1) != null) return "controller n: Waypoint=off must remove the label at once";
            r.S.Waypoint = "dist"; r.Ctl.SetUnits("km,kmh,m"); r.Run(2);
            var km = r.Labels.Find(-1000000 - 1);
            if (km == null || km.Text == nm || !km.Text.Contains("km")) return "controller n: units km, got '" + (km == null ? "none" : km.Text) + "'";
            r.Ctl.SetUnits("furlong"); r.Run(2);
            km = r.Labels.Find(-1000000 - 1);
            if (km == null || km.Text != nm) return "controller n: bad units must give the defaults";
            var persisted = new List<string>();
            r.Ctl.PersistShow = (l, on) => persisted.Add(l + "=" + on);
            r.Ctl.ApplyShow(CiLayer.Waypoint, false); r.Run(2);
            if (persisted.Count != 0) return "controller n: ApplyShow must not write the setting back";
            if (r.Ctl.LayerOn[(int)CiLayer.Waypoint] || r.Labels.Find(-1000000 - 1) != null) return "controller n: Show.Waypoint=false must hide the label";
            r.Ctl.ApplyShow(CiLayer.Waypoint, false);
            if (persisted.Count != 0) return "controller n: ApplyShow twice";
            r.Ctl.RespecMaster("F9"); r.Ctl.RespecLayer(CiLayer.Depth, "");
            r.Keys.DownKeys.Add("F9"); r.Run(1); r.Keys.DownKeys.Clear();
            if (r.Ctl.Master) return "controller n: new master hotkey F9 must work";
            r.S.MaxLabels = 10; r.Ctl.Refresh(); r.S.Show[(int)CiLayer.Waypoint] = true; r.Ctl.Master = true;
            for (int i = 1; i <= 30; i++) r.Game.Tracks[i] = Trk(i, i * 0.001, 0, 10, 0);
            r.Run(130);
            if (r.Labels.Last.Count > 11) return "controller n: MaxLabels live, labels " + r.Labels.Last.Count;
            return null;
        }
    }
}
