using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Lineage.Instrumentation.Tasks
{
    public sealed class InstrumentTask : Task
    {
        [Required]
        public string AssemblyPath { get; set; }

        public ITaskItem[] References { get; set; }

        public override bool Execute()
        {
            try
            {
                var refs = new string[(References ?? new ITaskItem[0]).Length];
                for (var i = 0; i < refs.Length; i++)
                {
                    refs[i] = References[i].ItemSpec;
                }

                var result = AssemblyInstrumenter.Instrument(AssemblyPath, refs);
                if (result.Instrumented)
                {
                    Log.LogMessage(MessageImportance.High, "Lineage instrumented {0} methods in {1}", result.MethodCount, AssemblyPath);
                }

                if (result.Diagnostics != null)
                {
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        Log.LogMessage(MessageImportance.Low, "Lineage: {0}", diagnostic);
                    }
                }

                return true;
            }
            catch (System.Exception ex)
            {
                Log.LogError("Lineage instrumentation failed: {0}", ex.ToString());
                return false;
            }
        }
    }
}
