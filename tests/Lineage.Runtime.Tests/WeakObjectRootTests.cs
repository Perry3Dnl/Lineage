using System.Runtime.CompilerServices;
using Lineage.Internal;

namespace Lineage.Runtime.Tests
{
    public sealed class WeakObjectRootTests : IDisposable
    {
        public WeakObjectRootTests()
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
        public void DeadObject_ReleasesItsFieldRootAndAncestry()
        {
            using (var scope = CaptureScope.Enter(32))
            {
                int fieldStep;
                var weak = CreateTrackedObject(701, out fieldStep);

                Assert.Equal(1, scope.RootCount);
                Assert.Equal(1, scope.TrackedObjectCount);
                Assert.Equal(2, scope.Buffer.Count);
                Assert.NotEmpty(CausalSlice.Collect(scope.Buffer, fieldStep));

                ForceDead(weak);
                var collected = scope.CollectGarbage();

                Assert.Equal(0, scope.RootCount);
                Assert.Equal(0, scope.TrackedObjectCount);
                Assert.Equal(0, scope.Buffer.Count);
                Assert.Equal(0, scope.Buffer.RelationCount);
                Assert.Equal(2, collected.StepsReclaimed);
                Assert.Equal(1, collected.RelationsReclaimed);
            }
        }

        [Fact]
        public void LiveObject_KeepsItsFieldRoot()
        {
            using (var scope = CaptureScope.Enter(32))
            {
                var target = new object();
                var source = Recorder.Produce(711, 0, 0, (int)EventKind.Constant);
                var fieldStep = Recorder.FieldWrite(target, 712, 91, source);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var collected = scope.CollectGarbage();

                Assert.Equal(0, collected.StepsReclaimed);
                Assert.Equal(1, scope.RootCount);
                Assert.Equal(1, scope.TrackedObjectCount);
                Assert.Equal(2, scope.Buffer.Count);
                Assert.Equal(fieldStep, MutationTracker.Read(target, 91, scope.SessionId));
                GC.KeepAlive(target);
            }
        }

        [Fact]
        public void NestedCaptureScopes_KeepIndependentFieldStateForSameObject()
        {
            var target = new object();
            using (var outer = CaptureScope.Enter(32))
            {
                var outerSource = Recorder.Produce(721, 0, 0, (int)EventKind.Constant);
                var outerField = Recorder.FieldWrite(target, 722, 92, outerSource);
                Assert.Equal(outerField, MutationTracker.Read(target, 92, outer.SessionId));

                using (var inner = CaptureScope.Enter(32))
                {
                    var innerSource = Recorder.Produce(723, 0, 0, (int)EventKind.Constant);
                    var innerField = Recorder.FieldWrite(target, 724, 92, innerSource);
                    Assert.Equal(innerField, MutationTracker.Read(target, 92, inner.SessionId));
                    Assert.Equal(1, inner.RootCount);
                }

                Assert.Same(outer, CaptureScope.Current);
                Assert.Equal(outerField, MutationTracker.Read(target, 92, outer.SessionId));
                Assert.Equal(1, outer.RootCount);
                GC.KeepAlive(target);
            }
        }

        [Fact]
        public void DeadObjectChurn_ReusesBoundedCapacityAcrossOneThousandObjects()
        {
            using (var scope = CaptureScope.Enter(128))
            {
                const int batchSize = 50;
                const int batches = 20;

                for (var batch = 0; batch < batches; batch++)
                {
                    var weak = new WeakReference[batchSize];
                    for (var i = 0; i < batchSize; i++)
                    {
                        int ignored;
                        weak[i] = CreateTrackedObject(800 + i, out ignored);
                    }

                    Assert.Equal(batchSize, scope.RootCount);
                    Assert.Equal(batchSize, scope.TrackedObjectCount);
                    Assert.Equal(batchSize * 2, scope.Buffer.Count);

                    ForceDead(weak);
                    scope.CollectGarbage();

                    Assert.Equal(0, scope.RootCount);
                    Assert.Equal(0, scope.TrackedObjectCount);
                    Assert.Equal(0, scope.Buffer.Count);
                    Assert.Equal(0, scope.Buffer.RelationCount);
                    Assert.False(scope.Buffer.Dropped);
                }

                Assert.Equal((batchSize * batches * 2) + 1, scope.NextValueId);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateTrackedObject(int locationId, out int fieldStep)
        {
            var target = new object();
            var source = Recorder.Produce(locationId, 0, 0, (int)EventKind.Constant);
            fieldStep = Recorder.FieldWrite(target, locationId + 1, locationId, source);
            return new WeakReference(target);
        }

        private static void ForceDead(WeakReference weak)
        {
            ForceDead(new[] { weak });
        }

        private static void ForceDead(WeakReference[] weak)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var allDead = true;
                for (var i = 0; i < weak.Length; i++)
                {
                    if (weak[i].IsAlive)
                    {
                        allDead = false;
                        break;
                    }
                }

                if (allDead)
                {
                    return;
                }
            }

            for (var i = 0; i < weak.Length; i++)
            {
                Assert.False(weak[i].IsAlive);
            }
        }
    }
}
