# Lineage

**Lineage** is an experimental .NET value-lineage debugger: it instruments assemblies at build time and records how values were produced so you can inspect the chain of operations behind a value while debugging.

> **Status:** `v0.1.0` — early release. APIs, instrumentation behavior, and package layout may change.

## What it does

Lineage combines three pieces:

- **Build-time instrumentation** — `Lineage.Instrumentation` rewrites compiled assemblies and injects lineage recording hooks.
- **Lightweight runtime capture** — `Lineage.Runtime` records value relationships and produces a `LineageReport` with origins, nodes, edges, source locations, and value previews.
- **Visual Studio integration** — the VSIX provides a Lineage tool window for exploring captured value history and navigating back to source.

The repository also contains a standalone WPF viewer, sample projects, and automated tests.

## Repository layout

```text
src/
  Lineage.Runtime/          Runtime capture and public API
  Lineage.Instrumentation/  Assembly instrumentation tool
  Lineage.Ide/              Shared graph/session UI logic
  Lineage.VisualStudio/     Visual Studio extension (VSIX)
  Lineage.Viewer/           Standalone WPF viewer
  Lineage.Package/          NuGet package project + MSBuild targets
samples/                    Example projects
tests/                      Runtime, instrumentation, IDE, and integration tests
Lineage.Bundle.proj         Builds the NuGet + VSIX bundle
```

## Requirements

The current source targets:

- `netstandard2.0` for the runtime and package facade
- `net10.0` for the instrumentation tool and console/integration samples
- `net8.0-windows` for the standalone viewer
- Visual Studio extension targets compatible with the manifest's Visual Studio `17.x`–`18.x` range

A matching .NET SDK and Visual Studio workload are required for the projects you build.

## Build

Build the solution normally:

```bash
dotnet build Lineage.slnx
```

To produce the bundled NuGet package and VSIX:

```bash
dotnet msbuild Lineage.Bundle.proj -t:Bundle -p:Configuration=Release
```

The bundle target writes its outputs under `artifacts/`.

## Use the package

For a locally built package, add the generated package source and install `Lineage` version `0.1.0`:

```bash
dotnet add package Lineage --version 0.1.0 --source <path-to-artifacts>
```

The package imports `Lineage.targets`, which enables instrumentation after compilation. Instrumentation can be disabled for a project with:

```xml
<PropertyGroup>
  <LineageInstrument>false</LineageInstrument>
</PropertyGroup>
```

## Trace a value

The runtime exposes both an explicit focus API and a fluent trace helper:

```csharp
using Lineage;

var total = subtotal - discount;

// Produce a report focused on this value.
Lineage.Lineage.Focus(total);

// Or trace inline while preserving the value.
var tracedTotal = total.Trace();

var report = Lineage.Lineage.LastReport;
Console.WriteLine(report);
```

Runtime behavior can be configured through `LineageSettings`, including capture mode, IDE publishing, console output, and break-on-report behavior.

## Visual Studio workflow

Build/install the VSIX from the bundle, open a project using Lineage, rebuild so instrumentation runs, and debug normally. The extension's Lineage tool window consumes published reports and lets you inspect the graph behind the focused value and navigate to recorded source locations.

## Release version

This repository is currently prepared as **v0.1.0**. The shared package version is defined in `Directory.Build.props`; the bundle project and VSIX metadata are kept in sync with it for this release.

## Contributing

Issues and pull requests are welcome. If you are changing instrumentation behavior, add or update coverage in `tests/Lineage.Instrumentation.Tests` and `tests/Lineage.Integration.Tests`; runtime/report changes should be covered in `tests/Lineage.Runtime.Tests`.
