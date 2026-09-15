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
            BeginCall = ImportRecorder(module, nameof(Recorder.BeginCall));
            PushArg = ImportRecorder(module, nameof(Recorder.PushArg), typeof(int));
            GetArg = ImportRecorder(module, nameof(Recorder.GetArg), typeof(int));
            EndCall = ImportRecorder(module, nameof(Recorder.EndCall));
            NoteCapture = ImportRecorder(module, nameof(Recorder.NoteCapture), typeof(int));
            ExternalCall = ImportRecorder(module, nameof(Recorder.ExternalCall), typeof(int), typeof(int));
            FinishInstrumentedCall = ImportRecorder(module, nameof(Recorder.FinishInstrumentedCall), typeof(int), typeof(int));
            SetReturn = ImportRecorder(module, nameof(Recorder.SetReturn), typeof(int));
            PopReturn = ImportRecorder(module, nameof(Recorder.PopReturn));
            EnterMethod = ImportRecorder(module, nameof(Recorder.EnterMethod), typeof(int));
            LeaveMethod = ImportRecorder(module, nameof(Recorder.LeaveMethod));
            SetFocusTarget = ImportRecorder(module, nameof(Recorder.SetFocusTarget), typeof(int));
            Produce = ImportRecorder(module, nameof(Recorder.Produce), typeof(int), typeof(int), typeof(int), typeof(int));
            Remember = ImportTyped(module, nameof(TypedValueRecorder.Remember), typeof(object), typeof(int));
            OnBranch = ImportRecorder(module, nameof(Recorder.OnBranch), typeof(int), typeof(int));
            FieldWrite = ImportRecorder(module, nameof(Recorder.FieldWrite), typeof(object), typeof(int), typeof(int), typeof(int));
            FieldRead = ImportRecorder(module, nameof(Recorder.FieldRead), typeof(object), typeof(int), typeof(int), typeof(int));
            EnterScope = ImportRecorder(module, nameof(Recorder.EnterScope));
            LeaveScope = ImportRecorder(module, nameof(Recorder.LeaveScope), typeof(CaptureScope));
            OnUnhandled = ImportRecorder(module, nameof(Recorder.OnUnhandled), typeof(Exception));
            ScopeType = module.ImportReference(typeof(CaptureScope));
            ExceptionType = module.ImportReference(typeof(Exception));
        }

        private static MethodReference ImportRecorder(ModuleDefinition module, string name, params Type[] parameters)
        {
            var method = typeof(Recorder).GetMethod(name, parameters);
            if (method == null)
            {
                throw new InvalidOperationException("Missing Recorder method " + name);
            }

            return module.ImportReference(method);
        }

        private static MethodReference ImportTyped(ModuleDefinition module, string name, params Type[] parameters)
        {
            var method = typeof(TypedValueRecorder).GetMethod(name, parameters);
            if (method == null)
            {
                throw new InvalidOperationException("Missing typed value recorder method " + name);
            }

            return module.ImportReference(method);
        }
    }
}
