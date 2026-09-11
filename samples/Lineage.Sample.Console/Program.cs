using Lineage;
using Lineage.Sample;

LineageSettings.Mode = LineageMode.All;
try
{
    SampleApp.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    throw;
}
