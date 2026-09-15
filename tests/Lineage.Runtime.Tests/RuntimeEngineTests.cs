using Lineage.Internal;
using static Lineage.Lineage;

namespace Lineage.Runtime.Tests
{
    public sealed class RuntimeEngineTests : IDisposable
    {
        public RuntimeEngineTests()
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
        public void SampleGraph_ExcludesUnrelatedAndFormatsForward()
        {
            using (CaptureScope.Enter())
            {
                Register(1, EventKind.Call, callName: "Console.ReadLine");
                Register(2, EventKind.LocalStore, localName: "username");
                Register(3, EventKind.Call, callName: "DoSomethingElse");
                Register(4, EventKind.LocalStore, localName: "unrelated");
                Register(5, EventKind.Call, callName: "Trim");
                Register(6, EventKind.LocalStore, localName: "cleaned");
                Register(7, EventKind.Call, callName: "ToUpperInvariant");
                Register(8, EventKind.LocalStore, localName: "upper");
                Register(9, EventKind.Constant, callName: "\"Hello \"");
                Register(10, EventKind.Call, callName: "Concat");
                Register(11, EventKind.LocalStore, localName: "message");

                var usernameSrc = Recorder.Produce(1, 0, 0, (int)EventKind.Call);
                var username = Recorder.Produce(2, usernameSrc, 0, (int)EventKind.LocalStore);

                var noiseSrc = Recorder.Produce(3, 0, 0, (int)EventKind.Call);
                Recorder.Produce(4, noiseSrc, 0, (int)EventKind.LocalStore);

                var cleanedSrc = Recorder.Produce(5, username, 0, (int)EventKind.Call);
                var cleaned = Recorder.Produce(6, cleanedSrc, 0, (int)EventKind.LocalStore);

                var upperSrc = Recorder.Produce(7, cleaned, 0, (int)EventKind.Call);
                var upper = Recorder.Produce(8, upperSrc, 0, (int)EventKind.LocalStore);

                var hello = Recorder.Produce(9, 0, 0, (int)EventKind.Constant);
                var concat = Recorder.Produce(10, hello, upper, (int)EventKind.Call);
                var message = Recorder.Produce(11, concat, 0, (int)EventKind.LocalStore);

                Recorder.SetFocusTarget(message);
                var report = Lineage.Focus("Hello ALICE");
                var text = report.ToString();

                Assert.Contains("username", report.Nodes[0].Label);
                Assert.Contains("Trace", report.Nodes[report.Nodes.Count - 1].Label);
                Assert.Contains("username", text);
                Assert.Contains("Trim()", text);
                Assert.Contains("cleaned", text);
                Assert.Contains("ToUpperInvariant()", text);
                Assert.Contains("upper", text);
                Assert.Contains("message", text);
                Assert.DoesNotContain("DoSomethingElse", text);
                Assert.DoesNotContain("unrelated", text);
                Assert.True(LineageMetrics.EventCount > 0);
            }
        }

        [Fact]
        public void Focus_UsesOccurrenceIdentityNotClrEquality()
        {
            using (CaptureScope.Enter())
            {
                Register(1, EventKind.Call, callName: "PathA");
                Register(2, EventKind.Call, callName: "PathB");

                var a = Recorder.Produce(1, 0, 0, (int)EventKind.Call);
                var b = Recorder.Produce(2, 0, 0, (int)EventKind.Call);
                Assert.NotEqual(a, b);

                Recorder.SetFocusTarget(a);
                var report = Lineage.Focus("same");
                var text = report.ToString();
                Assert.Contains("PathA()", text);
                Assert.DoesNotContain("PathB", text);
            }
        }

        [Fact]
        public void CompletedScope_DiscardsRawHistory()
        {
            using (var scope = CaptureScope.Enter())
            {
                Recorder.Produce(1, 0, 0, (int)EventKind.Constant);
                Assert.True(scope.Buffer.Count > 0);
            }

            Assert.Null(CaptureScope.Current);
        }

        [Fact]
        public void Focus_DiscardsRawHistoryAfterSlice()
        {
            using (var scope = CaptureScope.Enter())
            {
                var id = Recorder.Produce(1, 0, 0, (int)EventKind.Constant);
                Recorder.SetFocusTarget(id);
                Lineage.Focus(0);
                Assert.Equal(0, scope.Buffer.Count);
            }
        }

        [Fact]
        public void RawCapture_StoresFiveStepsAndSeparatePath()
        {
            using (var scope = CaptureScope.Enter(16))
            {
                var step1 = Recorder.Produce(101, 0, 0, (int)EventKind.Constant);
                scope.SetPreview(step1, "10");
                var step2 = Recorder.Produce(102, 0, 0, (int)EventKind.Constant);
                scope.SetPreview(step2, "5");
                var step3 = Recorder.Produce(103, step1, step2, (int)EventKind.LocalStore);
                scope.SetPreview(step3, "15");
                var step4 = Recorder.Produce(104, 0, 0, (int)EventKind.Constant);
                scope.SetPreview(step4, "2");
                var step5 = Recorder.Produce(105, step3, step4, (int)EventKind.LocalStore);
                scope.SetPreview(step5, "30");

                Assert.Equal(5, scope.Buffer.Count);
                Assert.Equal(1, scope.Buffer.Steps[0].Id);
                Assert.Equal("10", scope.Buffer.Steps[0].Value);
                Assert.Equal(101, scope.Buffer.Steps[0].LocationId);
                Assert.Equal(5, scope.Buffer.Steps[4].Id);
                Assert.Equal("30", scope.Buffer.Steps[4].Value);
                Assert.Equal(105, scope.Buffer.Steps[4].LocationId);

                Assert.Equal(4, scope.Buffer.RelationCount);
                Assert.Equal(3, scope.Buffer.Relations[0].ChildStepId);
                Assert.Equal(1, scope.Buffer.Relations[0].ParentStepId);
                Assert.Equal(3, scope.Buffer.Relations[1].ChildStepId);
                Assert.Equal(2, scope.Buffer.Relations[1].ParentStepId);
                Assert.Equal(5, scope.Buffer.Relations[2].ChildStepId);
                Assert.Equal(3, scope.Buffer.Relations[2].ParentStepId);
                Assert.Equal(5, scope.Buffer.Relations[3].ChildStepId);
                Assert.Equal(4, scope.Buffer.Relations[3].ParentStepId);

                var slice = CausalSlice.Collect(scope.Buffer, step5);
                Assert.Equal(5, slice.Count);
                for (var i = 0; i < slice.Count; i++)
                {
                    Assert.Equal(i + 1, slice[i].ValueId);
                }
            }
        }

        private static void Register(int id, EventKind kind, string localName = "", string callName = "")
        {
            MetadataRegistry.Register(new LocationInfo
            {
                LocationId = id,
                Kind = kind,
                LocalName = localName,
                CallName = callName
            });
        }
    }
}
