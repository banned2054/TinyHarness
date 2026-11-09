using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

[TestFixture]
public sealed class SessionStoreDeletionTests
{
    [Test]
    public async Task Delete_HoldsWriterLockAndRefusesOccupiedSession()
    {
        using var temp = new TestTempDir();
        var workspace = temp.CreateDirectory("workspace");
        var store = temp.CreateDirectory("sessions");
        var id = Guid.NewGuid().ToString("N");
        var logPath = Path.Combine(store, id + SessionStoreWriter.LogFileSuffix);

        using (var writer = new SessionStoreWriter(store, id))
        {
            await Assert.ThrowsAsync<SessionStoreException>(async () =>
                await SessionStoreDeletion.DeleteAsync(store, workspace, id, CancellationToken.None));
            Assert.That(File.Exists(logPath), Is.True, "writer 占用时不得删除日志。");
        }

        await SessionStoreDeletion.DeleteAsync(store, workspace, id, CancellationToken.None);
        Assert.That(File.Exists(logPath), Is.False);
    }

    [Test]
    public async Task Delete_RejectsStoreInsideWorkspaceWithoutChangingLog()
    {
        using var temp = new TestTempDir();
        var workspace = temp.CreateDirectory("workspace");
        var store = temp.CreateDirectory(Path.Combine("workspace", "sessions"));
        var id = Guid.NewGuid().ToString("N");
        var logPath = Path.Combine(store, id + SessionStoreWriter.LogFileSuffix);
        using (var writer = new SessionStoreWriter(store, id)) { }

        await Assert.ThrowsAsync<SessionStoreException>(async () =>
            await SessionStoreDeletion.DeleteAsync(store, workspace, id, CancellationToken.None));
        Assert.That(File.Exists(logPath), Is.True);
    }
}
