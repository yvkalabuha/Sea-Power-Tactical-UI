// T-178 Kf2 (ADR-0077 §3): UiKit.ShipPick: "[icon Name · 4,3 мили v] [⌖ На карте]". The list is a Dropdown of UiItem (NTDS icon, name, distance on the right);
// the button raises mapPick (the window starts UiMapPick). Only own ships are listed (ShipPickModel.Sorted). An empty list is a grey button with the reason.
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed partial class UiKit
    {
        /// <summary>Returns the uid chosen (selectedUid while nothing changes). mapPick = the "On the map" button was pressed in this event.</summary>
        public int ShipPick(string id, IList<ShipItem> items, int selectedUid, string tip, out bool mapPick)
        {
            var sorted = ShipPickModel.Sorted(items);
            int result = selectedUid;
            if (sorted.Count == 0)
            {
                Btn(CtlRect(Place(BtnWidth(UiText.Get("ui.ship.none"), 0.6f * (_right - _lineStart)))), id, UiText.Get("ui.ship.none"), UiText.Get("ui.ship.none"), false, false, UiTone.Normal);
            }
            else
            {
                var list = new List<UiItem>(sorted.Count);
                int sel = -1;
                for (int i = 0; i < sorted.Count; i++)
                {
                    var s = sorted[i];
                    if (s.Uid == selectedUid) sel = i;
                    list.Add(UiItem.Of(s.Name, s.DistNm < 1e8 ? UnitFmt.Nm(s.DistNm) : null, s.Icon));   // double.MaxValue = the distance is not known
                }
                int pick = Dropdown(id, list, sel, tip);
                if (pick != sel && pick >= 0 && pick < sorted.Count) result = sorted[pick].Uid;
            }
            string mapText = "⌖ " + UiText.Get("ui.ship.map");
            mapPick = Btn(CtlRect(Place(BtnWidth(mapText, 0.6f * (_right - _lineStart)))), id + ".map", mapText, UiText.Get("ui.ship.map.tip"), true, false, UiTone.Normal);
            return result;
        }
    }
}
