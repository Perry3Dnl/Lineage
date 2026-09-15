using System;
using System.Diagnostics;

namespace Lineage.Internal
{
    public static class Recorder
    {
        [ThreadStatic]
        private static int[] _args;

        [ThreadStatic]
        private static int[] _argFrames;

        [ThreadStatic]
        private static int _argCount;

        [ThreadStatic]
        private static int _argFrameCount;

        [ThreadStatic]
        private static int[] _captures;

        [ThreadStatic]
        private static int _captureCount;

        [ThreadStatic]
        private static int _returnId;

        [ThreadStatic]
        private static int _focusTarget;

        [ThreadStatic]
        private static int[] _methodFrames;

        [ThreadStatic]
        private static int[] _methodRootMarks;

        [ThreadStatic]
        private static int _methodFrameDepth;

        private const int RootKindArgument = 1;
        private const int RootKindLocalStore = 2;
        private const int RootKindArgumentStore = 3;

        private static int _unhandledHooked;

        public static bool IsEnabled()
        {
            return LineageSettings.ModeValue != 0;
        }

        public static void BeginCall()
        {
            if (!IsEnabled())
            {
                return;
            }

            EnsureArgStorage();
            if (_argFrameCount >= _argFrames.Length)
            {
                Array.Resize(ref _argFrames, _argFrames.Length * 2);
            }

            _argFrames[_argFrameCount++] = _argCount;
        }

        public static void PushArg(int valueId)
        {
            if (!IsEnabled())
            {
                return;
            }

            EnsureArgStorage();
            if (_argCount >= _args.Length)
            {
                Array.Resize(ref _args, _args.Length * 2);
            }

            _args[_argCount++] = valueId;
        }

        public static int GetArg(int index)
        {
            if (!IsEnabled() || _argFrameCount == 0)
            {
                return 0;
            }

            var start = _argFrames[_argFrameCount - 1];
            var count = _argCount - start;
            if (index < 0 || index >= count)
            {
                return 0;
            }

            var valueId = _args[start + index];
            TrackFrameRoot(RootKindArgument, index, valueId);
            return valueId;
        }

        public static void EndCall()
        {
            if (_argFrameCount == 0)
            {
                return;
            }

            _argCount = _argFrames[--_argFrameCount];
        }

        public static void NoteCapture(int valueId)
        {
            if (!IsEnabled() || valueId == 0)
            {
                return;
            }

            EnsureArgStorage();
            if (_captureCount >= _captures.Length)
            {
                Array.Resize(ref _captures, _captures.Length * 2);
            }

            _captures[_captureCount++] = valueId;
        }

        public static int ExternalCall(int locationId)
        {
            return ExternalCall(locationId, (int)EventKind.Call);
        }

        public static int ExternalCall(int locationId, int eventKind)
        {
            if (!IsEnabled())
            {
                EndCall();
                _captureCount = 0;
                return 0;
            }

            var start = _argFrameCount == 0 ? 0 : _argFrames[_argFrameCount - 1];
            var count = _argCount - start;
            var info = MetadataRegistry.Get(locationId);
            var operation = info != null ? info.Operation : OperationKind.None;

            int parent0;
            int parent1;
            if (operation == OperationKind.Search)
            {
                parent0 = _captureCount > 0 ? _captures[0] : (count > 1 ? _args[start + 1] : 0);
                parent1 = count > 0 ? _args[start] : 0;
                _captureCount = 0;
            }
            else if (operation == OperationKind.Aggregate)
            {
                parent0 = count > 0 ? _args[start] : 0;
                parent1 = 0;
            }
            else
            {
                parent0 = count > 0 ? _args[start] : 0;
                parent1 = count > 1 ? _args[start + 1] : 0;
                if (count > 2)
                {
                    var folded = Produce(locationId, parent0, parent1, eventKind);
                    for (var i = 2; i < count; i++)
                    {
                        folded = Produce(locationId, folded, _args[start + i], eventKind);
                    }

                    EndCall();
                    return folded;
                }
            }

            var id = Produce(locationId, parent0, parent1, eventKind);
            EndCall();
            return id;
        }

        public static int FinishInstrumentedCall(int locationId)
        {
            return FinishInstrumentedCall(locationId, (int)EventKind.Call);
        }

        public static int FinishInstrumentedCall(int locationId, int eventKind)
        {
            if (!IsEnabled())
            {
                EndCall();
                _returnId = 0;
                return 0;
            }

            var start = _argFrameCount == 0 ? 0 : _argFrames[_argFrameCount - 1];
            var count = _argCount - start;
            var returned = PopReturn();
            var parent0 = 0;
            var parent1 = 0;
            var scope = CaptureScope.Current;
            for (var i = 0; i < count; i++)
            {
                var arg = _args[start + i];
                if (arg == 0 || arg == returned)
                {
                    continue;
                }

                if (parent0 == 0)
                {
                    parent0 = arg;
                }
                else if (parent1 == 0 && arg != parent0)
                {
                    parent1 = arg;
                }

                if (returned != 0 && scope != null)
                {
                    scope.Buffer.AttachParent(returned, arg);
                }
            }

            EndCall();

            if (returned != 0)
            {
                return returned;
            }

            return Produce(locationId, parent0, parent1, eventKind);
        }

        public static void SetReturn(int valueId)
        {
            _returnId = valueId;
        }

        public static int PopReturn()
        {
            var id = _returnId;
            _returnId = 0;
            return id;
        }

        public static int EnterMethod(int locationId)
        {
            if (!IsEnabled())
            {
                return 0;
            }

            var scope = CaptureScope.Ensure();
            if (scope == null || scope.Frozen)
            {
                return 0;
            }

            // A method entered without an instrumented caller is a safe boundary. Any
            // pending return belongs to the previous top-level invocation and cannot be
            // consumed by an instrumented caller anymore.
            if (_methodFrameDepth == 0 && _argFrameCount == 0)
            {
                _returnId = 0;
                if (scope.Buffer.Count > 0)
                {
                    scope.CollectGarbage();
                }
            }

            EnsureMethodStorage();
            var frame = scope.NextFrameId++;
            _methodFrames[_methodFrameDepth] = frame;
            _methodRootMarks[_methodFrameDepth] = scope.FrameRootMark;
            _methodFrameDepth++;
            scope.CurrentFrameId = frame;
            Write(scope, locationId, 0, 0, 0, EventKind.MethodEntry);
            return frame;
        }

        public static void LeaveMethod()
        {
            var scope = CaptureScope.Current;
            if (scope == null)
            {
                return;
            }

            if (_methodFrameDepth <= 0)
            {
                scope.CurrentFrameId = 0;
                return;
            }

            var index = --_methodFrameDepth;
            scope.ReleaseFrameRoots(_methodRootMarks[index]);
            _methodRootMarks[index] = 0;
            _methodFrames[index] = 0;
            scope.CurrentFrameId = _methodFrameDepth > 0 ? _methodFrames[_methodFrameDepth - 1] : 0;

            // Do not collect while an instrumented caller is active: that caller can
            // still hold unrooted provenance ids on its IL evaluation stack. At the
            // outermost void boundary there is no such stack, so collection is safe.
            if (_methodFrameDepth == 0 && _returnId == 0 && !scope.Frozen)
            {
                scope.CollectGarbage();
            }
        }

        public static void SetFocusTarget(int valueId)
        {
            _focusTarget = valueId;
        }

        public static int Produce(int locationId, int parent0, int parent1, int kind)
        {
            if (!IsEnabled())
            {
                return 0;
            }

            var start = Stopwatch.GetTimestamp();
            var scope = CaptureScope.Ensure();
            if (scope == null || scope.Frozen)
            {
                return 0;
            }

            var valueId = scope.NextValueId++;
            var eventKind = (EventKind)kind;
            Write(scope, locationId, valueId, parent0, parent1, eventKind);
            if (parent0 != 0 && parent1 == 0)
            {
                scope.CopyPreview(parent0, valueId);
            }

            // Existing instrumentation already distinguishes local/argument stores, so
            // they can become roots without injecting another call into every method.
            if (eventKind == EventKind.LocalStore)
            {
                TrackFrameRoot(RootKindLocalStore, locationId, valueId);
            }
            else if (eventKind == EventKind.Argument)
            {
                TrackFrameRoot(RootKindArgumentStore, locationId, valueId);
            }

            LineageMetrics.AddProduce(Stopwatch.GetTimestamp() - start);
            return valueId;
        }

        public static void Remember(object value, int valueId)
        {
            if (!IsEnabled() || valueId <= 0)
            {
                return;
            }

            var scope = CaptureScope.Current;
            if (scope == null || scope.Frozen)
            {
                return;
            }

            scope.SetPreview(valueId, ValuePreview.Of(value));
            if (value != null)
            {
                scope.SetTypeName(valueId, value.GetType().FullName);
            }
        }

        public static void RememberFocusValue(object value)
        {
            Remember(value, _focusTarget);
        }

        public static int OnBranch(int locationId, int conditionId)
        {
            return Produce(locationId, conditionId, 0, (int)EventKind.Branch);
        }

        public static int FieldWrite(object target, int locationId, int fieldToken, int rhsId)
        {
            var id = Produce(locationId, rhsId, 0, (int)EventKind.FieldWrite);
            var scope = CaptureScope.Current;
            if (scope == null || id == 0)
            {
                return id;
            }

            var rootId = MutationTracker.Write(target, fieldToken, id, scope.SessionId);
            if (rootId != 0)
            {
                scope.SetRoot(rootId, id);
            }

            return id;
        }

        public static int FieldRead(object target, int locationId, int fieldToken, int objectId)
        {
            var scope = CaptureScope.Ensure();
            var existing = scope != null ? MutationTracker.Read(target, fieldToken, scope.SessionId) : 0;
            return Produce(locationId, objectId, existing, (int)EventKind.FieldRead);
        }

        public static LineageReport CompleteFocus()
        {
            return CompleteFocus(LineageTrigger.ManualTrace());
        }

        public static LineageReport CompleteFocus(LineageTrigger trigger)
        {
            if (!IsEnabled())
            {
                return new LineageReport(new LineageNode[0]);
            }

            var scope = CaptureScope.Ensure();
            if (scope == null)
            {
                return new LineageReport(new LineageNode[0]);
            }

            var focusParent = _focusTarget;
            _focusTarget = 0;
            var focusId = Produce(0, focusParent, 0, (int)EventKind.Focus);
            if (focusId == 0 && focusParent != 0)
            {
                focusId = focusParent;
            }

            scope.FocusValueId = focusId;
            scope.Frozen = true;

            var slice = CausalSlice.Collect(scope.Buffer, focusId);
            var report = ReportFormatter.Format(
                slice,
                scope.Buffer.Dropped,
                LineageMetrics.EventCount,
                LineageMetrics.ProduceTicks,
                scope,
                trigger ?? LineageTrigger.ManualTrace());

            LineageState.LastReport = report;
            var published = false;
            try
            {
                published = ReportTransport.TryPublish(report);
            }
            catch
            {
                published = false;
            }

            if (LineageSettings.WriteReportToConsole && !published)
            {
                try
                {
                    Console.Error.WriteLine(report);
                }
                catch
                {
                }
            }

            if (LineageSettings.BreakOnReport && System.Diagnostics.Debugger.IsAttached)
            {
                System.Diagnostics.Debugger.Break();
            }

            scope.Buffer.Clear();
            return report;
        }

        public static void OnUnhandled()
        {
            OnUnhandled(null);
        }

        public static void OnUnhandled(Exception exception)
        {
            if (!LineageSettings.AutomaticTriggersEnabled)
            {
                return;
            }

            var scope = CaptureScope.Current;
            if (scope == null || scope.Frozen)
            {
                return;
            }

            if (_focusTarget == 0)
            {
                _focusTarget = scope.NextValueId - 1;
            }

            if (_focusTarget <= 0)
            {
                _focusTarget = Produce(0, 0, 0, (int)EventKind.Origin);
            }

            CompleteFocus(LineageTrigger.UnhandledException(exception != null ? exception.GetType().Name : null));
        }

        public static void EnsureUnhandledHook()
        {
            if (!LineageSettings.AutomaticTriggersEnabled)
            {
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _unhandledHooked, 1, 0) != 0)
            {
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                OnUnhandled(args.ExceptionObject as Exception);
            };
        }

        public static CaptureScope EnterScope()
        {
            return CaptureScope.Enter();
        }

        public static void LeaveScope(CaptureScope scope)
        {
            if (scope != null)
            {
                scope.Dispose();
            }
        }

        private static void TrackFrameRoot(int kind, int slot, int valueId)
        {
            if (valueId <= 0)
            {
                return;
            }

            var scope = CaptureScope.Current;
            if (scope == null || scope.CurrentFrameId <= 0)
            {
                return;
            }

            scope.SetFrameRoot(FrameRootKey(scope.CurrentFrameId, kind, slot), valueId);
        }

        private static long FrameRootKey(int frameId, int kind, int slot)
        {
            return ((long)(uint)frameId << 32)
                | ((long)(kind & 3) << 30)
                | (uint)(slot & 0x3fffffff);
        }

        private static void Write(CaptureScope scope, int locationId, int valueId, int parent0, int parent1, EventKind kind)
        {
            var ev = new LineageEvent
            {
                LocationId = locationId,
                FrameId = scope.CurrentFrameId,
                ValueId = valueId,
                Parent0 = parent0,
                Parent1 = parent1,
                Kind = kind
            };
            scope.Buffer.TryAdd(ev);
        }

        private static void EnsureArgStorage()
        {
            if (_args == null)
            {
                _args = new int[16];
            }

            if (_argFrames == null)
            {
                _argFrames = new int[8];
            }

            if (_captures == null)
            {
                _captures = new int[8];
            }
        }

        private static void EnsureMethodStorage()
        {
            if (_methodFrames == null)
            {
                _methodFrames = new int[8];
                _methodRootMarks = new int[8];
                return;
            }

            if (_methodFrameDepth < _methodFrames.Length)
            {
                return;
            }

            Array.Resize(ref _methodFrames, _methodFrames.Length * 2);
            Array.Resize(ref _methodRootMarks, _methodRootMarks.Length * 2);
        }
    }
}
