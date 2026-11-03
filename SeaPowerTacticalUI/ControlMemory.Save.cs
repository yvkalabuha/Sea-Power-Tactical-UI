using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-256: route part of ControlMemory kept in the mod's save (EvasionSave.cs). A partial of Common/ControlMemory.cs that only the UI mod compiles;
    // Common/ and the frozen VoiceBotBridge are not touched.
    internal sealed partial class ControlMemory
    {
        /// <summary>Route part + telegraph of the unit; null when the entry has no route.</summary>
        public EvMemRecord ExportRoute(object u, int uid, string name)
        {
            var e = Peek(u);
            if (e == null || !e.HasRoute) return null;
            var r = new EvMemRecord { Uid = uid, Name = name, CourseDeg = e.SavedCourseDeg };
            foreach (var p in e.SavedRoute) r.Route.Add(new[] { p[0], p[1] });
            if (e.PlacedByMod != null) r.PlacedByMod = new[] { e.PlacedByMod[0], e.PlacedByMod[1] };
            FieldMem f;
            if (e.Fields.TryGetValue("telegraph", out f)) { r.TgSaved = f.Saved; r.TgSetByMod = f.SetByMod; }
            return r;
        }

        /// <summary>Merge into the (possibly post-load) entry: route, course and telegraph.Saved come from the file;
        /// PlacedByMod / telegraph.SetByMod stay live when already set, else from the file. ROE and other fields are untouched.</summary>
        public void ImportRoute(object u, EvMemRecord r)
        {
            if (u == null || r == null) return;
            var e = Entry(u);
            e.HasRoute = true;
            e.SavedRoute = new List<double[]>();
            foreach (var p in r.Route) e.SavedRoute.Add(new[] { p[0], p[1] });
            e.SavedCourseDeg = r.CourseDeg;
            if (e.PlacedByMod == null && r.PlacedByMod != null) e.PlacedByMod = new[] { r.PlacedByMod[0], r.PlacedByMod[1] };
            if (r.TgSaved != null)
            {
                FieldMem f;
                if (!e.Fields.TryGetValue("telegraph", out f)) { f = new FieldMem(); e.Fields["telegraph"] = f; }
                f.Saved = r.TgSaved;
                if (f.SetByMod == null) f.SetByMod = r.TgSetByMod;
            }
        }
    }
}
