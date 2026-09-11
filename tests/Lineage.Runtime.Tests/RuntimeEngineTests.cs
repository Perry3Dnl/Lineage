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
