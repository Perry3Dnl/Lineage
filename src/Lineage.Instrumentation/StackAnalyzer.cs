using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Lineage.Instrumentation
{
    internal static class StackAnalyzer
    {
        public static bool TryAnalyze(MethodDefinition method, out Dictionary<Instruction, int> depthBefore)
        {
            depthBefore = new Dictionary<Instruction, int>();
            if (method == null || !method.HasBody || method.Body.Instructions.Count == 0)
            {
                return false;
            }

            var body = method.Body;
            var work = new Queue<Instruction>();
            var first = body.Instructions[0];
            depthBefore[first] = 0;
            work.Enqueue(first);

            while (work.Count > 0)
            {
                var instruction = work.Dequeue();
                var depth = depthBefore[instruction];
                int pop;
                int push;
                if (!TryGetDelta(instruction, method, out pop, out push))
                {
                    return false;
                }

                if (pop < 0 || depth < pop)
                {
                    return false;
                }

                var nextDepth = depth - pop + push;
                if (nextDepth < 0)
                {
                    return false;
                }

                var opcode = instruction.OpCode;
                if (opcode.FlowControl == FlowControl.Return || opcode.FlowControl == FlowControl.Throw)
                {
                    continue;
                }

                if (opcode.FlowControl == FlowControl.Branch || opcode.FlowControl == FlowControl.Cond_Branch)
                {
                    if (instruction.Operand is Instruction target)
                    {
                        if (!Visit(depthBefore, work, target, nextDepth))
                        {
                            return false;
                        }
                    }
                    else if (instruction.Operand is Instruction[] targets)
                    {
                        for (var i = 0; i < targets.Length; i++)
                        {
                            if (!Visit(depthBefore, work, targets[i], nextDepth))
                            {
                                return false;
                            }
                        }
                    }

                    if (opcode.FlowControl == FlowControl.Branch)
                    {
                        continue;
                    }
                }

                var next = instruction.Next;
                if (next != null && !Visit(depthBefore, work, next, nextDepth))
                {
                    return false;
                }
            }

            return depthBefore.Count > 0;
        }

        private static bool Visit(Dictionary<Instruction, int> depthBefore, Queue<Instruction> work, Instruction instruction, int depth)
        {
            int existing;
            if (depthBefore.TryGetValue(instruction, out existing))
            {
                return existing == depth;
            }

            depthBefore[instruction] = depth;
            work.Enqueue(instruction);
            return true;
        }

        private static bool TryGetDelta(Instruction instruction, MethodDefinition method, out int pop, out int push)
        {
            pop = 0;
            push = 0;
            var code = instruction.OpCode.Code;
            if (code == Code.Call || code == Code.Callvirt)
            {
                var callee = instruction.Operand as MethodReference;
                if (callee == null)
                {
                    return false;
                }

                pop = ArgumentCount(callee);
                push = ReturnsValue(callee) ? 1 : 0;
                return true;
            }

            if (code == Code.Newobj)
            {
                var ctor = instruction.Operand as MethodReference;
                if (ctor == null)
                {
                    return false;
                }

                pop = ctor.Parameters.Count;
                push = 1;
                return true;
            }

            if (code == Code.Ret)
            {
                pop = ReturnsValue(method) ? 1 : 0;
                push = 0;
                return true;
            }

            if (instruction.OpCode.StackBehaviourPop == StackBehaviour.Varpop || instruction.OpCode.StackBehaviourPush == StackBehaviour.Varpush)
            {
                return false;
            }

            pop = CountPop(instruction.OpCode.StackBehaviourPop);
            push = CountPush(instruction.OpCode.StackBehaviourPush);
            return pop >= 0 && push >= 0;
        }

        internal static int ArgumentCount(MethodReference method)
        {
            var count = method.Parameters.Count;
            if (method.HasThis)
            {
                count++;
            }

            return count;
        }

        internal static bool ReturnsValue(MethodReference method)
        {
            return method.ReturnType != null && method.ReturnType.MetadataType != MetadataType.Void;
        }

        internal static bool ReturnsValue(MethodDefinition method)
        {
            return method.ReturnType != null && method.ReturnType.MetadataType != MetadataType.Void;
        }

        private static int CountPop(StackBehaviour behaviour)
        {
            switch (behaviour)
            {
                case StackBehaviour.Pop0:
                    return 0;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref:
                    return 1;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi:
                    return 2;
                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref:
                    return 3;
                default:
                    return -1;
            }
        }

        private static int CountPush(StackBehaviour behaviour)
        {
            switch (behaviour)
            {
                case StackBehaviour.Push0:
                    return 0;
                case StackBehaviour.Push1:
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref:
                    return 1;
                case StackBehaviour.Push1_push1:
                    return 2;
                default:
                    return -1;
            }
        }
    }
}
