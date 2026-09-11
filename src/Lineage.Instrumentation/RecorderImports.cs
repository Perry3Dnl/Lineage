using System;
using Lineage;
using Lineage.Internal;
using Mono.Cecil;

namespace Lineage.Instrumentation
{
    internal sealed class RecorderImports
    {
        public readonly MethodReference BeginCall;
        public readonly MethodReference PushArg;
        public readonly MethodReference GetArg;
        public readonly MethodReference EndCall;
        public readonly MethodReference NoteCapture;
        public readonly MethodReference ExternalCall;
        public readonly MethodReference FinishInstrumentedCall;
        public readonly MethodReference SetReturn;
        public readonly MethodReference PopReturn;
        public readonly MethodReference EnterMethod;
        public readonly MethodReference LeaveMethod;
        public readonly MethodReference SetFocusTarget;
        public readonly MethodReference Produce;
        public readonly MethodReference Remember;
        public readonly MethodReference OnBranch;
        public readonly MethodReference FieldWrite;
        public readonly MethodReference FieldRead;
        public readonly MethodReference EnterScope;
        public readonly MethodReference LeaveScope;
        public readonly MethodReference OnUnhandled;
        public readonly TypeReference ScopeType;
        public readonly TypeReference ExceptionType;

        public RecorderImports(ModuleDefinition module)
        {
            BeginCall = Import(module, nameof(Recorder.BeginCall));
            PushArg = Import(module, nameof(Recorder.PushArg), typeof(int));
            GetArg = Import(module, nameof(Recorder.GetArg), typeof(int));
            EndCall = Import(module, nameof(Recorder.EndCall));
            NoteCapture = Import(module, nameof(Recorder.NoteCapture), typeof(int));
            ExternalCall = Import(module, nameof(Recorder.ExternalCall), typeof(int), typeof(int));
            FinishInstrumentedCall = Import(module, nameof(Recorder.FinishInstrumentedCall), typeof(int), typeof(int));
            SetReturn = Import(module, nameof(Recorder.SetReturn), typeof(int));
            PopReturn = Import(module, nameof(Recorder.PopReturn));
            EnterMethod = Import(module, nameof(Recorder.EnterMethod), typeof(int));
            LeaveMethod = Import(module, nameof(Recorder.LeaveMethod));
            SetFocusTarget = Import(module, nameof(Recorder.SetFocusTarget), typeof(int));
            Produce = Import(module, nameof(Recorder.Produce), typeof(int), typeof(int), typeof(int), typeof(int));
            Remember = Import(module, nameof(Recorder.Remember), typeof(object), typeof(int));
            OnBranch = Import(module, nameof(Recorder.OnBranch), typeof(int), typeof(int));
            FieldWrite = Import(module, nameof(Recorder.FieldWrite), typeof(object), typeof(int), typeof(int), typeof(int));
            FieldRead = Import(module, nameof(Recorder.FieldRead), typeof(object), typeof(int), typeof(int), typeof(int));
            EnterScope = Import(module, nameof(Recorder.EnterScope));
            LeaveScope = Import(module, nameof(Recorder.LeaveScope), typeof(CaptureScope));
            OnUnhandled = Import(module, nameof(Recorder.OnUnhandled), typeof(Exception));
            ScopeType = module.ImportReference(typeof(CaptureScope));
            ExceptionType = module.ImportReference(typeof(Exception));
        }

        private static MethodReference Import(ModuleDefinition module, string name, params Type[] parameters)
        {
            var method = typeof(Recorder).GetMethod(name, parameters);
            if (method == null)
            {
                throw new InvalidOperationException("Missing Recorder method " + name);
            }

            return module.ImportReference(method);
        }
    }
}
