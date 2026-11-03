using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace VoiceBotBridge.Dev
{
    internal sealed class Pending
    {
        public DevRequest Request;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public int State;   // 0 pending, 1 running, 2 done, 3 cancelled
        public string Reply;
        public long EnqueuedTicks;   // Stopwatch timestamp of Enqueue (queue wait in the command log)
    }

    /// <summary>Hang triage: one line per dev command for LogOutput.log ("23:55:14.123 [Dev] cmd state -> ok exec_ms=0.4 wait_ms=12").</summary>
    internal static class DevCmdLog
    {
        public static string Line(DateTime local, string cmd, string reply, double execMs, double waitMs)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return local.ToString("HH:mm:ss.fff", ic) + " [Dev] cmd " + (cmd ?? "?") + " -> " + Result(reply) +
                   " exec_ms=" + execMs.ToString("F1", ic) + " wait_ms=" + waitMs.ToString("F0", ic);
        }

        public static string Timeout(DateTime local, string cmd, string why)
        {
            return local.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " [Dev] cmd " + (cmd ?? "?") + " -> timeout (" + why + ")";
        }

        /// <summary>"ok" or the error code of a reply (the top-level "ok"/"error" come before "data" in DevProtocol replies).</summary>
        public static string Result(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return "no_reply";
            int ok = reply.IndexOf("\"ok\":", StringComparison.Ordinal);
            if (ok >= 0 && string.CompareOrdinal(reply, ok + 5, "true", 0, 4) == 0) return "ok";
            const string key = "\"error\":\"";
            int e = reply.IndexOf(key, StringComparison.Ordinal);
            if (e < 0) return "error";
            int from = e + key.Length, to = reply.IndexOf('"', from);
            return to > from ? "!" + reply.Substring(from, Math.Min(to - from, 40)) : "error";
        }

        public static double Ms(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }

        public static string SelfTest()
        {
            var t = new DateTime(2026, 10, 22, 23, 55, 14, 123);
            string l = Line(t, "state", "{\"id\":1,\"ok\":true,\"data\":{\"ok\":false}}", 0.44, 12.4);
            if (l != "23:55:14.123 [Dev] cmd state -> ok exec_ms=0.4 wait_ms=12") return "line: " + l;
            if (Result("{\"id\":1,\"ok\":false,\"error\":\"bad_args\",\"detail\":\"x\"}") != "!bad_args") return "error code";
            if (Result(null) != "no_reply" || Result("{\"ok\":false}") != "error") return "edge cases";
            if (Timeout(t, "attack", "not executed") != "23:55:14.123 [Dev] cmd attack -> timeout (not executed)") return "timeout line";
            return null;
        }
    }

    /// <summary>Hand-over between the socket thread and the game main thread (Update): bounded work per frame, honest timeouts.</summary>
    internal sealed class DevPump
    {
        private readonly ConcurrentQueue<Pending> _q = new ConcurrentQueue<Pending>();
        private readonly Func<DevRequest, string> _exec;
        private readonly Func<long> _nowMs;
        private readonly Action<DevRequest, string> _onTimeout;
        private readonly Action<DevRequest, string, double, double> _onDone;

        /// <param name="onDone">Main thread, after each executed request: (request, reply, exec ms, queue wait ms). Must not throw (errors are swallowed).</param>
        public DevPump(Func<DevRequest, string> exec, Action<DevRequest, string> onTimeout = null, Func<long> nowMs = null, Action<DevRequest, string, double, double> onDone = null)
        {
            _exec = exec;
            _onTimeout = onTimeout;
            _onDone = onDone;
            if (nowMs == null) { var sw = Stopwatch.StartNew(); nowMs = () => sw.ElapsedMilliseconds; }
            _nowMs = nowMs;
        }

        public Pending Enqueue(DevRequest r)
        {
            var p = new Pending { Request = r, EnqueuedTicks = Stopwatch.GetTimestamp() };
            _q.Enqueue(p);
            return p;
        }

        /// <summary>Main thread. Runs up to maxN requests and stops after maxMs. Returns how many ran.</summary>
        public int Pump(int maxN = 4, int maxMs = 2)
        {
            long t0 = _nowMs();
            int n = 0;
            Pending p;
            while (n < maxN && _q.TryDequeue(out p))
            {
                if (Interlocked.CompareExchange(ref p.State, 1, 0) != 0) continue;   // cancelled by Await
                long s0 = Stopwatch.GetTimestamp();
                try { p.Reply = _exec(p.Request); }
                catch (Exception e) { p.Reply = DevProtocol.Err(p.Request.Id, "internal", e.GetType().Name + ": " + e.Message, null); }
                long s1 = Stopwatch.GetTimestamp();
                Volatile.Write(ref p.State, 2);
                p.Done.Set();
                if (_onDone != null)
                {
                    try { _onDone(p.Request, p.Reply, DevCmdLog.Ms(s1 - s0), DevCmdLog.Ms(s0 - p.EnqueuedTicks)); } catch { }
                }
                n++;
                if (_nowMs() - t0 >= maxMs) break;
            }
            return n;
        }

        private volatile bool _closed;

        /// <summary>T-220: on shutdown the waiting socket threads return at once (the main thread no longer pumps).</summary>
        public void Close() { _closed = true; }

        private bool WaitDone(Pending p, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (left <= 0) return p.Done.IsSet;
                if (p.Done.Wait(Math.Min(left, 50))) return true;
                if (_closed) return p.Done.IsSet;
            }
        }

        /// <summary>Socket thread. Waits for the main thread; on timeout either cancels the request (never executed) or reports an unknown outcome.</summary>
        public string Await(Pending p, int timeoutMs = 5000)
        {
            if (WaitDone(p, timeoutMs)) return p.Reply;
            if (Interlocked.CompareExchange(ref p.State, 3, 0) == 0)
            {
                if (_onTimeout != null) _onTimeout(p.Request, "not executed");
                return DevProtocol.Err(p.Request.Id, "timeout", "not executed", null);
            }
            if (WaitDone(p, Math.Max(250, timeoutMs / 2))) return p.Reply;
            if (_onTimeout != null) _onTimeout(p.Request, "outcome unknown");
            return DevProtocol.Err(p.Request.Id, "timeout", "outcome unknown", null);
        }
    }
}
