using Lineage.Internal;

namespace Lineage.Runtime.Tests
{
    public sealed class TypedValueTests : IDisposable
    {
        public TypedValueTests()
        {
            MetadataRegistry.Clear();
            LineageMetrics.Reset();
            LineageSettings.Reset();
            LineageSettings.PublishToIde = false;
            CaptureScope.Current?.Dispose();
        }

        public void Dispose()
        {
            CaptureScope.Current?.Dispose();
            MetadataRegistry.Clear();
            LineageMetrics.Reset();
            LineageSettings.Reset();
        }

        [Fact]
        public void PrimitiveValues_AreStoredAsTypedPayloadsInsteadOfPreviewStrings()
        {
            AssertValue(true, LineageValueKind.Boolean, "true");
            AssertValue('A', LineageValueKind.Char, "'A'");
            AssertValue((sbyte)-12, LineageValueKind.SByte, "-12");
            AssertValue((byte)250, LineageValueKind.Byte, "250");
            AssertValue((short)-32000, LineageValueKind.Int16, "-32000");
            AssertValue((ushort)65000, LineageValueKind.UInt16, "65000");
            AssertValue(-1234567, LineageValueKind.Int32, "-1234567");
            AssertValue(4000000000u, LineageValueKind.UInt32, "4000000000");
            AssertValue(-9000000000000000000L, LineageValueKind.Int64, "-9000000000000000000");
            AssertValue(18000000000000000000UL, LineageValueKind.UInt64, "18000000000000000000");
            AssertValue((nint)(-1234), LineageValueKind.NativeInt, "-1234");
            AssertValue((nuint)1234, LineageValueKind.NativeUInt, "1234");
            AssertValue(1.25f, LineageValueKind.Single, "1.25");
            AssertValue(-123.5d, LineageValueKind.Double, "-123.5");
            AssertValue(1234567890.123456789m, LineageValueKind.Decimal, "1234567890.123456789");
        }

        [Fact]
        public void FrameworkValueTypes_AreTypedAndDeterministic()
        {
            var date = new DateTime(2026, 9, 15, 12, 34, 56, DateTimeKind.Utc);
            AssertValue(date, LineageValueKind.DateTime, "2026-09-15T12:34:56.0000000Z");

            var offset = new DateTimeOffset(2026, 9, 15, 12, 34, 56, TimeSpan.FromHours(2));
            AssertValue(offset, LineageValueKind.DateTimeOffset, "2026-09-15T12:34:56.0000000+02:00");
            AssertValue(TimeSpan.FromMinutes(90), LineageValueKind.TimeSpan, "01:30:00");

            var guid = Guid.Parse("12345678-1234-5678-9abc-def012345678");
            AssertValue(guid, LineageValueKind.Guid, "12345678-1234-5678-9abc-def012345678");
        }

        [Fact]
        public void ModernValueTypes_AreRecognizedThroughNetStandardRuntimeBoundary()
        {
            AssertValue((Half)1.5f, LineageValueKind.Half, "1.5");
            AssertValue(Int128.Parse("170141183460469231731687303715884105727"), LineageValueKind.Int128, "170141183460469231731687303715884105727");
            AssertValue(UInt128.Parse("340282366920938463463374607431768211455"), LineageValueKind.UInt128, "340282366920938463463374607431768211455");
            AssertValue(new DateOnly(2026, 9, 15), LineageValueKind.DateOnly, "09/15/2026");
            AssertValue(new TimeOnly(12, 34, 56), LineageValueKind.TimeOnly, "12:34");
        }

        [Fact]
        public void EnumAndStructCategories_ArePreserved()
        {
            AssertValue(SampleStatus.Paid, LineageValueKind.Enum, "Paid");
            AssertValue((X: 10, Y: 20), LineageValueKind.ValueTuple, "\"(10, 20)\"");
            AssertValue(new SamplePoint(10, 20), LineageValueKind.Struct, "\"10,20\"");
        }

        [Fact]
        public void Null_IsExplicitlyTypedAndDisplayed()
        {
            AssertValue(null, LineageValueKind.Null, "null");
        }

        [Fact]
        public void TypedPayload_SurvivesColdJournalRoundTrip()
        {
            LineageSettings.ColdStorageHighWatermarkPercent = 50;
            LineageSettings.ColdStorageTargetPercent = 25;
            LineageSettings.ColdStoragePageSteps = 8;
            LineageSettings.ColdStorageQueuePages = 64;
            LineageSettings.ColdStorageMaxBytes = 8L * 1024L * 1024L;

            using (var scope = CaptureScope.Enter(64))
            {
                var first = Recorder.Produce(1, 0, 0, (int)EventKind.Constant);
                TypedValueRecorder.Remember(42, first);
                var current = first;
                for (var i = 1; i < 200; i++)
                {
                    current = Recorder.Produce(2, current, 0, (int)EventKind.Call);
                }

                var snapshot = scope.Buffer.CreateSnapshot();
                Assert.False(scope.Buffer.Dropped);
                Assert.Equal(200, snapshot.Steps.Length);
                Assert.Equal(LineageValueKind.Int32, snapshot.Steps[0].ValueKind);
                Assert.Null(snapshot.Steps[0].Value);
                Assert.Equal("42", snapshot.Steps[0].FormatValue());
            }
        }

        private static void AssertValue(object value, LineageValueKind expectedKind, string expectedDisplay)
        {
            using (var scope = CaptureScope.Enter(64))
            {
                var id = Recorder.Produce(1, 0, 0, (int)EventKind.Constant);
                TypedValueRecorder.Remember(value, id);

                var step = scope.Buffer.Steps[0];
                Assert.Equal(expectedKind, step.ValueKind);
                Assert.Equal(expectedDisplay, step.FormatValue());

                if (IsCompactScalar(expectedKind))
                {
                    Assert.Null(step.Value);
                }

                var events = scope.Buffer.Events;
                Assert.Equal(expectedKind, events[0].ValueKind);
                Assert.Equal(expectedDisplay, events[0].Value);
            }
        }

        private static bool IsCompactScalar(LineageValueKind kind)
        {
            return kind >= LineageValueKind.Boolean && kind <= LineageValueKind.Guid
                && kind != LineageValueKind.Enum;
        }

        private enum SampleStatus : short
        {
            Pending = 0,
            Paid = 7
        }

        private readonly record struct SamplePoint(int X, int Y)
        {
            public override string ToString() => X + "," + Y;
        }
    }
}
