# Lineage

Lineage instruments .NET assemblies at build time and records value provenance so you can inspect how a runtime value was produced.

> Package version: `0.2.0` — Provenance Core

## Install

```bash
dotnet add package Lineage --version 0.2.0
```

For a local build:

```bash
dotnet add package Lineage --version 0.2.0 --source <path-to-nupkg-folder>
```

The package imports its MSBuild targets automatically and runs the Lineage instrumentation step after compilation. To opt out for a project:

```xml
<PropertyGroup>
  <LineageInstrument>false</LineageInstrument>
</PropertyGroup>
```

The bundled Visual Studio extension is stored in the package at `tools/vsix/Lineage.VisualStudio.vsix` when the package is built with VSIX inclusion enabled.

## v0.2.0 provenance core

The current runtime uses normalized Steps and child→parent Relations, automatic provenance roots, weak object-field lifetime tracking, reclaimable hot storage, and a RAM-first cold journal with a default 100 MiB disk budget.

Typed payload capture is now first-class for ordinary scalar value types and important framework value types, so the runtime does not need to eagerly turn every captured number into a preview string. Formatting is deferred until reports are hydrated.

If cold storage cannot keep up or the configured budget is exhausted, Lineage marks history as incomplete rather than blocking the debugged application.

## Trace a value

```csharp
using Lineage;

var result = ComputeResult();
var report = Lineage.Lineage.Focus(result);
Console.WriteLine(report);
```

You can also call `value.Trace()` to capture a report inline and retrieve the most recent report from `Lineage.Lineage.LastReport`.
