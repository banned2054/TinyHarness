using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

public sealed class SessionStoreArchiveStoreTests
{
    [Test]
    public async Task SetArchived_NormalizesUppercaseSessionIdBeforePersisting()
    {
        using var temp    = new TestTempDir();
        var       upperId = "ABCDEF0123456789ABCDEF0123456789";
        var       lowerId = upperId.ToLowerInvariant();
        var archived = await SessionStoreArchiveStore.SetArchivedAsync(temp.Root, upperId, true,
                                                                       CancellationToken.None);
        var contents = await File.ReadAllTextAsync(SessionStoreArchiveStore.FilePath(temp.Root));

        Assert.That(archived.ArchivedSessionIds, Is.EqualTo(new[] { lowerId }));
        Assert.That(contents, Does.Contain(lowerId));
        Assert.That(contents, Does.Not.Contain(upperId));
    }

    [Test]
    public async Task Load_CanonicalizesUppercaseIdsAlreadySavedInArchiveState()
    {
        using var temp    = new TestTempDir();
        var       upperId = "ABCDEF0123456789ABCDEF0123456789";
        var       path    = SessionStoreArchiveStore.FilePath(temp.Root);
        await File.WriteAllTextAsync(
                                     path,
                                     "{\"schema\":\"tinyharness.session-store-archives/1\",\"archivedSessionIds\":[\"" +
                                     upperId                                                                           +
                                     "\"]}");

        var loaded = await SessionStoreArchiveStore.LoadAsync(temp.Root, CancellationToken.None);

        Assert.That(loaded.ArchivedSessionIds, Is.EqualTo(new[] { upperId.ToLowerInvariant() }));
    }

    [Test]
    public async Task SetArchived_RejectsInvalidSessionIdWithoutCreatingArchiveState()
    {
        using var temp = new TestTempDir();

        await Assert.ThrowsAsync<ArgumentException>(() =>
                                                        SessionStoreArchiveStore.SetArchivedAsync(
                                                         temp.Root, "not-a-session-id", true,
                                                         CancellationToken.None));

        Assert.That(File.Exists(SessionStoreArchiveStore.FilePath(temp.Root)), Is.False);
        Assert.That(File.Exists(SessionStoreArchiveStore.FilePath(temp.Root) + SessionStoreArchiveStore.SaveLockSuffix),
                    Is.False);
    }
}
