using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VoiceBotBridge
{
    /// <summary>T-125 (R2): the game part of FrameState: the Unity frame clock, the readers, the self-test and the allocation bench. UI part of GameApi only.</summary>
    internal sealed partial class GameApi
    {
        /// <summary>The Unity frame number for FrameState; UiRunner sets () => Time.frameCount before UiCore.Start. null = no per-frame cache.</summary>
        internal static Func<long> FrameClock;

        private readonly FrameState _frameState;

        /// <summary>The spec of the mission singleton (a field so the self-test can point it at a probe).</summary>
        private MemberSpec _omSpec = CoreSpecs.ObjectsManagerType;

        public GameApi()
        {
            _frameState = new FrameState(FrameNow, ReadObjectsManager, MissionTimeS, ReadUiScale);
        }

        /// <summary>The values of this frame (InMission, ObjectsManager, MissionTimeS, UiScale); created with the GameApi on the main thread.</summary>
        internal FrameState Frame { get { return _frameState; } }

        private static long FrameNow()
        {
            var c = FrameClock;
            return c == null ? -1 : c();
        }

        /// <summary>The old ObjectsManagerInstance: InstanceExists, then Instance, no liveness test.</summary>
        private object ReadObjectsManager() { return SingletonInstance(_omSpec); }

        // ---------- self-test (selfcheck ui, "FrameState") ----------

        public sealed class ProbeSingleton
        {
            public static int ExistsCalls, InstanceCalls;
            public static bool Alive = true;
            public static readonly ProbeSingleton Current = new ProbeSingleton();
            public static bool InstanceExists(bool create) { ExistsCalls++; return Alive; }
            public static ProbeSingleton Instance { get { InstanceCalls++; return Current; } }
        }

        public sealed class ProbeSingleton0
        {
            public static readonly ProbeSingleton0 Current = new ProbeSingleton0();
            public static bool InstanceExists() { return true; }
            public static ProbeSingleton0 Instance { get { return Current; } }
        }

        public sealed class ProbeNoExists
        {
            public static readonly ProbeNoExists Current = new ProbeNoExists();
            public static ProbeNoExists Instance { get { return Current; } }
        }

        /// <summary>InstanceExists(object) and a value-type Instance: neither fits the delegate shapes, so the MethodInfo/PropertyInfo fallback runs.</summary>
        public sealed class ProbeFallback
        {
            public static int ExistsCalls;
            public static bool InstanceExists(object create) { ExistsCalls++; return create is bool && !(bool)create; }
            public static int Instance { get { return 42; } }
        }

        public sealed class ProbeFieldHolder
        {
            public string Name = "n";
            public string Title { get; set; }
        }

        private static long _probeClock;

        private long Alloc() { return GC.GetAllocatedBytesForCurrentThread(); }

        /// <summary>null = ok.</summary>
        internal string SelfTestFrameState()
        {
            var saveClock = FrameClock; var saveSpec = _omSpec; var saveLog = Log;
            var lines = new List<string>();
            try
            {
                Log = lines.Add;
                if (Frame == null || !ReferenceEquals(Frame, Frame)) return "frame: Frame";
                var s = MemberSpec.TypeOf("t", typeof(ProbeSingleton).FullName);
                var s0 = MemberSpec.TypeOf("t", typeof(ProbeSingleton0).FullName);
                var sn = MemberSpec.TypeOf("t", typeof(ProbeNoExists).FullName);
                var sf = MemberSpec.TypeOf("t", typeof(ProbeFallback).FullName);
                var sx = MemberSpec.TypeOf("t", "No.Such.Type.T125");
                FrameClock = null;
                ProbeSingleton.Alive = true;
                if (!ReferenceEquals(Singleton(s), ProbeSingleton.Current)) return "singleton: instance";
                if (!ReferenceEquals(SingletonInstance(s), ProbeSingleton.Current)) return "singleton: SingletonInstance";
                ProbeSingleton.Alive = false;
                if (Singleton(s) != null || SingletonInstance(s) != null) return "singleton: InstanceExists=false must give null";
                ProbeSingleton.Alive = true;
                if (!ReferenceEquals(Singleton(s0), ProbeSingleton0.Current)) return "singleton: InstanceExists() without argument";
                if (!ReferenceEquals(Singleton(sn), ProbeNoExists.Current)) return "singleton: no InstanceExists";
                if (!Equals(Singleton(sf), 42) || ProbeFallback.ExistsCalls < 1) return "singleton: MethodInfo/PropertyInfo fallback";
                if (Singleton(sx) != null || SingletonInstance(sx) != null || Singleton(null) != null) return "singleton: a missing type must give null";
                // one read per frame
                _probeClock = 10; FrameClock = () => _probeClock;
                ProbeSingleton.InstanceCalls = 0; ProbeSingleton.ExistsCalls = 0;
                for (int i = 0; i < 100; i++) Singleton(s);
                if (ProbeSingleton.InstanceCalls != 1 || ProbeSingleton.ExistsCalls != 1) return "frame: 100 calls in one frame read " + ProbeSingleton.InstanceCalls + " times";
                _probeClock = 11;
                for (int i = 0; i < 100; i++) Singleton(s);
                if (ProbeSingleton.InstanceCalls != 2) return "frame: a new frame must read again";
                FrameClock = null;
                ProbeSingleton.InstanceCalls = 0;
                for (int i = 0; i < 5; i++) Singleton(s);
                if (ProbeSingleton.InstanceCalls != 5) return "frame: without a clock every call reads";
                // Frame wiring: ObjectsManager / InMission through the singleton cache
                _omSpec = s;
                _probeClock = 20; FrameClock = () => _probeClock;
                Frame.Invalidate(); ProbeSingleton.InstanceCalls = 0;
                for (int i = 0; i < 50; i++) { if (!Frame.InMission || !ReferenceEquals(Frame.ObjectsManager, ProbeSingleton.Current)) return "frame: Frame.ObjectsManager"; }
                if (ProbeSingleton.InstanceCalls != 1) return "frame: Frame read the singleton " + ProbeSingleton.InstanceCalls + " times";
                _probeClock = 21; ProbeSingleton.Alive = false;
                if (Frame.InMission) return "frame: Frame.InMission must follow InstanceExists in the next frame";
                ProbeSingleton.Alive = true;
                _omSpec = sx; Frame.Invalidate();
                if (Frame.InMission) return "frame: a missing type = not in mission";
                _omSpec = s;
                FrameClock = null;
                ProbeSingleton.InstanceCalls = 0; Frame.Invalidate();
                for (int i = 0; i < 4; i++) { var x = Frame.InMission; }
                if (ProbeSingleton.InstanceCalls != 4) return "frame: no clock = read through";
                // allocations (after the warm-up)
                _probeClock = 100; FrameClock = () => _probeClock++;
                for (int i = 0; i < 50; i++) { Singleton(s); SingletonInstance(s); }
                long b0 = Alloc();
                for (int i = 0; i < 1000; i++) Singleton(s);
                long singletonBytes = Alloc() - b0;
                if (singletonBytes != 0) return "alloc: Singleton(spec) allocated " + singletonBytes + " bytes in 1000 calls";
                var holder = new ProbeFieldHolder { Title = "t" };
                var fSpec = MemberSpec.Field("t", typeof(ProbeFieldHolder).FullName, "Name");
                var pSpec = MemberSpec.Prop("t", typeof(ProbeFieldHolder).FullName, "Title");
                for (int i = 0; i < 50; i++) { Get(holder, fSpec); Get(holder, pSpec); }
                long b1 = Alloc();
                for (int i = 0; i < 1000; i++) { Get(holder, fSpec); Get(holder, pSpec); }
                long getBytes = Alloc() - b1;
                if (getBytes != 0) return "alloc: Get(obj, spec) allocated " + getBytes + " bytes in 2000 calls";
                return null;
            }
            catch (Exception e) { return "frame: " + e.GetType().Name + ": " + e.Message; }
            finally { FrameClock = saveClock; _omSpec = saveSpec; Log = saveLog; Frame.Invalidate(); }
        }

        /// <summary>Informational: bytes and ns per call, old string path against the new one.</summary>
        internal string FrameStateBenchLine()
        {
            var saveClock = FrameClock;
            try
            {
                const int N = 2000;
                string name = typeof(ProbeSingleton).FullName;
                var s = MemberSpec.TypeOf("t", name);
                var fSpec = MemberSpec.Field("t", typeof(ProbeFieldHolder).FullName, "Name");
                var holder = new ProbeFieldHolder();
                FrameClock = () => _probeClock++;
                for (int i = 0; i < 100; i++) { SingletonInstance(name); Singleton(s); Get(holder, fSpec); }
                var sw = new Stopwatch();
                long b = Alloc(); sw.Restart();
                for (int i = 0; i < N; i++) SingletonInstance(name);
                sw.Stop(); long oldB = (Alloc() - b) / N; long oldNs = sw.ElapsedTicks * 1000000000L / Stopwatch.Frequency / N;
                b = Alloc(); sw.Restart();
                for (int i = 0; i < N; i++) Singleton(s);
                sw.Stop(); long newB = (Alloc() - b) / N; long newNs = sw.ElapsedTicks * 1000000000L / Stopwatch.Frequency / N;
                b = Alloc();
                for (int i = 0; i < N; i++) Get(holder, fSpec);
                long getB = (Alloc() - b) / N;
                FrameClock = () => 7;
                Frame.Invalidate(); var warm = Frame.InMission;
                b = Alloc();
                for (int i = 0; i < N; i++) { var x = Frame.InMission; }
                long hitB = (Alloc() - b) / N;
                return "singleton_old=" + oldB + "B/call singleton_new=" + newB + "B/call get=" + getB + "B/call frame_hit=" + hitB + "B/call ns_old=" + oldNs + " ns_new=" + newNs;
            }
            catch (Exception e) { return "bench failed: " + e.GetType().Name + ": " + e.Message; }
            finally { FrameClock = saveClock; Frame.Invalidate(); }
        }
    }
}
