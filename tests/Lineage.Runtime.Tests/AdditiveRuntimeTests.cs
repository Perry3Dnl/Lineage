using Lineage;
using Lineage.Internal;
using static Lineage.Lineage;

namespace Lineage.Runtime.Tests
{
    public sealed class AdditiveRuntimeTests : IDisposable
    {
        public AdditiveRuntimeTests()
        {
            MetadataRegistry.Clear();
            LineageMetrics.Reset();
            LineageSettings.Reset();
            LineageSettings.PublishToIde = false;
            CaptureScope.Current?.Dispose();
        }

        public void Dispose()
        {
            MetadataRegistry.Clear();
            LineageMetrics.Reset();
            LineageSettings.Reset();
            CaptureScope.Current?.Dispose();
        }

        [Fact]
        public void NoneMode_TraceReturnsValueAndRecordsNothing()
        {
            LineageSettings.Mode = LineageMode.None;
            using (CaptureScope.Enter())
            {
                var produced = Recorder.Produce(1, 0, 0, (int)EventKind.Constant);
                Assert.Equal(0, produced);
                Assert.Equal("Alice", "Alice".Trace());
                Assert.Null(LastReport);
            }
        }

        [Fact]
        public void Split_OnlyFocusedBranchSurvives()
        {
            using (CaptureScope.Enter())
            {
                Register(1, "input");
                Register(2, "a");
                Register(3, "b");
                var input = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                var a = Recorder.Produce(2, input, 0, (int)EventKind.LocalStore);
                Recorder.Produce(3, input, 0, (int)EventKind.LocalStore);
                Recorder.SetFocusTarget(a);
                var text = "x".Trace();
                Assert.Equal("x", text);
                var report = LastReport.ToString();
                Assert.Contains("input", report);
                Assert.Contains("a", report);
                Assert.DoesNotContain("\nb", report);
                Assert.DoesNotContain("b", Labels(LastReport));
            }
        }

        [Fact]
        public void Merge_KeepsBothParents()
        {
            using (CaptureScope.Enter())
            {
                Register(1, "left");
                Register(2, "right");
                Register(3, "message");
                var left = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                var right = Recorder.Produce(2, 0, 0, (int)EventKind.LocalStore);
                var message = Recorder.Produce(3, left, right, (int)EventKind.LocalStore);
                Recorder.SetFocusTarget(message);
                Lineage.Focus(0);
                var labels = Labels(LastReport);
                Assert.Contains("left", labels);
                Assert.Contains("right", labels);
                Assert.Contains("message", labels);
            }
        }

        [Fact]
        public void Search_UsesCapturedKeyNotOtherUsers()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo { LocationId = 1, LocalName = "requestedName" });
                MetadataRegistry.Register(new LocationInfo { LocationId = 2, CallName = "Find", Operation = OperationKind.Search, ReportLabel = "Find" });
                MetadataRegistry.Register(new LocationInfo { LocationId = 3, LocalName = "Bob" });
                var name = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                Recorder.NoteCapture(name);
                Recorder.BeginCall();
                Recorder.PushArg(Recorder.Produce(0, 0, 0, (int)EventKind.Origin));
                var found = Recorder.ExternalCall(2, (int)EventKind.Call);
                Recorder.Produce(3, 0, 0, (int)EventKind.LocalStore);
                Recorder.SetFocusTarget(found);
                Lineage.Focus(0);
                var text = LastReport.ToString();
                Assert.Contains("requestedName", text);
                Assert.Contains("Find", text);
                Assert.DoesNotContain("Bob", text);
            }
        }

        [Fact]
        public void Aggregate_DoesNotExpandItems()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo { LocationId = 1, LocalName = "prices" });
                MetadataRegistry.Register(new LocationInfo { LocationId = 2, CallName = "Sum", Operation = OperationKind.Aggregate, ReportLabel = "Sum" });
                MetadataRegistry.Register(new LocationInfo { LocationId = 3, LocalName = "item0" });
                var prices = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                Recorder.Produce(3, 0, 0, (int)EventKind.Constant);
                Recorder.BeginCall();
                Recorder.PushArg(prices);
                var sum = Recorder.ExternalCall(2, (int)EventKind.Call);
                Recorder.SetFocusTarget(sum);
                Lineage.Focus(0);
                var text = LastReport.ToString();
                Assert.Contains("prices", text);
                Assert.Contains("Sum", text);
                Assert.DoesNotContain("item0", text);
            }
        }

        [Fact]
        public void Mutation_ReadUsesLatestWrite()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo { LocationId = 1, LocalName = "originalStatus" });
                MetadataRegistry.Register(new LocationInfo { LocationId = 2, LocalName = "Status", Kind = EventKind.FieldWrite });
                MetadataRegistry.Register(new LocationInfo { LocationId = 3, LocalName = "Status", Kind = EventKind.FieldRead });
                var target = new object();
                var status = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                var written = Recorder.FieldWrite(target, 2, 42, status);
                var read = Recorder.FieldRead(target, 3, 42, 0);
                Recorder.SetFocusTarget(read);
                Lineage.Focus(0);
                var text = LastReport.ToString();
                Assert.Contains("originalStatus", text);
                Assert.Contains("Status", text);
                Assert.NotEqual(0, written);
            }
        }

        [Fact]
        public void OpaqueNode_IncludesCoverageNote()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo
                {
                    LocationId = 1,
                    CallName = "ThirdParty.Parse",
                    ReportLabel = "ThirdParty.Parse",
                    IsOpaque = true,
                    Operation = OperationKind.Opaque
                });
                var id = Recorder.Produce(1, 0, 0, (int)EventKind.Call);
                Recorder.SetFocusTarget(id);
                Lineage.Focus(0);
                Assert.Contains("[lineage unavailable]", LastReport.ToString());
            }
        }

        [Fact]
        public void PropertyWrite_UsesCollectionInitializerLineNotSetter()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo
                {
                    LocationId = 1,
                    ReportLabel = "\"Widget\"",
                    Kind = EventKind.Constant,
                    File = "SampleApp.cs",
                    Line = 50
                });
                MetadataRegistry.Register(new LocationInfo
                {
                    LocationId = 2,
                    LocalName = "Name",
                    Kind = EventKind.FieldWrite,
                    File = "Product.cs",
                    Line = 6
                });
                MetadataRegistry.Register(new LocationInfo
                {
                    LocationId = 3,
                    LocalName = "message",
                    File = "SampleApp.cs",
                    Line = 36
                });

                var literal = Recorder.Produce(1, 0, 0, (int)EventKind.Constant);
                Recorder.Remember("Widget", literal);
                var name = Recorder.FieldWrite(new object(), 2, 7, literal);
                Recorder.Remember("Widget", name);
                var message = Recorder.Produce(3, name, 0, (int)EventKind.LocalStore);
                Recorder.Remember("Widget", message);
                Recorder.SetFocusTarget(message);
                Lineage.Focus("Widget");

                Assert.Contains(LastReport.Nodes, n => n.DisplayName == "Name" && n.Line == 50);
                Assert.DoesNotContain(LastReport.Nodes, n => n.DisplayName == "Name" && n.Line == 6);
            }
        }

        [Fact]
        public void Quantity_KeepsRawQuantityFromReadLine()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo
                {
                    LocationId = 1,
                    CallName = "ReadLine",
                    ReportLabel = "Console.ReadLine()",
                    Kind = EventKind.Call,
                    File = "SampleApp.cs",
                    Line = 18,
                    MethodName = "Run"
                });
                MetadataRegistry.Register(new LocationInfo
                {
                    LocationId = 2,
                    LocalName = "rawQuantity",
                    File = "SampleApp.cs",
                    Line = 18,
                    MethodName = "Run"
                });
                MetadataRegistry.Register(new LocationInfo
                {
                    LocationId = 3,
                    CallName = "ParseQuantity",
                    ReportLabel = "ParseQuantity",
                    Kind = EventKind.Call,
                    File = "SampleApp.cs",
                    Line = 19,
                    MethodName = "Run"
                });
                MetadataRegistry.Register(new LocationInfo
                {
                    LocationId = 4,
                    LocalName = "quantity",
                    File = "SampleApp.cs",
                    Line = 19,
                    MethodName = "Run"
                });
                MetadataRegistry.Register(new LocationInfo
                {
                    LocationId = 5,
                    LocalName = "message",
                    File = "SampleApp.cs",
                    Line = 40,
                    MethodName = "Run"
                });

                var read = Recorder.Produce(1, 0, 0, (int)EventKind.Call);
                Recorder.Remember("25", read);
                var raw = Recorder.Produce(2, read, 0, (int)EventKind.LocalStore);
                Recorder.Remember("25", raw);
                var parsed = Recorder.Produce(3, raw, 0, (int)EventKind.Call);
                Recorder.Remember(25, parsed);
                var quantity = Recorder.Produce(4, parsed, 0, (int)EventKind.LocalStore);
                Recorder.Remember(25, quantity);
                var message = Recorder.Produce(5, quantity, 0, (int)EventKind.LocalStore);
                Recorder.Remember("ok", message);
                Recorder.SetFocusTarget(message);
                Lineage.Focus("ok");

                LineageNode rawNode = null;
                LineageNode quantityNode = null;
                for (var i = 0; i < LastReport.Nodes.Count; i++)
                {
                    var node = LastReport.Nodes[i];
                    if (node.DisplayName == "rawQuantity")
                    {
                        rawNode = node;
                    }

                    if (node.DisplayName == "quantity")
                    {
                        quantityNode = node;
                    }
                }

                Assert.NotNull(rawNode);
                Assert.NotNull(quantityNode);
                Assert.Equal(18, rawNode.Line);
                Assert.Equal(19, quantityNode.Line);
                Assert.Contains(rawNode.ValueId, quantityNode.Parents);
                Assert.DoesNotContain(LastReport.Nodes, n => n.DisplayName != null && n.DisplayName.IndexOf("ReadLine", StringComparison.Ordinal) >= 0);
                Assert.DoesNotContain(LastReport.Nodes, n => n.DisplayName == "ParseQuantity");
            }
        }

        [Fact]
        public void Arithmetic_UsesOperatorInsteadOfOp()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo { LocationId = 1, LocalName = "unitPrice", File = "App.cs", Line = 16 });
                MetadataRegistry.Register(new LocationInfo { LocationId = 2, LocalName = "quantity", File = "App.cs", Line = 19 });
                MetadataRegistry.Register(new LocationInfo
                {
                    LocationId = 3,
                    CallName = "mul",
                    ReportLabel = "mul",
                    Operation = OperationKind.Transformation,
                    Kind = EventKind.Call,
                    File = "App.cs",
                    Line = 25
                });
                MetadataRegistry.Register(new LocationInfo { LocationId = 4, LocalName = "subtotal", File = "App.cs", Line = 25 });
                var price = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                Recorder.Remember(20, price);
                var qty = Recorder.Produce(2, 0, 0, (int)EventKind.LocalStore);
                Recorder.Remember(125, qty);
                var mul = Recorder.Produce(3, price, qty, (int)EventKind.Call);
                Recorder.Remember(2500, mul);
                var subtotal = Recorder.Produce(4, mul, 0, (int)EventKind.LocalStore);
                Recorder.Remember(2500, subtotal);
                Recorder.SetFocusTarget(subtotal);
                Lineage.Focus(2500);
                var text = LastReport.ToString();
                Assert.Contains("×", text);
                Assert.Contains("125", text);
                Assert.Contains("subtotal = 2500", text);
                Assert.DoesNotContain("mul(", text);
                Assert.DoesNotContain("op(", text);
            }
        }

        [Fact]
        public void Remember_ShowsVariableNameAndValue()
        {
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo { LocationId = 1, LocalName = "requestedName" });
                var id = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                Recorder.Remember("Alice", id);
                Recorder.SetFocusTarget(id);
                Lineage.Focus("Alice");
                var text = LastReport.ToString();
                Assert.Contains("requestedName = \"Alice\"", text);
                Assert.Contains("Trace = \"Alice\"", text);
            }
        }

        [Fact]
        public void UnhandledException_ReportsLastValueWithoutTrace()
        {
            LineageSettings.Mode = LineageMode.All;
            using (CaptureScope.Enter())
            {
                MetadataRegistry.Register(new LocationInfo { LocationId = 1, LocalName = "message", File = "SampleApp.cs", Line = 36 });
                var id = Recorder.Produce(1, 0, 0, (int)EventKind.LocalStore);
                Recorder.Remember("Widget x10 - 20% = $160", id);
                Recorder.OnUnhandled(new InvalidOperationException("Widget x10 - 20% = $160"));
                Assert.NotNull(LastReport);
                Assert.Equal(LineageTriggerKind.UnhandledException, LastReport.Trigger.Kind);
                var text = LastReport.ToString();
                Assert.Contains("Widget x10 - 20% = $160", text);
                Assert.Contains("Unhandled Exception", text);
                Assert.DoesNotContain("Trace =", text);
            }
        }

        private static void Register(int id, string local)
        {
            MetadataRegistry.Register(new LocationInfo { LocationId = id, LocalName = local });
        }

        private static string Labels(LineageReport report)
        {
            return string.Join(",", report.Nodes.Select(n => n.Label));
        }
    }
}
