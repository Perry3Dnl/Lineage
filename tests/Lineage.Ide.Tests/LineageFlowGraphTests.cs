using System;
using Lineage;
using LineageIde;

namespace Lineage.Ide.Tests
{
    public sealed class LineageFlowGraphTests
    {
        [Fact]
        public void Build_Empty_ReturnsEmptyGraph()
        {
            var graph = LineageFlowGraph.Build(Array.Empty<LineageNode>());
            Assert.Empty(graph.Items);
            Assert.Empty(graph.Links);
        }

        [Fact]
        public void Build_Chain_StacksTopToBottom()
        {
            var a = Node(1, "input", "App.cs", 10, 0, 0, "hello");
            var b = Node(2, "mapped", "App.cs", 11, 1, 0, "HELLO");
            var c = Node(3, "Trace", "App.cs", 12, 2, 0, "HELLO", EventKind.Focus, ReportCategory.Focus);
            AssignChildren(new[] { a, b, c });

            var graph = LineageFlowGraph.Build(new[] { a, b, c });
            Assert.Equal(3, graph.Items.Count);
            Assert.Equal(2, graph.Links.Count);

            var first = graph.Find(1);
            var second = graph.Find(2);
            var third = graph.Find(3);
            Assert.True(second.Y > first.Y);
            Assert.True(third.Y > second.Y);
            Assert.Equal(first.X, second.X);
            Assert.Equal(second.X, third.X);
            Assert.True(first.CanNavigate);
            Assert.Equal("App.cs:10", first.LocationText);
            Assert.Equal("Set", first.CategoryText);
            Assert.Equal("what", first.NameCaption);
            Assert.Equal("change", first.ValueCaption);
            Assert.Equal("how", first.HowCaption);
        }

        [Fact]
        public void Build_Merge_PlacesParentsAboveChild()
        {
            var left = Node(1, "name", "App.cs", 8, 0, 0, "Widget");
            var right = Node(2, "status", "App.cs", 9, 0, 0, "Active");
            var merge = Node(3, "message", "App.cs", 20, 1, 2, "Widget Active");
            AssignChildren(new[] { left, right, merge });

            var graph = LineageFlowGraph.Build(new[] { left, right, merge });
            Assert.Equal(3, graph.Items.Count);
            Assert.Equal(2, graph.Links.Count);

            var leftItem = graph.Find(1);
            var rightItem = graph.Find(2);
            var child = graph.Find(3);
            Assert.Equal(leftItem.Y, rightItem.Y);
            Assert.True(child.Y > leftItem.Y);
            Assert.True(Math.Abs(leftItem.X - rightItem.X) > 10);
        }

        [Fact]
        public void Build_SkipsCopiesWhereTheValueDidNotChange()
        {
            var origin = Node(1, "product", "App.cs", 8, 0, 0, "Widget");
            var copy = Node(2, "name", "App.cs", 9, 1, 0, "Widget");
            var focus = Node(3, "Trace", "App.cs", 10, 2, 0, "Widget", EventKind.Focus, ReportCategory.Focus);
            AssignChildren(new[] { origin, copy, focus });

            var graph = LineageFlowGraph.Build(new[] { origin, copy, focus });
            Assert.Null(graph.Find(2));
            Assert.NotNull(graph.Find(1));
            Assert.NotNull(graph.Find(3));
            Assert.Single(graph.Links);
            Assert.Equal(1, graph.Links[0].From.Node.ValueId);
            Assert.Equal(3, graph.Links[0].To.Node.ValueId);
        }

        [Fact]
        public void Build_ShowsBeforeAfterAndHowForArithmetic()
        {
            var price = Node(1, "unitPrice", "App.cs", 16, 0, 0, "20");
            var quantity = Node(2, "quantity", "App.cs", 19, 0, 0, "125", EventKind.LocalStore, ReportCategory.Origin);
            var mul = Node(3, "mul", "App.cs", 25, 1, 2, "2500", EventKind.Call, ReportCategory.Transformation);
            var subtotal = Node(4, "subtotal", "App.cs", 25, 3, 0, "2500");
            AssignChildren(new[] { price, quantity, mul, subtotal });

            var graph = LineageFlowGraph.Build(new[] { price, quantity, mul, subtotal });
            Assert.Null(graph.Find(3));
            var item = graph.Find(4);
            Assert.NotNull(item);
            Assert.Equal("20 → 2500", item.ValueText);
            Assert.Equal("× 125", item.HowText);
            Assert.Equal("subtotal", item.Name);
            Assert.Equal(3, item.Bindings.Count);
            Assert.Equal("unitPrice", item.Bindings[0].Name);
            Assert.Equal("20", item.Bindings[0].Value);
            Assert.Equal("quantity", item.Bindings[1].Name);
            Assert.Equal("125", item.Bindings[1].Value);
            Assert.Equal("subtotal", item.Bindings[2].Name);
            Assert.Equal("2500", item.Bindings[2].Value);
            Assert.Equal("starting value", graph.Find(2).HowText);
        }

        [Fact]
        public void Build_KeepsUnitPriceFromFoundProduct()
        {
            var write = Node(1, "UnitPrice", "SampleApp.cs", 50, 0, 0, "20", EventKind.FieldWrite, ReportCategory.PropertyWrite);
            var unitPrice = Node(2, "unitPrice", "SampleApp.cs", 15, 1, 0, "20");
            var quantity = Node(3, "quantity", "SampleApp.cs", 19, 0, 0, "10", EventKind.LocalStore, ReportCategory.Origin);
            var mul = Node(4, "mul", "SampleApp.cs", 25, 2, 3, "200", EventKind.Call, ReportCategory.Transformation);
            var subtotal = Node(5, "subtotal", "SampleApp.cs", 25, 4, 0, "200");
            AssignChildren(new[] { write, unitPrice, quantity, mul, subtotal });

            var graph = LineageFlowGraph.Build(new[] { write, unitPrice, quantity, mul, subtotal });
            Assert.NotNull(graph.Find(2));
            Assert.Equal("20", graph.Find(2).ValueText);
            Assert.Equal("unitPrice", graph.Find(2).Name);
            Assert.Contains(graph.Find(2).Bindings, b => b.Name == "unitPrice" && b.Value == "20");
            Assert.Contains(graph.Find(2).Bindings, b => b.Name == "UnitPrice" && b.Value == "20");
        }

        [Fact]
        public void Build_ShowsRawQuantityFromReadLineThenQuantity()
        {
            var raw = Node(1, "rawQuantity", "SampleApp.cs", 18, 0, 0, "25", EventKind.LocalStore, ReportCategory.Origin);
            var quantity = Node(2, "quantity", "SampleApp.cs", 19, 1, 0, "25");
            var focus = Node(3, "Trace", "SampleApp.cs", 40, 2, 0, "ok", EventKind.Focus, ReportCategory.Focus);
            AssignChildren(new[] { raw, quantity, focus });

            var graph = LineageFlowGraph.Build(new[] { raw, quantity, focus });
            Assert.NotNull(graph.Find(1));
            Assert.NotNull(graph.Find(2));
            Assert.Equal("SampleApp.cs:18", graph.Find(1).LocationText);
            Assert.Equal("SampleApp.cs:19", graph.Find(2).LocationText);
            Assert.Equal("Console.ReadLine()", graph.Find(1).HowText);
            Assert.Equal("parsed", graph.Find(2).HowText);
            Assert.Equal(1, graph.Links[0].From.Node.ValueId);
            Assert.Equal(2, graph.Links[0].To.Node.ValueId);
        }

        private static LineageNode Node(
            int id,
            string name,
            string file,
            int line,
            int parent0 = 0,
            int parent1 = 0,
            string value = "value",
            EventKind kind = EventKind.LocalStore,
            ReportCategory category = ReportCategory.Assignment)
        {
            return new LineageNode(
                id,
                id,
                kind,
                kind == EventKind.Call ? OperationKind.Transformation : OperationKind.None,
                category,
                parent0,
                parent1,
                null,
                name,
                false,
                value,
                file,
                line,
                "Run",
                null,
                "string",
                ValueAvailability.Available);
        }

        private static void AssignChildren(LineageNode[] nodes)
        {
            new LineageReport(nodes);
        }
    }
}
