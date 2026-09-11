using System;
using System.Collections.Generic;
using Lineage;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Lineage.Instrumentation
{
    internal sealed class MethodWeaver
    {
        private readonly MethodDefinition _method;
        private readonly RecorderImports _rec;
        private readonly MetadataBuilder _metadata;
        private readonly HashSet<MethodDefinition> _instrumented;
        private readonly ModuleDefinition _module;
        private readonly TypeReference _int32;
        private Dictionary<Instruction, SequencePoint> _points;
        private readonly Dictionary<string, string[]> _sourceLines = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private List<VariableDefinition> _originalLocals = new List<VariableDefinition>();

        public MethodWeaver(MethodDefinition method, RecorderImports rec, MetadataBuilder metadata, HashSet<MethodDefinition> instrumented)
        {
            _method = method;
            _rec = rec;
            _metadata = metadata;
            _instrumented = instrumented;
            _module = method.Module;
            _int32 = _module.TypeSystem.Int32;
        }

        public static bool ShouldSkip(MethodDefinition method)
        {
            if (method == null || !method.HasBody || method.Body.Instructions.Count == 0)
            {
                return true;
            }

            if (method.IsAbstract || method.IsPInvokeImpl || method.IsRuntime || method.IsNative)
            {
                return true;
            }

            if (method.Body.HasExceptionHandlers)
            {
                return true;
            }

            if (method.Name == ".cctor")
            {
                return true;
            }

            var typeName = method.DeclaringType != null ? method.DeclaringType.Name : string.Empty;
            if (KnownOperations.IsCompilerGeneratedName(method.Name) || KnownOperations.IsCompilerGeneratedName(typeName))
            {
                return true;
            }

            var ns = method.DeclaringType != null ? method.DeclaringType.Namespace : string.Empty;
            if (ns == "Lineage.Internal" || ns == "Lineage" && method.DeclaringType.Name == "Recorder")
            {
                return true;
            }

            return false;
        }

        public bool Weave()
        {
            Dictionary<Instruction, int> depths;
            if (!StackAnalyzer.TryAnalyze(_method, out depths))
            {
                return false;
            }

            CaptureSequencePoints();
            _method.Body.SimplifyMacros();
            if (!StackAnalyzer.TryAnalyze(_method, out depths))
            {
                return false;
            }

            var body = _method.Body;
            body.InitLocals = true;
            var il = body.GetILProcessor();

            _originalLocals = new List<VariableDefinition>(body.Variables);
            var localShadows = new Dictionary<VariableDefinition, VariableDefinition>();
            foreach (var local in _originalLocals)
            {
                var shadow = new VariableDefinition(_int32);
                body.Variables.Add(shadow);
                localShadows[local] = shadow;
            }

            var argCount = (_method.HasThis ? 1 : 0) + _method.Parameters.Count;
            var argShadows = new VariableDefinition[argCount];
            for (var i = 0; i < argCount; i++)
            {
                argShadows[i] = new VariableDefinition(_int32);
                body.Variables.Add(argShadows[i]);
            }

            var stackSlots = body.MaxStackSize + 8;
            var stackShadows = new VariableDefinition[stackSlots];
            for (var i = 0; i < stackSlots; i++)
            {
                stackShadows[i] = new VariableDefinition(_int32);
                body.Variables.Add(stackShadows[i]);
            }

            body.MaxStackSize += 8;

            var methodName = _method.FullName;
            var entryId = _metadata.Add(EventKind.MethodEntry, OperationKind.None, methodName, FileOf(_method.Body.Instructions[0]), LineOf(_method.Body.Instructions[0]), "", "", "", false);

            var original = new List<Instruction>(body.Instructions);
            var first = original[0];
            InsertBefore(il, first,
                il.Create(OpCodes.Ldc_I4, entryId),
                il.Create(OpCodes.Call, _rec.EnterMethod),
                il.Create(OpCodes.Pop));

            for (var i = 0; i < argCount; i++)
            {
                InsertBefore(il, first,
                    il.Create(OpCodes.Ldc_I4, i),
                    il.Create(OpCodes.Call, _rec.GetArg),
                    il.Create(OpCodes.Stloc, argShadows[i]));
            }

            foreach (var instruction in original)
            {
                int depth;
                if (!depths.TryGetValue(instruction, out depth))
                {
                    continue;
                }

                WeaveInstruction(il, instruction, depth, localShadows, argShadows, stackShadows);
            }

            body.OptimizeMacros();
            return true;
        }

        private void WeaveInstruction(
            ILProcessor il,
            Instruction instruction,
            int depth,
            Dictionary<VariableDefinition, VariableDefinition> localShadows,
            VariableDefinition[] argShadows,
            VariableDefinition[] stackShadows)
        {
            var code = instruction.OpCode.Code;
            switch (code)
            {
                case Code.Ldstr:
                    PushConstant(il, instruction, depth, stackShadows, instruction.Operand as string);
                    return;
                case Code.Ldnull:
                case Code.Ldc_I4:
                case Code.Ldc_I4_S:
                case Code.Ldc_I4_M1:
                case Code.Ldc_I4_0:
                case Code.Ldc_I4_1:
                case Code.Ldc_I4_2:
                case Code.Ldc_I4_3:
                case Code.Ldc_I4_4:
                case Code.Ldc_I4_5:
                case Code.Ldc_I4_6:
                case Code.Ldc_I4_7:
                case Code.Ldc_I4_8:
                case Code.Ldc_I8:
                case Code.Ldc_R4:
                case Code.Ldc_R8:
                    PushConstant(il, instruction, depth, stackShadows, null);
                    return;
                case Code.Ldloc:
                case Code.Ldloc_S:
                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                case Code.Ldloca:
                case Code.Ldloca_S:
                    CopyToStack(il, instruction, stackShadows[depth], ShadowOf(instruction, localShadows));
                    return;
                case Code.Ldarg:
                case Code.Ldarg_S:
                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                    var argIndex = GetArgIndex(instruction);
                    if (argIndex >= 0 && argIndex < argShadows.Length)
                    {
                        CopyToStack(il, instruction, stackShadows[depth], argShadows[argIndex]);
                    }
                    return;
                case Code.Stloc:
                case Code.Stloc_S:
                case Code.Stloc_0:
                case Code.Stloc_1:
                case Code.Stloc_2:
                case Code.Stloc_3:
                    StoreLocal(il, instruction, depth, localShadows, stackShadows);
                    return;
                case Code.Starg:
                case Code.Starg_S:
                    StoreArg(il, instruction, depth, argShadows, stackShadows);
                    return;
                case Code.Dup:
                    InsertBefore(il, instruction,
                        il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                        il.Create(OpCodes.Stloc, stackShadows[depth]));
                    return;
                case Code.Call:
                case Code.Callvirt:
                    WeaveCall(il, instruction, depth, stackShadows, false);
                    return;
                case Code.Newobj:
                    WeaveCall(il, instruction, depth, stackShadows, true);
                    return;
                case Code.Ret:
                    if (StackAnalyzer.ReturnsValue(_method) && depth > 0)
                    {
                        InsertBefore(il, instruction,
                            il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                            il.Create(OpCodes.Call, _rec.SetReturn));
                    }

                    InsertBefore(il, instruction, il.Create(OpCodes.Call, _rec.LeaveMethod));
                    return;
                case Code.Throw:
                    if (depth > 0)
                    {
                        InsertBefore(il, instruction,
                            il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                            il.Create(OpCodes.Call, _rec.SetFocusTarget));
                    }

                    return;
                case Code.Ldfld:
                    WeaveFieldRead(il, instruction, depth, stackShadows, false);
                    return;
                case Code.Ldsfld:
                    WeaveFieldRead(il, instruction, depth, stackShadows, true);
                    return;
                case Code.Stfld:
                    WeaveFieldWrite(il, instruction, depth, stackShadows, false);
                    return;
                case Code.Stsfld:
                    WeaveFieldWrite(il, instruction, depth, stackShadows, true);
                    return;
                case Code.Brtrue:
                case Code.Brtrue_S:
                case Code.Brfalse:
                case Code.Brfalse_S:
                    if (depth > 0)
                    {
                        var branchId = _metadata.Add(EventKind.Branch, OperationKind.Branch, _method.FullName, FileOf(instruction), LineOf(instruction), "", "", "", false);
                        InsertBefore(il, instruction,
                            il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                            il.Create(OpCodes.Ldc_I4, branchId),
                            il.Create(OpCodes.Call, _rec.OnBranch),
                            il.Create(OpCodes.Pop));
                    }

                    return;
                case Code.Add:
                case Code.Add_Ovf:
                case Code.Add_Ovf_Un:
                    Binary(il, instruction, depth, stackShadows, EventKind.Call, OperationKind.Transformation, "add");
                    return;
                case Code.Sub:
                case Code.Sub_Ovf:
                case Code.Sub_Ovf_Un:
                    Binary(il, instruction, depth, stackShadows, EventKind.Call, OperationKind.Transformation, "sub");
                    return;
                case Code.Mul:
                    Binary(il, instruction, depth, stackShadows, EventKind.Call, OperationKind.Transformation, "mul");
                    return;
                case Code.Div:
                    Binary(il, instruction, depth, stackShadows, EventKind.Call, OperationKind.Transformation, "div");
                    return;
                case Code.Rem:
                    Binary(il, instruction, depth, stackShadows, EventKind.Call, OperationKind.Transformation, "rem");
                    return;
                case Code.Ceq:
                case Code.Cgt:
                case Code.Cgt_Un:
                case Code.Clt:
                case Code.Clt_Un:
                    Binary(il, instruction, depth, stackShadows, EventKind.Call, OperationKind.Comparison, "compare");
                    return;
            }
        }

        private void PushConstant(ILProcessor il, Instruction instruction, int depth, VariableDefinition[] stackShadows, string text)
        {
            var label = string.IsNullOrEmpty(text) ? "constant" : (text.Length <= 24 ? "\"" + text + "\"" : "constant");
            var id = _metadata.Add(EventKind.Constant, OperationKind.Literal, _method.FullName, FileOf(instruction), LineOf(instruction), "", "", label, false);
            InsertBefore(il, instruction,
                il.Create(OpCodes.Ldc_I4, id),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Ldc_I4, (int)EventKind.Constant),
                il.Create(OpCodes.Call, _rec.Produce),
                il.Create(OpCodes.Stloc, stackShadows[depth]));
            InsertRemember(il, instruction, true, ConstantType(instruction), stackShadows[depth]);
        }

        private void CopyToStack(ILProcessor il, Instruction instruction, VariableDefinition dest, VariableDefinition source)
        {
            if (dest == null)
            {
                return;
            }

            if (source == null)
            {
                InsertBefore(il, instruction,
                    il.Create(OpCodes.Ldc_I4_0),
                    il.Create(OpCodes.Stloc, dest));
                return;
            }

            InsertBefore(il, instruction,
                il.Create(OpCodes.Ldloc, source),
                il.Create(OpCodes.Stloc, dest));
        }

        private void StoreLocal(
            ILProcessor il,
            Instruction instruction,
            int depth,
            Dictionary<VariableDefinition, VariableDefinition> localShadows,
            VariableDefinition[] stackShadows)
        {
            var local = GetLocal(instruction);
            VariableDefinition shadow;
            if (local == null || !localShadows.TryGetValue(local, out shadow) || depth < 1)
            {
                return;
            }

            var name = GetLocalName(local);
            if (KnownOperations.IsCompilerGeneratedName(name) || name.StartsWith("CS$"))
            {
                InsertBefore(il, instruction,
                    il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                    il.Create(OpCodes.Stloc, shadow));
                return;
            }
            var id = _metadata.Add(EventKind.LocalStore, OperationKind.Assignment, _method.FullName, FileOf(instruction), LineOf(instruction), name, "", name, false);
            InsertBefore(il, instruction,
                il.Create(OpCodes.Ldc_I4, id),
                il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Ldc_I4, (int)EventKind.LocalStore),
                il.Create(OpCodes.Call, _rec.Produce),
                il.Create(OpCodes.Stloc, shadow));
            InsertRemember(il, instruction, false, local.VariableType, shadow);
        }

        private void StoreArg(ILProcessor il, Instruction instruction, int depth, VariableDefinition[] argShadows, VariableDefinition[] stackShadows)
        {
            var index = GetArgIndex(instruction);
            if (index < 0 || index >= argShadows.Length || depth < 1)
            {
                return;
            }

            var id = _metadata.Add(EventKind.Argument, OperationKind.Assignment, _method.FullName, FileOf(instruction), LineOf(instruction), "", "", "", false);
            InsertBefore(il, instruction,
                il.Create(OpCodes.Ldc_I4, id),
                il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Ldc_I4, (int)EventKind.Argument),
                il.Create(OpCodes.Call, _rec.Produce),
                il.Create(OpCodes.Stloc, argShadows[index]));
        }

        private void WeaveCall(ILProcessor il, Instruction instruction, int depth, VariableDefinition[] stackShadows, bool newobj)
        {
            var callee = instruction.Operand as MethodReference;
            if (callee == null)
            {
                return;
            }

            var typeName = callee.DeclaringType != null ? callee.DeclaringType.Name : string.Empty;
            if (KnownOperations.IsTraceOrFocus(typeName, callee.Name))
            {
                if (depth > 0)
                {
                    InsertBefore(il, instruction,
                        il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                        il.Create(OpCodes.Call, _rec.SetFocusTarget));
                }

                return;
            }

            var argc = newobj ? callee.Parameters.Count : StackAnalyzer.ArgumentCount(callee);
            var returns = newobj || StackAnalyzer.ReturnsValue(callee);
            if (argc > depth)
            {
                return;
            }

            if (KnownOperations.IsCompilerGeneratedName(typeName) || KnownOperations.IsCompilerGeneratedName(callee.Name) || KnownOperations.IsDelegateTypeName(typeName))
            {
                var generatedSlot = depth - argc;
                if (returns && generatedSlot >= 0)
                {
                    var skipLast = InsertAfterCall(il, instruction,
                        il.Create(OpCodes.Ldc_I4_0),
                        il.Create(OpCodes.Stloc, stackShadows[generatedSlot]));
                    InsertRemember(il, skipLast, true, newobj ? callee.DeclaringType : callee.ReturnType, stackShadows[generatedSlot]);
                }

                return;
            }

            InsertBefore(il, instruction, il.Create(OpCodes.Call, _rec.BeginCall));
            for (var i = 0; i < argc; i++)
            {
                InsertBefore(il, instruction,
                    il.Create(OpCodes.Ldloc, stackShadows[depth - argc + i]),
                    il.Create(OpCodes.Call, _rec.PushArg));
            }

            var resolved = TryResolve(callee);
            var instrumented = !newobj && resolved != null && _instrumented.Contains(resolved);
            OperationKind operation;
            string label;
            var classified = KnownOperations.TryClassify(callee.Name, out operation, out label);
            if (!classified)
            {
                operation = instrumented ? OperationKind.Transformation : OperationKind.Opaque;
                label = callee.DeclaringType != null ? callee.DeclaringType.Name + "." + callee.Name : callee.Name;
            }

            if (callee.Name == "ReadLine")
            {
                label = "Console.ReadLine()";
                operation = OperationKind.ExternalCall;
                classified = true;
            }

            if (callee.Name == "ToString")
            {
                operation = OperationKind.Transformation;
                label = "ToString";
                classified = true;
            }

            var opaque = !instrumented && !classified;
            var locId = _metadata.Add(
                EventKind.Call,
                operation,
                _method.FullName,
                FileOf(instruction),
                LineOf(instruction),
                "",
                callee.Name,
                classified || callee.Name == "ReadLine" ? (label.EndsWith("()") ? label : (callee.Name == "ReadLine" ? "Console.ReadLine()" : label)) : label,
                opaque);

            var resultSlot = depth - argc;
            var resultShadow = stackShadows[resultSlot < 0 ? 0 : resultSlot];
            Instruction last;
            if (instrumented)
            {
                last = InsertAfterCall(il, instruction,
                    il.Create(OpCodes.Ldc_I4, locId),
                    il.Create(OpCodes.Ldc_I4, (int)EventKind.Call),
                    il.Create(OpCodes.Call, _rec.FinishInstrumentedCall),
                    il.Create(OpCodes.Stloc, resultShadow));
            }
            else
            {
                last = InsertAfterCall(il, instruction,
                    il.Create(OpCodes.Ldc_I4, locId),
                    il.Create(OpCodes.Ldc_I4, (int)EventKind.Call),
                    il.Create(OpCodes.Call, _rec.ExternalCall),
                    il.Create(OpCodes.Stloc, resultShadow));
            }

            if (returns)
            {
                InsertRemember(il, last, true, newobj ? callee.DeclaringType : callee.ReturnType, resultShadow);
            }
        }

        private void WeaveFieldRead(ILProcessor il, Instruction instruction, int depth, VariableDefinition[] stackShadows, bool isStatic)
        {
            var field = instruction.Operand as FieldReference;
            if (field == null)
            {
                return;
            }

            var token = field.MetadataToken.ToInt32();
            var label = FieldLabel(field);
            var locId = _metadata.Add(EventKind.FieldRead, OperationKind.Selection, _method.FullName, FileOf(instruction), LineOf(instruction), label, "", label, false);
            if (isStatic)
            {
                InsertBefore(il, instruction,
                    il.Create(OpCodes.Ldnull),
                    il.Create(OpCodes.Ldc_I4, locId),
                    il.Create(OpCodes.Ldc_I4, token),
                    il.Create(OpCodes.Ldc_I4_0),
                    il.Create(OpCodes.Call, _rec.FieldRead),
                    il.Create(OpCodes.Stloc, stackShadows[depth]));
                InsertRemember(il, instruction, true, field.FieldType, stackShadows[depth]);
                return;
            }

            if (depth < 1)
            {
                return;
            }

            if (field.DeclaringType != null && field.DeclaringType.IsValueType)
            {
                InsertBefore(il, instruction,
                    il.Create(OpCodes.Ldc_I4, locId),
                    il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                    il.Create(OpCodes.Ldc_I4_0),
                    il.Create(OpCodes.Ldc_I4, (int)EventKind.FieldRead),
                    il.Create(OpCodes.Call, _rec.Produce),
                    il.Create(OpCodes.Stloc, stackShadows[depth - 1]));
                return;
            }

            InsertBefore(il, instruction,
                il.Create(OpCodes.Dup),
                il.Create(OpCodes.Castclass, _module.TypeSystem.Object),
                il.Create(OpCodes.Ldc_I4, locId),
                il.Create(OpCodes.Ldc_I4, token),
                il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                il.Create(OpCodes.Call, _rec.FieldRead),
                il.Create(OpCodes.Stloc, stackShadows[depth - 1]));
            InsertRemember(il, instruction, true, field.FieldType, stackShadows[depth - 1]);
        }

        private void WeaveFieldWrite(ILProcessor il, Instruction instruction, int depth, VariableDefinition[] stackShadows, bool isStatic)
        {
            var field = instruction.Operand as FieldReference;
            if (field == null)
            {
                return;
            }

            var token = field.MetadataToken.ToInt32();
            var label = FieldLabel(field);
            var locId = _metadata.Add(EventKind.FieldWrite, OperationKind.Assignment, _method.FullName, FileOf(instruction), LineOf(instruction), label, "", label, false);
            var capture = field.DeclaringType != null && KnownOperations.IsCompilerGeneratedName(field.DeclaringType.Name);
            var spill = new VariableDefinition(_module.ImportReference(field.FieldType));
            var idSpill = new VariableDefinition(_int32);
            _method.Body.Variables.Add(spill);
            _method.Body.Variables.Add(idSpill);

            if (isStatic)
            {
                if (depth < 1)
                {
                    return;
                }

                InsertBefore(il, instruction,
                    il.Create(OpCodes.Ldnull),
                    il.Create(OpCodes.Ldc_I4, locId),
                    il.Create(OpCodes.Ldc_I4, token),
                    il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                    il.Create(OpCodes.Call, _rec.FieldWrite),
                    capture ? il.Create(OpCodes.Dup) : il.Create(OpCodes.Nop),
                    capture ? il.Create(OpCodes.Call, _rec.NoteCapture) : il.Create(OpCodes.Nop),
                    il.Create(OpCodes.Pop));
                return;
            }

            if (depth < 2)
            {
                return;
            }

            if (field.DeclaringType != null && field.DeclaringType.IsValueType)
            {
                InsertBefore(il, instruction,
                    il.Create(OpCodes.Ldc_I4, locId),
                    il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                    il.Create(OpCodes.Ldc_I4_0),
                    il.Create(OpCodes.Ldc_I4, (int)EventKind.FieldWrite),
                    il.Create(OpCodes.Call, _rec.Produce),
                    capture ? il.Create(OpCodes.Dup) : il.Create(OpCodes.Nop),
                    capture ? il.Create(OpCodes.Call, _rec.NoteCapture) : il.Create(OpCodes.Nop),
                    il.Create(OpCodes.Pop));
                return;
            }

            InsertBefore(il, instruction,
                il.Create(OpCodes.Stloc, spill),
                il.Create(OpCodes.Dup),
                il.Create(OpCodes.Ldloc, spill),
                il.Create(OpCodes.Stfld, field),
                il.Create(OpCodes.Castclass, _module.TypeSystem.Object),
                il.Create(OpCodes.Ldc_I4, locId),
                il.Create(OpCodes.Ldc_I4, token),
                il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                il.Create(OpCodes.Call, _rec.FieldWrite),
                il.Create(OpCodes.Stloc, idSpill));
            InsertRememberFrom(il, instruction, field.FieldType, spill, idSpill);
            if (capture)
            {
                InsertBefore(il, instruction,
                    il.Create(OpCodes.Ldloc, idSpill),
                    il.Create(OpCodes.Call, _rec.NoteCapture));
            }

            instruction.OpCode = OpCodes.Nop;
            instruction.Operand = null;
        }

        private void Binary(ILProcessor il, Instruction instruction, int depth, VariableDefinition[] stackShadows, EventKind kind, OperationKind operation, string label)
        {
            if (depth < 2)
            {
                return;
            }

            var id = _metadata.Add(kind, operation, _method.FullName, FileOf(instruction), LineOf(instruction), "", label, label, false);
            InsertBefore(il, instruction,
                il.Create(OpCodes.Ldc_I4, id),
                il.Create(OpCodes.Ldloc, stackShadows[depth - 2]),
                il.Create(OpCodes.Ldloc, stackShadows[depth - 1]),
                il.Create(OpCodes.Ldc_I4, (int)kind),
                il.Create(OpCodes.Call, _rec.Produce),
                il.Create(OpCodes.Stloc, stackShadows[depth - 2]));
        }

        private string FieldLabel(FieldReference field)
        {
            var fromAccessor = PropertyNameFromAccessor();
            if (!string.IsNullOrEmpty(fromAccessor))
            {
                return fromAccessor;
            }

            return FriendlyFieldName(field != null ? field.Name : null);
        }

        private string PropertyNameFromAccessor()
        {
            var name = _method.Name;
            if (name != null && name.Length > 4 && (name.StartsWith("get_") || name.StartsWith("set_")))
            {
                return name.Substring(4);
            }

            return null;
        }

        private static string FriendlyFieldName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "field";
            }

            if (name.Length > 2 && name[0] == '<' && name.IndexOf(">k__BackingField", StringComparison.Ordinal) > 0)
            {
                return name.Substring(1, name.IndexOf('>') - 1);
            }

            return name;
        }

        private VariableDefinition ShadowOf(Instruction instruction, Dictionary<VariableDefinition, VariableDefinition> localShadows)
        {
            var local = GetLocal(instruction);
            VariableDefinition shadow;
            if (local != null && localShadows.TryGetValue(local, out shadow))
            {
                return shadow;
            }

            var index = LocalIndex(instruction);
            if (index >= 0 && index < _originalLocals.Count && localShadows.TryGetValue(_originalLocals[index], out shadow))
            {
                return shadow;
            }

            return null;
        }

        private VariableDefinition GetLocal(Instruction instruction)
        {
            var index = LocalIndex(instruction);
            if (index >= 0 && index < _originalLocals.Count)
            {
                return _originalLocals[index];
            }

            var local = instruction.Operand as VariableDefinition;
            if (local != null)
            {
                return local;
            }

            return null;
        }

        private static int LocalIndex(Instruction instruction)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldloc_0:
                case Code.Stloc_0:
                    return 0;
                case Code.Ldloc_1:
                case Code.Stloc_1:
                    return 1;
                case Code.Ldloc_2:
                case Code.Stloc_2:
                    return 2;
                case Code.Ldloc_3:
                case Code.Stloc_3:
                    return 3;
            }

            var reference = instruction.Operand as VariableReference;
            if (reference != null)
            {
                return reference.Index;
            }

            if (instruction.Operand is byte)
            {
                return (byte)instruction.Operand;
            }

            if (instruction.Operand is int)
            {
                return (int)instruction.Operand;
            }

            return -1;
        }

        private int GetArgIndex(Instruction instruction)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldarg_0:
                    return 0;
                case Code.Ldarg_1:
                    return 1;
                case Code.Ldarg_2:
                    return 2;
                case Code.Ldarg_3:
                    return 3;
            }

            var parameter = instruction.Operand as ParameterDefinition;
            if (parameter != null)
            {
                return _method.HasThis ? parameter.Index + 1 : parameter.Index;
            }

            if (instruction.Operand is int)
            {
                return (int)instruction.Operand;
            }

            return -1;
        }

        private string GetLocalName(VariableDefinition local)
        {
            if (_method.DebugInformation.Scope == null)
            {
                return "local";
            }

            return FindName(_method.DebugInformation.Scope, OriginalIndex(local)) ?? "local";
        }

        private int OriginalIndex(VariableDefinition local)
        {
            if (local == null)
            {
                return -1;
            }

            for (var i = 0; i < _originalLocals.Count; i++)
            {
                if (ReferenceEquals(_originalLocals[i], local))
                {
                    return i;
                }
            }

            return local.Index;
        }

        private static string FindName(ScopeDebugInformation scope, int index)
        {
            if (scope == null || index < 0)
            {
                return null;
            }

            if (scope.HasVariables)
            {
                foreach (var variable in scope.Variables)
                {
                    if (variable.Index == index)
                    {
                        return string.IsNullOrEmpty(variable.Name) ? null : variable.Name;
                    }
                }
            }

            if (scope.HasScopes)
            {
                foreach (var child in scope.Scopes)
                {
                    var name = FindName(child, index);
                    if (name != null)
                    {
                        return name;
                    }
                }
            }

            return null;
        }

        private MethodDefinition TryResolve(MethodReference reference)
        {
            try
            {
                return reference.Resolve();
            }
            catch
            {
                return null;
            }
        }

        private string FileOf(Instruction instruction)
        {
            var point = PointOf(instruction);
            return point != null && point.Document != null ? point.Document.Url : string.Empty;
        }

        private int LineOf(Instruction instruction)
        {
            var point = PointOf(instruction);
            var line = point != null ? point.StartLine : 0;
            var literal = instruction != null && instruction.OpCode.Code == Code.Ldstr ? instruction.Operand as string : null;
            if (string.IsNullOrEmpty(literal) || point == null || point.Document == null)
            {
                return line;
            }

            var start = line > 0 ? line : 1;
            var end = point.EndLine >= start ? point.EndLine : start + 12;
            var found = FindQuotedLiteralLine(point.Document.Url, literal, start, end);
            return found > 0 ? found : line;
        }

        private int FindQuotedLiteralLine(string path, string literal, int startLine, int endLine)
        {
            var lines = SourceLines(path);
            if (lines == null || lines.Length == 0)
            {
                return 0;
            }

            var needle = "\"" + literal + "\"";
            var from = startLine < 1 ? 1 : startLine;
            var to = endLine < from ? from : endLine;
            if (to > lines.Length)
            {
                to = lines.Length;
            }

            for (var i = from; i <= to; i++)
            {
                if (lines[i - 1].IndexOf(needle, StringComparison.Ordinal) >= 0)
                {
                    return i;
                }
            }

            return 0;
        }

        private string[] SourceLines(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string[] lines = null;
            if (_sourceLines.TryGetValue(path, out lines))
            {
                return lines;
            }

            try
            {
                if (System.IO.File.Exists(path))
                {
                    lines = System.IO.File.ReadAllLines(path);
                }
            }
            catch
            {
                lines = null;
            }

            _sourceLines[path] = lines;
            return lines;
        }

        private SequencePoint PointOf(Instruction instruction)
        {
            for (var current = instruction; current != null; current = current.Previous)
            {
                SequencePoint point;
                if (_points != null && _points.TryGetValue(current, out point) && IsUsable(point))
                {
                    return point;
                }
            }

            var steps = 0;
            for (var current = instruction != null ? instruction.Next : null; current != null && steps < 8; current = current.Next, steps++)
            {
                SequencePoint point;
                if (_points != null && _points.TryGetValue(current, out point) && IsUsable(point))
                {
                    return point;
                }
            }

            return null;
        }

        private void CaptureSequencePoints()
        {
            _points = new Dictionary<Instruction, SequencePoint>();
            if (!_method.HasBody || !_method.DebugInformation.HasSequencePoints)
            {
                return;
            }

            foreach (var instruction in _method.Body.Instructions)
            {
                var point = _method.DebugInformation.GetSequencePoint(instruction);
                if (point != null)
                {
                    _points[instruction] = point;
                }
            }
        }

        private static bool IsUsable(SequencePoint point)
        {
            return point != null
                && !point.IsHidden
                && point.StartLine > 0
                && point.Document != null
                && !string.IsNullOrEmpty(point.Document.Url);
        }

        private static void InsertBefore(ILProcessor il, Instruction target, params Instruction[] extras)
        {
            if (extras == null || extras.Length == 0 || target == null)
            {
                return;
            }

            for (var i = 0; i < extras.Length; i++)
            {
                il.InsertBefore(target, extras[i]);
            }

            Retarget(il.Body, target, extras[0]);
        }

        private static void Retarget(MethodBody body, Instruction from, Instruction to)
        {
            if (body == null || from == null || to == null || from == to)
            {
                return;
            }

            foreach (var instruction in body.Instructions)
            {
                if (instruction.Operand == from)
                {
                    instruction.Operand = to;
                    continue;
                }

                var targets = instruction.Operand as Instruction[];
                if (targets == null)
                {
                    continue;
                }

                for (var i = 0; i < targets.Length; i++)
                {
                    if (targets[i] == from)
                    {
                        targets[i] = to;
                    }
                }
            }

            if (!body.HasExceptionHandlers)
            {
                return;
            }

            foreach (var handler in body.ExceptionHandlers)
            {
                if (handler.TryStart == from)
                {
                    handler.TryStart = to;
                }

                if (handler.HandlerStart == from)
                {
                    handler.HandlerStart = to;
                }

                if (handler.FilterStart == from)
                {
                    handler.FilterStart = to;
                }
            }
        }

        private static Instruction InsertAfterCall(ILProcessor il, Instruction call, params Instruction[] extras)
        {
            Instruction previous = call;
            for (var i = 0; i < extras.Length; i++)
            {
                il.InsertAfter(previous, extras[i]);
                previous = extras[i];
            }

            return previous;
        }

        private TypeReference ConstantType(Instruction instruction)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldstr:
                    return _module.TypeSystem.String;
                case Code.Ldnull:
                    return _module.TypeSystem.Object;
                case Code.Ldc_I8:
                    return _module.TypeSystem.Int64;
                case Code.Ldc_R4:
                    return _module.TypeSystem.Single;
                case Code.Ldc_R8:
                    return _module.TypeSystem.Double;
                default:
                    return _module.TypeSystem.Int32;
            }
        }

        private static bool CanRemember(TypeReference type)
        {
            if (type == null || type.IsByReference || type.IsPointer || type.IsFunctionPointer || type.IsGenericParameter)
            {
                return false;
            }

            return type.MetadataType != MetadataType.Void && type.MetadataType != MetadataType.TypedByReference;
        }

        private void InsertRemember(ILProcessor il, Instruction target, bool after, TypeReference type, VariableDefinition shadow)
        {
            if (!CanRemember(type) || shadow == null)
            {
                return;
            }

            var extras = RememberInstructions(il, type, null, shadow);
            if (after)
            {
                InsertAfterCall(il, target, extras);
            }
            else
            {
                InsertBefore(il, target, extras);
            }
        }

        private void InsertRememberFrom(ILProcessor il, Instruction target, TypeReference type, VariableDefinition valueLocal, VariableDefinition shadow)
        {
            if (!CanRemember(type) || valueLocal == null || shadow == null)
            {
                return;
            }

            InsertBefore(il, target, RememberInstructions(il, type, valueLocal, shadow));
        }

        private Instruction[] RememberInstructions(ILProcessor il, TypeReference type, VariableDefinition valueLocal, VariableDefinition shadow)
        {
            var imported = _module.ImportReference(type);
            var list = new List<Instruction>();
            if (valueLocal != null)
            {
                list.Add(il.Create(OpCodes.Ldloc, valueLocal));
            }
            else
            {
                list.Add(il.Create(OpCodes.Dup));
            }

            if (imported.IsValueType)
            {
                list.Add(il.Create(OpCodes.Box, imported));
            }
            else if (imported.IsGenericParameter)
            {
                list.Add(il.Create(OpCodes.Box, imported));
            }

            list.Add(il.Create(OpCodes.Ldloc, shadow));
            list.Add(il.Create(OpCodes.Call, _rec.Remember));
            return list.ToArray();
        }
    }
}
