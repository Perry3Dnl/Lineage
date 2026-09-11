# Lineage

Lineage instruments .NET assemblies at build time and records how values were produced so you can inspect a value's history while debugging.

> Package version: `0.1.0` (early release)

## Install

```bash
dotnet add package Lineage --version 0.1.0
```

For a local build:

```bash
dotnet add package Lineage --version 0.1.0 --source <path-to-nupkg-folder>
```

The package imports its MSBuild targets automatically and runs the Lineage instrumentation step after compilation. To opt out for a project:

```xml
<PropertyGroup>
  <LineageInstrument>false</LineageInstrument>
</PropertyGroup>
```

The bundled Visual Studio extension is stored in the package at `tools/vsix/Lineage.VisualStudio.vsix` when the package is built with VSIX inclusion enabled.

## Trace a value

```csharp
using Lineage;

var result = ComputeResult();
var report = Lineage.Lineage.Focus(result);
Console.WriteLine(report);
```

You can also call `value.Trace()` to capture a report inline and retrieve the most recent report from `Lineage.Lineage.LastReport`.
