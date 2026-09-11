using System;
using System.Collections.Generic;
using System.IO;
using Lineage;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Lineage.Instrumentation
{
    public sealed class InstrumentationResult
    {
        public bool Instrumented { get; set; }
        public int MethodCount { get; set; }
        public string AssemblyPath { get; set; }
        public IReadOnlyList<string> Diagnostics { get; set; }
    }

    public static class AssemblyInstrumenter
    {
        public static InstrumentationResult Instrument(string assemblyPath, IEnumerable<string> referencePaths = null)
        {
            var diagnostics = new List<string>();
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new FileNotFoundException("Assembly not found", assemblyPath);
            }

            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath));
            if (referencePaths != null)
            {
                foreach (var path in referencePaths)
                {
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        resolver.AddSearchDirectory(dir);
                    }
                }
            }

            var reader = new ReaderParameters
            {
                ReadWrite = true,
                ReadSymbols = File.Exists(pdbPath),
                AssemblyResolver = resolver
            };

            using (var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, reader))
            {
                if (assembly.Name.Name == "Lineage.Runtime" || assembly.Name.Name == "Lineage.Instrumentation")
                {
                    return new InstrumentationResult
                    {
                        Instrumented = false,
                        AssemblyPath = assemblyPath,
                        Diagnostics = diagnostics
                    };
                }

                var already = false;
                foreach (var resource in assembly.MainModule.Resources)
                {
                    if (resource.Name == MetadataRegistry.ResourceName)
                    {
                        already = true;
                        break;
                    }
                }

                if (already)
                {
                    diagnostics.Add("Already instrumented");
                    return new InstrumentationResult
                    {
                        Instrumented = false,
                        AssemblyPath = assemblyPath,
                        Diagnostics = diagnostics
                    };
                }

                var module = assembly.MainModule;
                var imports = new RecorderImports(module);
                var metadata = new MetadataBuilder();
                var candidates = new List<MethodDefinition>();
                var instrumented = new HashSet<MethodDefinition>();

                foreach (var type in module.Types)
                {
                    CollectMethods(type, candidates);
                }

                foreach (var method in candidates)
                {
                    Dictionary<Instruction, int> depths;
                    if (!MethodWeaver.ShouldSkip(method) && StackAnalyzer.TryAnalyze(method, out depths))
                    {
                        instrumented.Add(method);
                    }
                }

                var count = 0;
                foreach (var method in instrumented)
                {
                    try
                    {
                        if (new MethodWeaver(method, imports, metadata, instrumented).Weave())
                        {
                            count++;
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add(method.FullName + ": " + ex.Message);
                    }
                }

                if (assembly.EntryPoint != null && !assembly.EntryPoint.Body.HasExceptionHandlers)
                {
                    try
                    {
                        WrapEntryPoint(assembly.EntryPoint, imports);
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add("EntryPoint wrap: " + ex.Message);
                    }
                }

                var bytes = MetadataCodec.Write(metadata.Infos);
                module.Resources.Add(new EmbeddedResource(MetadataRegistry.ResourceName, ManifestResourceAttributes.Public, bytes));

                var writer = new WriterParameters { WriteSymbols = reader.ReadSymbols };
                assembly.Write(writer);
                return new InstrumentationResult
                {
                    Instrumented = true,
                    MethodCount = count,
                    AssemblyPath = assemblyPath,
                    Diagnostics = diagnostics
                };
            }
        }

        private static void CollectMethods(TypeDefinition type, List<MethodDefinition> methods)
        {
            if (type.HasMethods)
            {
                methods.AddRange(type.Methods);
            }

            if (type.HasNestedTypes)
            {
                foreach (var nested in type.NestedTypes)
                {
                    CollectMethods(nested, methods);
                }
            }
        }

        internal static void WrapEntryPoint(MethodDefinition main, RecorderImports rec)
        {
            var body = main.Body;
            var il = body.GetILProcessor();
            var scopeVar = new VariableDefinition(rec.ScopeType);
            body.Variables.Add(scopeVar);
            VariableDefinition resultVar = null;
            if (StackAnalyzer.ReturnsValue(main))
            {
                resultVar = new VariableDefinition(main.ReturnType);
                body.Variables.Add(resultVar);
            }

            var tryStart = body.Instructions[0];
            il.InsertBefore(tryStart, il.Create(OpCodes.Call, rec.EnterScope));
            il.InsertBefore(tryStart, il.Create(OpCodes.Stloc, scopeVar));

            var catchStart = il.Create(OpCodes.Call, rec.OnUnhandled);
            var rethrow = il.Create(OpCodes.Rethrow);
            var finallyStart = il.Create(OpCodes.Ldloc, scopeVar);
            var after = il.Create(OpCodes.Nop);
            var endFinally = il.Create(OpCodes.Endfinally);
            var finalRet = il.Create(OpCodes.Ret);

            var toReplace = new List<Instruction>();
            foreach (var instruction in body.Instructions)
            {
                if (instruction.OpCode == OpCodes.Ret)
                {
                    toReplace.Add(instruction);
                }
            }

            foreach (var ret in toReplace)
            {
                if (resultVar != null)
                {
                    ret.OpCode = OpCodes.Stloc;
                    ret.Operand = resultVar;
                    il.InsertAfter(ret, il.Create(OpCodes.Leave, after));
                }
                else
                {
                    ret.OpCode = OpCodes.Leave;
                    ret.Operand = after;
                }
            }

            body.Instructions.Add(catchStart);
            body.Instructions.Add(rethrow);
            body.Instructions.Add(finallyStart);
            body.Instructions.Add(il.Create(OpCodes.Call, rec.LeaveScope));
            body.Instructions.Add(endFinally);
            body.Instructions.Add(after);
            if (resultVar != null)
            {
                body.Instructions.Add(il.Create(OpCodes.Ldloc, resultVar));
            }

            body.Instructions.Add(finalRet);

            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                CatchType = rec.ExceptionType,
                TryStart = tryStart,
                TryEnd = catchStart,
                HandlerStart = catchStart,
                HandlerEnd = finallyStart
            });
            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
            {
                TryStart = tryStart,
                TryEnd = finallyStart,
                HandlerStart = finallyStart,
                HandlerEnd = after
            });
        }
    }
}
