using System;
using System.Linq;

namespace Lineage.Instrumentation
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                Console.Error.WriteLine("Usage: Lineage.Instrumentation <assembly> [referenceDir...]");
                return 1;
            }

            var result = AssemblyInstrumenter.Instrument(args[0], args.Skip(1).ToArray());
            if (result.Instrumented)
            {
                Console.WriteLine("Lineage instrumented {0} methods in {1}", result.MethodCount, result.AssemblyPath);
            }

            if (result.Diagnostics != null)
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    Console.WriteLine("Lineage: {0}", diagnostic);
                }
            }

            return 0;
        }
    }
}
