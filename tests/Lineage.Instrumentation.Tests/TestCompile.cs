using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Mono.Cecil;

namespace Lineage.Instrumentation.Tests
{
    internal static class TestCompile
    {
        public static string CompileLibrary(string source, string assemblyName)
        {
            var dir = Path.Combine(Path.GetTempPath(), "lineage-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dll = Path.Combine(dir, assemblyName + ".dll");

            var trees = new[] { CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: Path.Combine(dir, "input.cs")) };
            var refs = new List<MetadataReference>();
            var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            foreach (var path in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(path))
                {
                    refs.Add(MetadataReference.CreateFromFile(path));
                }
            }

            refs.Add(MetadataReference.CreateFromFile(typeof(LineageSettings).Assembly.Location));

            var compilation = CSharpCompilation.Create(
                assemblyName,
                trees,
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));

            var pdb = Path.ChangeExtension(dll, ".pdb");
            using (var pe = File.Create(dll))
            using (var pdbStream = File.Create(pdb))
            {
                var result = compilation.Emit(pe, pdbStream);
                if (!result.Success)
                {
                    var errors = string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
                    throw new InvalidOperationException(errors);
                }
            }

            return dll;
        }

        public static bool CallsRecorder(string assemblyPath, string methodName)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            foreach (var type in assembly.MainModule.Types)
            {
                foreach (var method in type.Methods)
                {
                    if (method.Name != methodName || !method.HasBody)
                    {
                        continue;
                    }

                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is MethodReference callee && callee.DeclaringType != null && callee.DeclaringType.Name == "Recorder")
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public static bool HasMetadata(string assemblyPath)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            foreach (var resource in assembly.MainModule.Resources)
            {
                if (resource.Name == MetadataRegistry.ResourceName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
