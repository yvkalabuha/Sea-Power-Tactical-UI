// T-148 U2 / T-155 K1: metrics of the mod windows (ui-mod-window-layout.md §3, ADR-0052). Pure: no Unity types; colours live in UnityPainter.
// Two sets: Compact (default, dense) and Normal (the ADR-0049 numbers, [Ui] Density = normal).
using System;

namespace VoiceBotBridge
{
    internal enum UiDensity { Compact, Normal }

    internal sealed class UiTheme
    {
        public float S, RowH, CtlH, TableRowH, SectionH, Pad, Gap, SectionGap, LabelMin, LabelMax, NumW, BtnPad, BtnMin;
        public float TitleH, HintH, ScrollW, IconW;
        public int FontBody, FontSmall, FontTitle, FontSection;
        public UiDensity Density;

        private static float Px(double v, double s) { return (float)Math.Round(v * s, MidpointRounding.AwayFromZero); }
        private static int Font(int px, double s) { return Math.Max(10, (int)Math.Round(px * s, MidpointRounding.AwayFromZero)); }

        public static UiTheme For(double s, UiDensity d = UiDensity.Compact)
        {
            if (!(s > 0.1) || double.IsInfinity(s)) s = 1.0;
            s = UiScale.Windows(s);   // T-178 Kd2: windows never scale below 0.85
            if (d == UiDensity.Normal)
                return new UiTheme
                {
                    S = (float)s, Density = d,
                    RowH = Px(24, s), CtlH = Px(20, s), TableRowH = Px(20, s), SectionH = Px(20, s), TitleH = Px(24, s), HintH = Px(24, s),
                    Pad = Px(6, s), Gap = Px(4, s), SectionGap = Px(6, s),
                    LabelMin = Px(84, s), LabelMax = Px(140, s), NumW = Px(44, s), BtnPad = Px(10, s), BtnMin = Px(56, s), ScrollW = Px(14, s), IconW = Px(20, s),
                    FontBody = Font(13, s), FontSmall = Font(12, s), FontTitle = Font(14, s), FontSection = Font(13, s),
                };
            return new UiTheme
            {
                S = (float)s, Density = d,
                RowH = Px(20, s), CtlH = Px(18, s), TableRowH = Px(16, s), SectionH = Px(18, s), TitleH = Px(20, s), HintH = Px(18, s),
                Pad = Px(4, s), Gap = Px(3, s), SectionGap = Px(3, s),
                LabelMin = Px(64, s), LabelMax = Px(110, s), NumW = Px(34, s), BtnPad = Px(6, s), BtnMin = Px(36, s), ScrollW = Px(10, s), IconW = Px(18, s),
                FontBody = Font(12, s), FontSmall = Font(11, s), FontTitle = Font(12, s), FontSection = Font(12, s),
            };
        }
    }
}
