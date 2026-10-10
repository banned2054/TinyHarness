using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     帧协议与 wire DTO 测试：长度前缀、版本硬校验、截断/超长、空 payload 控制帧、
///     spawn_request/setup payload 的精确字段形状，以及 cap_sid、状态检查、凭据与策略解析。
///     全部离线，不启动进程、不修改机器状态。
///     Frame-protocol and wire-DTO tests: length prefixes, the hard version
///     check, truncation/oversize, empty-payload control frames, exact field
///     shapes of spawn_request/setup payloads, plus cap_sid, readiness checks,
///     credentials, and policy resolution. Fully offline — no processes, no
///     machine-state changes.
/// </summary>
public class WindowsSandboxProtocolTests
{
    // ---- RunnerFrameCodec ----

    [Fact]
    public async Task WriteFrame_RoundTripsThroughLengthPrefixAndVersion()
    {
        using var stream = new MemoryStream();
        await RunnerFrameCodec.WriteFrameAsync(stream, "exit",
                                               new RunnerExitPayload { ExitCode = 23, TimedOut = false },
                                               CancellationToken.None);

        stream.Position = 0;
        var frame = await RunnerFrameCodec.ReadFrameAsync(stream, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal("exit", frame.Type);
        var payload = RunnerFrameCodec.ParsePayload<RunnerExitPayload>(frame);
        Assert.Equal(23, payload.ExitCode);
        Assert.False(payload.TimedOut);
    }

    [Fact]
    public async Task WriteFrame_ProducesExactEnvelopeShape()
    {
        using var stream = new MemoryStream();
        await RunnerFrameCodec.WriteFrameAsync(stream, "spawn_ready", new RunnerSpawnReady { ProcessId = 4242 },
                                               CancellationToken.None);

        stream.Position = 0;
        var body     = ReadFrameBody(stream);
        var envelope = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(6, (int)envelope["version"]!);
        Assert.Equal("spawn_ready", (string)envelope["type"]!);
        Assert.Equal(4242, (int)envelope["payload"]!["process_id"]!);
    }

    [Fact]
    public async Task WriteControlFrame_KeepsEmptyPayloadObject()
    {
        using var stream = new MemoryStream();
        await RunnerFrameCodec.WriteControlFrameAsync(stream, "terminate", CancellationToken.None);

        stream.Position = 0;
        var body = ReadFrameBody(stream);
        Assert.Equal("""{"version":6,"type":"terminate","payload":{}}""", body);
    }

    [Fact]
    public async Task ReadFrame_ThrowsEndOfStreamWhenDeclaredBodyTruncates()
    {
        // The length prefix promises a full frame but the stream ends ten
        // bytes short: a real mid-frame truncation, unlike a short-but
        // complete frame with malformed JSON.
        var body =
            Encoding.UTF8.GetBytes("""{"version":6,"type":"exit","payload":{"exit_code":0,"timed_out":false}}""");
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)body.Length);
        using var stream = new MemoryStream();
        stream.Write(prefix, 0, prefix.Length);
        stream.Write(body, 0, body.Length - 10);
        stream.Position = 0;

        await Assert.ThrowsAnyAsync<IOException>(() => RunnerFrameCodec.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task WriteFrame_RejectsPayloadBeyondFrameLimitBeforeWriting()
    {
        using var stream = new MemoryStream();
        var oversized = new RunnerOutputPayload
        {
            DataBase64 = new string('A', RunnerFrameCodec.MaxFrameLength),
            Stream     = "stdout"
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => RunnerFrameCodec.WriteFrameAsync(stream, "output",
                                                           oversized, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task ReadFrame_ReturnsNullOnCleanEof()
    {
        using var stream = new MemoryStream();
        Assert.Null(await RunnerFrameCodec.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_RejectsMismatchedProtocolVersion()
    {
        var body =
            Encoding.UTF8.GetBytes("""{"version":5,"type":"exit","payload":{"exit_code":0,"timed_out":false}}""");
        using var stream = FrameStream(body);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                           RunnerFrameCodec.ReadFrameAsync(stream,
                                                               CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_RejectsOversizedLengthPrefix()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 8 * 1024 * 1024 + 1);
        using var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                           RunnerFrameCodec.ReadFrameAsync(stream,
                                                               CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_RejectsTruncatedBody()
    {
        var body =
            Encoding.UTF8.GetBytes("""{"version":6,"type":"exit","payload":{"exit_code":0,"timed_out":false}}""");
        var       truncated = body[..^10];
        using var stream    = FrameStream(truncated);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                           RunnerFrameCodec.ReadFrameAsync(stream,
                                                               CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_RejectsMissingPayloadObject()
    {
        var       body   = Encoding.UTF8.GetBytes("""{"version":6,"type":"exit"}""");
        using var stream = FrameStream(body);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
                                                           RunnerFrameCodec.ReadFrameAsync(stream,
                                                               CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_ParsesWellFormedFrameOfAnyType()
    {
        var       body   = Encoding.UTF8.GetBytes("""{"version":6,"type":"resize","payload":{"rows":10,"cols":10}}""");
        using var stream = FrameStream(body);
        var       frame  = await RunnerFrameCodec.ReadFrameAsync(stream, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal("resize", frame!.Type);
    }

    [Fact]
    public void ParsePayload_MissingRequiredExitFieldThrowsJsonException()
    {
        // exit_code and timed_out are required; a frame missing either fails
        // deserialization instead of silently becoming 0/false.
        var frame = new RunnerFrame("exit", JsonNode.Parse("""{"timed_out":false}""")!.AsObject());

        Assert.Throws<JsonException>(() => RunnerFrameCodec.ParsePayload<RunnerExitPayload>(frame));
    }

    private static MemoryStream FrameStream(byte[] body)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)body.Length);
        var stream = new MemoryStream();
        stream.Write(prefix, 0, 4);
        stream.Write(body, 0, body.Length);
        stream.Position = 0;
        return stream;
    }

    private static string ReadFrameBody(MemoryStream stream)
    {
        var prefix = new byte[4];
        stream.ReadExactly(prefix);
        var body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(prefix)];
        stream.ReadExactly(body);
        return Encoding.UTF8.GetString(body);
    }

    // ---- spawn_request wire shape ----

    [Fact]
    public async Task SpawnRequest_SerializesExactSnakeCaseWireShape()
    {
        var request = new RunnerSpawnRequest
        {
            Command          = ["cmd.exe", "/c", "whoami"],
            WorkingDirectory = @"C:\work\repo",
            Environment      = new Dictionary<string, string> { ["PATH"] = "C:\\windows" },
            PermissionProfile = SandboxPolicyResolver.Resolve(SandboxPolicyKind.ReadOnly, @"C:\work\repo",
                                                              new Dictionary<string, string>())
                                                     .SpawnProfile,
            WorkspaceRoots             = [@"C:\work\repo"],
            SandboxDirectory           = @"C:\harness\.codex-home\.sandbox",
            RealSandboxHome            = @"C:\harness\.codex-home",
            CapabilitySids             = ["S-1-5-21-1-2-3-4"],
            NetworkProxyRestrictingSid = null,
            TimeoutMilliseconds        = 30000,
            Tty                        = false,
            StdinOpen                  = false,
            PrivateDesktopName         = "CodexSandboxDesktop-0123456789abcdef0123456789abcdef"
        };

        using var stream = new MemoryStream();
        await RunnerFrameCodec.WriteFrameAsync(stream, "spawn_request", request, CancellationToken.None);
        stream.Position = 0;
        var frame   = await RunnerFrameCodec.ReadFrameAsync(stream, CancellationToken.None);
        var payload = frame!.Payload;

        Assert.Equal(["cmd.exe", "/c", "whoami"],
                     payload["command"]!.AsArray().Select(node => (string)node!).ToArray());
        Assert.Equal(@"C:\work\repo", (string)payload["cwd"]!);
        Assert.Equal("C:\\windows", (string)payload["env"]!["PATH"]!);
        Assert.Equal(@"C:\work\repo", (string)payload["workspace_roots"]![0]!);
        Assert.Equal(@"C:\harness\.codex-home\.sandbox", (string)payload["codex_home"]!);
        Assert.Equal(@"C:\harness\.codex-home", (string)payload["real_codex_home"]!);
        Assert.Equal("S-1-5-21-1-2-3-4", (string)payload["cap_sids"]![0]!);
        Assert.Null(payload["network_proxy_restricting_sid"]);
        Assert.Equal(30000, (int)payload["timeout_ms"]!);
        Assert.False((bool)payload["tty"]!);
        Assert.False((bool)payload["stdin_open"]!);
        Assert.Equal("CodexSandboxDesktop-0123456789abcdef0123456789abcdef",
                     (string)payload["private_desktop_name"]!);
        var profile = payload["permission_profile"]!;
        Assert.Equal("managed", (string)profile["type"]!);
        Assert.Equal("restricted", (string)profile["network"]!);
        Assert.Equal("restricted", (string)profile["file_system"]!["type"]!);
        Assert.Equal("root", (string)profile["file_system"]!["entries"]![0]!["path"]!["value"]!["kind"]!);
        Assert.Equal("read", (string)profile["file_system"]!["entries"]![0]!["access"]!);

        var decoded = RunnerFrameCodec.ParsePayload<RunnerSpawnRequest>(frame);
        Assert.Equal(["cmd.exe", "/c", "whoami"], decoded.Command);
        Assert.Equal(30000uL, decoded.TimeoutMilliseconds);
    }

    // ---- permission profile shapes ----

    [Fact]
    public void PolicyResolver_ReadOnly_MatchesBuiltInProfile()
    {
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.ReadOnly, @"C:\work\repo",
                                                   new Dictionary<string, string>());

        Assert.False(policy.UsesWriteCapabilities);
        Assert.Empty(policy.EffectiveWriteRoots);
        Assert.Empty(policy.DenyWritePaths);
        Assert.Empty(policy.TempWriteRoots);
        var entry = Assert.Single(policy.SpawnProfile.FileSystem.Entries);
        Assert.Equal(SandboxPolicyPath.Root(), entry.Path);
        Assert.Equal("read", entry.Access);
        Assert.Equal("restricted", policy.SpawnProfile.Network);
    }

    [Fact]
    public void PolicyResolver_WorkspaceWrite_MatchesBuiltInSemantics()
    {
        var environment = new Dictionary<string, string>
        {
            ["TEMP"] = @"C:\temp\custom",
            ["TMP"]  = @"C:\temp\custom"
        };
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, @"C:\work\repo", environment);

        Assert.True(policy.UsesWriteCapabilities);
        Assert.Equal([@"C:\work\repo", @"C:\temp\custom"], policy.EffectiveWriteRoots);
        Assert.Equal([@"C:\temp\custom"], policy.TempWriteRoots);
        Assert.Equal(SandboxIsolationPolicy.ProtectedMetadataSubpaths,
                     policy.DenyWritePaths.Select(path => Path.GetFileName(path)).ToArray());

        var entries  = policy.SpawnProfile.FileSystem.Entries;
        var metadata = entries.Skip(4).ToArray();
        Assert.Equal(8, entries.Count);
        Assert.Equal("read", entries[0].Access);                // root
        Assert.Equal("write", entries[1].Access);               // project_roots
        Assert.Equal("slash_tmp", entries[2].Path.Value!.Kind); // slash_tmp write
        Assert.Equal("tmpdir", entries[3].Path.Value!.Kind);    // tmpdir write
        Assert.All(metadata, entry =>
        {
            Assert.Equal("read", entry.Access);
            Assert.Equal("skip", entry.MissingPathBehavior);
            Assert.Equal("project_roots", entry.Path.Value!.Kind);
        });
        Assert.Equal([".git", ".agents", ".codex", ".aws"],
                     metadata.Select(entry => entry.Path.Value!.Subpath!).ToArray());
    }

    [Fact]
    public void PolicyResolver_IgnoresRelativeAndUnsetTempValues()
    {
        var environment = new Dictionary<string, string>
        {
            ["TEMP"] = "relative\\temp",
            ["TMP"]  = ""
        };
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, @"C:\work\repo", environment);
        Assert.Empty(policy.TempWriteRoots);
        Assert.Equal([@"C:\work\repo"], policy.EffectiveWriteRoots);
    }

    [Fact]
    public void PolicyResolver_IgnoresDriveRelativeTempValues()
    {
        // "C:temp" is drive-relative on Windows (Path.IsPathFullyQualified is
        // false) and relative elsewhere; resolving it against the host's
        // current directory would freeze an unintended write root.
        using var dir = new TestTempDir();
        var environment = new Dictionary<string, string>
        {
            ["TEMP"] = "C:temp",
            ["TMP"]  = "C:tmp"
        };
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root, environment);

        Assert.Empty(policy.TempWriteRoots);
        Assert.Equal([dir.Root], policy.EffectiveWriteRoots);
    }

    [Fact]
    public void Policy_CreateSetupPayload_RefreshOnlyCarriesResolvedRoots()
    {
        using var home = new SandboxTestHome();
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, @"C:\work\repo",
                                                   new Dictionary<string, string> { ["TEMP"] = @"C:\temp\custom" });

        var payload = policy.CreateSetupPayload(home.Components, @"C:\work\repo\sub", "tester", true);
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(
                                                 payload, WindowsSandboxJsonContext.Default.SandboxSetupPayload));
        var root = document.RootElement;
        Assert.Equal(5, root.GetProperty("version").GetInt32());
        Assert.Equal("TinyHarnessOffline", root.GetProperty("offline_username").GetString());
        Assert.Equal("tester", root.GetProperty("real_user").GetString());
        Assert.True(root.GetProperty("refresh_only").GetBoolean());
        Assert.Equal("full", root.GetProperty("mode").GetString());
        Assert.Equal(@"C:\work\repo\sub", root.GetProperty("command_cwd").GetString());
        Assert.Equal([@"C:\work\repo", @"C:\work\repo\sub", @"C:\temp\custom"],
                     root.GetProperty("read_roots").EnumerateArray().Select(node => node.GetString()!).ToArray());
        Assert.Equal([@"C:\work\repo", @"C:\temp\custom"],
                     root.GetProperty("write_roots").EnumerateArray().Select(node => node.GetString()!).ToArray());
        Assert.Equal([".git", ".agents", ".codex", ".aws"],
                     root.GetProperty("deny_write_paths").EnumerateArray()
                         .Select(node => Path.GetFileName(node.GetString()!)).ToArray());
        Assert.Equal(0, root.GetProperty("proxy_ports").GetArrayLength());
    }

    // ---- capability SID store ----

    [Fact]
    public async Task CapabilitySidStore_CreatesTableWhenMissingAndStaysStable()
    {
        using var dir = new TestTempDir();
        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = dir.WriteBytes("setup.exe", []),
            RunnerExecutablePath = dir.WriteBytes("runner.exe", []),
            SandboxHome          = dir.Root
        };
        var store = new SandboxCapabilitySidStore(components);
        var readOnly =
            SandboxPolicyResolver.Resolve(SandboxPolicyKind.ReadOnly, dir.Root, new Dictionary<string, string>());

        var first  = await store.ResolveForPolicy(readOnly);
        var second = await store.ResolveForPolicy(readOnly);

        Assert.Single(first);
        Assert.Equal(first, second);
        Assert.Matches(@"^S-1-5-21-\d+-\d+-\d+-\d+$", first[0]);
        Assert.True(File.Exists(components.CapabilitySidPath));
    }

    [Fact]
    public async Task CapabilitySidStore_GetOrCreatePerWriteRootPersistsEntries()
    {
        using var dir = new TestTempDir();
        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = dir.WriteBytes("setup.exe", []),
            RunnerExecutablePath = dir.WriteBytes("runner.exe", []),
            SandboxHome          = dir.Root
        };
        var store = new SandboxCapabilitySidStore(components);
        var policy =
            SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root, new Dictionary<string, string>());

        var first  = await store.ResolveForPolicy(policy);
        var second = await store.ResolveForPolicy(policy);

        // No TEMP/TMP in the environment, so the workspace root is the only
        // effective write root.
        Assert.Single(first);
        Assert.Equal(first, second);
        var savedTable = await new SandboxCapabilitySidStore(components).LoadOrCreate();
        Assert.Contains(SandboxCapabilitySidStore.CanonicalRootKey(dir.Root), savedTable.WritableRootByPath.Keys);
    }

    [Fact]
    public void CapabilitySidStore_CanonicalRootKeyMatchesRustKeyFormat()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Pinned to the Rust setup helper's canonical_path_key: forward
        // slashes and lower casing, so "C:\Work\Repo" and "c:/WORK/REPO"
        // normalize to the same table key.
        Assert.Equal("c:/work/repo", SandboxCapabilitySidStore.CanonicalRootKey(@"C:\Work\Repo"));
        Assert.Equal("c:/work/repo", SandboxCapabilitySidStore.CanonicalRootKey(@"c:/WORK/REPO"));
    }

    [Fact]
    public async Task CapabilitySidStore_RejectsLegacyTextTable()
    {
        using var dir = new TestTempDir();
        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = dir.WriteBytes("setup.exe", []),
            RunnerExecutablePath = dir.WriteBytes("runner.exe", []),
            SandboxHome          = dir.Root
        };
        dir.WriteFile("cap_sid", "S-1-5-21-9-9-9-9");
        var store = new SandboxCapabilitySidStore(components);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadOrCreate());
    }

    // ---- state inspector ----

    [Fact]
    public async Task StateInspector_ReportsReadyForFullyProvisionedHome()
    {
        using var home   = new SandboxTestHome();
        var       status = await new WindowsSandboxStateInspector(home.Components).InspectAsync();
        Assert.True(status.Ready, string.Join("; ", status.Problems));
    }

    [Fact]
    public async Task StateInspector_AggregatesEveryProblemForEmptyHome()
    {
        using var dir = new TestTempDir();
        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = Path.Combine(dir.Root, "missing-setup.exe"),
            RunnerExecutablePath = Path.Combine(dir.Root, "missing-runner.exe"),
            SandboxHome          = dir.Root
        };
        var status = await new WindowsSandboxStateInspector(components).InspectAsync();
        Assert.False(status.Ready);
        Assert.Equal(5, status.Problems.Count);
        Assert.Contains(status.Problems, problem => problem.Contains("Setup executable not found"));
        Assert.Contains(status.Problems, problem => problem.Contains("Runner executable not found"));
        Assert.Contains(status.Problems, problem => problem.Contains("not provisioned"));
        Assert.Contains(status.Problems, problem => problem.Contains("Capability SID table not found"));
        Assert.Contains(status.Problems, problem => problem.Contains("credentials not found"));
    }

    [Fact]
    public async Task StateInspector_RejectsVersionMismatchAndEmptyMarker()
    {
        using var home = new SandboxTestHome();
        File.WriteAllText(home.Components.MarkerPath, """{"version":4,"offline_username":"x","online_username":"y"}""");
        var mismatched = await new WindowsSandboxStateInspector(home.Components).InspectAsync();
        Assert.False(mismatched.Ready);
        Assert.Contains(mismatched.Problems, problem => problem.Contains("does not match the required 5"));

        File.WriteAllText(home.Components.MarkerPath, "");
        var empty = await new WindowsSandboxStateInspector(home.Components).InspectAsync();
        Assert.False(empty.Ready);
        Assert.Contains(empty.Problems, problem => problem.Contains("did not complete"));
    }

    [Fact]
    public async Task StateInspector_RefusesLegacyCodexMarkerAndCredentials()
    {
        using var home = new SandboxTestHome();
        File.WriteAllText(home.Components.MarkerPath,
                          """{"version":5,"offline_username":"CodexSandboxOffline","online_username":"CodexSandboxOnline","created_at":"2026-10-01T00:00:00Z"}""");
        File.WriteAllText(home.Components.UsersFilePath,
                          """{"version":5,"offline":{"username":"CodexSandboxOffline","password":"QUJDRA=="},"online":{"username":"CodexSandboxOnline","password":"QUJDRA=="}}""");

        var status = await new WindowsSandboxStateInspector(home.Components).InspectAsync();
        Assert.False(status.Ready);
        // Marker and credentials each report the legacy Codex namespace twice
        // (offline + online) with an explicit do-not-reuse explanation.
        Assert.Equal(4, status.Problems.Count(problem => problem.Contains("legacy Codex-release account name")));
        Assert.Contains(status.Problems,
                        problem => problem.Contains("Refusing to reuse it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StateInspector_RejectsAccountNameMismatchAgainstExpectedAccounts()
    {
        using var home = new SandboxTestHome();
        File.WriteAllText(home.Components.MarkerPath,
                          """{"version":5,"offline_username":"TinyHarnessOffline","online_username":"SomeOtherOnline","created_at":"2026-10-01T00:00:00Z"}""");

        var status = await new WindowsSandboxStateInspector(home.Components).InspectAsync();
        Assert.False(status.Ready);
        Assert.Contains(status.Problems,
                        problem => problem.Contains("'SomeOtherOnline'") &&
                                   problem.Contains("TinyHarnessOnline"));
        // The matching offline username contributes no problem of its own.
        Assert.DoesNotContain(status.Problems, problem => problem.Contains("TinyHarnessOffline'"));
    }

    [Fact]
    public void SandboxHome_DefaultAndResolutionRules()
    {
        Assert.EndsWith(Path.Combine("tinyharness", "windows-sandbox-home"),
                        WindowsSandboxComponents.DefaultSandboxHome);
        Assert.True(Path.IsPathFullyQualified(WindowsSandboxComponents.DefaultSandboxHome));

        // Blank resolves to the independent fixed default; explicit absolute
        // values pass through; explicitly relative values fail closed (padding
        // is not forgiven — same rule as the other trusted path settings).
        Assert.Equal(WindowsSandboxComponents.DefaultSandboxHome, WindowsSandboxComponents.ResolveSandboxHome(null));
        Assert.Equal(WindowsSandboxComponents.DefaultSandboxHome, WindowsSandboxComponents.ResolveSandboxHome("  "));
        Assert.Equal(@"C:\sb\home", WindowsSandboxComponents.ResolveSandboxHome(@"C:\sb\home"));
        Assert.Throws<InvalidOperationException>(() => WindowsSandboxComponents.ResolveSandboxHome("relative\\home"));
        Assert.Throws<InvalidOperationException>(() => WindowsSandboxComponents.ResolveSandboxHome("  C:\\sb\\home  "));
    }

    // ---- desktop SDDL ----

    [Fact]
    public void DesktopFactory_BuildSddlGrantsOwnerAllAndSandboxParticipant()
    {
        if (!OperatingSystem.IsWindows()) return;

        var owner   = new SecurityIdentifier("S-1-5-21-100-200-300-1001");
        var sandbox = new SecurityIdentifier("S-1-5-21-100-200-300-404");

        var sddl = WindowsSandboxDesktopFactory.BuildSddl(owner, sandbox);

        Assert.StartsWith("D:P", sddl); // protected DACL, no inheritance
        // The owner ACE carries DESKTOP_ALL_ACCESS and the sandbox ACE carries
        // DESKTOP_PARTICIPANT_ACCESS; both masks appear as lowercase hex.
        Assert.Equal("f01ff", $"{WindowsSandboxDesktopFactory.DesktopAllAccess:x}");
        Assert.Equal("201ff", $"{WindowsSandboxDesktopFactory.DesktopParticipantAccess:x}");
        Assert.Contains($"""(A;;0x{WindowsSandboxDesktopFactory.DesktopAllAccess:x};;;{owner.Value})""", sddl,
                        StringComparison.Ordinal);
        Assert.Contains($"""(A;;0x{WindowsSandboxDesktopFactory.DesktopParticipantAccess:x};;;{sandbox.Value})""", sddl,
                        StringComparison.Ordinal);
    }

    // ---- credential reader (DPAPI roundtrip, Windows only) ----

    [Fact]
    public async Task CredentialReader_DecryptsDpapiPasswordForExistingAccount()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home     = new SandboxTestHome();
        var       identity = WindowsIdentity.GetCurrent().Name.Split('\\');
        if (identity.Length != 2 || !identity[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            // SID lookup requires a machine-local account; skip elsewhere.
            return;

        var secret = "sandbox-password-Ø";
        var blob = ProtectedData.Protect(
                                         Encoding.UTF8.GetBytes(secret), null,
                                         DataProtectionScope.LocalMachine);
        File.WriteAllText(home.Components.UsersFilePath,
                          "{\"version\":5,\"offline\":{\"username\":\"" + identity[1]                  +
                          "\",\"password\":\""                          + Convert.ToBase64String(blob) +
                          "\"},\"online\":{\"username\":\"TinyHarnessOnline\",\"password\":\"QUJDRA==\"}}");

        // The reader accepts whichever offline account the components expect;
        // the DPAPI roundtrip itself is the point of this test.
        var components = home.Components with { OfflineUsername = identity[1] };
        var account    = await new SandboxCredentialReader(components).LoadOfflineAccount();
        Assert.Equal(identity[1], account.Username);
        Assert.Equal(secret, account.Password);
        Assert.StartsWith("S-1-", account.AccountSid.Value);
    }

    [Fact]
    public async Task CredentialReader_RejectsLegacyCodexUsernameWithoutDecrypting()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        // The password blob is placeholder base64: the username check must
        // refuse before any DPAPI decryption is attempted.
        File.WriteAllText(home.Components.UsersFilePath,
                          """{"version":5,"offline":{"username":"CodexSandboxOffline","password":"QUJDRA=="},"online":{"username":"CodexSandboxOnline","password":"QUJDRA=="}}""");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SandboxCredentialReader(home.Components).LoadOfflineAccount());
        Assert.Contains("legacy Codex account 'CodexSandboxOffline'", exception.Message);
        Assert.Contains("refusing to reuse", exception.Message);
    }

    [Fact]
    public async Task CredentialReader_RejectsUnexpectedOfflineAccountName()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        File.WriteAllText(home.Components.UsersFilePath,
                          """{"version":5,"offline":{"username":"SomeOtherAccount","password":"QUJDRA=="},"online":{"username":"TinyHarnessOnline","password":"QUJDRA=="}}""");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SandboxCredentialReader(home.Components).LoadOfflineAccount());
        Assert.Contains("SomeOtherAccount", exception.Message);
        Assert.Contains("TinyHarnessOffline", exception.Message);
    }

    [Fact]
    public async Task CredentialReader_RejectsVersionMismatch()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        File.WriteAllText(home.Components.UsersFilePath,
                          """{"version":4,"offline":{"username":"a","password":"QQ=="},"online":{"username":"b","password":"QQ=="}}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SandboxCredentialReader(home.Components)
                                                               .LoadOfflineAccount());
    }

    // ---- setup invoker payload channel ----

    [Fact]
    public void SetupInvoker_UsesArgumentChannelForSmallPayloads()
    {
        using var home        = new SandboxTestHome();
        var       invoker     = new ProcessSandboxSetupInvoker(home.Components);
        var       smallBase64 = Convert.ToBase64String([1, 2, 3]);

        var startInfo = invoker.BuildStartInfo(smallBase64);
        Assert.Equal(smallBase64, startInfo.Arguments);
        Assert.False(startInfo.UseShellExecute); // refresh never elevates
    }

    [Fact]
    public void SetupInvoker_SwitchesToChunkedEnvChannelForLargePayloads()
    {
        using var home        = new SandboxTestHome();
        var       invoker     = new ProcessSandboxSetupInvoker(home.Components);
        var       largeBase64 = new string('A', ProcessSandboxSetupInvoker.PayloadArgumentCharacterLimit + 5);

        var startInfo = invoker.BuildStartInfo(largeBase64);
        Assert.Equal("--launch-payload-env", startInfo.Arguments);
        Assert.Equal(largeBase64.Length.ToString(), startInfo.Environment["CODEX_SANDBOX_LAUNCH_BYTES"]);
        var chunkCount = int.Parse(startInfo.Environment["CODEX_SANDBOX_LAUNCH_COUNT"]!);
        var reassembled = string.Concat(Enumerable.Range(0, chunkCount)
                                                  .Select(index =>
                                                              startInfo.Environment[$"CODEX_SANDBOX_LAUNCH_{index}"]));
        Assert.Equal(largeBase64, reassembled);
        Assert.All(Enumerable.Range(0, chunkCount),
                   index => Assert.True(startInfo.Environment[$"CODEX_SANDBOX_LAUNCH_{index}"]!.Length <= 16 * 1024));
    }

    // ---- runner command-line quoting ----

    [Theory]
    [InlineData("plain.exe", "plain.exe")]
    [InlineData("", "\"\"")]
    [InlineData("with space.exe", "\"with space.exe\"")]
    [InlineData("quote\"inside.exe", "\"quote\\\"inside.exe\"")]
    [InlineData(@"C:\path\endswith\", "C:\\path\\endswith\\")]
    [InlineData("with space\\", "\"with space\\\\\"")]
    [InlineData(@"C:\pre\""quote", "\"C:\\pre\\\\\\\"quote\"")]
    public void RunnerLauncher_QuoteArgumentFollowsCrtRules(string value, string expected)
    {
        Assert.Equal(expected, WindowsSandboxRunnerLauncher.QuoteArgument(value));
    }

    [Fact]
    public void RunnerLauncher_CommandLinePassesFullPipeNames()
    {
        var commandLine = WindowsSandboxRunnerLauncher.BuildRunnerCommandLine(
                                                                              @"C:\tools\codex-command-runner.exe",
                                                                              @"\\.\pipe\codex-runner-abcd-in",
                                                                              @"\\.\pipe\codex-runner-abcd-out");
        // CRT rules add no quotes for tokens without whitespace/quotes.
        Assert.Equal(@"C:\tools\codex-command-runner.exe"           +
                     " --pipe-in=\\\\.\\pipe\\codex-runner-abcd-in" +
                     " --pipe-out=\\\\.\\pipe\\codex-runner-abcd-out",
                     commandLine);
    }

    // ---- desktop name format ----

    [Fact]
    public void DesktopName_Uses32LowercaseHexDigits()
    {
        var suffix = WindowsSandboxDesktopFactory.GenerateNameSuffix();
        Assert.Equal(32, suffix.Length);
        Assert.All(suffix, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }
}
