using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase B: the groups next to the game save (StrikeSave). The plan is not saved: after a load the group is "stale, loaded" and waits until the first tick.
    internal sealed partial class StrikeController
    {
        /// <summary>Groups of the moment as StrikeSave json; null = no groups (the slot is cleared by the caller).</summary>
        public string Export(SavStamp stamp = null)
        {
            if (_groups.Count == 0) return null;
            var d = new StrikeSaveData { MissionT = _g.Now, Sav = stamp };
            foreach (var g in _groups)
            {
                var sg = new StrikeSaveGroup { Name = g.Name, Seq = g.Seq, Mode = g.Target.Mode, MainId = g.Target.MainId, Goal = g.Goal };
                sg.Members.AddRange(g.Members); sg.Ships.AddRange(g.Target.Ships);
                if (g.WaitPt.HasValue) { sg.HasWait = true; sg.WaitLat = g.WaitPt.Value.Lat; sg.WaitLon = g.WaitPt.Value.Lon; }
                sg.AutoWas = g.Auto.On; sg.ClearWas = g.Clear != null;
                sg.Roe = EffectiveRoe;
                foreach (int uid in g.Members) { string pr; if (_prevRoe.TryGetValue(uid, out pr)) sg.RoePrev[uid] = pr; }
                foreach (var es in g.EwExec) if (es.On) sg.EwOn.Add(es.Uid);
                d.Groups.Add(sg);
            }
            string json = StrikeSave.ToJson(d);
            _log("[Strike] strike save n=" + I(_groups.Count));
            return json;
        }

        /// <summary>The file of the loaded save: the groups come back with their members that exist (claimed again), the target and the wait point; the plan is "stale, loaded". Returns "n=.." or null with err.</summary>
        public string Import(string json, out string err)
        {
            var d = StrikeSave.Parse(json, out err);
            if (d == null) return null;
            int n = 0;
            foreach (var sg in d.Groups)
            {
                if (Find(sg.Name) != null) continue;
                var g = new StrikeGroup { Name = sg.Name, Seq = sg.Seq > 0 ? sg.Seq : _seq + 1, Goal = sg.Goal };
                _seq = Math.Max(_seq, g.Seq);
                foreach (int uid in sg.Members)
                {
                    if (GroupOf(uid) != null || Air(uid) == null) continue;
                    if (Ownership != null && Ownership.Claim(uid, "strike", g.Name) != null) continue;
                    g.Members.Add(uid);
                }
                foreach (int uid in g.Members)
                {
                    string pr;
                    if (sg.RoePrev.TryGetValue(uid, out pr)) _prevRoe[uid] = pr;
                    else if (sg.Roe == "hold") { _prevRoe[uid] = "free"; _log("[Strike] strike roe restored_default uid=" + I(uid)); }   // review of C: an old save without the previous status
                }
                foreach (int uid in sg.EwOn) if (!_ewOffPending.Contains(uid)) _ewOffPending.Add(uid);
                g.Target = new StrikeTargetSet { Mode = sg.Mode, MainId = sg.MainId };
                g.Target.Ships.AddRange(sg.Ships);
                if (sg.HasWait) { g.WaitPt = new GeoPt(sg.WaitLat, sg.WaitLon); g.WaitR = _p.WaitOrbitNm; g.WaitWhy = "ok"; }
                g.Timeline = new StrikeTimeline { PlanId = 0, CreatedT = _g.Now, Status = "stale", StaleWhy = "loaded" };
                if (sg.AutoWas)
                {
                    AutoNote(g, "auto_off_loaded");
                    _log("[Strike] strike auto grp=" + g.Name + " wave=0 max=" + I(_p.AutoWaves) + " action=off why=loaded");
                }
                if (sg.ClearWas)
                {
                    AutoNote(g, "clear_loaded");
                    _log("[Strike] strike clear end grp=" + g.Name + " why=loaded");
                }
                g.DeckBlock = "loaded";   // T-342 B: the game keeps the orders of the saved session on the decks; the window does not know their keys, so no new order until "order again"
                _log("[Strike] strike deck block grp=" + g.Name + " why=loaded");
                if (Ownership != null) Ownership.RestoreSeq("strike", g.Name, g.Seq);
                _groups.Add(g);
                n++;
            }
            _log("[Strike] strike load n=" + I(n) + " stale=loaded");
            return "n=" + I(n);
        }
    }
}
