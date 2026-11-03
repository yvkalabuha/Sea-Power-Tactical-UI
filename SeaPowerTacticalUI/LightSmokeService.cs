using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-165 / ADR-0062: the state of light smoke: flag, k, errors, counters, cost, diagnostics. No Unity.</summary>
    internal sealed class LightSmokeService
    {
        public const int MaxErrors = 3;
        private readonly Action<string> _log;
        private readonly Func<double> _clock;
        private bool _cappedLogged;
        private long _spawns, _src0, _src1, _src2;
        private double _costMax, _costSum;
        private long _costN;

        public LightSmokeService(IParticleAccess access, Func<double> clock, Action<string> log)
        {
            _log = log ?? (m => { });
            _clock = clock;
            Registry = new LightSmokeRegistry(access, clock);
            K = LightSmokeMath.DefaultK;
        }

        public LightSmokeRegistry Registry { get; private set; }
        public bool Flag { get; private set; }
        public bool Broken { get; private set; }
        public bool On { get { return Flag && !Broken; } }
        public double K { get; private set; }
        public int Errors { get; private set; }
        /// <summary>null = every effect (the seam for a name filter, ADR-0062 "risks").</summary>
        public Func<string, bool> Filter { get; set; }

        private void Log(string m) { try { _log(m); } catch { } }

        /// <summary>false -> RestoreAll + one log line "light smoke off: restored N, dead M".</summary>
        public void SetOn(bool on)
        {
            if (on == Flag) return;
            Flag = on;
            if (on) return;
            long dead0 = Registry.SkippedDead;
            int n = Registry.RestoreAll();
            Log("light smoke off: restored " + n + ", dead " + (Registry.SkippedDead - dead0));
        }

        /// <summary>ClampK; a clamped value writes one log line. Only new / re-taken systems see the new k.</summary>
        public void SetK(double k)
        {
            bool clamped;
            double v = LightSmokeMath.ClampK(k, out clamped);
            if (clamped) Log("light smoke factor " + Json.Num(k) + " is out of range, using " + Json.Num(v));
            K = v;
        }

        /// <summary>Main thread, from the postfix, only when On. Filter(effectName) false -> nothing. Each system -> Registry.Apply(ps, K).
        /// First Capped of the session -> one log line.</summary>
        public void OnSpawn(string effectName, IList<object> systems) { OnSpawn(LsSource.Spawn, effectName, systems, LsProfile.Full); }

        public void OnSpawn(LsSource src, string effectName, IList<object> systems, LsProfile profile)
        {
            if (!On || systems == null) return;
            var f = Filter;
            if (f != null && !f(effectName)) return;
            _spawns++;
            if (src == LsSource.Spawn) _src0++; else if (src == LsSource.Direct) _src1++; else _src2++;
            for (int i = 0; i < systems.Count; i++)
            {
                if (Registry.Apply(systems[i], K, profile) == LightSmokeOutcome.Capped && !_cappedLogged)
                {
                    _cappedLogged = true;
                    Log("light smoke registry is full (" + LightSmokeRegistry.Cap + "), new systems stay as they are");
                }
            }
        }

        public void AddCost(double microseconds)
        {
            _costN++; _costSum += microseconds;
            if (microseconds > _costMax) _costMax = microseconds;
        }

        public void Fail(Exception e)
        {
            Errors++;
            if (Errors < MaxErrors || Broken) return;
            Broken = true;
            Log("light smoke off until restart: " + MaxErrors + " errors, last: " + (e == null ? "?" : e.GetType().Name + ": " + e.Message));
            try { Registry.RestoreAll(); } catch { }
        }

        public void Tick() { Registry.Prune(false); }
        public void LeaveMission() { Registry.Prune(true); }

        /// <summary>{"on","flag","broken","k","entries","reduced","reused","rescaled","restored","pruned","capped","errors","spawns","src_spawn","src_direct","src_chaff","spawn_cost_us_max","spawn_cost_us_avg"}</summary>
        public string DiagJson()
        {
            var r = Registry;
            var sb = new StringBuilder("{");
            sb.Append("\"on\":").Append(Json.Bool(On));
            sb.Append(",\"flag\":").Append(Json.Bool(Flag));
            sb.Append(",\"broken\":").Append(Json.Bool(Broken));
            sb.Append(",\"k\":").Append(Json.Num(K));
            sb.Append(",\"entries\":").Append(r.Count);
            sb.Append(",\"reduced\":").Append(r.Reduced);
            sb.Append(",\"reused\":").Append(r.Reused);
            sb.Append(",\"rescaled\":").Append(r.Rescaled);
            sb.Append(",\"restored\":").Append(r.Restored);
            sb.Append(",\"pruned\":").Append(r.Pruned);
            sb.Append(",\"capped\":").Append(r.Capped);
            sb.Append(",\"errors\":").Append(Errors);
            sb.Append(",\"spawns\":").Append(_spawns);
            sb.Append(",\"src_spawn\":").Append(_src0);
            sb.Append(",\"src_direct\":").Append(_src1);
            sb.Append(",\"src_chaff\":").Append(_src2);
            sb.Append(",\"spawn_cost_us_max\":").Append(Json.Num(Math.Round(_costMax, 2)));
            sb.Append(",\"spawn_cost_us_avg\":").Append(Json.Num(_costN == 0 ? 0 : Math.Round(_costSum / _costN, 2)));
            return sb.Append('}').ToString();
        }

        public static string SelfTest()
        {
            var f = new LightSmokeRegistry.FakeAccess();
            for (int i = 1; i <= 3; i++) f.Add(i);
            var logs = new List<string>();
            double now = 0;
            var s = new LightSmokeService(f, () => now, logs.Add);
            var sys = new List<object> { 1, 2 };
            s.OnSpawn(null, sys);
            if (f.Writes != 0 || s.Registry.Count != 0) return "off: OnSpawn wrote";
            s.SetOn(true);
            s.OnSpawn(null, sys);
            if (s.Registry.Reduced != 2 || f.Writes != 2) return "on: reduced " + s.Registry.Reduced;
            var orig = new LightSmokeRegistry.FakeAccess(); orig.Add(1);
            s.SetOn(false);
            if (s.Registry.Count != 0 || !f.Vals[1].SameAs(orig.Vals[1])) return "SetOn(false) did not restore";
            if (logs.Count != 1 || !logs[0].StartsWith("light smoke off: restored 2, dead 0")) return "off log: " + string.Join("|", logs);
            s.SetOn(true);
            s.SetK(0.05);
            if (s.K != 0.2 || logs.Count != 2) return "SetK clamp";
            // filter
            s.SetK(0.35);
            s.Filter = n => n != "x";
            s.OnSpawn("x", sys);
            if (s.Registry.Count != 0) return "filter let x through";
            s.OnSpawn(null, sys);
            if (s.Registry.Count != 2) return "filter blocked null";
            s.Filter = null;
            s.OnSpawn("x", new List<object> { 3 });
            if (s.Registry.Count != 3) return "no filter";
            // sources
            s.OnSpawn(LsSource.Direct, null, new List<object> { 3 }, LsProfile.Full);
            s.OnSpawn(LsSource.Chaff, null, new List<object> { 3 }, LsProfile.KeepLifetime);
            // cost
            s.AddCost(10); s.AddCost(30);
            var d = (Dictionary<string, object>)Json.Parse(s.DiagJson());
            foreach (var key in new[] { "on", "flag", "broken", "k", "entries", "reduced", "reused", "rescaled", "restored", "pruned", "capped", "errors", "spawns", "src_spawn", "src_direct", "src_chaff", "spawn_cost_us_max", "spawn_cost_us_avg" })
                if (!d.ContainsKey(key)) return "DiagJson lacks " + key;
            if ((double)d["spawn_cost_us_avg"] != 20 || (double)d["spawn_cost_us_max"] != 30) return "cost";
            if ((double)d["src_spawn"] != 3 || (double)d["src_direct"] != 1 || (double)d["src_chaff"] != 1) return "sources " + s.DiagJson();
            // errors
            int before = logs.Count;
            s.Fail(new Exception("a")); s.Fail(new Exception("b"));
            if (s.Broken || !s.On) return "broken too early";
            s.Fail(new Exception("c"));
            if (!s.Broken || s.On || logs.Count != before + 1) return "not broken after 3";
            if (s.Registry.Count != 0 || !f.Vals[1].SameAs(orig.Vals[1])) return "broken did not restore";
            s.OnSpawn(null, sys);
            if (s.Registry.Count != 0) return "broken still reduces";
            // capped log once
            var f2 = new LightSmokeRegistry.FakeAccess();
            var l2 = new List<string>();
            var s2 = new LightSmokeService(f2, () => 0, l2.Add);
            s2.SetOn(true);
            var many = new List<object>();
            for (int i = 0; i < LightSmokeRegistry.Cap + 3; i++) { f2.Add(i); many.Add(i); }
            s2.OnSpawn(null, many);
            if (s2.Registry.Capped != 3 || l2.Count != 1) return "capped log " + l2.Count;
            return null;
        }
    }
}
