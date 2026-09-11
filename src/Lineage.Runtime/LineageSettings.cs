using System;

namespace Lineage
{
    public static class LineageSettings
    {
        public static int ModeValue = (int)LineageMode.Strict;

        public static LineageMode Mode
        {
            get { return (LineageMode)ModeValue; }
            set { ModeValue = (int)value; }
        }

        public static bool IsEnabled => ModeValue != (int)LineageMode.None;

        public static bool AutomaticTriggersEnabled => ModeValue == (int)LineageMode.All;

        public static bool AutomaticScopesEnabled => ModeValue == (int)LineageMode.All;

        public static bool BreakOnReport;

        public static bool PublishToIde = true;

        public static bool WriteReportToConsole;

        public static void Reset()
        {
            Mode = LineageMode.Strict;
            BreakOnReport = false;
            PublishToIde = true;
            WriteReportToConsole = false;
            LineageState.LastReport = null;
        }
    }
}
