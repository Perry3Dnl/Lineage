namespace Lineage.Runtime.Tests
{
    public sealed class ProvenanceGcCapacityTests
    {
        [Fact]
        public void Collector_ReusesPhysicalCapacityWhileStepIdsKeepIncreasing()
        {
            var buffer = new EventBuffer(8);

            for (var id = 1; id <= 1000; id++)
            {
                Assert.True(buffer.TryAdd(new LineageEvent
                {
                    ValueId = id,
                    LocationId = id,
                    Kind = EventKind.LocalStore
                }));

                buffer.SetValue(id, id.ToString());
                var collected = buffer.Collect(new[] { id });

                Assert.Equal(1, buffer.Count);
                Assert.Equal(id, buffer.Steps[0].Id);
                Assert.Equal(id.ToString(), buffer.GetValue(id));
                Assert.False(buffer.Dropped);
                if (id > 1)
                {
                    Assert.Equal(1, collected.StepsReclaimed);
                }
            }

            Assert.Equal(1000, buffer.Steps[0].Id);
            Assert.Equal("1000", buffer.Steps[0].Value);
        }
    }
}
