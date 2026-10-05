using ModernImageViewer.Application.Browsing;

namespace ModernImageViewer.Tests;

public sealed class BrowseSortingTests
{
    [Fact]
    public async Task NaturalDescendingPreservesDisplayedPathAndIndex()
    {
        string[] items = Paths("image10.png", "image2.png", "image1.png");
        ImageBrowseSession session = new((_, _, _) => items);
        session.Commit(items[1]);
        Assert.Equal([items[2], items[1], items[0]], session.Items);

        Assert.True(await session.ChangeSortAsync(BrowseSortMode.Name, true, TestContext.Current.CancellationToken));

        Assert.Equal([items[0], items[1], items[2]], session.Items);
        Assert.Equal(items[1], session.CurrentPath);
        Assert.Equal(1, session.CurrentIndex);
        Assert.True(session.SortDescending);
        Assert.False(session.IsSorting);
    }

    [Fact]
    public async Task AttributeSnapshotsAreReadOnceAndFailuresStayLastInBothDirections()
    {
        string[] items = Paths("image10.png", "image2.png", "unreadable.png", "deleted.png");
        Dictionary<string, int> reads = [];
        DateTime newer = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        ImageBrowseSession session = new((_, _, _) => items, path =>
        {
            reads[path] = reads.GetValueOrDefault(path) + 1;
            return Path.GetFileName(path) switch
            {
                "image10.png" => new(20, newer),
                "image2.png" => new(20, newer.AddDays(-1)),
                "unreadable.png" => throw new UnauthorizedAccessException(),
                _ => null
            };
        });
        session.Commit(items[0]);

        Assert.True(await session.ChangeSortAsync(BrowseSortMode.Size, true, TestContext.Current.CancellationToken));
        Assert.Equal([items[1], items[0], items[3], items[2]], session.Items);
        Assert.All(items, path => Assert.Equal(1, reads[path]));
        Assert.True(await session.ChangeSortAsync(BrowseSortMode.ModifiedTime, false, TestContext.Current.CancellationToken));
        Assert.Equal([items[1], items[0], items[3], items[2]], session.Items);
        Assert.True(await session.ChangeSortAsync(BrowseSortMode.ModifiedTime, true, TestContext.Current.CancellationToken));
        Assert.Equal([items[0], items[1], items[3], items[2]], session.Items);
        Assert.All(items, path => Assert.Equal(3, reads[path]));
    }

    [Fact]
    public async Task RefreshFolderPreparationAndBackgroundIndexRetainCommittedPolicy()
    {
        string[] items = Paths("image1.png", "image2.png", "image10.png");
        ImageBrowseSession session = new((_, _, _) => items);
        session.Commit(items[1]);
        Assert.True(await session.ChangeSortAsync(BrowseSortMode.Name, true, TestContext.Current.CancellationToken));
        await session.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal([items[2], items[1], items[0]], session.Items);
        Assert.Equal(items[1], session.CurrentPath);
        Assert.Equal(items[2], await session.FindFirstAsync(Path.GetDirectoryName(items[0])!, TestContext.Current.CancellationToken));
        Assert.True(session.TryCommitCached(items[2], true));
        Assert.Equal([items[2], items[1], items[0]], session.Items);

        long revision = session.BeginIndexing(items[1]);
        ImageBrowseSession.BrowseSnapshot snapshot = await session.PrepareSnapshotAsync(items[1], TestContext.Current.CancellationToken);
        Assert.True(session.CompleteIndexing(revision, snapshot, items[1]));
        Assert.Equal([items[2], items[1], items[0]], session.Items);
        Assert.True(session.SortDescending);
    }

    [Fact]
    public async Task LatestSortWinsWhenEarlierAttributeReadFinishesLate()
    {
        string[] items = Paths("image1.png", "image2.png", "image10.png");
        using ControlledProperties properties = new();
        ImageBrowseSession session = new((_, _, _) => items, properties.Read);
        session.Commit(items[0]);
        Task<bool> older = session.ChangeSortAsync(BrowseSortMode.Size, false, TestContext.Current.CancellationToken);
        await properties.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(session.IsSorting);
        Assert.True(session.CanSort);

        Assert.True(await session.ChangeSortAsync(BrowseSortMode.Name, true, TestContext.Current.CancellationToken));
        properties.Release();
        Assert.False(await older);
        Assert.Equal(BrowseSortMode.Name, session.SortMode);
        Assert.True(session.SortDescending);
        Assert.Equal([items[2], items[1], items[0]], session.Items);
        Assert.False(session.IsSorting);
    }

    [Fact]
    public async Task CancelledSortAndSelectionCommitCannotReplaceOldOrSelectedOrder()
    {
        string[] items = Paths("image1.png", "image2.png", "image10.png");
        using ControlledProperties properties = new();
        ImageBrowseSession session = new((_, _, _) => items, properties.Read);
        session.Commit(items[0]);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<bool> pending = session.ChangeSortAsync(BrowseSortMode.Size, true, cancellation.Token);
        await properties.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        properties.Release();
        Assert.False(await pending);
        Assert.Equal(items, session.Items);
        Assert.Equal(BrowseSortMode.Name, session.SortMode);
        Assert.False(session.SortDescending);
        Assert.False(session.IsSorting);

        using ControlledProperties lateProperties = new();
        ImageBrowseSession selected = new((_, _, _) => items, lateProperties.Read);
        selected.Commit(items[0]);
        Task<bool> late = selected.ChangeSortAsync(BrowseSortMode.ModifiedTime, false, TestContext.Current.CancellationToken);
        await lateProperties.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        selected.CommitSelection([items[2], items[0]], items[2]);
        lateProperties.Release();
        Assert.False(await late);
        Assert.Equal([items[2], items[0]], selected.Items);
        Assert.Equal(items[2], selected.CurrentPath);
        Assert.False(selected.CanSort);
        Assert.False(await selected.ChangeSortAsync(BrowseSortMode.Name, true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LateFolderPreparationCannotReplaceMoreRecentPreparedFolder()
    {
        string olderPath = Path.GetFullPath("older/image1.png");
        string newerPath = Path.GetFullPath("newer/image2.png");
        string olderDirectory = Path.GetDirectoryName(olderPath)!;
        string newerDirectory = Path.GetDirectoryName(newerPath)!;
        TaskCompletionSource olderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string[]> olderFiles = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ImageBrowseSession session = new((directory, _, _) =>
        {
            if (directory == olderDirectory)
            {
                olderStarted.TrySetResult();
                return olderFiles.Task.GetAwaiter().GetResult();
            }
            return [newerPath];
        });
        Task<string?> older = session.FindFirstAsync(olderDirectory, TestContext.Current.CancellationToken);
        try
        {
            await olderStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(newerPath, await session.FindFirstAsync(newerDirectory, TestContext.Current.CancellationToken));
        }
        finally
        {
            olderFiles.TrySetResult([olderPath]);
        }
        Assert.Null(await older);
        Assert.True(session.TryCommitCached(newerPath, true));
        Assert.Equal([newerPath], session.Items);
    }

    private static string[] Paths(params string[] names) => names.Select(name => Path.GetFullPath(Path.Combine("sorting", name))).ToArray();

    private sealed class ControlledProperties : IDisposable
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ImageBrowseSession.FileSortProperties? Read(string path)
        {
            Started.TrySetResult();
            // Deliberately ignore cancellation to model a filesystem call that returns late.
            _completion.Task.GetAwaiter().GetResult();
            return new(1, DateTime.UnixEpoch);
        }

        public void Release() => _completion.TrySetResult();
        public void Dispose() => Release();
    }
}
