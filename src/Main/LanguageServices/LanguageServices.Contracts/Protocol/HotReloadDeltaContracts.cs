// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.

#nullable enable

namespace ICSharpCode.SharpDevelop.LanguageServices.Protocol;

/// <summary>Stable key for one evaluated project/TFM slice in the Roslyn host workspace.</summary>
public sealed record HotReloadProjectId(string ProjectFileName, string? TargetFramework);

/// <summary>
/// IDE-to-host launch snapshot. Both images are copied before the debuggee starts, so an ordinary
/// rebuild cannot silently pair a current Roslyn compilation with a different module or PDB.
/// </summary>
public sealed record HotReloadBaselineRequest(HotReloadProjectId Project, byte[] ModuleImage, byte[] PdbImage);

/// <summary>IDE-to-host request to calculate against the baseline captured at launch.</summary>
public sealed record HotReloadDeltaRequest(HotReloadProjectId Project);

public enum HotReloadDeltaStatus
{
	Applied,
	NoChanges,
	RudeEdit,
	CompilationError,
	Failed,
}

/// <summary>One serializable CLR metadata update, identified by the loaded module MVID.</summary>
public sealed record HotReloadModuleDelta(
	System.Guid Mvid,
	byte[] Metadata,
	byte[] IL,
	byte[] Pdb,
	int[] UpdatedTypeTokens);

/// <summary>Host result for baseline capture or delta production.</summary>
public sealed record HotReloadDeltaResponse(
	HotReloadDeltaStatus Status,
	HotReloadModuleDelta? Delta,
	string Diagnostic);
