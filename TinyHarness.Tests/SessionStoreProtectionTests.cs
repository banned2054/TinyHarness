using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public sealed class SessionStoreProtectionTests
{
    private static ChatSession NewSession(string? id = null) => new(id ?? Guid.NewGuid().ToString("N"),
                                                                    Path.Combine(Path.GetTempPath(),
                                                                             "tinyharness-store-protection"),
                                                                    new SessionModelIdentity("model",
                                                                             "https://example.invalid/v1",
                                                                             ChatApiKind.ChatCompletions));

    [Test]
    public void ResolveProtected_RejectsWorkspaceChildAndRelativePathAndAcceptsExternalPath()
    {
        using var workspace = new TestTempDir();
        using var external  = new TestTempDir();
        var child = TestAssert.Throws<SessionStoreException>(() => SessionStorePaths.ResolveProtected(
                                                                  Path.Combine(workspace.Root, "sessions"),
                                                                  workspace.Root));
        var relative = TestAssert.Throws<SessionStoreException>(() => SessionStorePaths.ResolveProtected(
                                                                     "relative/sessions", workspace.Root));
        Assert.That(child.Message, Does.Contain("lies inside the workspace"));
        Assert.That(relative.Message, Does.Contain("absolute path"));
        Assert.That(SessionStorePaths.ResolveProtected(external.Root, workspace.Root),
                    Is.EqualTo(Path.GetFullPath(external.Root)));
    }

    [Test]
    public void ResolveProtected_RejectsJunctionIntoWorkspaceWhenAvailable()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows junction behavior is platform-specific.");
        using var workspace = new TestTempDir();
        using var external  = new TestTempDir();
        var       link      = Path.Combine(external.Root, "store-link");
        if (!TryCreateJunction(link, workspace.Root)) Assert.Ignore("The environment cannot create a junction.");
        var error =
            TestAssert.Throws<SessionStoreException>(() => SessionStorePaths.ResolveProtected(link, workspace.Root));
        Assert.That(error.Message, Does.Contain("resolves through links into the workspace"));
    }

    [Test]
    public void DefaultDirectory_IsAbsoluteAndOutsideWorkspace()
    {
        using var workspace = new TestTempDir();
        var       directory = SessionStorePaths.DefaultDirectory();
        Assert.That(Path.IsPathFullyQualified(directory), Is.True);
        Assert.That(Workspace.IsInside(workspace.Root, directory), Is.False);
    }

    [Test]
    public async Task Store_RejectsInvalidAndEscapingIdentifiersButCanonicalizesHexCase()
    {
        using var dir = new TestTempDir();
        Assert.Throws<ArgumentException>(() => new SessionStoreWriter(dir.Root, "../escape"));
        Assert.Throws<ArgumentException>(() => new SessionStoreWriter(dir.Root, "not-a-session-id"));
        var session = NewSession();
        using (var writer = new SessionStoreWriter(dir.Root, session.Id.ToUpperInvariant()))
        {
            await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        }

        Assert.That(Path.GetFileName(Directory.GetFiles(dir.Root, "*.session.jsonl").Single()),
                    Is.EqualTo(session.Id + ".session.jsonl"));
        Assert.That((await SessionStoreReader.ReadAsync(dir.Root, session.Id.ToUpperInvariant(),
                                                        CancellationToken.None))!.Id,
                    Is.EqualTo(session.Id));
    }

    [Test]
    public async Task Store_OnlyJournalReservesIdentityAndPreservesOldFiles()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       oldPath = Path.Combine(dir.Root, session.Id + ".session.json");
        await File.WriteAllTextAsync(oldPath, "unreadable old data");
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        }

        Assert.That((await SessionStoreReader.ReadAsync(dir.Root, session.Id, CancellationToken.None))!.Id,
                    Is.EqualTo(session.Id));
        Assert.That(await File.ReadAllTextAsync(oldPath), Is.EqualTo("unreadable old data"));
        Assert.Throws<SessionStoreException>(() => new SessionStoreWriter(dir.Root, session.Id));
    }

    [Test]
    public async Task LockAndLogLeafLinks_AreRejectedWhenLinkCreationIsAvailable()
    {
        using var dir        = new TestTempDir();
        using var outside    = new TestTempDir();
        var       session    = NewSession();
        var       lockTarget = Path.Combine(outside.Root, "outside.lock");
        File.WriteAllText(lockTarget, "target");
        if (!TryCreateFileLink(Path.Combine(dir.Root, session.Id + ".lock"), lockTarget))
            Assert.Ignore("The environment cannot create file symbolic links.");
        var lockError = TestAssert.Throws<SessionStoreException>(() => new SessionStoreWriter(dir.Root, session.Id));
        Assert.That(lockError.Message, Does.Contain("link or reparse point"));

        File.Delete(Path.Combine(dir.Root, session.Id + ".lock"));
        var logTarget = Path.Combine(outside.Root, "outside.session.jsonl");
        File.WriteAllText(logTarget, "not a journal");
        if (!TryCreateFileLink(Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix), logTarget))
            Assert.Ignore("The environment cannot create file symbolic links.");
        var logError = TestAssert.Throws<SessionStoreException>(() => new SessionStoreWriter(dir.Root, session.Id));
        Assert.That(logError.Message, Does.Contain("link or reparse point"));
        var readError = await Assert.ThrowsAsync<SessionStoreException>(() =>
                                                                            SessionStoreReader.ReadAsync(dir.Root,
                                                                                     session.Id,
                                                                                     CancellationToken.None));
        Assert.That(readError!.Message, Does.Contain("link or reparse point"));
        Assert.That(File.ReadAllText(logTarget), Is.EqualTo("not a journal"));
    }

    [Test]
    public async Task Store_SecondWriterFailsAndDisposeAllowsReopen()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       first   = new SessionStoreWriter(dir.Root, session.Id);
        await first.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        var error = Assert.Throws<SessionStoreException>(() => new SessionStoreWriter(dir.Root, session.Id));
        Assert.That(error!.Message, Does.Contain("already open"));
        first.Dispose();
        using var reopened =
            await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null, CancellationToken.None);
        Assert.That(reopened.CommitSequence, Is.EqualTo(1));
    }

    [Test]
    public async Task Reader_RejectsUnknownFormatDeepJsonOversizedLogAndMidLogDamage()
    {
        using var dir     = new TestTempDir();
        var       session = NewSession();
        var       path    = Path.Combine(dir.Root, session.Id + SessionStoreWriter.LogFileSuffix);
        using (var writer = new SessionStoreWriter(dir.Root, session.Id))
        {
            await writer.CommitAsync(session, new ConversationContext(), CancellationToken.None);
        }

        var line    = (await File.ReadAllLinesAsync(path))[0];
        var unknown = JsonNode.Parse(line)!.AsObject();
        unknown["format"] = "future/format";
        await File.WriteAllTextAsync(path, unknown.ToJsonString() + "\n");
        Assert.That((await Assert.ThrowsAsync<SessionStoreException>(() =>
                                                                         SessionStoreReader.ReadAsync(dir.Root,
                                                                                  session.Id, CancellationToken.None)))!
                   .Message, Does.Contain("unknown format"));
        await File.WriteAllTextAsync(path, line + "\n");

        var nested = new StringBuilder();
        for (var i = 0; i < SessionStoreReader.MaxJsonDepth + 4; i++) nested.Append("{\"x\":");
        nested.Append("null");
        for (var i = 0; i < SessionStoreReader.MaxJsonDepth + 4; i++) nested.Append('}');
        var auditPayload =
            JsonNode.Parse(JsonSerializer
                          .SerializeToElement(new AuditRecord { RunId = "run", Kind = "test" },
                                              PersistenceJsonContext.Default.AuditRecord).GetRawText())!.AsObject();
        auditPayload["nested"] = JsonNode.Parse(nested.ToString());
        using (var writer =
               await SessionStoreWriter.OpenExistingAsync(dir.Root, session.Id, null, CancellationToken.None))
        {
            await writer.AppendRecordsAsync([
                new SessionLogRecord("", "audit", JsonDocument.Parse(auditPayload.ToJsonString()).RootElement.Clone())
            ], CancellationToken.None);
        }

        Assert.That((await Assert.ThrowsAsync<SessionStoreException>(() =>
                                                                         SessionStoreReader.ReadAsync(dir.Root,
                                                                                  session.Id, CancellationToken.None)))!
                   .Message, Does.Contain("damaged batch"));

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(SessionStoreWriter.MaxLogBytes + 1);
        }

        Assert.That((await Assert.ThrowsAsync<SessionStoreException>(() =>
                                                                         SessionStoreReader.ReadAsync(dir.Root,
                                                                                  session.Id, CancellationToken.None)))!
                   .Message,
                    Does.Contain("exceeds"));

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            var firstLine = Encoding.UTF8.GetBytes(line + "\n{ broken\n");
            await stream.WriteAsync(firstLine);
        }

        Assert.That((await Assert.ThrowsAsync<SessionStoreException>(() =>
                                                                         SessionStoreReader.ReadAsync(dir.Root,
                                                                                  session.Id, CancellationToken.None)))!
                   .Message, Does.Contain("damaged batch"));
    }

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        });
        if (process is null) return false;
        process.WaitForExit(5_000);
        return process.ExitCode == 0;
    }
}
