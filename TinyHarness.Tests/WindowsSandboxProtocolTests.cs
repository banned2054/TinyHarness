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
///     <para>
///         帧协议与 wire DTO 测试：长度前缀、版本硬校验、截断/超长、空 payload 控制帧、
///         spawn_request/setup payload 的精确字段形状，以及 cap_sid、状态检查、凭据与策略解析。
///         全部离线，不启动进程、不修改机器状态。
///     </para>
///     <para>
///         Frame-protocol and wire-DTO tests: length prefixes, the hard version
///         check, truncation/oversize, empty-payload control frames, exact field
///         shapes of spawn_request/setup payloads, plus cap_sid, readiness checks,
///         credentials, and policy resolution. Fully offline — no processes, no
///         machine-state changes.
///     </para>
/// </summary>
public class WindowsSandboxProtocolTests
{
    // ---- RunnerFrameCodec ----

    [Test]
    public async Task WriteFrame_RoundTripsThroughLengthPrefixAndVersion()
    {
        using var stream = new MemoryStream();
        await RunnerFrameCodec.WriteFrameAsync(stream, "exit",
                                               new RunnerExitPayload { ExitCode = 23, TimedOut = false },
                                               CancellationToken.None);

        stream.Position = 0;
        var frame = await RunnerFrameCodec.ReadFrameAsync(stream, CancellationToken.None);
        TestAssert.NotNull(frame);
        Assert.That(frame.Type, Is.EqualTo("exit"));
        var payload = RunnerFrameCodec.ParsePayload<RunnerExitPayload>(frame);
        Assert.That(payload.ExitCode, Is.EqualTo(23));
        Assert.That(payload.TimedOut, Is.False);
    }

    [Test]
    public async Task WriteFrame_ProducesExactEnvelopeShape()
    {
        using var stream = new MemoryStream();
        await RunnerFrameCodec.WriteFrameAsync(stream, "spawn_ready", new RunnerSpawnReady { ProcessId = 4242 },
                                               CancellationToken.None);

        stream.Position = 0;
        var body     = ReadFrameBody(stream);
        var envelope = JsonNode.Parse(body)!.AsObject();
        Assert.That((int)envelope["version"]!, Is.EqualTo(6));
        Assert.That((string)envelope["type"]!, Is.EqualTo("spawn_ready"));
        Assert.That((int)envelope["payload"]!["process_id"]!, Is.EqualTo(4242));
    }

    [Test]
    public async Task WriteControlFrame_KeepsEmptyPayloadObject()
    {
        using var stream = new MemoryStream();
        await RunnerFrameCodec.WriteControlFrameAsync(stream, "terminate", CancellationToken.None);

        stream.Position = 0;
        var body = ReadFrameBody(stream);
        Assert.That(body, Is.EqualTo("""{"version":6,"type":"terminate","payload":{}}"""));
    }

    [Test]
    public async Task ReadFrame_ThrowsEndOfStreamWhenDeclaredBodyTruncates()
    {
        // 长度前缀承诺完整帧而流提前十个字节结束：真正的帧中截断，区别于"短但完整、
        // JSON 非法"的帧。
        var body =
            Encoding.UTF8.GetBytes("""{"version":6,"type":"exit","payload":{"exit_code":0,"timed_out":false}}""");
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)body.Length);
        using var stream = new MemoryStream();
        stream.Write(prefix, 0, prefix.Length);
        stream.Write(body, 0, body.Length - 10);
        stream.Position = 0;

        await Assert.CatchAsync<IOException>(() => RunnerFrameCodec.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Test]
    public async Task WriteFrame_RejectsPayloadBeyondFrameLimitBeforeWriting()
    {
        using var stream = new MemoryStream();
        var oversized = new RunnerOutputPayload
        {
            DataBase64 = new string('A', RunnerFrameCodec.MaxFrameLength),
            Stream     = "stdout"
        };

        await TestAssert.ThrowsAsync<InvalidDataException>(() => RunnerFrameCodec.WriteFrameAsync(stream, "output",
                                                                    oversized, CancellationToken.None));
        Assert.That(stream.Length, Is.EqualTo(0));
    }

    [Test]
    public async Task ReadFrame_ReturnsNullOnCleanEof()
    {
        using var stream = new MemoryStream();
        Assert.That(await RunnerFrameCodec.ReadFrameAsync(stream, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task ReadFrame_RejectsMismatchedProtocolVersion()
    {
        var body =
            Encoding.UTF8.GetBytes("""{"version":5,"type":"exit","payload":{"exit_code":0,"timed_out":false}}""");
        using var stream = FrameStream(body);
        await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                               RunnerFrameCodec.ReadFrameAsync(stream,
                                                                        CancellationToken.None));
    }

    [Test]
    public async Task ReadFrame_RejectsOversizedLengthPrefix()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 8 * 1024 * 1024 + 1);
        using var stream = new MemoryStream(prefix);
        await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                               RunnerFrameCodec.ReadFrameAsync(stream,
                                                                        CancellationToken.None));
    }

    [Test]
    public async Task ReadFrame_RejectsTruncatedBody()
    {
        var body =
            Encoding.UTF8.GetBytes("""{"version":6,"type":"exit","payload":{"exit_code":0,"timed_out":false}}""");
        var       truncated = body[..^10];
        using var stream    = FrameStream(truncated);
        await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                               RunnerFrameCodec.ReadFrameAsync(stream,
                                                                        CancellationToken.None));
    }

    [Test]
    public async Task ReadFrame_RejectsMissingPayloadObject()
    {
        var       body   = Encoding.UTF8.GetBytes("""{"version":6,"type":"exit"}""");
        using var stream = FrameStream(body);
        await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                               RunnerFrameCodec.ReadFrameAsync(stream,
                                                                        CancellationToken.None));
    }

    [Test]
    public async Task ReadFrame_ParsesWellFormedFrameOfAnyType()
    {
        var       body   = Encoding.UTF8.GetBytes("""{"version":6,"type":"resize","payload":{"rows":10,"cols":10}}""");
        using var stream = FrameStream(body);
        var       frame  = await RunnerFrameCodec.ReadFrameAsync(stream, CancellationToken.None);
        TestAssert.NotNull(frame);
        Assert.That(frame!.Type, Is.EqualTo("resize"));
    }

    [Test]
    public void ParsePayload_MissingRequiredExitFieldThrowsJsonException()
    {
        // exit_code 与 timed_out 必填：缺任一字段的帧反序列化失败，而不是静默变成 0/false。
        var frame = new RunnerFrame("exit", JsonNode.Parse("""{"timed_out":false}""")!.AsObject());

        TestAssert.Throws<JsonException>(() => RunnerFrameCodec.ParsePayload<RunnerExitPayload>(frame));
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

    [Test]
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

        Assert.That(payload["command"]!.AsArray().Select(node => (string)node!).ToArray(),
                    Is.EqualTo(["cmd.exe", "/c", "whoami"]));
        Assert.That((string)payload["cwd"]!, Is.EqualTo(@"C:\work\repo"));
        Assert.That((string)payload["env"]!["PATH"]!, Is.EqualTo("C:\\windows"));
        Assert.That((string)payload["workspace_roots"]![0]!, Is.EqualTo(@"C:\work\repo"));
        Assert.That((string)payload["codex_home"]!, Is.EqualTo(@"C:\harness\.codex-home\.sandbox"));
        Assert.That((string)payload["real_codex_home"]!, Is.EqualTo(@"C:\harness\.codex-home"));
        Assert.That((string)payload["cap_sids"]![0]!, Is.EqualTo("S-1-5-21-1-2-3-4"));
        Assert.That(payload["network_proxy_restricting_sid"], Is.Null);
        Assert.That((int)payload["timeout_ms"]!, Is.EqualTo(30000));
        Assert.That((bool)payload["tty"]!, Is.False);
        Assert.That((bool)payload["stdin_open"]!, Is.False);
        Assert.That((string)payload["private_desktop_name"]!,
                    Is.EqualTo("CodexSandboxDesktop-0123456789abcdef0123456789abcdef"));
        var profile = payload["permission_profile"]!;
        Assert.That((string)profile["type"]!, Is.EqualTo("managed"));
        Assert.That((string)profile["network"]!, Is.EqualTo("restricted"));
        Assert.That((string)profile["file_system"]!["type"]!, Is.EqualTo("restricted"));
        Assert.That((string)profile["file_system"]!["entries"]![0]!["path"]!["value"]!["kind"]!, Is.EqualTo("root"));
        Assert.That((string)profile["file_system"]!["entries"]![0]!["access"]!, Is.EqualTo("read"));

        var decoded = RunnerFrameCodec.ParsePayload<RunnerSpawnRequest>(frame);
        Assert.That(decoded.Command, Is.EqualTo(["cmd.exe", "/c", "whoami"]));
        Assert.That(decoded.TimeoutMilliseconds, Is.EqualTo(30000uL));
    }

    // ---- permission profile shapes ----

    [Test]
    public void PolicyResolver_ReadOnly_MatchesBuiltInProfile()
    {
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.ReadOnly, @"C:\work\repo",
                                                   new Dictionary<string, string>());

        Assert.That(policy.UsesWriteCapabilities, Is.False);
        Assert.That(policy.EffectiveWriteRoots, Is.Empty);
        Assert.That(policy.DenyWritePaths, Is.Empty);
        Assert.That(policy.TempWriteRoots, Is.Empty);
        var entry = TestAssert.Single(policy.SpawnProfile.FileSystem.Entries);
        Assert.That(entry.Path, Is.EqualTo(SandboxPolicyPath.Root()));
        Assert.That(entry.Access, Is.EqualTo("read"));
        Assert.That(policy.SpawnProfile.Network, Is.EqualTo("restricted"));
    }

    [Test]
    public void PolicyResolver_WorkspaceWrite_MatchesBuiltInSemantics()
    {
        var environment = new Dictionary<string, string>
        {
            ["TEMP"] = @"C:\temp\custom",
            ["TMP"]  = @"C:\temp\custom"
        };
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, @"C:\work\repo", environment);

        Assert.That(policy.UsesWriteCapabilities, Is.True);
        Assert.That(policy.EffectiveWriteRoots, Is.EqualTo([@"C:\work\repo", @"C:\temp\custom"]));
        Assert.That(policy.TempWriteRoots, Is.EqualTo([@"C:\temp\custom"]));
        Assert.That(policy.DenyWritePaths.Select(path => Path.GetFileName(path)).ToArray(),
                    Is.EqualTo(SandboxIsolationPolicy.ProtectedMetadataSubpaths));

        var entries  = policy.SpawnProfile.FileSystem.Entries;
        var metadata = entries.Skip(4).ToArray();
        Assert.That(entries.Count, Is.EqualTo(8));
        Assert.That(entries[0].Access, Is.EqualTo("read"));                // root
        Assert.That(entries[1].Access, Is.EqualTo("write"));               // project_roots
        Assert.That(entries[2].Path.Value!.Kind, Is.EqualTo("slash_tmp")); // slash_tmp write
        Assert.That(entries[3].Path.Value!.Kind, Is.EqualTo("tmpdir"));    // tmpdir write
        TestAssert.All(metadata, entry =>
        {
            Assert.That(entry.Access, Is.EqualTo("read"));
            Assert.That(entry.MissingPathBehavior, Is.EqualTo("skip"));
            Assert.That(entry.Path.Value!.Kind, Is.EqualTo("project_roots"));
        });
        Assert.That(metadata.Select(entry => entry.Path.Value!.Subpath!).ToArray(),
                    Is.EqualTo([".git", ".agents", ".codex", ".aws"]));
    }

    [Test]
    public void PolicyResolver_IgnoresRelativeAndUnsetTempValues()
    {
        var environment = new Dictionary<string, string>
        {
            ["TEMP"] = "relative\\temp",
            ["TMP"]  = ""
        };
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, @"C:\work\repo", environment);
        Assert.That(policy.TempWriteRoots, Is.Empty);
        Assert.That(policy.EffectiveWriteRoots, Is.EqualTo([@"C:\work\repo"]));
    }

    [Test]
    public void PolicyResolver_IgnoresDriveRelativeTempValues()
    {
        // "C:temp" 在 Windows 上是驱动器相对路径（Path.IsPathFullyQualified 为 false），
        // 其他平台是相对路径；按宿主当前目录解析它会把非预期的写根冻结进策略。
        using var dir = new TestTempDir();
        var environment = new Dictionary<string, string>
        {
            ["TEMP"] = "C:temp",
            ["TMP"]  = "C:tmp"
        };
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root, environment);

        Assert.That(policy.TempWriteRoots, Is.Empty);
        Assert.That(policy.EffectiveWriteRoots, Is.EqualTo([dir.Root]));
    }

    [Test]
    public void Policy_CreateSetupPayload_RefreshOnlyCarriesResolvedRoots()
    {
        using var home = new SandboxTestHome();
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, @"C:\work\repo",
                                                   new Dictionary<string, string> { ["TEMP"] = @"C:\temp\custom" });

        var payload = policy.CreateSetupPayload(home.Components, @"C:\work\repo\sub", "tester", true);
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(
                                                     payload, WindowsSandboxJsonContext.Default.SandboxSetupPayload));
        var root = document.RootElement;
        Assert.That(root.GetProperty("version").GetInt32(), Is.EqualTo(5));
        Assert.That(root.GetProperty("offline_username").GetString(), Is.EqualTo("TinyHarnessOffline"));
        Assert.That(root.GetProperty("real_user").GetString(), Is.EqualTo("tester"));
        Assert.That(root.GetProperty("refresh_only").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("mode").GetString(), Is.EqualTo("full"));
        Assert.That(root.GetProperty("command_cwd").GetString(), Is.EqualTo(@"C:\work\repo\sub"));
        Assert.That(root.GetProperty("read_roots").EnumerateArray().Select(node => node.GetString()!).ToArray(),
                    Is.EqualTo([@"C:\work\repo", @"C:\work\repo\sub", @"C:\temp\custom"]));
        Assert.That(root.GetProperty("write_roots").EnumerateArray().Select(node => node.GetString()!).ToArray(),
                    Is.EqualTo([@"C:\work\repo", @"C:\temp\custom"]));
        Assert.That(root.GetProperty("deny_write_paths").EnumerateArray()
                        .Select(node => Path.GetFileName(node.GetString()!)).ToArray(),
                    Is.EqualTo([".git", ".agents", ".codex", ".aws"]));
        Assert.That(root.GetProperty("proxy_ports").GetArrayLength(), Is.EqualTo(0));
    }

    // ---- capability SID store ----

    [Test]
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

        var first  = await store.ResolveForPolicy(readOnly, dir.Root);
        var second = await store.ResolveForPolicy(readOnly, dir.Root);

        TestAssert.Single(first);
        Assert.That(second, Is.EqualTo(first));
        Assert.That(first[0], Does.Match(@"^S-1-5-21-\d+-\d+-\d+-\d+$"));
        Assert.That(File.Exists(components.CapabilitySidPath), Is.True);
    }

    [Test]
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

        var first  = await store.ResolveForPolicy(policy, dir.Root);
        var second = await store.ResolveForPolicy(policy, dir.Root);

        // 环境中没有 TEMP/TMP，工作区根是唯一有效写根。
        TestAssert.Single(first);
        Assert.That(second, Is.EqualTo(first));
        var savedTable = await new SandboxCapabilitySidStore(components).LoadOrCreate();
        // Rust 契约（cap.rs 的 workspace_write_cap_sid_for_root）：写根等于命令 cwd 时使用
        // per-cwd workspace 表——SID setup 授权的正是该目录——绝不使用 per-root 表。
        Assert.That(savedTable.WorkspaceByCwd.Keys, Does.Contain(SandboxCapabilitySidStore.CanonicalRootKey(dir.Root)));
        Assert.That(savedTable.WritableRootByPath.Keys,
                    Does.Not.Contain(SandboxCapabilitySidStore.CanonicalRootKey(dir.Root)));
    }

    [Test]
    public async Task CapabilitySidStore_AdditionalWriteRootsWithDifferentCwdUsePerRootTable()
    {
        using var dir = new TestTempDir();
        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = dir.WriteBytes("setup.exe", []),
            RunnerExecutablePath = dir.WriteBytes("runner.exe", []),
            SandboxHome          = dir.Root
        };
        var store = new SandboxCapabilitySidStore(components);
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root,
                                                   new Dictionary<string, string>(),
                                                   [Path.Combine(dir.Root, "extra-cache")]);

        // 命令从子目录运行，工作区根不是 cwd：所有有效写根必须使用 per-root 表。
        var resolved = await store.ResolveForPolicy(policy, Path.Combine(dir.Root, "sub"));

        var savedTable = await store.LoadOrCreate();
        Assert.That(resolved.Count, Is.EqualTo(2));
        Assert.That(savedTable.WritableRootByPath.Keys,
                    Does.Contain(SandboxCapabilitySidStore.CanonicalRootKey(dir.Root)));
        Assert.That(savedTable.WritableRootByPath.Keys,
                    Does.Contain(SandboxCapabilitySidStore.CanonicalRootKey(Path.Combine(dir.Root, "extra-cache"))));
        Assert.That(savedTable.WorkspaceByCwd, Is.Empty);
    }

    [Test]
    public void CapabilitySidStore_CanonicalRootKeyMatchesRustKeyFormat()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 与 Rust setup 辅助的 canonical_path_key 对齐：正斜杠加小写化，
        // "C:\Work\Repo" 与 "c:/WORK/REPO" 归一为同一表键。
        Assert.That(SandboxCapabilitySidStore.CanonicalRootKey(@"C:\Work\Repo"), Is.EqualTo("c:/work/repo"));
        Assert.That(SandboxCapabilitySidStore.CanonicalRootKey(@"c:/WORK/REPO"), Is.EqualTo("c:/work/repo"));
    }

    [Test]
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
        await TestAssert.ThrowsAsync<InvalidOperationException>(() => store.LoadOrCreate());
    }

    // ---- state inspector ----

    [Test]
    public async Task StateInspector_ReportsReadyForFullyProvisionedHome()
    {
        using var home   = new SandboxTestHome();
        var       status = await new WindowsSandboxStateInspector(home.Components).InspectAsync();
        Assert.That(status.Ready, Is.True, string.Join("; ", status.Problems));
    }

    [Test]
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
        Assert.That(status.Ready, Is.False);
        Assert.That(status.Problems.Count, Is.EqualTo(5));
        TestAssert.ContainsAll(problem => problem.Contains("Setup executable not found"), status.Problems);
        TestAssert.ContainsAll(problem => problem.Contains("Runner executable not found"), status.Problems);
        TestAssert.ContainsAll(problem => problem.Contains("not provisioned"), status.Problems);
        TestAssert.ContainsAll(problem => problem.Contains("Capability SID table not found"), status.Problems);
        TestAssert.ContainsAll(problem => problem.Contains("credentials not found"), status.Problems);
    }

    [Test]
    public async Task StateInspector_RejectsVersionMismatchAndEmptyMarker()
    {
        using var home = new SandboxTestHome();
        File.WriteAllText(home.Components.MarkerPath, """{"version":4,"offline_username":"x","online_username":"y"}""");
        var mismatched = await new WindowsSandboxStateInspector(home.Components).InspectAsync();
        Assert.That(mismatched.Ready, Is.False);
        TestAssert.ContainsAll(problem => problem.Contains("does not match the required 5"), mismatched.Problems);

        File.WriteAllText(home.Components.MarkerPath, "");
        var empty = await new WindowsSandboxStateInspector(home.Components).InspectAsync();
        Assert.That(empty.Ready, Is.False);
        TestAssert.ContainsAll(problem => problem.Contains("did not complete"), empty.Problems);
    }

    [Test]
    public async Task StateInspector_RefusesLegacyCodexMarkerAndCredentials()
    {
        using var home = new SandboxTestHome();
        File.WriteAllText(home.Components.MarkerPath,
                          """{"version":5,"offline_username":"CodexSandboxOffline","online_username":"CodexSandboxOnline","created_at":"2026-10-01T00:00:00Z"}""");
        File.WriteAllText(home.Components.UsersFilePath,
                          """{"version":5,"offline":{"username":"CodexSandboxOffline","password":"QUJDRA=="},"online":{"username":"CodexSandboxOnline","password":"QUJDRA=="}}""");

        var status = await new WindowsSandboxStateInspector(home.Components).InspectAsync();
        Assert.That(status.Ready, Is.False);
        // marker 与凭据各报告两次旧 Codex 命名空间（offline + online），并附明确的
        // 禁止复用说明。
        Assert.That(status.Problems.Count(problem => problem.Contains("legacy Codex-release account name")),
                    Is.EqualTo(4));
        TestAssert.ContainsAll(problem => problem.Contains("Refusing to reuse it", StringComparison.Ordinal),
                               status.Problems);
    }

    [Test]
    public async Task StateInspector_RejectsAccountNameMismatchAgainstExpectedAccounts()
    {
        using var home = new SandboxTestHome();
        File.WriteAllText(home.Components.MarkerPath,
                          """{"version":5,"offline_username":"TinyHarnessOffline","online_username":"SomeOtherOnline","created_at":"2026-10-01T00:00:00Z"}""");

        var status = await new WindowsSandboxStateInspector(home.Components).InspectAsync();
        Assert.That(status.Ready, Is.False);
        TestAssert.ContainsAll(problem => problem.Contains("'SomeOtherOnline'") &&
                                          problem.Contains("TinyHarnessOnline"), status.Problems);
        // 匹配的 offline 用户名本身不产生问题项。
        TestAssert.DoesNotContainAny(problem => problem.Contains("TinyHarnessOffline'"), status.Problems);
    }

    [Test]
    public void SandboxHome_DefaultAndResolutionRules()
    {
        Assert.That(WindowsSandboxComponents.DefaultSandboxHome,
                    Does.EndWith(Path.Combine("tinyharness", "windows-sandbox-home")));
        Assert.That(Path.IsPathFullyQualified(WindowsSandboxComponents.DefaultSandboxHome), Is.True);

        // 空白解析为独立的固定默认值；显式绝对值原样通过；显式相对值失败关闭
        //（填充空白不豁免——与其他可信路径设置同规）。
        Assert.That(WindowsSandboxComponents.ResolveSandboxHome(null),
                    Is.EqualTo(WindowsSandboxComponents.DefaultSandboxHome));
        Assert.That(WindowsSandboxComponents.ResolveSandboxHome("  "),
                    Is.EqualTo(WindowsSandboxComponents.DefaultSandboxHome));
        Assert.That(WindowsSandboxComponents.ResolveSandboxHome(@"C:\sb\home"), Is.EqualTo(@"C:\sb\home"));
        TestAssert.Throws<InvalidOperationException>(() => WindowsSandboxComponents
                                                        .ResolveSandboxHome("relative\\home"));
        TestAssert.Throws<InvalidOperationException>(() =>
                                                         WindowsSandboxComponents
                                                            .ResolveSandboxHome("  C:\\sb\\home  "));
    }

    // ---- desktop SDDL ----

    [Test]
    public void DesktopFactory_BuildSddlGrantsOwnerAllAndSandboxParticipant()
    {
        if (!OperatingSystem.IsWindows()) return;

        var owner   = new SecurityIdentifier("S-1-5-21-100-200-300-1001");
        var sandbox = new SecurityIdentifier("S-1-5-21-100-200-300-404");

        var sddl = WindowsSandboxDesktopFactory.BuildSddl(owner, sandbox);

        Assert.That(sddl, Does.StartWith("D:P")); // 受保护 DACL，不继承
        // 属主 ACE 携带 DESKTOP_ALL_ACCESS，沙箱 ACE 携带 DESKTOP_PARTICIPANT_ACCESS；
        // 两个掩码都以小写十六进制出现。
        Assert.That($"{WindowsSandboxDesktopFactory.DesktopAllAccess:x}", Is.EqualTo("f01ff"));
        Assert.That($"{WindowsSandboxDesktopFactory.DesktopParticipantAccess:x}", Is.EqualTo("201ff"));
        TestAssert.Contains($"""(A;;0x{WindowsSandboxDesktopFactory.DesktopAllAccess:x};;;{owner.Value})""", sddl,
                            StringComparison.Ordinal);
        TestAssert.Contains($"""(A;;0x{WindowsSandboxDesktopFactory.DesktopParticipantAccess:x};;;{sandbox.Value})""",
                            sddl, StringComparison.Ordinal);
    }

    // ---- credential reader (DPAPI roundtrip, Windows only) ----

    [Test]
    public async Task CredentialReader_DecryptsDpapiPasswordForExistingAccount()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home     = new SandboxTestHome();
        var       identity = WindowsIdentity.GetCurrent().Name.Split('\\');
        if (identity.Length != 2 || !identity[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            // SID 查找需要本机账户；其他环境跳过。
            return;

        var secret = "sandbox-password-Ø";
        var blob   = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.LocalMachine);
        File.WriteAllText(home.Components.UsersFilePath,
                          "{\"version\":5,\"offline\":{\"username\":\"" + identity[1]                  +
                          "\",\"password\":\""                          + Convert.ToBase64String(blob) +
                          "\"},\"online\":{\"username\":\"TinyHarnessOnline\",\"password\":\"QUJDRA==\"}}");

        // 读取器接受组件期望的任一 offline 账户；本测试的核心是 DPAPI 往返本身。
        var components = home.Components with { OfflineUsername = identity[1] };
        var account    = await new SandboxCredentialReader(components).LoadOfflineAccount();
        Assert.That(account.Username, Is.EqualTo(identity[1]));
        Assert.That(account.Password, Is.EqualTo(secret));
        Assert.That(account.AccountSid.Value, Does.StartWith("S-1-"));
    }

    [Test]
    public async Task CredentialReader_RejectsLegacyCodexUsernameWithoutDecrypting()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        // 密码 blob 是占位 base64：用户名检查必须在尝试任何 DPAPI 解密前拒绝。
        File.WriteAllText(home.Components.UsersFilePath,
                          """{"version":5,"offline":{"username":"CodexSandboxOffline","password":"QUJDRA=="},"online":{"username":"CodexSandboxOnline","password":"QUJDRA=="}}""");
        var exception =
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => new SandboxCredentialReader(home.Components)
                                                                       .LoadOfflineAccount());
        Assert.That(exception.Message, Does.Contain("legacy Codex account 'CodexSandboxOffline'"));
        Assert.That(exception.Message, Does.Contain("refusing to reuse"));
    }

    [Test]
    public async Task CredentialReader_RejectsUnexpectedOfflineAccountName()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        File.WriteAllText(home.Components.UsersFilePath,
                          """{"version":5,"offline":{"username":"SomeOtherAccount","password":"QUJDRA=="},"online":{"username":"TinyHarnessOnline","password":"QUJDRA=="}}""");
        var exception =
            await TestAssert.ThrowsAsync<InvalidOperationException>(() => new SandboxCredentialReader(home.Components)
                                                                       .LoadOfflineAccount());
        Assert.That(exception.Message, Does.Contain("SomeOtherAccount"));
        Assert.That(exception.Message, Does.Contain("TinyHarnessOffline"));
    }

    [Test]
    public async Task CredentialReader_RejectsVersionMismatch()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        File.WriteAllText(home.Components.UsersFilePath,
                          """{"version":4,"offline":{"username":"a","password":"QQ=="},"online":{"username":"b","password":"QQ=="}}""");
        await TestAssert.ThrowsAsync<InvalidOperationException>(() => new SandboxCredentialReader(home.Components)
                                                                   .LoadOfflineAccount());
    }

    // ---- setup invoker payload channel ----

    [Test]
    public void SetupInvoker_UsesArgumentChannelForSmallPayloads()
    {
        using var home        = new SandboxTestHome();
        var       invoker     = new ProcessSandboxSetupInvoker(home.Components);
        var       smallBase64 = Convert.ToBase64String([1, 2, 3]);

        var startInfo = invoker.BuildStartInfo(smallBase64);
        Assert.That(startInfo.Arguments, Is.EqualTo(smallBase64));
        Assert.That(startInfo.UseShellExecute, Is.False); // refresh 绝不提权
    }

    [Test]
    public void SetupInvoker_SwitchesToChunkedEnvChannelForLargePayloads()
    {
        using var home        = new SandboxTestHome();
        var       invoker     = new ProcessSandboxSetupInvoker(home.Components);
        var       largeBase64 = new string('A', ProcessSandboxSetupInvoker.PayloadArgumentCharacterLimit + 5);

        var startInfo = invoker.BuildStartInfo(largeBase64);
        Assert.That(startInfo.Arguments, Is.EqualTo("--launch-payload-env"));
        Assert.That(startInfo.Environment["CODEX_SANDBOX_LAUNCH_BYTES"], Is.EqualTo(largeBase64.Length.ToString()));
        var chunkCount = int.Parse(startInfo.Environment["CODEX_SANDBOX_LAUNCH_COUNT"]!);
        var reassembled = string.Concat(Enumerable.Range(0, chunkCount)
                                                  .Select(index =>
                                                              startInfo.Environment[$"CODEX_SANDBOX_LAUNCH_{index}"]));
        Assert.That(reassembled, Is.EqualTo(largeBase64));
        TestAssert.All(Enumerable.Range(0, chunkCount),
                       index => Assert.That(startInfo.Environment[$"CODEX_SANDBOX_LAUNCH_{index}"]!.Length <= 16 * 1024,
                                            Is.True));
    }

    // ---- runner command-line quoting ----


    [TestCase("plain.exe", "plain.exe")]
    [TestCase("", "\"\"")]
    [TestCase("with space.exe", "\"with space.exe\"")]
    [TestCase("quote\"inside.exe", "\"quote\\\"inside.exe\"")]
    [TestCase(@"C:\path\endswith\", "C:\\path\\endswith\\")]
    [TestCase("with space\\", "\"with space\\\\\"")]
    [TestCase(@"C:\pre\""quote", "\"C:\\pre\\\\\\\"quote\"")]
    public void RunnerLauncher_QuoteArgumentFollowsCrtRules(string value, string expected)
    {
        Assert.That(WindowsSandboxRunnerLauncher.QuoteArgument(value), Is.EqualTo(expected));
    }

    [Test]
    public void RunnerLauncher_CommandLinePassesFullPipeNames()
    {
        var commandLine = WindowsSandboxRunnerLauncher.BuildRunnerCommandLine(@"C:\tools\codex-command-runner.exe",
                                                                              @"\\.\pipe\codex-runner-abcd-in",
                                                                              @"\\.\pipe\codex-runner-abcd-out");
        // CRT 规则对不含空白或引号的 token 不加引号。
        Assert.That(commandLine, Is.EqualTo(@"C:\tools\codex-command-runner.exe"           +
                                            " --pipe-in=\\\\.\\pipe\\codex-runner-abcd-in" +
                                            " --pipe-out=\\\\.\\pipe\\codex-runner-abcd-out"));
    }

    // ---- desktop name format ----

    [Test]
    public void DesktopName_Uses32LowercaseHexDigits()
    {
        var suffix = WindowsSandboxDesktopFactory.GenerateNameSuffix();
        Assert.That(suffix.Length, Is.EqualTo(32));
        TestAssert.All(suffix, character => Assert.That(character is >= '0' and <= '9' or >= 'a' and <= 'f', Is.True));
    }
}
