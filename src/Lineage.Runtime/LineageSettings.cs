using System;

namespace Lineage
{
    public static class LineageSettings
    {
        internal static bool ColdStorageEnabledByDefault = true;

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

        /// <summary>
        /// Enables the disk-backed overflow tier. No journal directory or writer thread is
        /// created until the hot in-memory store reaches the high-water mark.
        /// </summary>
        public static bool ColdStorageEnabled = true;

        /// <summary>
        /// Hard cap for persisted cold provenance. Oldest immutable segments are removed
        /// when this rolling budget is exceeded. Default: 100 MiB.
        /// </summary>
        public static long ColdStorageMaxBytes = 100L * 1024L * 1024L;

        /// <summary>
        /// Percentage of hot Step capacity that triggers asynchronous spilling.
        /// </summary>
        public static int ColdStorageHighWatermarkPercent = 70;

        /// <summary>
        /// Spill target after pressure begins. The recorder attempts to return the hot
        /// store to this percentage without waiting for disk I/O.
        /// </summary>
        public static int ColdStorageTargetPercent = 50;

        /// <summary>
        /// Number of Steps copied into one immutable cold segment handoff.
        /// </summary>
        public static int ColdStoragePageSteps = 4096;

        /// <summary>
        /// Maximum number of immutable pages waiting for the writer. Keeping this small
        /// bounds RAM even if storage becomes slow.
        /// </summary>
        public static int ColdStorageQueuePages = 2;

        /// <summary>
        /// Optional root directory for temporary session journals. Null uses the system
        /// temporary directory. Session directories are removed when their scope ends.
        /// </summary>
        public static string ColdStorageDirectory;

        public static void Reset()
        {
            Mode = LineageMode.Strict;
            BreakOnReport = false;
            PublishToIde = true;
            WriteReportToConsole = false;
            ColdStorageEnabled = ColdStorageEnabledByDefault;
            ColdStorageMaxBytes = 100L * 1024L * 1024L;
            ColdStorageHighWatermarkPercent = 70;
            ColdStorageTargetPercent = 50;
            ColdStoragePageSteps = 4096;
            ColdStorageQueuePages = 2;
            ColdStorageDirectory = null;
            LineageState.LastReport = null;
        }
    }
}
