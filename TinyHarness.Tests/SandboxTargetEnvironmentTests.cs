using System.Diagnostics;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>离线验证目录约束及本机 SDK 的 NuGet 路径查找，不启动真实沙箱或读取用户配置。</para>
///     <para>Verifies directory constraints and local SDK NuGet path lookup offline without sandbox launch or user configuration.</para>
/// </summary>
public class SandboxTargetEnvironmentTests
{
    [Test]
    public void Build_DerivesContainedPathsWithoutCreatingDirectories()
    {
        using var directory = new TestTempDir();
        var root = Path.Combine(directory.Root, "not-created", "sandbox");
        var environment = SandboxTargetEnvironment.Build(root);

        foreach (var name in new[] { "TEMP", "TMP", "USERPROFILE", "HOME", "DOTNET_CLI_HOME", "APPDATA", "LOCALAPPDATA" })
        {
            Assert.That(Path.IsPathFullyQualified(environment[name]), Is.True, name);
            Assert.That(Workspace.IsInside(root, environment[name]), Is.True, name);
        }

        Assert.That(Directory.Exists(Path.GetDirectoryName(root)), Is.False);
    }

    [Test]
    public void Build_TrustedDirectoryOverridesWinButSecretKeysAreRemovedLast()
    {
        using var directory = new TestTempDir();
        var overrides = new Dictionary<string, string>
        {
            [" appdata "] = Path.Combine(directory.Root, "explicit-roaming"),
            ["localappdata"] = Path.Combine(directory.Root, "explicit-local"),
            ["dotnet_cli_home"] = directory.Root,
            ["programfiles"] = Path.Combine(directory.Root, "explicit-programs"),
            ["synthetic_secret"] = "test-only"
        };
        var environment = SandboxTargetEnvironment.Build(Path.Combine(directory.Root, "temp"), overrides,
            new Dictionary<string, string> { ["APPDATA"] = "test-only", ["SYNTHETIC_SECRET"] = "test-only" });

        Assert.That(environment.ContainsKey("APPDATA"), Is.False);
        Assert.That(environment.ContainsKey("synthetic_secret"), Is.False);
        Assert.That(environment["LOCALAPPDATA"], Is.EqualTo(overrides["localappdata"]));
        Assert.That(environment["DOTNET_CLI_HOME"], Is.EqualTo(directory.Root));
        Assert.That(environment["ProgramFiles"], Is.EqualTo(overrides["programfiles"]));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SdkNuGetPathLookup_UsesRedirectedEnvironment(bool omitRequiredPaths)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("This regression covers Windows NuGet environment lookups.");
            return;
        }

        using var directory = new TestTempDir();
        var root = directory.CreateDirectory("target-temp");
        // SDK 内置 Roslyn task 在独立进程加载同版 NuGet，避免路径缓存和进程环境污染其他测试。
        var project = directory.WriteFile("paths.proj", """
            <Project>
              <UsingTask TaskName="ProbeNuGetPaths" TaskFactory="RoslynCodeTaskFactory"
                         AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
                <ParameterGroup><SdkDirectory ParameterType="System.String" Required="true" /></ParameterGroup>
                <Task>
                  <Code Type="Fragment" Language="cs"><![CDATA[
                    var assembly = System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(SdkDirectory, "NuGet.Common.dll"));
                    var folderType = assembly.GetType("NuGet.Common.NuGetFolderPath", true);
                    var method = assembly.GetType("NuGet.Common.NuGetEnvironment", true)
                        .GetMethod("GetFolderPath", new[] { folderType });
                    foreach (var folder in new[] { "UserSettingsDirectory", "MachineWideSettingsBaseDirectory",
                        "NuGetHome", "HttpCacheDirectory", "NuGetPluginsCacheDirectory" })
                    {
                        try { Log.LogMessage(Microsoft.Build.Framework.MessageImportance.High,
                            "PROBE:" + folder + "=" + method.Invoke(null, new[] { System.Enum.Parse(folderType, folder) })); }
                        catch (System.Reflection.TargetInvocationException ex)
                        {
                            var cause = ex.InnerException as System.ArgumentNullException;
                            if (cause == null) throw;
                            Log.LogMessage(Microsoft.Build.Framework.MessageImportance.High,
                                "PROBE:" + folder + ":null=" + cause.ParamName);
                        }
                    }
                  ]]></Code>
                </Task>
              </UsingTask>
              <Target Name="Probe"><ProbeNuGetPaths SdkDirectory="$(MSBuildToolsPath)" /></Target>
            </Project>
            """);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-target:Probe");
        start.ArgumentList.Add("-nologo");
        start.ArgumentList.Add("-verbosity:minimal");
        start.Environment.Clear();
        foreach (var (key, value) in SandboxTargetEnvironment.Build(root, new Dictionary<string, string>
                 {
                     ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                     ["DOTNET_NOLOGO"] = "1", ["MSBuildEnableWorkloadResolver"] = "false"
                 }))
            start.Environment[key] = value;
        if (omitRequiredPaths)
            foreach (var key in new[] { "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "ProgramData" })
                start.Environment.Remove(key);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("SDK path probe did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var text = await output + await error;
            Assert.That(process.ExitCode, Is.Zero, text);
            TestContext.Out.WriteLine(text);
            if (omitRequiredPaths)
            {
                Assert.That(text, Does.Contain("PROBE:UserSettingsDirectory:null=path1"));
                Assert.That(text, Does.Contain("PROBE:MachineWideSettingsBaseDirectory:null=path1"));
                return;
            }

            Assert.That(text, Does.Not.Contain(":null="));
            Assert.That(text, Does.Contain("PROBE:UserSettingsDirectory=" + Path.Combine(root, "AppData", "Roaming", "NuGet")));
            Assert.That(text, Does.Contain("PROBE:NuGetHome=" + Path.Combine(root, ".nuget")));
            Assert.That(text, Does.Contain("PROBE:HttpCacheDirectory=" + Path.Combine(root, "AppData", "Local", "NuGet", "v3-cache")));
            Assert.That(text, Does.Contain("PROBE:NuGetPluginsCacheDirectory=" + Path.Combine(root, "AppData", "Local", "NuGet", "plugins-cache")));
            start.Environment.TryGetValue("ProgramFiles(x86)", out var programs);
            if (string.IsNullOrEmpty(programs)) programs = start.Environment["ProgramFiles"]!;
            Assert.That(text, Does.Contain("PROBE:MachineWideSettingsBaseDirectory=" + Path.Combine(programs, "NuGet")));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
