using Lineage;
using Lineage.Internal;
using Lineage.Sample;
using static Lineage.Lineage;

namespace Lineage.Integration.Tests
{
    public sealed class SampleAcceptanceTests : IDisposable
    {
        private readonly TextReader _originalIn;
        private readonly TextWriter _originalOut;

        public SampleAcceptanceTests()
        {
            _originalIn = Console.In;
            _originalOut = Console.Out;
            LineageSettings.Mode = LineageMode.All;
            LineageSettings.PublishToIde = false;
            LineageMetrics.Reset();
            CaptureScope.Current?.Dispose();
            EnsureInstrumented();
        }

        private static void EnsureInstrumented()
        {
            var assembly = typeof(SampleApp).Assembly;
            var resource = assembly.GetManifestResourceStream(MetadataRegistry.ResourceName);
            if (resource != null)
            {
                resource.Dispose();
                return;
            }

            throw new InvalidOperationException("Sample.Core was not instrumented. Expected Lineage MSBuild targets to rewrite " + assembly.Location);
        }

        public void Dispose()
        {
            Console.SetIn(_originalIn);
            Console.SetOut(_originalOut);
            LineageSettings.Reset();
            CaptureScope.Current?.Dispose();
        }

        [Fact]
        public void SampleLineage_FollowsPriceQuantityAndBulkDiscount()
        {
            Console.SetIn(new StringReader("10" + Environment.NewLine));
            Console.SetOut(new StringWriter());

            using (CaptureScope.Enter())
            {
                var ex = RunUntilCrash();
                Assert.Equal("Widget x10 - 20% = $160", ex.Message);

                var report = LastReport;
                Assert.NotNull(report);
                var text = report.ToString();

                Assert.Equal(LineageTriggerKind.UnhandledException, report.Trigger.Kind);
                Assert.Contains("Widget", text);
                Assert.Contains("Find", text);
                Assert.Contains("160", text);
                Assert.Contains("message = \"Widget x10 - 20% = $160\"", text);
                Assert.DoesNotContain("Trace =", text);
                Assert.DoesNotContain("Gadget", text);
                Assert.DoesNotContain("Cable", text);
                Assert.DoesNotContain("LogWarehouseSync", text);
                Assert.DoesNotContain("item", text);
                Assert.DoesNotContain("counted", text);
                Assert.DoesNotContain("Concat", text);
                Assert.DoesNotContain("ldloc", text);
                Assert.True(LineageMetrics.EventCount > 0);
                Assert.True(report.ProduceTicks >= 0);
                Assert.Contains(report.Nodes, n => !string.IsNullOrEmpty(n.File) && n.Line > 0);
                Assert.DoesNotContain(report.Nodes, n => n.DisplayName == "quantity" && n.Line == 10);
                Assert.Contains(report.Nodes, n => n.DisplayName == "rawQuantity" && n.Line == 18);
                Assert.Contains(report.Nodes, n => n.DisplayName == "quantity" && n.Line == 19);
                Assert.Contains(report.Nodes, n => n.DisplayName != null && n.DisplayName.IndexOf("Find", StringComparison.Ordinal) >= 0 && n.Line > 10);
                Assert.Contains(report.Nodes, n => n.DisplayName == "Name" && n.Line == 50);
                Assert.DoesNotContain(report.Nodes, n => n.DisplayName == "Name" && n.Line == 6);
                Assert.Contains(report.Nodes, n => (n.DisplayName == "unitPrice" || n.DisplayName == "UnitPrice") && n.FormatStepValue() == "20");
            }
        }

        [Fact]
        public void SampleLineage_QuantityShowsWhereTheValueCameFrom()
        {
            Console.SetIn(new StringReader("125" + Environment.NewLine));
            Console.SetOut(new StringWriter());

            using (CaptureScope.Enter())
            {
                var ex = RunUntilCrash();
                Assert.Equal("Widget x125 - 50% = $1250", ex.Message);

                var report = LastReport;
                Assert.NotNull(report);
                var text = report.ToString();

                Assert.Equal(LineageTriggerKind.UnhandledException, report.Trigger.Kind);
                Assert.Contains("quantity", text);
                Assert.Contains("125", text);
                Assert.Contains("Find", text);
                Assert.Contains("1250", text);
                Assert.Contains(report.Nodes, n => (n.DisplayName == "unitPrice" || n.DisplayName == "UnitPrice") && n.FormatStepValue() == "20");
                Assert.DoesNotContain("TryParse", text);
                Assert.DoesNotContain("[lineage unavailable]", text);
                Assert.DoesNotContain("op(", text);
                Assert.DoesNotContain("catalog", text);
                Assert.DoesNotContain("5 = 5", text);
                Assert.DoesNotContain("10 = 10", text);
                Assert.DoesNotContain("Trace =", text);
            }
        }

        private static InvalidOperationException RunUntilCrash()
        {
            try
            {
                SampleApp.Run();
            }
            catch (InvalidOperationException ex)
            {
                Recorder.OnUnhandled(ex);
                return ex;
            }

            throw new InvalidOperationException("SampleApp.Run did not crash");
        }
    }
}
