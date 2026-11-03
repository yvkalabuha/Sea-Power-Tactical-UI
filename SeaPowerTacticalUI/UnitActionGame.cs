using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-234 (ADR-0096): the game side of the "Orders:" label. Main thread. Writes only OrderFeedbackText of the player's own units (through RxSet),
    /// reads CurrentOrderText (smart mode) and never writes it. The label sources (controllers) do not call anything here.
    /// </summary>
    internal sealed partial class GameApi
    {
        private readonly Dictionary<int, object> _uaUnits = new Dictionary<int, object>();
        private readonly HashSet<int> _uaWarned = new HashSet<int>();
        private HashSet<string> _uaPlain;
        private int _uaPlainRev = -1;

        /// <summary>One pass over the lists of the player's taskforce: uid -> object for the tick. Units of the allied taskforce are not touched.</summary>
        internal void UaBeginTick()
        {
            _uaUnits.Clear();
            var tf = GetStatic(CoreSpecs.PlayerTaskforce);
            if (tf == null) return;
            foreach (var listSpec in UnitActionSpecs.Lists)
            {
                var list = Get(tf, listSpec) as IEnumerable;
                if (list == null) continue;
                foreach (var u in list)
                {
                    if (u == null) continue;
                    var id = D(Get(u, UnitActionSpecs.ObjUniqueId));
                    if (id.HasValue) _uaUnits[(int)id.Value] = u;
                }
            }
        }

        /// <summary>The current OrderFeedbackText of an own unit of this tick; null = no such unit (deck row, destroyed, not own).</summary>
        internal string UaFeedback(int uid)
        {
            object u;
            if (!_uaUnits.TryGetValue(uid, out u)) return null;
            try { return (RxGetOwn(u, UnitActionSpecs.Feedback) as string) ?? ""; }
            catch (Exception) { return null; }
        }

        /// <summary>Writes OrderFeedbackText through RxSet. false on any error (logged once per uid); the caller then reports Failed.</summary>
        internal bool UaSetFeedback(int uid, string text)
        {
            object u;
            if (!_uaUnits.TryGetValue(uid, out u)) return false;
            try { RxSet(u, UnitActionSpecs.Feedback, text ?? ""); return true; }
            catch (Exception e)
            {
                if (_uaWarned.Add(uid)) { try { Log("[UnitAction] write failed uid=" + uid + ": " + e.GetType().Name + ": " + e.Message); } catch (Exception) { } }
                return false;
            }
        }

        /// <summary>The game's own order text (CurrentOrderText) of an own unit of this tick, "" when unknown.</summary>
        internal string UaGameText(int uid)
        {
            object u;
            if (!_uaUnits.TryGetValue(uid, out u) || !Present(UnitActionSpecs.OrderText)) return "";
            try { return (RxGetOwn(u, UnitActionSpecs.OrderText) as string) ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>Game texts that say nothing beyond "moving / waiting" (they do not matter next to the mod label), on the current language. Rebuilt when the language changes.</summary>
        internal ICollection<string> UaPlainTexts()
        {
            if (_uaPlain != null && _uaPlainRev == UiText.Revision) return _uaPlain;
            var set = new HashSet<string> { "" };
            var look = UiText.GameLookup;
            if (look != null)
            {
                string[][] keys =
                {
                    new[] { "Status", "Default" }, new[] { "Status", "Default_NoMovement" }, new[] { "Status", "Default_CourseChange" }, new[] { "Status", "Default_Idle" },
                    new[] { "Status", "Loitering" }, new[] { "Status", "MovingInFormation" }, new[] { "Status", "JoiningFormation" }, new[] { "Status", "FormationLeader" },
                    new[] { "BottomRow", "StatusLoitering" },
                };
                foreach (var k in keys)
                {
                    try { var t = look(k[0], k[1]); if (!string.IsNullOrEmpty(t) && !UiText.IsMissing(t, k[1])) set.Add(t.Trim()); } catch (Exception) { }
                }
            }
            _uaPlain = set; _uaPlainRev = UiText.Revision;
            return set;
        }

        /// <summary>Mission leave: forget the per-mission caches.</summary>
        internal void UaLeaveMission() { _uaUnits.Clear(); _uaWarned.Clear(); }
    }
}
