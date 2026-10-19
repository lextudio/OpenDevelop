using OpenDevelop.IntegrationTests;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Offline checks of the integration tests' dotnetup SDK provider (linked into this project): the
/// pre-release/completeness filter and the three signature verdicts. No network, no SDK download.
/// </summary>
public sealed class DotnetupSdkProviderTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "opendevelop-dotnetup-provider-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    void CreateSdk(string version, bool complete = true)
    {
        string sdk = Path.Combine(root, "sdk", version);
        Directory.CreateDirectory(Path.Combine(sdk, "Sdks", "Microsoft.NET.Sdk", "Sdk"));
        File.WriteAllText(Path.Combine(root, "dotnet.exe"), "");
        if (!complete)
            return;
        File.WriteAllText(Path.Combine(sdk, "MSBuild.dll"), "");
        File.WriteAllText(Path.Combine(sdk, "Sdks", "Microsoft.NET.Sdk", "Sdk", "Sdk.props"), "");
    }

    [Fact]
    public void FindCompleteSdkVersion_PrefersAStableSdk_OverAHigherPreRelease()
    {
        CreateSdk("9.0.400-preview.1.12345");
        CreateSdk("9.0.319");

        Assert.Equal("9.0.319", DotnetupSdkProvider.FindCompleteSdkVersion(root, "9.0"));
    }

    [Fact]
    public void FindCompleteSdkVersion_ReturnsNull_WhenOnlyAPreReleaseIsInstalled()
    {
        CreateSdk("9.0.400-preview.1.12345");

        Assert.Null(DotnetupSdkProvider.FindCompleteSdkVersion(root, "9.0"));
    }

    [Fact]
    public void FindCompleteSdkVersion_AcceptsAPreRelease_OnlyWhenAllowed_AndStillPrefersAStableOne()
    {
        CreateSdk("11.0.100-rc.2.26504.105");

        Assert.Null(DotnetupSdkProvider.FindCompleteSdkVersion(root, "11.0"));
        Assert.Equal("11.0.100-rc.2.26504.105", DotnetupSdkProvider.FindCompleteSdkVersion(root, "11.0", allowPrerelease: true));

        CreateSdk("11.0.100");
        Assert.Equal("11.0.100", DotnetupSdkProvider.FindCompleteSdkVersion(root, "11.0", allowPrerelease: true));
    }

    [Fact]
    public void FindCompleteSdkVersion_IgnoresAnIncompleteInstall()
    {
        CreateSdk("9.0.319", complete: false);

        Assert.Null(DotnetupSdkProvider.FindCompleteSdkVersion(root, "9.0"));
    }

    [Fact]
    public void FindCompleteSdkVersion_OnlyMatchesTheRequestedChannel()
    {
        CreateSdk("10.0.401");

        Assert.Null(DotnetupSdkProvider.FindCompleteSdkVersion(root, "9.0"));
        Assert.Equal("10.0.401", DotnetupSdkProvider.FindCompleteSdkVersion(root, "10.0"));
        // A major-only channel matches its own major ...
        Assert.Equal("10.0.401", DotnetupSdkProvider.FindCompleteSdkVersion(root, "10"));
        // ... but the match includes the dot, so channel "1" does not claim "10.0.401".
        Assert.Null(DotnetupSdkProvider.FindCompleteSdkVersion(root, "1"));
    }

    [Fact]
    public void FindCompleteSdkVersion_ReturnsNull_WithoutAHostExecutable()
    {
        CreateSdk("9.0.319");
        File.Delete(Path.Combine(root, "dotnet.exe"));

        Assert.Null(DotnetupSdkProvider.FindCompleteSdkVersion(root, "9.0"));
    }

    [Fact]
    public async Task SignatureCheck_AcceptsAMicrosoftSignedBinary()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode check is Windows-only.");
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var (verdict, detail) = await DotnetupSdkProvider.CheckMicrosoftSignatureAsync(cmd);

        SkipIfUnverifiable(verdict, detail);
        Assert.True(verdict == DotnetupSdkProvider.SignatureVerdict.Microsoft, detail);
    }

    // The check can legitimately return Unverifiable (PowerShell blocked by policy, no revocation
    // server reachable); like the integration provider, these offline tests skip then, not fail.
    static void SkipIfUnverifiable(DotnetupSdkProvider.SignatureVerdict verdict, string detail) =>
        Assert.SkipWhen(verdict == DotnetupSdkProvider.SignatureVerdict.Unverifiable,
            "The Authenticode check could not run on this machine: " + detail);

    [Fact]
    public async Task CachedBinary_IsTrusted_WhenTheHashMatchesAndItIsMicrosoftSigned()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode check is Windows-only.");
        Directory.CreateDirectory(root);
        string exe = Path.Combine(root, "dotnetup.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe); // a copy keeps the signature
        File.WriteAllText(exe + ".sha256", DotnetupSdkProvider.HashOf(exe));

        // IsCachedBinaryTrustedAsync answers false for "could not verify" too, so establish first
        // that this machine can run the check at all; otherwise there is nothing to assert.
        var (verdict, detail) = await DotnetupSdkProvider.CheckMicrosoftSignatureAsync(exe);
        SkipIfUnverifiable(verdict, detail);

        Assert.True(await DotnetupSdkProvider.IsCachedBinaryTrustedAsync(exe, exe + ".sha256"));
    }

    [Fact]
    public async Task CachedBinary_IsNotTrusted_WhenTheHashMatchesButItIsNotMicrosoftSigned()
    {
        // The case a cache left by the old unsigned (daily) helper is in: recorded hash is right, the
        // file is not Microsoft's, so it must be bootstrapped again instead of reused.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode check is Windows-only.");
        Directory.CreateDirectory(root);
        string exe = Path.Combine(root, "dotnetup.exe");
        await File.WriteAllTextAsync(exe, "not a signed binary");
        await File.WriteAllTextAsync(exe + ".sha256", DotnetupSdkProvider.HashOf(exe));

        Assert.False(await DotnetupSdkProvider.IsCachedBinaryTrustedAsync(exe, exe + ".sha256"));
    }

    [Fact]
    public async Task CachedBinary_IsNotTrusted_WhenTheHashDoesNotMatch()
    {
        Directory.CreateDirectory(root);
        string exe = Path.Combine(root, "dotnetup.exe");
        await File.WriteAllTextAsync(exe, "tampered");
        await File.WriteAllTextAsync(exe + ".sha256", "0000");

        Assert.False(await DotnetupSdkProvider.IsCachedBinaryTrustedAsync(exe, exe + ".sha256"));
    }

    [Fact]
    public void IsSharingViolation_RecognisesOnlyAFileInUseError()
    {
        // HRESULT_FROM_WIN32: 0x80070020 = ERROR_SHARING_VIOLATION, 0x80070021 = ERROR_LOCK_VIOLATION,
        // 0x80070070 = ERROR_DISK_FULL.
        Assert.True(DotnetupSdkProvider.IsSharingViolation(new IOException("in use", unchecked((int)0x80070020))));
        Assert.True(DotnetupSdkProvider.IsSharingViolation(new IOException("locked", unchecked((int)0x80070021))));
        Assert.False(DotnetupSdkProvider.IsSharingViolation(new IOException("disk full", unchecked((int)0x80070070))));
        Assert.False(DotnetupSdkProvider.IsSharingViolation(new IOException("no hresult")));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Blocked</body></html>", true)]
    [InlineData("  \r\n<html>", true)]
    [InlineData("﻿<?xml version=\"1.0\"?>", true)]
    [InlineData("<#\r\n.SYNOPSIS\r\n#>\r\nparam()", false)] // a PowerShell block comment, not markup
    [InlineData("#!/usr/bin/env pwsh\r\n<#\r\n.SYNOPSIS\r\n#>", false)] // the shape of the real get-dotnetup.ps1
    [InlineData("param([string]$InstallDir)", false)]
    [InlineData("# comment\r\nparam()", false)]
    public void LooksLikeHtml_RecognisesAnHtmlOrXmlPageButNotAScript(string content, bool expected)
    {
        Assert.Equal(expected, DotnetupSdkProvider.LooksLikeHtml(System.Text.Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public async Task SignatureCheck_RejectsAnUnsignedFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode check is Windows-only.");
        Directory.CreateDirectory(root);
        string unsigned = Path.Combine(root, "unsigned.ps1");
        await File.WriteAllTextAsync(unsigned, "Write-Output 'hello'");

        var (verdict, detail) = await DotnetupSdkProvider.CheckMicrosoftSignatureAsync(unsigned);

        SkipIfUnverifiable(verdict, detail);
        Assert.True(verdict == DotnetupSdkProvider.SignatureVerdict.NotMicrosoft, $"{verdict}: {detail}");
    }

    [Fact]
    public async Task SignatureCheck_ReportsUnverifiable_NotNotMicrosoft_WhenThereIsNothingToCheck()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode check is Windows-only.");

        var (verdict, detail) = await DotnetupSdkProvider.CheckMicrosoftSignatureAsync(Path.Combine(root, "does-not-exist.exe"));

        // A missing file says nothing about who signed it: the caller skips on this, never fails.
        Assert.True(verdict == DotnetupSdkProvider.SignatureVerdict.Unverifiable, $"{verdict}: {detail}");
    }
}
