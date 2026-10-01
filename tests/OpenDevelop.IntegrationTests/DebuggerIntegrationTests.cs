using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("50 Debugger fixture")]
public sealed class DebuggerIntegrationTests
{
    readonly OpenDevelopAppFixture _app;

    public DebuggerIntegrationTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task ClassLibrary_DebugOptionsLaunchExternalHostAndHitLibraryBreakpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opendevelop-library-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "LibraryUnderTest.csproj");
        var librarySource = Path.Combine(directory, "LibraryCode.cs");
        var hostDirectory = Path.Combine(directory, "HostApp");
        Directory.CreateDirectory(hostDirectory);
        var hostProjectPath = Path.Combine(hostDirectory, "HostApp.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"LibraryCode.cs\" /></ItemGroup></Project>");
        File.WriteAllText(librarySource, "public static class LibraryCode\n{\n    public static string Run()\n    {\n        var result = \"library hit\";\n        return result;\n    }\n}\n");
        File.WriteAllText(hostProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Program.cs\" /><ProjectReference Include=\"../LibraryUnderTest.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(hostDirectory, "Program.cs"), "System.Console.WriteLine(LibraryCode.Run());");
        try
        {
            using (var build = Process.Start(new ProcessStartInfo("dotnet") {
                ArgumentList = { "build", hostProjectPath, "-c", "Debug", "--nologo" },
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!)
            {
                var output = await build.StandardOutput.ReadToEndAsync();
                var error = await build.StandardError.ReadToEndAsync();
                await build.WaitForExitAsync();
                Assert.True(build.ExitCode == 0, output + error);
            }
            var hostOutput = Path.Combine(hostDirectory, "bin", "Debug", "net10.0");
            var hostDll = Path.Combine(hostOutput, "HostApp.dll");
            Assert.True(File.Exists(hostDll));

            var opened = await _app.ReopenSolutionAsync(projectPath);
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
            var selected = await _app.InvokeAsync("od.project-browser.select", "Project", "LibraryUnderTest");
            Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());

            var result = await _app.InvokeAsync("od.project-browser.open-selected");
            Assert.True(result.GetProperty("projectOptionsOpen").GetBoolean(), result.ToString());
            Assert.Equal("LibraryUnderTest", result.GetProperty("projectName").GetString());
            Assert.Contains(result.GetProperty("tabs").EnumerateArray(), tab =>
                tab.GetString()?.Contains("Debug", StringComparison.OrdinalIgnoreCase) == true);

            var configured = await _app.InvokeAsync("od.project-options.configure-debug-host", hostDll, hostOutput);
            Assert.True(configured.GetProperty("success").GetBoolean(), configured.ToString());
            Assert.True(configured.GetProperty("libraryHintVisible").GetBoolean(), configured.ToString());
            Assert.True(configured.GetProperty("startable").GetBoolean(), configured.ToString());
            Assert.Equal("Program", configured.GetProperty("startAction").GetString());
            var projectXml = XDocument.Load(projectPath);
            Assert.Equal("Program", projectXml.Descendants("StartAction").Single().Value);
            Assert.Equal(hostDll, projectXml.Descendants("StartProgram").Single().Value);

            var breakpointLine = FindLine(librarySource, "var result = \"library hit\";");
            await _app.InvokeAsync("od.open-file", librarySource);
            await _app.InvokeAsync("od.debug.clear-breakpoints");
            var breakpoint = await _app.InvokeAsync("od.debug.set-breakpoint", librarySource, breakpointLine);
            Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());
            var shortcut = await _app.InvokeAsync("od.workbench.invoke-shortcut", "f5");
            Assert.True(shortcut.GetProperty("success").GetBoolean(), shortcut.ToString());
            JsonElement debug = default;
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                debug = await _app.InvokeAsync("od.debug.location");
                if (debug.GetProperty("stopped").GetBoolean()) break;
                await Task.Delay(200);
            }
            if (!debug.GetProperty("stopped").GetBoolean())
            {
                var output = await _app.InvokeAsync("od.debug.output");
                Assert.Fail($"The library breakpoint was not hit. Start: {debug}; Debug output: {output}");
            }
            Assert.Equal(breakpointLine, debug.GetProperty("currentLine").GetInt32());
            Assert.EndsWith("LibraryCode.cs", Normalize(debug.GetProperty("currentFile").GetString()));
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
            await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    // A managed app pinned by PlatformTarget runs, and so must be debugged, as that architecture
    // regardless of the IDE's own: the adapter and the `dotnet` host follow the debuggee.
    [Theory]
    [InlineData("x86")]
    [InlineData("x64")]
    [InlineData("arm64")]
    public async Task ConsoleApp_WithPlatformTarget_HitsBreakpointRegardlessOfIdeArchitecture(string platformTarget)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Per-architecture dotnet hosts are located the Windows way");
        var architecture = ArchitectureFor(platformTarget);
        Assert.SkipWhen(architecture == Architecture.Arm64 && RuntimeInformation.OSArchitecture != Architecture.Arm64,
            "An ARM64 program cannot run on this machine");
        Assert.SkipWhen(FindDotNetRoot(architecture) == null, SkipMessageForMissingRuntime(architecture));
        var directory = Path.Combine(Path.GetTempPath(), "opendevelop-platform-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "PlatformApp.csproj");
        var source = Path.Combine(directory, "Program.cs");
        File.WriteAllText(projectPath, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><PlatformTarget>{platformTarget}</PlatformTarget><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Program.cs\" /></ItemGroup></Project>");
        File.WriteAllText(source, "var bitness = System.IntPtr.Size * 8;\nSystem.Console.WriteLine(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + \" \" + bitness);\n");
        try
        {
            await RunToolAsync("dotnet", directory, "build", projectPath, "-c", "Debug", "--nologo");

            var opened = await _app.ReopenSolutionAsync(projectPath);
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
            var breakpointLine = FindLine(source, "System.Console.WriteLine");
            await _app.InvokeAsync("od.open-file", source);
            await _app.InvokeAsync("od.debug.clear-breakpoints");
            var breakpoint = await _app.InvokeAsync("od.debug.set-breakpoint", source, breakpointLine);
            Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());
            var shortcut = await _app.InvokeAsync("od.workbench.invoke-shortcut", "f5");
            Assert.True(shortcut.GetProperty("success").GetBoolean(), shortcut.ToString());

            JsonElement debug = default;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                debug = await _app.InvokeAsync("od.debug.location");
                if (debug.GetProperty("stopped").GetBoolean()) break;
                await Task.Delay(200);
            }
            var output = await _app.InvokeAsync("od.debug.output");
            Assert.True(debug.GetProperty("stopped").GetBoolean(), $"The breakpoint was not hit. Location: {debug}; Debug output: {output}");
            Assert.Equal(breakpointLine, debug.GetProperty("currentLine").GetInt32());
            Assert.EndsWith("Program.cs", Normalize(debug.GetProperty("currentFile").GetString()));
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
            await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Issue #14: a class library whose start program is a native host (EXCEL.EXE loading an
    // Excel-DNA add-in) was launched as `dotnet EXCEL.EXE`. NativeHost.exe stands in for Excel: it
    // starts with no CLR, then loads the library late through hostfxr.
    //
    // macOS runs the same journey with a Mach-O host (dlopen'ing libhostfxr.dylib). There is no
    // 32-bit macOS; an x64 host runs under Rosetta 2 on Apple Silicon, with DapSession running the
    // adapter under the x64 dotnet in /usr/local/share/dotnet/x64 (dbgshim cannot cross either).
    //
    // Host architecture is a theory because it is the one dimension a user cannot choose:
    // Excel-DNA add-ins ship for whichever bitness their Excel is, and a 32-bit Excel is still
    // common. dbgshim cannot cross architectures, so DapSession runs the adapter under a dotnet
    // host matching the native program; each case only needs that architecture's runtime installed.
    [Theory]
    [InlineData("x86")]
    [InlineData("x64")]
    [InlineData("arm64")]
    public async Task ClassLibrary_NativeStartProgramLoadingRuntimeLate_HitsLibraryBreakpoint(string hostArchitecture)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "The native host fixture exists for Windows and macOS");
        var architecture = ArchitectureFor(hostArchitecture);
        Assert.SkipWhen(architecture == Architecture.Arm64 && RuntimeInformation.OSArchitecture != Architecture.Arm64,
            "An ARM64 native host cannot run on this machine");
        if (OperatingSystem.IsMacOS())
        {
            Assert.SkipWhen(architecture == Architecture.X86, "macOS has no 32-bit processes");
            Assert.SkipWhen(architecture == Architecture.X64 && RuntimeInformation.OSArchitecture == Architecture.Arm64 && !RosettaInstalled(),
                "An x64 native host needs Rosetta 2 on Apple Silicon");
        }
        var dotnetRoot = FindDotNetRoot(architecture);
        Assert.SkipWhen(dotnetRoot == null, SkipMessageForMissingRuntime(architecture));
        var directory = Path.Combine(Path.GetTempPath(), "opendevelop-native-host-debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // The host resolves "NativeHostedLibrary.AddIn, NativeHostedLibrary" -> Run(int).
        // The library itself stays AnyCPU: it is loaded into the host, so one build serves all
        // three architectures and only the native host needs a per-architecture compile.
        var projectPath = Path.Combine(directory, "NativeHostedLibrary.csproj");
        var librarySource = Path.Combine(directory, "AddIn.cs");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><EnableDynamicLoading>true</EnableDynamicLoading><GenerateRuntimeConfigurationFiles>true</GenerateRuntimeConfigurationFiles><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"AddIn.cs\" /></ItemGroup></Project>");
        File.WriteAllText(librarySource, "namespace NativeHostedLibrary;\n\npublic static class AddIn\n{\n    [System.Runtime.InteropServices.UnmanagedCallersOnly]\n    public static int Run(int input)\n    {\n        var doubled = input * 2;\n        return doubled + 1;\n    }\n}\n");
        try
        {
            await RunToolAsync("dotnet", directory, "build", projectPath, "-c", "Debug", "--nologo");
            var libraryOutput = Path.Combine(directory, "bin", "Debug", "net10.0");
            var libraryDll = Path.Combine(libraryOutput, "NativeHostedLibrary.dll");
            var hostExe = await BuildNativeHostAsync(directory, architecture);
            var hostArguments = $"\"{FindHostFxr(dotnetRoot!, architecture)}\" \"{Path.ChangeExtension(libraryDll, ".runtimeconfig.json")}\" \"{libraryDll}\"";

            var opened = await _app.ReopenSolutionAsync(projectPath);
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
            var selected = await _app.InvokeAsync("od.project-browser.select", "Project", "NativeHostedLibrary");
            Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
            var result = await _app.InvokeAsync("od.project-browser.open-selected");
            Assert.True(result.GetProperty("projectOptionsOpen").GetBoolean(), result.ToString());
            var configured = await _app.InvokeAsync("od.project-options.configure-debug-host", hostExe, libraryOutput, hostArguments);
            Assert.True(configured.GetProperty("success").GetBoolean(), configured.ToString());
            Assert.True(configured.GetProperty("startable").GetBoolean(), configured.ToString());

            var breakpointLine = FindLine(librarySource, "return doubled + 1;");
            await _app.InvokeAsync("od.open-file", librarySource);
            await _app.InvokeAsync("od.debug.clear-breakpoints");
            var breakpoint = await _app.InvokeAsync("od.debug.set-breakpoint", librarySource, breakpointLine);
            Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());
            var shortcut = await _app.InvokeAsync("od.workbench.invoke-shortcut", "f5");
            Assert.True(shortcut.GetProperty("success").GetBoolean(), shortcut.ToString());

            JsonElement debug = default;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                debug = await _app.InvokeAsync("od.debug.location");
                if (debug.GetProperty("stopped").GetBoolean()) break;
                await Task.Delay(200);
            }
            var output = await _app.InvokeAsync("od.debug.output");
            Assert.True(debug.GetProperty("stopped").GetBoolean(), $"The library breakpoint was not hit. Location: {debug}; Debug output: {output}");
            Assert.Equal(breakpointLine, debug.GetProperty("currentLine").GetInt32());
            Assert.EndsWith("AddIn.cs", Normalize(debug.GetProperty("currentFile").GetString()));
            var outputText = output.GetProperty("text").GetString() ?? string.Empty;
            Assert.Contains("native program", outputText);
            Assert.DoesNotContain("ERROR", outputText);
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
            await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Stands in for EXCEL.EXE: no CLR at startup, .NET arrives later through hostfxr. Written
    // out here rather than read from a submodule's test tree, so this regression test does not
    // depend on another repository's source layout.
    const string NativeHostSource = """
        // Minimal native host that loads a .NET class library late through hostfxr, mirroring how
        // Excel-DNA loads a .NET add-in into EXCEL.EXE. No CLR is present when the process starts.
        // Usage: NativeHost.exe <hostfxr.dll> <library.runtimeconfig.json> <library.dll>
        #include <windows.h>
        #include <stdio.h>

        typedef void* hostfxr_handle;
        typedef int (__cdecl *hostfxr_initialize_for_runtime_config_fn)(const wchar_t* runtime_config_path, const void* parameters, hostfxr_handle* host_context_handle);
        typedef int (__cdecl *hostfxr_get_runtime_delegate_fn)(const hostfxr_handle host_context_handle, int type, void** delegate);
        typedef int (__cdecl *hostfxr_close_fn)(const hostfxr_handle host_context_handle);
        typedef int (__stdcall *load_assembly_and_get_function_pointer_fn)(const wchar_t* assembly_path, const wchar_t* type_name, const wchar_t* method_name, const wchar_t* delegate_type_name, void* reserved, void** delegate);
        typedef int (__stdcall *run_fn)(int input);

        #define HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER 5
        #define UNMANAGEDCALLERSONLY_METHOD ((const wchar_t*)-1)

        int wmain(int argc, wchar_t** argv)
        {
            if (argc < 4)
            {
                fwprintf(stderr, L"usage: NativeHost.exe <hostfxr.dll> <runtimeconfig.json> <library.dll>\n");
                return 2;
            }

            // Native-only startup phase, like Excel initialising before it loads any .xll
            wprintf(L"NativeHost: native startup\n");
            fflush(stdout);
            Sleep(500);

            HMODULE hostfxr = LoadLibraryW(argv[1]);
            if (!hostfxr)
            {
                fwprintf(stderr, L"NativeHost: failed to load %s (%lu)\n", argv[1], GetLastError());
                return 3;
            }
            hostfxr_initialize_for_runtime_config_fn init = (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(hostfxr, "hostfxr_initialize_for_runtime_config");
            hostfxr_get_runtime_delegate_fn get_delegate = (hostfxr_get_runtime_delegate_fn)GetProcAddress(hostfxr, "hostfxr_get_runtime_delegate");
            hostfxr_close_fn close = (hostfxr_close_fn)GetProcAddress(hostfxr, "hostfxr_close");
            if (!init || !get_delegate || !close)
            {
                fwprintf(stderr, L"NativeHost: hostfxr exports not found\n");
                return 4;
            }

            hostfxr_handle context = NULL;
            int rc = init(argv[2], NULL, &context);
            if (rc < 0 || !context)
            {
                fwprintf(stderr, L"NativeHost: hostfxr_initialize_for_runtime_config failed 0x%08x\n", rc);
                return 5;
            }

            load_assembly_and_get_function_pointer_fn load = NULL;
            rc = get_delegate(context, HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER, (void**)&load);
            close(context);
            if (rc < 0 || !load)
            {
                fwprintf(stderr, L"NativeHost: hostfxr_get_runtime_delegate failed 0x%08x\n", rc);
                return 6;
            }

            run_fn run = NULL;
            rc = load(argv[3], L"NativeHostedLibrary.AddIn, NativeHostedLibrary", L"Run", UNMANAGEDCALLERSONLY_METHOD, NULL, (void**)&run);
            if (rc < 0 || !run)
            {
                fwprintf(stderr, L"NativeHost: load_assembly_and_get_function_pointer failed 0x%08x\n", rc);
                return 7;
            }

            int result = run(20);
            wprintf(L"NativeHost: result %d\n", result);
            return result == 41 ? 0 : 8;
        }
        """;

    static Architecture ArchitectureFor(string name) => name switch
    {
        "x86" => Architecture.X86,
        "x64" => Architecture.X64,
        "arm64" => Architecture.Arm64,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown native host architecture")
    };

    // The vcvarsall target triple for an architecture.
    static string NativeHostTargetTriple(Architecture architecture) => architecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "No vcvarsall target for this architecture")
    };

    // The VS component that installs the compiler for an architecture. Each one is a separate
    // install, so the requirement has to follow the target: asking for the x64 tools on an
    // ARM64-only install reports "no C++ tools were found" when the real problem is "the wrong
    // C++ tools". Note x64 and x86 share one component, which is why its id says x86.x64.
    static string NativeHostToolsComponent(Architecture architecture) => architecture switch
    {
        Architecture.X86 or Architecture.X64 => "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
        Architecture.Arm64 => "Microsoft.VisualStudio.Component.VC.Tools.ARM64",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "No Visual Studio C++ component for this architecture")
    };

    // The macOS counterpart of NativeHostSource: dlopen/dlsym instead of LoadLibraryW/GetProcAddress,
    // and char rather than wchar_t paths, which is what hostfxr takes outside Windows.
    const string MacNativeHostSource = """
        // Minimal native host that loads a .NET class library late through hostfxr.
        // Usage: NativeHost <libhostfxr.dylib> <library.runtimeconfig.json> <library.dll>
        #include <dlfcn.h>
        #include <stdio.h>
        #include <unistd.h>

        typedef void* hostfxr_handle;
        typedef int (*hostfxr_initialize_for_runtime_config_fn)(const char* runtime_config_path, const void* parameters, hostfxr_handle* host_context_handle);
        typedef int (*hostfxr_get_runtime_delegate_fn)(const hostfxr_handle host_context_handle, int type, void** delegate);
        typedef int (*hostfxr_close_fn)(const hostfxr_handle host_context_handle);
        typedef int (*load_assembly_and_get_function_pointer_fn)(const char* assembly_path, const char* type_name, const char* method_name, const char* delegate_type_name, void* reserved, void** delegate);
        typedef int (*run_fn)(int input);

        #define HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER 5
        #define UNMANAGEDCALLERSONLY_METHOD ((const char*)-1)

        int main(int argc, char** argv)
        {
            if (argc < 4)
            {
                fprintf(stderr, "usage: NativeHost <libhostfxr.dylib> <runtimeconfig.json> <library.dll>\n");
                return 2;
            }

            // Native-only startup phase, like Excel initialising before it loads any add-in
            printf("NativeHost: native startup\n");
            fflush(stdout);
            usleep(500 * 1000);

            void* hostfxr = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
            if (!hostfxr)
            {
                fprintf(stderr, "NativeHost: failed to load %s (%s)\n", argv[1], dlerror());
                return 3;
            }
            hostfxr_initialize_for_runtime_config_fn init = (hostfxr_initialize_for_runtime_config_fn)dlsym(hostfxr, "hostfxr_initialize_for_runtime_config");
            hostfxr_get_runtime_delegate_fn get_delegate = (hostfxr_get_runtime_delegate_fn)dlsym(hostfxr, "hostfxr_get_runtime_delegate");
            hostfxr_close_fn close_context = (hostfxr_close_fn)dlsym(hostfxr, "hostfxr_close");
            if (!init || !get_delegate || !close_context)
            {
                fprintf(stderr, "NativeHost: hostfxr exports not found\n");
                return 4;
            }

            hostfxr_handle context = NULL;
            int rc = init(argv[2], NULL, &context);
            if (rc < 0 || !context)
            {
                fprintf(stderr, "NativeHost: hostfxr_initialize_for_runtime_config failed 0x%08x\n", rc);
                return 5;
            }

            load_assembly_and_get_function_pointer_fn load = NULL;
            rc = get_delegate(context, HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER, (void**)&load);
            close_context(context);
            if (rc < 0 || !load)
            {
                fprintf(stderr, "NativeHost: hostfxr_get_runtime_delegate failed 0x%08x\n", rc);
                return 6;
            }

            run_fn run = NULL;
            rc = load(argv[3], "NativeHostedLibrary.AddIn, NativeHostedLibrary", "Run", UNMANAGEDCALLERSONLY_METHOD, NULL, (void**)&run);
            if (rc < 0 || !run)
            {
                fprintf(stderr, "NativeHost: load_assembly_and_get_function_pointer failed 0x%08x\n", rc);
                return 7;
            }

            int result = run(20);
            printf("NativeHost: result %d\n", result);
            return result == 41 ? 0 : 8;
        }
        """;

    // xcrun picks the active Xcode's SDK. A bare cc can link against a Command Line Tools SDK
    // newer than its own linker ("tapi error: unknown architecture arm64e.x1-macos", measured).
    static async Task<string> BuildMacNativeHostAsync(string outputDirectory, Architecture architecture)
    {
        var source = Path.Combine(outputDirectory, "native_host.c");
        File.WriteAllText(source, MacNativeHostSource);
        var arch = architecture == Architecture.Arm64 ? "arm64" : "x86_64";
        var exe = Path.Combine(outputDirectory, "NativeHost-" + arch);
        await RunToolAsync("xcrun", outputDirectory, "--sdk", "macosx", "clang", "-g", "-arch", arch, "-o", exe, source);
        Assert.True(File.Exists(exe), $"{Path.GetFileName(exe)} was not produced");
        return exe;
    }

    static async Task<string> BuildNativeHostAsync(string outputDirectory, Architecture architecture)
    {
        if (OperatingSystem.IsMacOS())
            return await BuildMacNativeHostAsync(outputDirectory, architecture);
        var source = Path.Combine(outputDirectory, "native_host.c");
        File.WriteAllText(source, NativeHostSource);
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        Assert.True(File.Exists(vswhere), "Visual Studio with the C++ workload is required to build the native host fixture");
        var component = NativeHostToolsComponent(architecture);
        var vs = (await RunToolAsync(vswhere, outputDirectory, "-latest", "-products", "*", "-requires", component, "-property", "installationPath")).Trim();
        Assert.False(string.IsNullOrEmpty(vs), $"No Visual Studio installation providing '{component}' was found");
        var vcvars = Path.Combine(vs.Split('\n')[0].Trim(), "VC", "Auxiliary", "Build", "vcvarsall.bat");
        // Must match the dotnet host the adapter runs under - dbgshim cannot cross architectures
        var triple = NativeHostTargetTriple(architecture);
        var exe = Path.Combine(outputDirectory, $"NativeHost-{triple}.exe");
        await RunToolAsync("cmd.exe", outputDirectory, "/s", "/c", $"\"call \"{vcvars}\" {triple} >nul && cl /nologo /Zi /Fe:\"{exe}\" \"{source}\"\"");
        Assert.True(File.Exists(exe), $"{Path.GetFileName(exe)} was not produced");
        return exe;
    }

    // The standard install locations per architecture: an ARM64 machine keeps its emulated x64
    // runtime under Program Files\dotnet\x64, and 32-bit always lives under Program Files (x86).
    // Deliberately not the registry DapSession also consults, so a runtime installed elsewhere
    // skips here while the product still finds it - SkipMessageForMissingRuntime names the folders.
    static string[] DotNetRootCandidates(Architecture architecture)
    {
        if (OperatingSystem.IsMacOS())
            return MacDotNetRootCandidates();
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return architecture == Architecture.X86
            ? new[] { Path.Combine(programFilesX86, "dotnet") }
            : new[] { Path.Combine(programFiles, "dotnet", architecture == Architecture.X64 ? "x64" : "arm64"), Path.Combine(programFiles, "dotnet") };
    }

    // DOTNET_ROOT, then the installer's default location, then Homebrew's. An x64 runtime on
    // Apple Silicon lives in its own x64 subfolder; this test only uses the native one.
    static string[] MacDotNetRootCandidates() => new[] {
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_ARM64"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            "/usr/local/share/dotnet/x64",
            "/usr/local/share/dotnet",
            "/opt/homebrew/opt/dotnet/libexec",
        }.Where(root => !string.IsNullOrEmpty(root)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();

    // Rosetta 2 is installed when its runtime is present; without it an x86_64 binary cannot start.
    static bool RosettaInstalled() => File.Exists("/Library/Apple/usr/libexec/oah/libRosettaRuntime");

    static string? FindDotNetRoot(Architecture architecture)
    {
        if (OperatingSystem.IsMacOS())
            return MacDotNetRootCandidates().FirstOrDefault(root => MacHostFxrPaths(root).Any(path => IsMachOFor(path, architecture)));
        var machine = MachineFor(architecture);
        return DotNetRootCandidates(architecture).FirstOrDefault(root => IsPeFor(Path.Combine(root, "dotnet.exe"), machine));
    }

    static string SkipMessageForMissingRuntime(Architecture architecture) => OperatingSystem.IsMacOS()
        ? $"No {architecture} .NET runtime in {string.Join(", ", DotNetRootCandidates(architecture))}. On Apple Silicon an x64 one can be installed without sudo: " +
          "dotnet-install.sh --runtime dotnet --channel 10.0 --architecture x64 --install-dir ~/.dotnet-x64, then set DOTNET_ROOT_X64=~/.dotnet-x64 (the IDE reads it too)"
        : $"No {architecture} dotnet.exe in the standard locations ({string.Join(", ", DotNetRootCandidates(architecture))}); a runtime installed elsewhere is not looked for";

    static System.Reflection.PortableExecutable.Machine MachineFor(Architecture architecture) => architecture switch
    {
        Architecture.X86 => System.Reflection.PortableExecutable.Machine.I386,
        Architecture.X64 => System.Reflection.PortableExecutable.Machine.Amd64,
        Architecture.Arm64 => System.Reflection.PortableExecutable.Machine.Arm64,
        _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "Unknown native host architecture")
    };

    static IEnumerable<string> MacHostFxrPaths(string dotnetRoot)
    {
        var fxrRoot = Path.Combine(dotnetRoot, "host", "fxr");
        if (!Directory.Exists(fxrRoot)) return Array.Empty<string>();
        return Directory.GetDirectories(fxrRoot)
            .Where(dir => Version.TryParse(Path.GetFileName(dir).Split('-')[0], out _))
            .OrderByDescending(dir => Version.Parse(Path.GetFileName(dir).Split('-')[0]))
            .Select(dir => Path.Combine(dir, "libhostfxr.dylib"))
            .Where(File.Exists);
    }

    // Thin Mach-O only (a hostfxr is never fat): 64-bit magic, then the CPU type.
    static bool IsMachOFor(string path, Architecture architecture)
    {
        const uint MachO64Magic = 0xFEEDFACF, CpuTypeArm64 = 0x0100000C, CpuTypeX64 = 0x01000007;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadUInt32() != MachO64Magic) return false;
            var cpu = reader.ReadUInt32();
            return architecture == Architecture.Arm64 ? cpu == CpuTypeArm64 : architecture == Architecture.X64 && cpu == CpuTypeX64;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            return false;
        }
    }

    static string FindHostFxr(string dotnetRoot, Architecture architecture)
    {
        if (OperatingSystem.IsMacOS())
        {
            var dylib = MacHostFxrPaths(dotnetRoot).FirstOrDefault(path => IsMachOFor(path, architecture));
            Assert.True(dylib != null, $"No {architecture} libhostfxr.dylib exists under {dotnetRoot}");
            return dylib!;
        }
        // The debuggee LoadLibraryW's this, so it must match the debuggee's bitness; picking the
        // wrong one fails at LoadLibrary with ERROR_BAD_EXE_FORMAT (193), which names no cause.
        var fxrRoot = Path.Combine(dotnetRoot, "host", "fxr");
        Assert.True(Directory.Exists(fxrRoot), "No host/fxr directory exists under " + dotnetRoot);
        var candidates = Directory.GetDirectories(fxrRoot)
            .Where(dir => Version.TryParse(Path.GetFileName(dir).Split('-')[0], out _))
            .OrderByDescending(dir => Version.Parse(Path.GetFileName(dir).Split('-')[0]))
            .ToArray();
        Assert.NotEmpty(candidates);
        // Several versions can coexist; only the one whose hostfxr matches the debuggee is
        // loadable, so filter by the PE machine rather than trusting the newest directory.
        var expected = MachineFor(architecture);
        var fxr = candidates.FirstOrDefault(dir => IsPeFor(Path.Combine(dir, "hostfxr.dll"), expected));
        Assert.True(fxr != null, $"No {architecture} hostfxr.dll exists under {fxrRoot}");
        return Path.Combine(fxr!, "hostfxr.dll");
    }

    static bool IsPeFor(string path, System.Reflection.PortableExecutable.Machine machine)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            return pe.PEHeaders.CoffHeader.Machine == machine;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException)
        {
            return false;
        }
    }

    static async Task<string> RunToolAsync(string fileName, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // cmd.exe's /c needs its command line verbatim; ArgumentList would re-quote it
        if (fileName == "cmd.exe") startInfo.Arguments = string.Join(" ", arguments);
        else foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{fileName} failed ({process.ExitCode}): {await stdout}{await stderr}");
        return await stdout;
    }

    [Fact]
    public async Task DebuggerService_IsRegisteredByAddIn()
    {
        var info = await _app.InvokeAsync("od.debug.service-info");

        Assert.True(info.GetProperty("available").GetBoolean());
        Assert.Equal("ICSharpCode.SharpDevelop.Services.WindowsDebugger", info.GetProperty("typeName").GetString());
        Assert.False(info.GetProperty("isDebugging").GetBoolean());

        var pads = await _app.InvokeAsync("od.pads");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.BreakPointsPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.CallStackPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.LocalVarPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.ThreadsPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.LoadedModulesPad");
    }

    [Fact]
    public async Task BreakpointHit_ExposesInspectionPadsThreadsModulesAndOutput()
    {
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        var breakpoint = await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);
        Assert.True(breakpoint.GetProperty("success").GetBoolean());
        Assert.Contains(breakpoint.GetProperty("lines").EnumerateArray(), l => l.GetInt32() == breakpointLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());
            Assert.EndsWith("Program.cs", Normalize(start.GetProperty("currentFile").GetString()));
            Assert.Equal(breakpointLine, start.GetProperty("currentLine").GetInt32());

            var stack = await _app.InvokeAsync("od.debug.call-stack");
            Assert.Contains(stack.EnumerateArray(), f => f.GetProperty("Name").GetString()!.Contains("Main"));

            var locals = await _app.InvokeAsync("od.debug.locals");
            Assert.Contains(locals.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "greeting"
                && v.GetProperty("Value").GetString()!.Contains("Hello, Debugger!"));
            Assert.Contains(locals.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "answer"
                && v.GetProperty("Value").GetString()!.Contains("42"));

            var evaluated = await _app.InvokeAsync("od.debug.evaluate", "answer");
            Assert.Equal("answer", evaluated.GetProperty("Name").GetString());
            Assert.Contains("42", evaluated.GetProperty("Value").GetString());

            var breakpointPad = await _app.InvokeAsync("od.debug.pad-snapshot", "BreakPointsPad");
            Assert.True(breakpointPad.GetProperty("found").GetBoolean());
            Assert.Contains(breakpointPad.GetProperty("items").EnumerateArray(), i =>
                Normalize(i.GetProperty("File").GetString()).EndsWith("Program.cs")
                && i.GetProperty("Line").GetInt32() == breakpointLine);

            var callStackPad = await _app.InvokeAsync("od.debug.pad-snapshot", "CallStackPad");
            Assert.True(callStackPad.GetProperty("found").GetBoolean());
            Assert.Contains(callStackPad.GetProperty("items").EnumerateArray(), f =>
                f.GetProperty("Name").GetString()!.Contains("Main"));

            var localsPad = await _app.InvokeAsync("od.debug.pad-snapshot", "LocalVarPad");
            Assert.True(localsPad.GetProperty("found").GetBoolean());
            Assert.Contains(localsPad.GetProperty("items").EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "answer"
                && v.GetProperty("Value").GetString()!.Contains("42"));

            var threadsPad = await _app.InvokeAsync("od.debug.pad-snapshot", "ThreadsPad");
            Assert.True(threadsPad.GetProperty("found").GetBoolean());
            Assert.NotEmpty(threadsPad.GetProperty("items").EnumerateArray());

            var modulesPad = await _app.InvokeAsync("od.debug.pad-snapshot", "LoadedModulesPad");
            Assert.True(modulesPad.GetProperty("found").GetBoolean());
            Assert.Equal(JsonValueKind.Array, modulesPad.GetProperty("items").ValueKind);

            var threads = await _app.InvokeAsync("od.debug.threads");
            Assert.NotEmpty(threads.EnumerateArray());

            var modules = await _app.InvokeAsync("od.debug.modules");
            Assert.NotEmpty(modules.EnumerateArray());

            var output = await _app.InvokeAsync("od.debug.output");
            Assert.NotEmpty(output.GetProperty("text").GetString()!);
            Assert.Equal("Debug", output.GetProperty("selectedCategory").GetString());
            Assert.True(output.GetProperty("isOutputVisible").GetBoolean());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task SharpDbgVisualizers_OfferAndRenderTextXmlAndCollectionValues()
    {
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        var breakpoint = await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);
        Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            var text = await _app.InvokeAsync("od.debug.visualizer.inspect", "greeting", "Text visualizer");
            Assert.True(text.GetProperty("success").GetBoolean(), text.ToString());
            Assert.Contains("Text visualizer", text.GetProperty("availableVisualizers").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("greeting", text.GetProperty("title").GetString());
            Assert.Contains("Hello, Debugger!", text.GetProperty("text").GetString());

            var xml = await _app.InvokeAsync("od.debug.visualizer.inspect", "xml", "XML visualizer");
            Assert.True(xml.GetProperty("success").GetBoolean(), xml.ToString());
            Assert.Contains("Text visualizer", xml.GetProperty("availableVisualizers").EnumerateArray().Select(x => x.GetString()));
            Assert.Contains("XML visualizer", xml.GetProperty("availableVisualizers").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("xml", xml.GetProperty("title").GetString());
            Assert.Contains("<root><item>visualizer</item></root>", xml.GetProperty("text").GetString());
            Assert.Contains("XML", xml.GetProperty("highlighting").GetString(), StringComparison.OrdinalIgnoreCase);

            var grid = await _app.InvokeAsync("od.debug.visualizer.inspect", "numbers", "Collection visualizer");
            Assert.True(grid.GetProperty("success").GetBoolean(), grid.ToString());
            Assert.Equal(new[] { "Collection visualizer" }, grid.GetProperty("availableVisualizers").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal("numbers", grid.GetProperty("title").GetString());
            Assert.Contains(grid.GetProperty("rows").EnumerateArray(), row =>
                row.GetProperty("Name").GetString() == "[0]" && row.GetProperty("Value").GetString()!.Contains("7"));
            Assert.Contains(grid.GetProperty("rows").EnumerateArray(), row =>
                row.GetProperty("Name").GetString() == "[1]" && row.GetProperty("Value").GetString()!.Contains("11"));
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task StepIntoAndStepOver_UpdateCurrentFrameAndLocals()
    {
        var program = ProgramPath;
        var callLine = FindLine(program, "var message = ComputeGreeting(\"World\");");
        var writeLine = FindLine(program, "Console.WriteLine(message);");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, callLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            var stepInto = await _app.InvokeAsync("od.debug.step-into", 30);
            Assert.True(stepInto.GetProperty("stopped").GetBoolean(), stepInto.ToString());

            var stackAfterStepInto = await WaitForTopFrameNameAsync("ComputeGreeting", 5);
            var topFrameAfterStepInto = stackAfterStepInto.EnumerateArray().First();
            Assert.Contains("ComputeGreeting", topFrameAfterStepInto.GetProperty("Name").GetString());
            Assert.Contains(stackAfterStepInto.EnumerateArray(), f => f.GetProperty("Name").GetString()!.Contains("Main"));

            var localsInsideMethod = await _app.InvokeAsync("od.debug.locals");
            Assert.Contains(localsInsideMethod.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "name"
                && v.GetProperty("Value").GetString()!.Contains("World"));

            var stepOut = await _app.InvokeAsync("od.debug.step-out", 30);
            Assert.True(stepOut.GetProperty("stopped").GetBoolean(), stepOut.ToString());
            Assert.True(stepOut.GetProperty("currentLine").GetInt32() >= callLine);

            var stepOver = await _app.InvokeAsync("od.debug.step-over", 30);
            Assert.True(stepOver.GetProperty("stopped").GetBoolean(), stepOver.ToString());
            var topFrameAfterStepOver = await WaitForTopFrameLineAsync(writeLine, 5);
            Assert.Equal(writeLine, topFrameAfterStepOver.GetProperty("Line").GetInt32());

            var localsAfterStepOver = await _app.InvokeAsync("od.debug.locals");
            Assert.Contains(localsAfterStepOver.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "message"
                && v.GetProperty("Value").GetString()!.Contains("Hello, World!"));
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task ContinueDebug_HitsSecondBreakpoint()
    {
        var program = ProgramPath;
        var firstLine = FindLine(program, "var message = ComputeGreeting(\"World\");");
        var secondLine = FindLine(program, "Console.WriteLine(message);");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, firstLine);
        await _app.InvokeAsync("od.debug.set-breakpoint", program, secondLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());
            Assert.Equal(firstLine, start.GetProperty("currentLine").GetInt32());

            var cont = await _app.InvokeAsync("od.debug.continue", true, 30);
            Assert.True(cont.GetProperty("stopped").GetBoolean(), cont.ToString());
            Assert.Equal(secondLine, cont.GetProperty("currentLine").GetInt32());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task DebugStart_SwitchesToDebugLayout_AndStopRestoresDefault_KeepingPadsOpen()
    {
        // Regression coverage for the debug-session layout switching (doc/technotes/ilspy.md
        // "Legacy pad migration", 2026-08-09):
        //  - BaseDebuggerService.OnDebugStarting switches the workbench to the "Debug" layout,
        //    and that switch must NOT evict pads the user had open (the incremental layout
        //    contract - previously the Debug layout switch *closed* ErrorList & co).
        //  - WindowsDebugger.Stop() used to skip SessionExited entirely for an explicit stop
        //    (DapSession.CleanupSession never raises Exited), so OnDebugStopped never ran and the
        //    workbench stayed on "Debug" forever - the fix calls SessionExited() after
        //    CurrentSession.Stop(), which must switch the layout back to "Default".
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            // While stopped at the breakpoint, the debugger's automatic layout switch is active:
            var layoutDuring = await _app.InvokeAsync("od.layout.current-name");
            Assert.Equal("Debug", layoutDuring.GetProperty("layoutName").GetString());

            // ...and the pads that were open before the session started must still be docked.
            var during = await _app.InvokeAsync("od.layout.tool-panes");
            var visibleDuring = VisibleContentIds(during).ToHashSet();
            Assert.Contains("ProjectBrowser", visibleDuring);
            Assert.Contains("OutputPad", visibleDuring);
            Assert.Contains("ErrorList", visibleDuring);

            await _app.InvokeAsync("od.debug.stop");

            // The explicit-stop fix: the layout must switch back to Default on its own.
            var layoutAfter = await _app.InvokeAsync("od.layout.current-name");
            Assert.Equal("Default", layoutAfter.GetProperty("layoutName").GetString());

            var after = await _app.InvokeAsync("od.layout.tool-panes");
            var visibleAfter = VisibleContentIds(after).ToHashSet();
            Assert.Contains("ProjectBrowser", visibleAfter);
            Assert.Contains("OutputPad", visibleAfter);
            Assert.Contains("ErrorList", visibleAfter);

            var info = await _app.InvokeAsync("od.debug.service-info");
            Assert.False(info.GetProperty("isDebugging").GetBoolean());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    static IEnumerable<string> VisibleContentIds(JsonElement toolPanes)
        => toolPanes.GetProperty("panes").EnumerateArray()
            .Where(p => p.GetProperty("IsVisible").GetBoolean())
            .Select(p => p.GetProperty("ContentId").GetString()!);

    [Fact]
    public async Task DebugStart_WhenTargetMissing_FailsCleanlyInsteadOfHanging()
    {
        // Regression coverage for the premature-exit/adapter-failure bug: WindowsDebugger.StartAsync
        // used to leave the paused-line marker in place and the toolbar in a "still debugging"-looking
        // state if the session died right after starting (e.g. SharpDbg's CommandUnknownException
        // when handed an apphost path, or the debuggee's target framework being missing). We can't
        // easily force those exact OS/runtime-specific failures on demand, but WindowsDebugger.StartAsync
        // takes the same catch-block path (clear marker, print "ERROR: ...", activate the Debug output
        // channel, stop) whenever the debug target can't be launched at all - so making the target
        // executable briefly disappear exercises that same code path deterministically.
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);

        string outputDir = Path.Combine(Path.GetDirectoryName(_app.DebugTestProjectPath)!, "bin", "Debug", "net10.0");
        var moved = new List<(string From, string To)>();
        foreach (string file in Directory.GetFiles(outputDir, "DebugTestApp.*"))
        {
            string away = file + ".movedfortest";
            File.Move(file, away);
            moved.Add((file, away));
        }

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 20);

            // Must return promptly reporting failure - not hang, not report a phantom "still debugging".
            Assert.False(start.GetProperty("started").GetBoolean(), start.ToString());
            Assert.False(start.GetProperty("isDebugging").GetBoolean(), start.ToString());

            var info = await _app.InvokeAsync("od.debug.service-info");
            Assert.False(info.GetProperty("isDebugging").GetBoolean());
            Assert.False(info.GetProperty("isProcessRunning").GetBoolean());

            var output = await _app.InvokeAsync("od.debug.output");
            Assert.Contains("ERROR", output.GetProperty("text").GetString());
        }
        finally
        {
            foreach (var (from, to) in moved)
                File.Move(to, from);
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    string ProgramPath => Path.Combine(Path.GetDirectoryName(_app.DebugTestProjectPath)!, "Program.cs");

    static int FindLine(string path, string marker)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        }
        throw new InvalidOperationException($"Marker '{marker}' not found in {path}.");
    }

    static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/');

    async Task<JsonElement> WaitForTopFrameLineAsync(int expectedLine, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        JsonElement result = default;
        while (DateTime.UtcNow < deadline)
        {
            var stack = await _app.InvokeAsync("od.debug.call-stack");
            result = stack.EnumerateArray().First();
            if (result.GetProperty("Line").GetInt32() == expectedLine)
                break;
            await Task.Delay(100);
        }
        return result;
    }

    async Task<JsonElement> WaitForTopFrameNameAsync(string expectedName, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        JsonElement result = default;
        while (DateTime.UtcNow < deadline)
        {
            result = await _app.InvokeAsync("od.debug.call-stack");
            var frames = result.EnumerateArray().ToArray();
            if (frames.Length > 0 && frames[0].GetProperty("Name").GetString()!.Contains(expectedName))
                break;
            await Task.Delay(100);
        }
        return result;
    }
}
