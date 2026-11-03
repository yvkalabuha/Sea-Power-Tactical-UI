using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Dev
{
    /// <summary>Runs one dev request on the main thread: read -> diag slot / log ring; control and fire -> closed table -> `link` slot.</summary>
    internal sealed class DevExecutor
    {
        public const string LogSource = "Sea Power Tactical UI Dev";
        private readonly DevLogRing _ring;
        private readonly Func<bool> _fireOn;
        private readonly Action<string> _warn;
        private readonly Func<DevSlots> _resolve;
        private readonly DevAirReady _airReady;
        private readonly DevGfx _gfx;
        private readonly Func<bool> _forceOn;

        public DevExecutor(DevLogRing ring, Func<bool> fireOn, Action<string> warn, Func<DevSlots> resolve = null, DevAirReady airReady = null, DevGfx gfx = null, Func<bool> forceOn = null)
        {
            _ring = ring; _fireOn = fireOn; _warn = warn ?? (s => { }); _resolve = resolve ?? DevSlots.Resolve; _airReady = airReady ?? new DevAirReady();
            _gfx = gfx ?? new DevGfx();
            _forceOn = forceOn ?? (() => true);
        }

        public bool FireOn { get { return _fireOn(); } }

        public string Hello()
        {
            var s = _resolve();
            return DevProtocol.Hello(DevInfo.Version, s.UiVersion, s.Diag != null, s.Link != null, _fireOn());
        }

        private void Journal(string line)
        {
            _ring.Add("Warning", LogSource, line, DateTime.UtcNow);
            _warn(line);
        }

        /// <summary>Journal line for a fire command that timed out in the pump (id is unknown to the caller).</summary>
        public void FireTimeout(DevRequest r, string why)
        {
            if (DevCommandTable.GroupOf(r.Cmd) != "fire") return;
            Journal("[Dev][FIRE] " + r.Cmd + " ... → timeout (" + why + ")");
        }

        public string Handle(DevRequest r)
        {
            try { return Run(r); }
            catch (Exception e) { return DevProtocol.Err(r.Id, "internal", e.GetType().Name + ": " + e.Message, null); }
        }

        private string Run(DevRequest r)
        {
            string group = DevCommandTable.GroupOf(r.Cmd);
            if (group == null) return DevProtocol.Err(r.Id, "forbidden", r.Cmd, null);
            List<Dictionary<string, object>> steps; string err;
            if (!DevCommandTable.Translate(r.Cmd, r.Args, out steps, out err))
            {
                if (err.StartsWith("bad_args:")) return DevProtocol.Err(r.Id, "bad_args", err.Substring(9), null);
                return DevProtocol.Err(r.Id, err, null, null);
            }
            if (r.Cmd == "gc") return Gc(r);
            if (group == "read") return r.Cmd == "log" ? Log(r) : State(r);
            if (group == "fire") return Fire(r, steps);
            if (r.Cmd == "air_ready") return AirReady(r);
            if (r.Cmd == "gfx") return Gfx(r);
            if (r.Cmd == "force") return Force(r, steps);
            return RunSteps(r, steps, null);
        }

        private string Log(DevRequest r)
        {
            int lines = FireGate.ToInt(r.Args.ContainsKey("lines") ? r.Args["lines"] : null, 50);
            string contains = r.Args.ContainsKey("contains") ? r.Args["contains"] as string : null;
            string level = r.Args.ContainsKey("level") ? r.Args["level"] as string : null;
            var tail = _ring.Tail(lines, contains, level);
            return DevProtocol.Ok(r.Id, new Dictionary<string, object> { { "lines", tail } }, null);
        }

        /// <summary>T-212: full collection on the calling (main) thread; live_mb = what the mod and the game really keep. Journaled as [Dev][GC].</summary>
        private string Gc(DevRequest r)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long before = GC.GetTotalMemory(false);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long after = GC.GetTotalMemory(false);
            double ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            var d = new Dictionary<string, object> { { "live_mb", Math.Round(after / 1048576.0, 1) }, { "before_mb", Math.Round(before / 1048576.0, 1) }, { "ms", ms } };
            Journal("[Dev][GC] live_mb=" + d["live_mb"] + " before_mb=" + d["before_mb"] + " ms=" + ms);
            return DevProtocol.Ok(r.Id, d, null);
        }

        private string State(DevRequest r)
        {
            var s = _resolve();
            if (s.Diag == null) return DevProtocol.Err(r.Id, s.DiagError ?? "ui_mod_absent", "diag slot", null);
            var q = new Dictionary<string, object> { { "q", "state" } };
            if (r.Args.ContainsKey("sections")) q["sections"] = r.Args["sections"];
            if (r.Args.ContainsKey("limit")) q["limit"] = r.Args["limit"];
            string raw = s.Diag.Query(DevJson.Write(q));
            var d = Json.Parse(raw) as Dictionary<string, object>;
            if (d == null) return DevProtocol.Err(r.Id, "diag_error", "not an object", null);
            if (d.ContainsKey("error")) return DevProtocol.Err(r.Id, "diag_error", d["error"] as string, null);
            return DevProtocol.Ok(r.Id, new DevRaw(raw), null);
        }

        /// <summary>QA cheat (dev DLL only): finish own deck ready-up / cool-down now. Every call is journaled as [Dev][AIR].</summary>
        private string AirReady(DevRequest r)
        {
            Dictionary<string, object> data; string detail;
            string err = _airReady.Run(r.Args, out data, out detail);
            Journal(DevAirReady.Line(r.Args, err ?? ("ok changed=" + data["changed"])));
            return err == null ? DevProtocol.Ok(r.Id, data, null) : DevProtocol.Err(r.Id, err, detail, null);
        }

        /// <summary>T-165 graphics A/B (dev DLL only): dump / set lever / reset. Every call is journaled as [Dev][GFX].</summary>
        private string Gfx(DevRequest r)
        {
            string op = r.Args["op"] as string;
            if (op == "dump")
            {
                var d = _gfx.Dump();
                Journal(DevGfx.Line(r.Args, "ok cameras=" + ((System.Collections.ICollection)d["cameras"]).Count));
                return DevProtocol.Ok(r.Id, d, null);
            }
            if (op == "reset")
            {
                var d = _gfx.Reset();
                Journal(DevGfx.Line(r.Args, "ok restored=" + string.Join(",", ((List<object>)d["restored"]).ConvertAll(x => (string)x).ToArray())));
                return DevProtocol.Ok(r.Id, d, null);
            }
            Dictionary<string, object> data; string detail;
            string err = _gfx.Set((string)r.Args["lever"], (bool)r.Args["on"], out data, out detail);
            string res = err ?? ("ok changes=" + ((List<object>)data["changes"]).Count + " errors=" + ((List<object>)data["errors"]).Count);
            Journal(DevGfx.Line(r.Args, err == null ? res : res + " (" + detail + ")"));
            return err == null ? DevProtocol.Ok(r.Id, data, null) : DevProtocol.Err(r.Id, err, detail, null);
        }

        /// <summary>T-361 (ADR-0136): QA scenario by real game orders; the work is the `qa_force` link command. Every outcome is journaled as [Dev][FORCE].</summary>
        private string Force(DevRequest r, List<Dictionary<string, object>> steps)
        {
            if (!_forceOn())
            {
                Journal(DevForce.Line(r.Args, "force_disabled"));
                return DevProtocol.Err(r.Id, "force_disabled", "[Dev] ForceCommands = false", null);
            }
            string reply = RunSteps(r, steps, null);
            var d = Json.Parse(reply) as Dictionary<string, object>;
            bool ok = d != null && d.ContainsKey("ok") && d["ok"] is bool && (bool)d["ok"];
            string res = ok ? "ok" : (d != null && d.ContainsKey("error") ? d["error"] as string : null) ?? "bad_reply";
            Journal(DevForce.Line(r.Args, res));
            return reply;
        }

        private string Fire(DevRequest r, List<Dictionary<string, object>> steps)
        {
            var s = _resolve();
            FireInfo info;
            string deny = FireGate.Check(r.Cmd, r.Args, _fireOn(), s.Diag, out info);
            if (deny != null)
            {
                string line = FireGate.FireLine(info, deny);
                Journal(line);
                return DevProtocol.Err(r.Id, deny, null, line);
            }
            return RunSteps(r, steps, info);
        }

        private string RunSteps(DevRequest r, List<Dictionary<string, object>> steps, FireInfo fire)
        {
            var s = _resolve();
            if (s.Link == null)
            {
                string code = s.LinkError ?? "ui_mod_absent";
                string line = null;
                if (fire != null) { line = FireGate.FireLine(fire, code); Journal(line); }
                return DevProtocol.Err(r.Id, code, "link slot", line);
            }
            var done = new List<object>();
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                string cmd = (string)step["cmd"];
                var send = new Dictionary<string, object>(step);
                send["id"] = i + 1;
                string raw = s.Link.Call(DevJson.Write(send));
                var ack = Json.Parse(raw) as Dictionary<string, object>;
                bool ok = ack != null && ack.ContainsKey("ok") && ack["ok"] is bool && (bool)ack["ok"];
                var rec = new Dictionary<string, object> { { "cmd", cmd }, { "ok", ok } };
                if (ok) rec["data"] = ack.ContainsKey("data") ? ack["data"] : null;
                else
                {
                    rec["error"] = ack != null && ack.ContainsKey("error") ? ack["error"] : "bad_ack";
                    if (ack != null && ack.ContainsKey("detail")) rec["detail"] = ack["detail"];
                }
                done.Add(rec);
                if (!ok)
                {
                    string code = rec["error"] as string ?? "bad_ack";
                    string line = null;
                    if (fire != null) { line = FireGate.FireLine(fire, code); Journal(line); }
                    return DevProtocol.Err(r.Id, code, rec.ContainsKey("detail") ? rec["detail"] as string : null, line,
                        new Dictionary<string, object> { { "steps", done } });
                }
            }
            string okLine = null;
            if (fire != null) { okLine = FireGate.FireLine(fire, "ok"); Journal(okLine); }
            return DevProtocol.Ok(r.Id, new Dictionary<string, object> { { "steps", done } }, okLine);
        }
    }
}
