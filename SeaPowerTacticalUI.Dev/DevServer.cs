using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VoiceBotBridge.Dev
{
    /// <summary>Loopback-only line server (127.0.0.1 only, up to maxClients clients (default 2: a long-lived poller plus MCP, T-227), token per request, 64 KB per line, idle disconnect).</summary>
    internal sealed class DevServer
    {
        private readonly int[] _ports;
        private readonly string _token;
        private readonly Func<DevRequest, string> _submit;
        private readonly Func<string> _hello;
        private readonly TimeSpan _idle;
        private readonly int _maxLine;
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _stop;
        private readonly int _maxClients;
        private int _clients;                                   // taken slots (0.._maxClients)
        private readonly object _clientLock = new object();
        private readonly System.Collections.Generic.List<TcpClient> _active = new System.Collections.Generic.List<TcpClient>();
        private readonly System.Collections.Generic.List<Thread> _clientThreads = new System.Collections.Generic.List<Thread>();
        private readonly Action<string> _warn;
        private int _connNo;
        private long _lastWarnTicks;
        private readonly System.Collections.Generic.Queue<TcpClient> _q = new System.Collections.Generic.Queue<TcpClient>();
        private int _clientThreadsCreated;
        private volatile string _acceptExit = "";

        /// <summary>T-282: the accept loop wakes this often to look at the stop flag (no blocking Accept: Stop does not depend on a socket close).</summary>
        public const int AcceptPollMs = 250;

        public int Port { get; private set; }

        /// <summary>T-282: how the accept loop ended: "poll" (stop flag seen), "closed" (listener closed under it), "error". Empty while running.</summary>
        public string AcceptExit { get { return _acceptExit; } }

        /// <summary>T-282: client threads created so far (a fixed pool made in Start, never one per connection).</summary>
        public int ClientThreadsCreated { get { return Volatile.Read(ref _clientThreadsCreated); } }

        public DevServer(int[] ports, string token, Func<DevRequest, string> submit, Func<string> hello, TimeSpan idle, int maxLine = 65536, Action<string> warn = null, int maxClients = 2)
        {
            _ports = ports; _token = token; _submit = submit; _hello = hello; _idle = idle; _maxLine = maxLine; _warn = warn; _maxClients = Math.Max(1, maxClients);
        }

        /// <summary>T-198: an empty line (stray newline, "\r\n", a BOM) is not a request: no answer, no bad_json.</summary>
        public static bool IsBlank(string line)
        {
            return string.IsNullOrEmpty(line) || line.Trim('\uFEFF', ' ', '\t', '\r', '\n').Length == 0;
        }

        private static readonly Regex TokenValue = new Regex("(\"token\"\\s*:\\s*\")[^\"]*", RegexOptions.Compiled);

        /// <summary>T-198: the start of a line that did not parse, safe for the log: control characters escaped, the token value masked.</summary>
        public static string MaskHead(string line, int max = 60)
        {
            string masked = TokenValue.Replace(line ?? "", "$1***");
            var sb = new StringBuilder();
            foreach (char ch in masked)
            {
                if (sb.Length >= max) { sb.Append("..."); break; }
                if (ch == '\n') sb.Append("\\n"); else if (ch == '\r') sb.Append("\\r"); else if (ch == '\t') sb.Append("\\t");
                else if (ch < 0x20 || ch == 0x7f || ch == '\uFEFF') sb.Append("\\x").Append(((int)ch).ToString("x2"));
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        private void Warn(string text)
        {
            if (_warn == null) return;
            long now = DateTime.UtcNow.Ticks;
            long prev = Interlocked.Read(ref _lastWarnTicks);
            if (prev != 0 && now - prev < TimeSpan.TicksPerSecond) return;   // at most one line per second
            Interlocked.Exchange(ref _lastWarnTicks, now);
            try { _warn(text); } catch (Exception) { }
        }

        /// <summary>Tries the ports in order (0 = any free port). False when none could be bound.</summary>
        public bool Start()
        {
            foreach (int port in _ports)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, port);
                    l.Start(1);
                    _listener = l;
                    Port = ((IPEndPoint)l.LocalEndpoint).Port;
                    for (int i = 0; i < _maxClients; i++)
                    {
                        var w = new Thread(ClientWorkerTagged) { IsBackground = true, Name = "sptui-dev-client" };
                        lock (_clientLock) _clientThreads.Add(w);
                        Interlocked.Increment(ref _clientThreadsCreated);
                        w.Start();
                    }
                    _thread = new Thread(AcceptLoopTagged) { IsBackground = true, Name = "sptui-dev-accept" };
                    _thread.Start();
                    return true;
                }
                catch (SocketException) { }
            }
            return false;
        }

        private void CloseAllClients()
        {
            lock (_clientLock) foreach (var c in _active) { try { c.Close(); } catch (Exception) { } }
        }

        private Thread[] JoinList()
        {
            var l = new System.Collections.Generic.List<Thread> { _thread };
            lock (_clientLock) l.AddRange(_clientThreads);
            return l.ToArray();
        }

        /// <summary>T-220/T-282: raises the stop flag and wakes the workers, lets the accept loop leave by its poll timeout, then closes the sockets and joins every thread. False = a thread is still alive after the budget.</summary>
        public bool Stop(int timeoutMs = 1000)
        {
            _stop = true;
            lock (_q) Monitor.PulseAll(_q);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { if (_thread != null) _thread.Join(AcceptPollMs + 200); } catch (Exception) { }
            try { if (_listener != null) _listener.Stop(); } catch (Exception) { }
            lock (_q) { foreach (var qc in _q) { try { qc.Close(); } catch (Exception) { } } _q.Clear(); Monitor.PulseAll(_q); }
            CloseAllClients();
            int rest = (int)Math.Max(50, timeoutMs - sw.ElapsedMilliseconds);
            var left = VoiceBotBridge.Ui.ModShutdown.JoinAll(JoinList(), rest);
            // a client accepted while we were stopping: its socket is closed again and the threads get one more short join
            if (left.Count > 0)
            {
                CloseAllClients();
                lock (_q) Monitor.PulseAll(_q);
                left = VoiceBotBridge.Ui.ModShutdown.JoinAll(JoinList(), 100);
            }
            return left.Count == 0;
        }

        private void AcceptLoopTagged() { VoiceBotBridge.Ui.ModThreads.Enter("sptui-dev-accept"); try { AcceptLoop(); } finally { VoiceBotBridge.Ui.ModThreads.Exit(); } }   // T-212

        private void ClientWorkerTagged() { VoiceBotBridge.Ui.ModThreads.Enter("sptui-dev-client"); try { ClientWorker(); } finally { VoiceBotBridge.Ui.ModThreads.Exit(); } }

        /// <summary>T-282: a pooled worker: takes accepted sockets from the queue, serves one connection at a time.</summary>
        private void ClientWorker()
        {
            while (true)
            {
                TcpClient c;
                lock (_q)
                {
                    while (_q.Count == 0 && !_stop) Monitor.Wait(_q, AcceptPollMs);
                    if (_q.Count == 0) return;   // stop and nothing left
                    c = _q.Dequeue();
                }
                if (_stop) { try { c.Close(); } catch (Exception) { } lock (_clientLock) _active.Remove(c); Interlocked.Decrement(ref _clients); return; }
                Serve(c, Interlocked.Increment(ref _connNo));
            }
        }

        private void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try
                {
                    // T-282: no blocking Accept; wake by timeout so Stop never needs a socket close to get us out
                    bool ready = _listener.Server.Poll(AcceptPollMs * 1000, SelectMode.SelectRead);
                    if (_stop) { _acceptExit = "poll"; return; }
                    if (!ready) continue;
                    c = _listener.AcceptTcpClient();
                }
                catch (ObjectDisposedException) { _acceptExit = "closed"; return; }
                catch (SocketException) { _acceptExit = _stop ? "closed" : "error"; return; }
                catch (Exception) { _acceptExit = "error"; return; }
                // a previous client may have just disconnected: give its thread a moment to release the slot
                bool taken = TakeSlot();
                for (int i = 0; !taken && i < 20 && !_stop; i++)
                {
                    Thread.Sleep(25);
                    taken = TakeSlot();
                }
                if (!taken)
                {
                    try { Send(c.GetStream(), DevProtocol.Err(null, "busy", _maxClients == 1 ? "one client at a time" : _maxClients + " clients at a time", null)); } catch (Exception) { }
                    try { c.Close(); } catch (Exception) { }
                    continue;
                }
                if (_stop) { try { c.Close(); } catch (Exception) { } Interlocked.Decrement(ref _clients); _acceptExit = "poll"; return; }
                lock (_clientLock) _active.Add(c);
                lock (_q) { _q.Enqueue(c); Monitor.Pulse(_q); }
            }
        }

        private bool TakeSlot()
        {
            while (true)
            {
                int cur = Volatile.Read(ref _clients);
                if (cur >= _maxClients) return false;
                if (Interlocked.CompareExchange(ref _clients, cur + 1, cur) == cur) return true;
            }
        }

        private static void Send(Stream s, string line)
        {
            var b = Encoding.UTF8.GetBytes(line + "\n");
            s.Write(b, 0, b.Length);
            s.Flush();
        }

        private void Serve(TcpClient c, int connNo)
        {
            try
            {
                c.ReceiveTimeout = (int)Math.Max(100, _idle.TotalMilliseconds);
                c.SendTimeout = 10000;
                var s = c.GetStream();
                Send(s, _hello());
                var buf = new byte[4096];
                var line = new MemoryStream();
                while (!_stop)
                {
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i] == (byte)'\n')
                        {
                            string text = Encoding.UTF8.GetString(line.ToArray());
                            line.SetLength(0);
                            string reply = Respond(text, connNo);
                            if (reply != null) Send(s, reply);
                        }
                        else
                        {
                            line.WriteByte(buf[i]);
                            if (line.Length > _maxLine)
                            {
                                Send(s, DevProtocol.Err(null, "line_too_long", null, null));
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                try { c.Close(); } catch (Exception) { }
                lock (_clientLock) _active.Remove(c);
                Interlocked.Decrement(ref _clients);
            }
        }

        private string Respond(string line, int connNo)
        {
            if (IsBlank(line)) return null;
            DevRequest req; string err;
            if (!DevProtocol.TryParse(line.TrimEnd('\r').TrimStart('\uFEFF'), out req, out err))
            {
                Warn("[Dev] bad line conn=#" + connNo + " len=" + line.Length + " head=" + MaskHead(line));
                return DevProtocol.Err(req == null ? null : req.Id, err, null, null);
            }
            if (!TokenOk(req.Token)) return DevProtocol.Err(req.Id, "unauthorized", null, null);
            try { return _submit(req); }
            catch (Exception e) { return DevProtocol.Err(req.Id, "internal", e.GetType().Name + ": " + e.Message, null); }
        }

        private bool TokenOk(string t)
        {
            if (t == null || t.Length != _token.Length) return false;
            int diff = 0;
            for (int i = 0; i < t.Length; i++) diff |= t[i] ^ _token[i];
            return diff == 0;
        }
    }
}
