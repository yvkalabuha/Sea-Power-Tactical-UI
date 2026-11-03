// T-311 A1: budget sections of MapPx (a share of the frame budget per layer), GridCorner, AddVerts.
using System;

namespace VoiceBotBridge.ProjTest
{
    internal static class PxBudgetCases
    {
        private static MapPx Tactical()
        {
            MapViewSnap s = MapViewSnap.Ortho(12.94, 43.11, GameRef.ZoomLevelToScale(14), 0, 12.94, 43.11, 960, 540, 1920, 1080);
            return MapPx.Create(in s, new double[] { 1, 0, 0, 0, 1, 0 }, 1920, 1080);
        }

        public static string Run()
        {
            double[] id = { 1, 0, 0, 0, 1, 0 };
            MapPx px = Tactical();
            // section: Arc is cut at the share
            px.BeginSection(100);
            if (px.Limit != 100) return "Limit in section " + px.Limit;
            var sk = new RecSink();
            px.Arc(12.94, 43.11, 300, 0, 360, 2000, sk);
            if (px.SectionVerts - px.SectionCut != 100 || px.SectionCut <= 0 || !px.OverBudget || px.Remaining != 0) return "section cut: verts " + px.Verts + " cut " + px.SectionCut;
            px.EndSection();
            if (px.Limit != px.VertexBudget || px.SectionVerts != px.Verts || px.SectionCut <= 0) return "EndSection keeps the numbers";
            var s2 = new RecSink();
            px.Arc(12.94, 43.11, 2, 0, 360, 24, s2);
            if (s2.Lines.Count <= 0) return "ring after the section got no lines";
            // a section after some usage: the share counts from the current Verts
            int v0 = px.Verts;
            px.BeginSection(50);
            if (px.Limit != v0 + 50 || px.SectionVerts != 0 || px.SectionCut != 0) return "section after usage " + px.Limit;
            px.AddVerts(4);
            if (px.SectionVerts != 4 || px.Remaining != 46) return "AddVerts/Remaining";
            px.EndSection();
            // BeginSection(0) = no limit; ResetCounters drops the section
            px.ResetCounters();
            px.BeginSection(0);
            if (px.Limit != px.VertexBudget) return "BeginSection(0)";
            px.BeginSection(10);
            px.ResetCounters();
            if (px.Limit != px.VertexBudget || px.Verts != 0 || px.Remaining != px.VertexBudget) return "ResetCounters keeps the section";
            // Share
            if (px.Share(0.6) != 36000 || px.Share(-1) != 0) return "Share ortho " + px.Share(0.6);
            MapViewSnap s = MapViewSnap.Ortho(12.94, 43.11, GameRef.ZoomLevelToScale(14), 0, 12.94, 43.11, 960, 540, 1920, 1080);
            MapPx pr = MapPx.Reflect(in s, delegate (double la, double lo, out double ox, out double oy) { return MapProjector.GeoToView(in s, la, lo, out ox, out oy); }, id, 1920, 1080);
            if (pr.Kind != MapProjKind.Reflect || pr.Share(0.6) != 2400) return "Share reflect " + pr.Share(0.6);
            // GridCorner = GridPt, but does not count
            MapViewSnap sg = MapViewSnap.Ortho(10, 20, GameRef.ZoomLevelToScale(8), 33, 10, 20, 960, 540, 1920, 1080);
            MapPx pg = MapPx.Create(in sg, id, 1920, 1080);
            pg.PrepareGrid(15, 0.5, 20, 15, 0.5, 20);
            for (int r = 0; r <= 21; r++)
                for (int c = 0; c <= 21; c++)
                {
                    int before = pg.Verts;
                    double cx, cy, gx, gy;
                    bool cv = pg.GridCorner(r, c, out cx, out cy);
                    if (pg.Verts != before) return "GridCorner counted";
                    bool gv = pg.GridPt(r, c, out gx, out gy);
                    if (cv != gv || cx != gx || cy != gy) return "GridCorner != GridPt " + r + "," + c;
                    int add = (r > 20 || c > 20) ? 0 : 1;
                    if (pg.Verts != before + add) return "GridPt count";
                }
            // section limit also applies to the Reflect / over-budget check with the budget itself
            MapPx pm = Tactical();
            pm.VertexBudget = 30;
            pm.BeginSection(100);
            if (pm.Limit != 30) return "Limit = min";
            return null;
        }
    }
}
