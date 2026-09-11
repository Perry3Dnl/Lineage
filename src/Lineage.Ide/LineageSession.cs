using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Lineage;

namespace LineageIde
{
    public sealed class LineageSession : INotifyPropertyChanged, IDisposable
    {
        public const int HistoryLimit = 32;

        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource _listen;
        private FileSystemWatcher _watcher;
        private string _loadedReportId;
        private bool _raw;
        private LineageReport _current;
        private LineageNode _selected;
        private LineageFlowGraph _flow = LineageFlowGraph.Empty;
        private string _status = EmptyNoApp;

        public const string EmptyNoApp = "Start debugging an application using Lineage to view value history.";
        public const string EmptyWaiting = "Lineage is active.\n\nWaiting for a Trace or automatic trigger.";
        public const string EmptyDisabled = "Lineage is disabled for this application.";

        public ObservableCollection<ReportHistoryItem> History { get; } = new ObservableCollection<ReportHistoryItem>();

        public LineageReport Current
        {
            get { return _current; }
            private set
            {
                _current = value;
                Raise(nameof(Current));
                Raise(nameof(HasReport));
                Raise(nameof(HistoryText));
                Raise(nameof(HeaderText));
                Raise(nameof(FocusText));
                Raise(nameof(TriggerText));
                RebuildGraph();
            }
        }

        public LineageFlowGraph Flow
        {
            get { return _flow; }
            private set
            {
                _flow = value ?? LineageFlowGraph.Empty;
                _flow.SetSelected(_selected);
                Raise(nameof(Flow));
            }
        }

        public LineageNode Selected
        {
            get { return _selected; }
            set
            {
                _selected = value;
                if (_flow != null)
                {
                    _flow.SetSelected(value);
                }

                Raise(nameof(Selected));
                Raise(nameof(DetailsText));
            }
        }

        public bool ShowRaw
        {
            get { return _raw; }
            set
            {
                _raw = value;
                Raise(nameof(ShowRaw));
                Raise(nameof(HistoryText));
                Raise(nameof(HeaderText));
                RebuildGraph();
            }
        }

        public bool HasReport => Current != null && Current.Nodes.Count > 0;

        public string Status
        {
            get { return _status; }
            set
            {
                _status = value;
                Raise(nameof(Status));
            }
        }

        public string TriggerText
        {
            get
            {
                if (Current == null || Current.Trigger == null || Current.Trigger.Kind == LineageTriggerKind.None)
                {
                    return string.Empty;
                }

                var text = "Trigger: " + Current.Trigger.Title;
                if (!string.IsNullOrEmpty(Current.Trigger.Detail))
                {
                    text += "\n" + Current.Trigger.Detail;
                }

                if (!string.IsNullOrEmpty(Current.Trigger.File))
                {
                    text += "\n" + Current.Trigger.File + ":" + Current.Trigger.Line;
                }

                return text;
            }
        }

        public string FocusText
        {
            get
            {
                if (Current == null || Current.Focus == null)
                {
                    return string.Empty;
                }

                return "Focus: " + Current.Focus.Label;
            }
        }

        public string HeaderText
        {
            get
            {
                var mode = ShowRaw ? "All recorded events" : "Value changes";
                if (string.IsNullOrEmpty(TriggerText))
                {
                    return "LINEAGE  ·  " + mode;
                }

                return "LINEAGE  ·  " + mode + "\n" + TriggerText + "\n" + FocusText;
            }
        }

        public string HistoryText
        {
            get
            {
                if (Current == null)
                {
                    return Status;
                }

                return ShowRaw ? Current.ToDiagnosticString() : Current.ToString();
            }
        }

        public string DetailsText
        {
            get
            {
                var node = Selected ?? (Current != null ? Current.Focus : null);
                if (node == null)
                {
                    return string.Empty;
                }

                return "DETAILS\n"
                    + "Name: " + node.DisplayName + "\n"
                    + "Value: " + FormatValue(node) + "\n"
                    + "Availability: " + node.ValueAvailability + "\n"
                    + "Type: " + (string.IsNullOrEmpty(node.TypeName) ? "(not captured)" : node.TypeName) + "\n"
                    + "Operation: " + node.Operation + "\n"
                    + "Category: " + node.Category + "\n"
                    + "Method: " + node.Method + "\n"
                    + "File: " + node.File + "\n"
                    + "Line: " + node.Line + "\n"
                    + "Assembly: " + node.Assembly + "\n"
                    + "Parents: " + node.Parents.Count + "\n"
                    + "Children: " + node.Children.Count
                    + (node.IsOpaque || node.Category == ReportCategory.FrameworkBoundary ? "\nCoverage: lineage unavailable" : string.Empty);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public event Action<LineageReport> ReportReceived;

        public LineageSession(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
            Status = EmptyWaiting;
        }

        public void Add(LineageReport report)
        {
            if (report == null)
            {
                return;
            }

            _dispatcher.Invoke(() =>
            {
                for (var i = 0; i < History.Count; i++)
                {
                    if (History[i].Report.ReportId == report.ReportId)
                    {
                        Current = History[i].Report;
                        Selected = Current.Focus;
                        return;
                    }
                }

                History.Insert(0, new ReportHistoryItem(report));
                while (History.Count > HistoryLimit)
                {
                    History.RemoveAt(History.Count - 1);
                }

                Current = report;
                Selected = report.Focus;
                _loadedReportId = report.ReportId;
                Status = EmptyWaiting;
                var handler = ReportReceived;
                if (handler != null)
                {
                    handler(report);
                }
            });
        }

        public void Load(LineageReport report)
        {
            if (report == null)
            {
                return;
            }

            Current = report;
            Selected = report.Focus;
        }

        public void Clear()
        {
            History.Clear();
            Current = null;
            Selected = null;
            _loadedReportId = null;
            Status = EmptyWaiting;
            RebuildGraph();
        }

        public void ShowPrevious()
        {
            Move(-1);
        }

        public void ShowNext()
        {
            Move(1);
        }

        private void Move(int delta)
        {
            if (Current == null || History.Count == 0)
            {
                return;
            }

            var index = -1;
            for (var i = 0; i < History.Count; i++)
            {
                if (History[i].Report.ReportId == Current.ReportId)
                {
                    index = i;
                    break;
                }
            }

            index += delta;
            if (index < 0 || index >= History.Count)
            {
                return;
            }

            Current = History[index].Report;
            Selected = Current.Focus;
        }

        public void StartListening()
        {
            if (_listen != null)
            {
                return;
            }

            _listen = new CancellationTokenSource();
            TryLoadLatest();
            StartFileWatch();
            var token = _listen.Token;
            Task.Run(() => Listen(token));
        }

        public void Dispose()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }

            if (_listen != null)
            {
                _listen.Cancel();
                _listen.Dispose();
                _listen = null;
            }
        }

        private void StartFileWatch()
        {
            try
            {
                var directory = ReportTransport.ReportDirectory;
                Directory.CreateDirectory(directory);
                _watcher = new FileSystemWatcher(directory, ReportTransport.LatestFileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
                };
                _watcher.Changed += (_, __) => TryLoadLatest();
                _watcher.Created += (_, __) => TryLoadLatest();
                _watcher.Renamed += (_, __) => TryLoadLatest();
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
            }
        }

        private void TryLoadLatest()
        {
            LineageReport report;
            if (!ReportTransport.TryReadLatest(out report))
            {
                return;
            }

            if (report.ReportId == _loadedReportId)
            {
                return;
            }

            _loadedReportId = report.ReportId;
            Add(report);
        }

        private void Listen(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(ReportTransport.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        server.WaitForConnection();
                        string json;
                        if (ReportTransport.TryRead(server, out json))
                        {
                            var report = ReportJson.Deserialize(json);
                            Add(report);
                        }
                    }
                }
                catch (Exception)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    Thread.Sleep(200);
                }
            }
        }

        public static string FormatValue(LineageNode node)
        {
            return node == null ? string.Empty : node.FormatStepValue();
        }

        private void RebuildGraph()
        {
            if (Current == null)
            {
                Flow = LineageFlowGraph.Empty;
                return;
            }

            var nodes = ShowRaw ? Current.RawNodes : Current.Nodes;
            Flow = LineageFlowGraph.Build(nodes);
        }

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    public sealed class ReportHistoryItem
    {
        public ReportHistoryItem(LineageReport report)
        {
            Report = report;
            var value = report.Focus != null ? report.Focus.FormatStepValue() : string.Empty;
            var trigger = report.Trigger != null ? report.Trigger.Title : string.Empty;
            Title = report.Timestamp.ToLocalTime().ToString("HH:mm:ss")
                + (string.IsNullOrEmpty(trigger) ? "" : "  " + trigger)
                + (string.IsNullOrEmpty(value) ? "" : "  " + value);
        }

        public LineageReport Report { get; }
        public string Title { get; }

        public override string ToString()
        {
            return Title;
        }
    }
}
