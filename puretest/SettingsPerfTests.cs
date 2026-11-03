// T-239: allocation and scan counters of one pass of the settings window body + footer on the stand (NullPainter, fixture of every key).
using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal static partial class SettingsTests
    {
        private sealed class PerfEnv
        {
            public UiContext.TestHost Host = new UiContext.TestHost();
            public UiSettingsRegistry Reg;
            public SettingsView V;
            public UiKit K;
            public MemState S = new MemState();
            public float W = 560f;

            public PerfEnv(bool anyOpen, string only)
            {
                Reg = SettingsFixture.Build(Host);
                V = new SettingsView { Reg = Reg, Draft = new SettingsDraft(Reg), Rec = new HotkeyRecorder() };
                K = new UiKit(new NullPainter(), UiTheme.For(1.0), S, "settings");
                foreach (var id in Reg.Sections())
                    S.Open["settings." + id] = anyOpen && (only == null || only == id);
            }

            /// <summary>Which part of the pass runs: 0 = all, 1 = body only, 2 = footer only, 3 = kit frame only (profiling of the thresholds).</summary>
            public int Part;

            public void Pass()
            {
                if (Part != 0)
                {
                    K.Begin(W);
                    if (Part == 1) SettingsWindowBody.Draw(K, V);
                    else if (Part == 2) SettingsWindowBody.DrawFooter(K, V);
                    K.End();
                    return;
                }
                SettingsWindowBody.BeginPass(V);
                K.Begin(W);
                SettingsWindowBody.Draw(K, V);
                SettingsWindowBody.DrawFooter(K, V);
                K.End();
            }

            public UiSettingEntry E(string full)
            {
                foreach (var e in Reg.All) if (e.FullKey == full) return e;
                throw new InvalidOperationException("no key " + full);
            }
        }

        private static long Bytes() { return GC.GetAllocatedBytesForCurrentThread(); }

        private static long PerPass(PerfEnv e, int passes)
        {
            for (int i = 0; i < 3; i++) e.Pass();
            GC.Collect();
            long b0 = Bytes();
            for (int i = 0; i < passes; i++) e.Pass();
            return (Bytes() - b0) / passes;
        }

        /// <summary>Thresholds of the plan T-239 section 4 step 1.</summary>
        public static string Perf()
        {
            using (UiText.Push("ru"))
            {
                var a = new PerfEnv(false, null);
                long ba = PerPass(a, 20);
                var b = new PerfEnv(true, "radar");
                long bb = PerPass(b, 20);
                var c = new PerfEnv(true, null);
                long bc = PerPass(c, 20);
                Console.WriteLine("settings_perf bytes/pass: collapsed=" + ba + " radar=" + bb + " all=" + bc);
                if (Environment.GetEnvironmentVariable("T239_PROFILE") != null)
                    foreach (var id in c.Reg.Sections())
                    {
                        var pe = new PerfEnv(true, id);
                        Console.WriteLine("settings_perf   section " + id + ": " + PerPass(pe, 20) + " B/pass, rows " + SettingsWindowBody.Rows(pe.Reg, id, false).Count);
                    }
                foreach (int part in new[] { 1, 2, 3 })
                {
                    var pe = new PerfEnv(false, null) { Part = part };
                    Console.WriteLine("settings_perf   collapsed part " + part + " (1 body, 2 footer, 3 frame): " + PerPass(pe, 20) + " B/pass");
                }
                if (ba > 256) return "settings_perf: all collapsed " + ba + " B/pass (limit 256); radar=" + bb + " all=" + bc;
                if (bb > 4096) return "settings_perf: radar open " + bb + " B/pass (limit 4096); collapsed=" + ba + " all=" + bc;
                if (bc > 32768) return "settings_perf: all open " + bc + " B/pass (limit 32768); collapsed=" + ba + " radar=" + bb;
                return PerfCounters(c);
            }
        }

        /// <summary>No scan and no rebuild without edits; an edit costs O(edits) per pass.</summary>
        private static string PerfCounters(PerfEnv e)
        {
            for (int i = 0; i < 3; i++) e.Pass();
            int scans = SettingsDraft.PruneScans, rebuilds = e.V.Cache.Rebuilds;
            for (int i = 0; i < 20; i++) e.Pass();
            if (SettingsDraft.PruneScans != scans) return "settings_perf: Prune scanned " + (SettingsDraft.PruneScans - scans) + " entries in 20 passes without edits";
            if (e.V.Cache.Rebuilds != rebuilds) return "settings_perf: the layout was rebuilt " + (e.V.Cache.Rebuilds - rebuilds) + " times in 20 passes";
            e.V.Draft.Edit(e.E("Overview.ShowOnStart"), !(bool)e.E("Overview.ShowOnStart").Box.Get());
            scans = SettingsDraft.PruneScans; rebuilds = e.V.Cache.Rebuilds;
            for (int i = 0; i < 20; i++) e.Pass();
            if (SettingsDraft.PruneScans - scans > 20) return "settings_perf: Prune scanned " + (SettingsDraft.PruneScans - scans) + " entries in 20 passes with one edit";
            if (e.V.Cache.Rebuilds != rebuilds) return "settings_perf: an edit rebuilt the layout";
            // a language change rebuilds the layout once, with new texts; ShowAdvanced too
            int r0 = e.V.Cache.Rebuilds;
            string before = e.V.Cache.ResetText;
            using (UiText.Push("en"))
            {
                e.Pass();
                if (e.V.Cache.Rebuilds != r0 + 1 || e.V.Cache.ResetText == before) return "settings_perf: a language change rebuilds once (" + (e.V.Cache.Rebuilds - r0) + ")";
                e.Pass();
                if (e.V.Cache.Rebuilds != r0 + 1) return "settings_perf: no rebuild without a change";
            }
            e.Pass();
            e.V.ShowAdvanced = !e.V.ShowAdvanced;
            int r1 = e.V.Cache.Rebuilds;
            e.Pass();
            if (e.V.Cache.Rebuilds != r1 + 1) return "settings_perf: ShowAdvanced rebuilds once";
            return null;
        }
    }
}
