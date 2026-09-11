using System.Reflection;
using Lineage;
using Mono.Cecil;
using static Lineage.Lineage;

namespace Lineage.Instrumentation.Tests
{
    public sealed class InstrumenterTests
    {
        [Fact]
        public void Metadata_IncludesSourceFileAndLine()
        {
            var dll = TestCompile.CompileLibrary(@"
using Lineage;
public static class S {
    public static string Run(string username) {
        var cleaned = username.Trim();
        return cleaned.Trace();
    }
}
", "SourceInfo");
            AssemblyInstrumenter.Instrument(dll);
            using var assembly = AssemblyDefinition.ReadAssembly(dll);
            EmbeddedResource resource = null;
            foreach (var item in assembly.MainModule.Resources)
            {
                if (item.Name == MetadataRegistry.ResourceName)
                {
                    resource = (EmbeddedResource)item;
                    break;
                }
            }

            Assert.NotNull(resource);
            using var stream = resource.GetResourceStream();
            var infos = MetadataCodec.Read(stream);
            Assert.Contains(infos, info => info.Kind == EventKind.LocalStore && info.Line > 0 && !string.IsNullOrEmpty(info.File));
            Assert.Contains(infos, info => info.Kind == EventKind.Call && info.Line > 0 && !string.IsNullOrEmpty(info.File));
        }

        [Fact]
        public void LocalStore_UsesStatementLineNotMethodBrace()
        {
            var dll = TestCompile.CompileLibrary(@"
using Lineage;
public static class S {
    public static string Run(string username) {
        var cleaned = username.Trim();
        return cleaned.Trace();
    }
}
", "StoreLine");
            AssemblyInstrumenter.Instrument(dll);
            using var assembly = AssemblyDefinition.ReadAssembly(dll);
            EmbeddedResource resource = null;
            foreach (var item in assembly.MainModule.Resources)
            {
                if (item.Name == MetadataRegistry.ResourceName)
                {
                    resource = (EmbeddedResource)item;
                    break;
                }
            }

            Assert.NotNull(resource);
            using var stream = resource.GetResourceStream();
            var infos = MetadataCodec.Read(stream);
            LocationInfo store = null;
            LocationInfo entry = null;
            for (var i = 0; i < infos.Count; i++)
            {
                if (infos[i].Kind == EventKind.LocalStore && infos[i].LocalName == "cleaned")
                {
                    store = infos[i];
                }

                if (infos[i].Kind == EventKind.MethodEntry)
                {
                    entry = infos[i];
                }
            }

            Assert.NotNull(store);
            Assert.True(store.Line > 0);
            if (entry != null && entry.Line > 0)
            {
                Assert.True(store.Line > entry.Line, "store line " + store.Line + " should be after method brace " + entry.Line);
            }
        }

        [Fact]
        public void InstrumentsMethodEntryAndLocalStore()
        {
            var dll = TestCompile.CompileLibrary(@"
using Lineage;
public static class S {
    public static string Run(string username) {
        var cleaned = username.Trim();
        return cleaned.Trace();
    }
}
", "Locals");
            var result = AssemblyInstrumenter.Instrument(dll);
            Assert.True(result.Instrumented);
            Assert.True(TestCompile.HasMetadata(dll));
            Assert.True(TestCompile.CallsRecorder(dll, "Run"));
        }

        [Fact]
        public void InstrumentsCallsAndFocus()
        {
            var dll = TestCompile.CompileLibrary(@"
using Lineage;
public static class S {
    public static string Run() {
        var value = ""Alice"";
        return value.Trace();
    }
}
", "FocusCall");
            AssemblyInstrumenter.Instrument(dll);
            Assert.True(TestCompile.CallsRecorder(dll, "Run"));
        }

        [Fact]
        public void InstrumentsFieldWrite()
        {
            var dll = TestCompile.CompileLibrary(@"
public class Box { public string Status; }
public static class S {
    public static void Run(Box box, string status) {
        box.Status = status;
    }
}
", "Fields");
            AssemblyInstrumenter.Instrument(dll);
            Assert.True(TestCompile.CallsRecorder(dll, "Run"));
        }

        [Fact]
        public void InstrumentsBranch()
        {
            var dll = TestCompile.CompileLibrary(@"
public static class S {
    public static string Run(string username) {
        return username?.Trim();
    }
}
", "Branch");
            AssemblyInstrumenter.Instrument(dll);
            Assert.True(TestCompile.CallsRecorder(dll, "Run"));
        }

        [Fact]
        public void UnitPrice_ReachesMultiplyAfterGuard()
        {
            var dll = TestCompile.CompileLibrary(@"
using Lineage;
public class Product { public int UnitPrice { get; set; } }
public static class S {
    public static string Run(int quantity) {
        var product = new Product { UnitPrice = 20 };
        var unitPrice = product.UnitPrice;
        if (quantity <= 0) {
            return ""bad"".Trace();
        }
        var subtotal = unitPrice * quantity;
        return subtotal.ToString().Trace();
    }
}
", "UnitPriceMul");
            AssemblyInstrumenter.Instrument(dll);

            LineageSettings.Mode = LineageMode.Strict;
            LineageSettings.PublishToIde = false;
            MetadataRegistry.Clear();
            CaptureScope.Current?.Dispose();

            var assembly = Assembly.LoadFrom(dll);
            using (CaptureScope.Enter())
            {
                var result = (string)assembly.GetType("S").GetMethod("Run").Invoke(null, new object[] { 10 });
                Assert.Equal("200", result);
                Assert.NotNull(LastReport);
                Assert.Contains(LastReport.Nodes, n => (n.DisplayName == "unitPrice" || n.DisplayName == "UnitPrice") && n.FormatStepValue() == "20");
            }
        }

        [Fact]
        public void SkipsAlreadyInstrumented()
        {
            var dll = TestCompile.CompileLibrary(@"
public static class S { public static int Run() { return 1; } }
", "Twice");
            Assert.True(AssemblyInstrumenter.Instrument(dll).Instrumented);
            Assert.False(AssemblyInstrumenter.Instrument(dll).Instrumented);
        }
    }
}
