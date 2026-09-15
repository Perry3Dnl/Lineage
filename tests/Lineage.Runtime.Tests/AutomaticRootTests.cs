using Lineage.Internal;

namespace Lineage.Runtime.Tests
{
    public sealed class AutomaticRootTests : IDisposable
    {
        public AutomaticRootTests()
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
        public void NestedFrameExit_PreservesCallerAndRestoresFrame()
        {
            using (var scope = CaptureScope.Enter(32))
            {
                var parentFrame = Recorder.EnterMethod(900);
                var parentOrigin = Recorder.Produce(901, 0, 0, (int)EventKind.Constant);
                var parentLocal = Recorder.Produce(902, parentOrigin, 0, (int)EventKind.LocalStore);

                var childFrame = Recorder.EnterMethod(910);
                var childOrigin = Recorder.Produce(911, 0, 0, (int)EventKind.Constant);
                Recorder.Produce(912, childOrigin, 0, (int)EventKind.LocalStore);

                Assert.Equal(childFrame, scope.CurrentFrameId);
                Recorder.LeaveMethod();

                Assert.Equal(parentFrame, scope.CurrentFrameId);
                Assert.True(scope.Buffer.Count >= 4);
                Assert.NotEmpty(CausalSlice.Collect(scope.Buffer, parentLocal));

                Recorder.LeaveMethod();
                Assert.Equal(0, scope.CurrentFrameId);
                Assert.Equal(0, scope.Buffer.Count);
            }
        }

        [Fact]
        public void FieldRoot_SurvivesFrameCollectionAndMovesOnOverwrite()
        {
            using (var scope = CaptureScope.Enter(32))
            {
                var target = new object();

                Recorder.EnterMethod(1000);
                var firstOrigin = Recorder.Produce(1001, 0, 0, (int)EventKind.Constant);
                var firstWrite = Recorder.FieldWrite(target, 1002, 77, firstOrigin);
                Recorder.LeaveMethod();

                Assert.Equal(2, scope.Buffer.Count);
                Assert.Equal(2, CausalSlice.Collect(scope.Buffer, firstWrite).Count);

                Recorder.EnterMethod(1010);
                var secondOrigin = Recorder.Produce(1011, 0, 0, (int)EventKind.Constant);
                var secondWrite = Recorder.FieldWrite(target, 1012, 77, secondOrigin);
                Recorder.LeaveMethod();

                Assert.Equal(2, scope.Buffer.Count);
                Assert.Empty(CausalSlice.Collect(scope.Buffer, firstWrite));
                var slice = CausalSlice.Collect(scope.Buffer, secondWrite);
                Assert.Equal(2, slice.Count);
                Assert.Equal(secondOrigin, slice[0].ValueId);
                Assert.Equal(secondWrite, slice[1].ValueId);
            }
        }

        [Fact]
        public void FieldState_DoesNotLeakAcrossCaptureSessions()
        {
            var target = new object();
            int oldWrite;

            using (var first = CaptureScope.Enter(16))
            {
                Recorder.EnterMethod(1100);
                var origin = Recorder.Produce(1101, 0, 0, (int)EventKind.Constant);
                oldWrite = Recorder.FieldWrite(target, 1102, 88, origin);
                Recorder.LeaveMethod();
                Assert.Equal(2, first.Buffer.Count);
            }

            using (var second = CaptureScope.Enter(16))
            {
                Recorder.EnterMethod(1110);
                var read = Recorder.FieldRead(target, 1111, 88, 0);
                var slice = CausalSlice.Collect(second.Buffer, read);

                Assert.Single(slice);
                Assert.Equal(read, slice[0].ValueId);
                Assert.NotEqual(oldWrite, 0);

                Recorder.LeaveMethod();
            }
        }

        [Fact]
        public void TopLevelReturn_IsKeptUntilAFollowingSafeBoundary()
        {
            using (var scope = CaptureScope.Enter(16))
            {
                Recorder.EnterMethod(1200);
                var origin = Recorder.Produce(1201, 0, 0, (int)EventKind.Constant);
                var result = Recorder.Produce(1202, origin, 0, (int)EventKind.LocalStore);
                Recorder.SetReturn(result);
                Recorder.LeaveMethod();

                Assert.Equal(2, scope.Buffer.Count);

                // No BeginCall frame exists, so this is a new top-level entry. The old
                // pending return can no longer be consumed by instrumented code.
                Recorder.EnterMethod(1210);
                Assert.Equal(0, scope.Buffer.Count);
                Recorder.LeaveMethod();
            }
        }
    }
}
