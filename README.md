# OpenDevelop

**A modern, open-source, cross-platform IDE for C# and .NET, built from the best ideas and technologies across the .NET development tools ecosystem.**

OpenDevelop started from the classic SharpDevelop code base, but it has since been extensively re-engineered for modern .NET. It brings together technologies and ideas from SharpDevelop, MonoDevelop and Visual Studio for Mac, ILSpy, XAML Studio, Roslyn, and the Visual Studio Editor platform, while building new infrastructure of its own.

OpenDevelop runs on .NET 10 through [LibreWPF](https://github.com/wieslawsoltes/WPF), bringing a WPF-based development environment to Windows, macOS, and Linux.

![WPF designer](images/wpf-designer.png)

*OpenDevelop running its WPF visual designer.*

## Why OpenDevelop?

SharpDevelop started in 2000 as one of the earliest open-source IDEs for .NET. It demonstrated that a capable .NET development environment did not have to be tied to Visual Studio, and over the years built a remarkably extensible IDE around its workbench, AddInTree, AvalonEdit, AvalonDock, project system, debugger integrations, and visual designers.

The .NET tooling ecosystem changed dramatically after that.

Roslyn replaced the old C# and Visual Basic compiler services. SDK-style projects changed the project model. MonoDevelop evolved into the foundation of Visual Studio for Mac and developed its own package-management, T4, code-coverage, and IDE infrastructure. ILSpy became the standard open-source .NET decompiler. Debug Adapter Protocol made debuggers reusable across editors. Microsoft Testing Platform started doing the same for testing. XAML Studio explored a modern architecture for interactive XAML design. Important parts of the Visual Studio editor model were also released as open-source APIs.

Many of these technologies were created at different times, for different products, and never met inside one fully open development environment.

**OpenDevelop is an attempt to bring those pieces together and continue the story.**

That does not mean preserving SharpDevelop as it was. Large parts of the original code have been replaced, refactored, or connected to modern infrastructure. SharpDevelop provides the historical foundation and a proven extensibility model, but OpenDevelop is increasingly a convergence point for open-source .NET development tooling from across the ecosystem.

## From SharpDevelop to OpenDevelop

The original SharpDevelop architecture still provides several important foundations:

- the workbench and AddInTree extensibility model;
- AvalonEdit as the source editor;
- AvalonDock for documents and tool windows;
- the WPF visual designer lineage;
- familiar IDE concepts such as pads, commands, projects, solutions, and display bindings.

Around that foundation, OpenDevelop is replacing assumptions that belonged to the .NET Framework era.

Modern SDK-style projects and the `.slnx` format are supported through modern project-system work. Roslyn provides contemporary compiler and language infrastructure. Microsoft Testing Platform supplies a modern test execution model. SharpDbg brings managed debugging through DAP. LibreWPF makes the WPF application itself viable beyond Windows.

At the same time, OpenDevelop is selectively recovering useful technology from other open-source IDE lineages instead of attempting to reinvent every subsystem.

MonoDevelop and Visual Studio for Mac are sources of technologies such as T4, NuGet integration, and code-coverage infrastructure. ILSpy is integrated as a first-class assembly exploration and decompilation subsystem. XAML Studio contributes another generation of XAML designer technology.

The result is no longer simply "SharpDevelop on .NET 10."

## Built from the .NET development tools ecosystem

OpenDevelop deliberately builds on existing open-source work wherever a strong implementation already exists.

### SharpDevelop

[SharpDevelop](https://github.com/icsharpcode/SharpDevelop) provides the historical architecture of the IDE. Its workbench and AddInTree remain especially valuable because they make large IDE features independently composable rather than forcing everything into one monolithic application.

### AvalonEdit

[AvalonEdit](https://github.com/lextudio/AvalonEdit) is the source-code editor at the center of OpenDevelop. In addition to syntax highlighting, folding, selection, navigation, and editor primitives, it is becoming the foundation for compatibility with the Visual Studio Editor API.

### AvalonDock

[AvalonDock](https://github.com/lextudio/AvalonDock) provides the docking and document layout model used by the IDE shell.

### LibreWPF

[LibreWPF](https://github.com/wieslawsoltes/WPF) provides the cross-platform WPF-compatible runtime that allows the same OpenDevelop desktop application to run on Windows, macOS, and Linux.

This is important for more than the shell itself. It also makes it possible to preserve and modernize substantial WPF-based development-tooling investments instead of rewriting every UI component for a different framework.

### Roslyn

Roslyn supplies the modern C# and Visual Basic compiler platform and language infrastructure. OpenDevelop uses modern compiler services instead of the NRefactory-era architecture inherited from old SharpDevelop.

### .NET Project System

Modern project support is built around the current .NET project model rather than the assumptions of old MSBuild project systems. This includes SDK-style projects and modern solution formats.

### SharpDbg and DAP

[SharpDbg](https://github.com/lextudio/SharpDbg) provides managed debugging and a Debug Adapter Protocol backend.

OpenDevelop uses it for breakpoints, stepping, call stacks, locals, watches, and the deeper source-debugging work described below.

### Microsoft Testing Platform

[Microsoft Testing Platform](https://github.com/microsoft/testfx) provides the foundation for modern test discovery and execution.

Rather than keeping the test runner tied to historical Visual Studio-specific interfaces, OpenDevelop can build around a testing platform designed for the current .NET ecosystem.

### ILSpy

[ILSpy](https://github.com/lextudio/ILSpy) is integrated directly into OpenDevelop for assembly browsing and decompilation.

The goal is deeper than hosting an ILSpy window. ILSpy can become part of source navigation and debugging, allowing OpenDevelop to recover a source-level representation of binaries when original source is unavailable.

### MonoDevelop and Visual Studio for Mac

OpenDevelop also recovers useful pieces from the MonoDevelop and Visual Studio for Mac lineage, including work around T4, NuGet package management, and code coverage.

This is intentionally selective. OpenDevelop does not attempt to revive the entire later MonoDevelop architecture. It reuses individual technologies where they fit the OpenDevelop model.

### XAML Studio

[XAML Studio](https://github.com/lextudio/xamlstudio) contributes modern XAML designer technology and another important piece of the open-source XAML tooling story.

Its integration is especially relevant as OpenDevelop expands visual design beyond the original SharpDevelop WPF designer.

### Visual Studio Editor API

The Visual Studio editor has a mature abstraction model around concepts such as text buffers, snapshots, tracking, classifications, taggers, and editor operations.

Parts of that model were opened by Microsoft during the Visual Studio for Mac era. OpenDevelop is bringing the `Microsoft.VisualStudio.Text.*` API model to AvalonEdit so that editor components written against those abstractions can have a path into an open, cross-platform IDE.

## More than integration

OpenDevelop is not simply packaging these projects together.

In several areas, combining them exposes opportunities for new infrastructure that did not exist in the individual projects.

### Debugging binaries as source

A debugger normally reaches a hard boundary when execution enters a NuGet package or DLL without usable source and symbols.

OpenDevelop is working toward making that boundary disappear.

SharpDbg already contains an ILSpy-based fallback capable of decompiling a managed assembly and generating a Portable PDB with sequence points, local-variable information, async/state-machine metadata, and embedded decompiled source.

The intended OpenDevelop pipeline is:

```text
NuGet package / DLL
        |
        v
real symbols and source?
        |
   +----+----+
   |         |
  yes        no
   |         |
   |       ILSpy
   |         |
   |    decompiled C#
   |    + Portable PDB
   |         |
   +----+----+
        |
        v
      SharpDbg
        |
        v
source-level debugging
```

Real source, Embedded Source, Source Link, symbol packages, and symbol servers should always take precedence when available. ILSpy becomes the final source-recovery layer when they are not.

The deeper goal is that stepping into a dependency, clicking a stack frame, using Go to Definition, or opening the same type in ILSpy should eventually lead to the same source representation.

At that point ILSpy is no longer merely an embedded decompiler. It becomes part of the debugger's source infrastructure.

### Bringing the Visual Studio Editor API to AvalonEdit

The Visual Studio Editor API has its own unusual open-source history.

During the evolution of MonoDevelop into Visual Studio for Mac, Microsoft worked to share the non-UI editor model between Visual Studio on Windows and the native macOS editor. Some of those APIs and implementations eventually became public.

OpenDevelop is taking that idea in another direction:

```text
editor extensions / language components
                |
                v
    Microsoft.VisualStudio.Text.*
                |
                v
   OpenDevelop compatibility layer
                |
                v
            AvalonEdit
```

AvalonEdit remains the editor. OpenDevelop does not embed the Visual Studio editor and does not require Visual Studio.

Instead, the goal is to expose familiar Visual Studio editor abstractions such as text buffers, snapshots, versions, edits, tracking points, tracking spans, views, tagging, and classification on top of AvalonEdit.

This gives existing .NET editor technology another possible open-source home.

### An open protocol for visual designers

Language services have LSP.

Debuggers have DAP.

Modern .NET testing is moving toward reusable infrastructure through Microsoft Testing Platform.

Visual designers have historically remained much more tightly coupled to individual IDEs.

OpenDevelop is exploring a different model: run the designer out of process and define an open protocol between the IDE and the designer host.

```text
 WPF Designer     WinForms Designer     WinUI Designer
      \                  |                   /
       \                 |                  /
        +--------- Designer Host ----------+
                         |
                  open protocol
                         |
          +--------------+--------------+
          |              |              |
     OpenDevelop      VS Code       other IDEs
```

This has several advantages.

The designer can run against the framework and runtime it actually targets. A crash in designer code does not have to take down the IDE. The same designer implementation can potentially be consumed by multiple editors. And design tooling no longer has to be treated as a private implementation detail of one development environment.

OpenDevelop's WPF and WinForms designer work, the XAML Studio integration, and the existing VS Code WPF tooling provide practical foundations for this direction.

## What OpenDevelop can do

OpenDevelop is under active development, but it already covers a broad part of the traditional .NET IDE workflow.

### Editing and language services

The editor is based on AvalonEdit and supports syntax highlighting, folding, bookmarks, breakpoints, and other core editing features.

Roslyn is the foundation for modern C# and Visual Basic language intelligence, while work on Visual Studio Editor API compatibility is intended to broaden the set of reusable editor components.

### Projects and packages

OpenDevelop supports modern SDK-style .NET projects and the `.slnx` solution format.

NuGet package management is integrated into the IDE, with parts of the implementation drawing from the MonoDevelop ecosystem.

### Debugging and diagnostics

Managed debugging is provided through SharpDbg and DAP, including breakpoints, stepping, locals, call stacks, and watches.

ILSpy-based source recovery is being developed to make third-party binaries and NuGet packages deeply debuggable even when their original source or symbols are unavailable.

### Visual designers

OpenDevelop carries forward the SharpDevelop visual-designer tradition while moving it toward an out-of-process architecture.

Current work includes WPF and Windows Forms design, with XAML Studio technology contributing to newer XAML/WinUI design scenarios.

### Testing and code coverage

Unit-test support is based on Microsoft Testing Platform.

Code coverage integrates open-source engines including AltCover and coverlet, with results presented directly in the editor.

### Assembly and code exploration

ILSpy is hosted directly in the workbench for assembly browsing and decompilation.

Class diagrams provide another way to explore code structure and type relationships.

### Extensibility

The AddInTree inherited from SharpDevelop remains one of the architectural strengths of OpenDevelop.

Pads, commands, menus, display bindings, project features, and other IDE services can be introduced as addins instead of being hard-coded into the shell.

OpenDevelop's own feature set is organized into addins for analysis, backend bindings, debugging, display bindings, miscellaneous IDE features, and version control.

## Architecture

At a high level, OpenDevelop combines a long-lived IDE shell with modern .NET services and reusable protocol-based tooling.

```text
                         OpenDevelop
                              |
        +---------------------+---------------------+
        |                     |                     |
    Workbench             IDE Services          AddInTree
        |                     |                     |
        +---------------------+---------------------+
                              |
       +----------------------+----------------------+
       |                      |                      |
   AvalonEdit             AvalonDock          Visual Designers
       |                                             |
VS Editor API layer                         out-of-process protocol
       |
       +----------------------+----------------------+
                              |
                 Modern .NET infrastructure
                              |
        +-----------+---------+---------+-----------+
        |           |                   |           |
      Roslyn   .NET Project System     MTP      SharpDbg/DAP
                                                    |
                                                   ILSpy
                                                    |
                                           source recovery
```

The important architectural direction is to keep major development services separable.

A language service should not need to be owned by one editor. A debugger should not need to be built into one IDE. A test runner should not need to understand the entire workbench. A visual designer should be able to run outside the IDE process.

OpenDevelop uses existing open protocols where they already exist and is experimenting with new ones where the .NET tooling ecosystem still lacks them.

## Current status

OpenDevelop is an active development project rather than a finished replacement for Visual Studio or Rider.

Many core workflows are already functional, including project loading, editing, debugging, visual design, package management, testing, code coverage, assembly exploration, and class diagrams. Other areas, including deeper Visual Studio Editor API compatibility, decompiled-source debugging, newer XAML designers, and the generalized out-of-process visual designer protocol, are still evolving.

The project intentionally develops these pieces in the open so that individual components can also improve independently.

## Screenshots

### ILSpy integration

![ILSpy decompiler layout](images/ilspy-layout.png)

### Code coverage

![Code coverage](images/code-coverage.png)

### Class diagrams

![Class diagram](images/class-diagram.png)

## Building OpenDevelop

### Requirements

- .NET 10 SDK
- Git with submodule support

Clone with submodules, or initialize them after cloning.

### macOS

```sh
./launch.sh            # build OpenDevelop.Mvp.slnx and run
./launch.sh --no-build # run the last build output
./launch.sh --build-only
./rebuild-all.sh       # full rebuild of the app and its dependencies
./dist.macos.sh        # produce a distributable bundle
```

The main application solution is:

```text
OpenDevelop.Mvp.slnx
```

A `DEVFLOW_DISABLE=1` environment variable turns off the built-in DevFlow automation agent.

## Testing

Unit and integration tests live in `tests/`.

Integration tests drive the running IDE through DevFlow's UI/automation endpoints under:

```text
/api/v1/ui/*
```

This makes it possible to verify real workbench behavior such as themes, menus, pads, and other UI interactions deterministically rather than limiting testing to isolated view models.

DevFlow listens on `localhost:9299` during the relevant development and test scenarios unless disabled.

## Technology foundations

The repository directly incorporates or builds on work from many open-source projects, including:

- [SharpDevelop](https://github.com/icsharpcode/SharpDevelop)
- [AvalonEdit](https://github.com/lextudio/AvalonEdit)
- [AvalonDock](https://github.com/lextudio/AvalonDock)
- [LibreWPF](https://github.com/wieslawsoltes/WPF)
- [Roslyn](https://github.com/dotnet/roslyn)
- [.NET Project System](https://github.com/dotnet/project-system)
- [SharpDbg](https://github.com/lextudio/SharpDbg)
- [Microsoft Testing Platform](https://github.com/microsoft/testfx)
- [ILSpy](https://github.com/lextudio/ILSpy)
- [MonoDevelop](https://github.com/mono/monodevelop)
- [XAML Studio](https://github.com/lextudio/xamlstudio)
- [AltCover](https://github.com/SteveGilham/altcover)
- [coverlet](https://github.com/coverlet-coverage/coverlet)
- [NuGet](https://github.com/NuGet/NuGet.Client)

This list is intentionally about major technology foundations rather than every NuGet dependency used by the application.

## Contributing

OpenDevelop is being developed in the open and contributions are welcome.

The project covers a wide range of development-tooling areas, so useful contributions are not limited to the IDE shell itself. Work on AvalonEdit, AvalonDock, LibreWPF compatibility, SharpDbg, designers, testing, package management, decompilation, project-system integration, and protocol design can all contribute to the broader goal.

When reporting an issue, include the operating system, .NET SDK version, relevant project type, and enough reproduction information to identify whether the problem belongs to OpenDevelop itself or one of its underlying components.

## License

OpenDevelop is licensed under the MIT License.

Individual incorporated projects and dependencies retain their own licenses. See their respective repositories and package metadata for details.

## Credits

OpenDevelop stands on more than two decades of work by the .NET open-source development-tools community.

The SharpDevelop project was initiated by Mike Krüger in 2000 and developed for many years by the SharpDevelop team. OpenDevelop also incorporates ideas and technology created by contributors to MonoDevelop and Visual Studio for Mac, ILSpy, AvalonEdit, AvalonDock, Roslyn, the .NET Project System, XAML Studio, NuGet, Microsoft Testing Platform, SharpDbg, LibreWPF, and many other projects.

Copyright © 2002-2016 AlphaSierraPapa for the SharpDevelop team.  
Copyright © 2026 LeXtudio Inc.
