using ModernImageViewer.UI.Controls;

namespace ModernImageViewer.UI.Tests;

public sealed class ThumbnailCacheTests
{
    [Fact]
    public void CacheEvictsLeastRecentlyUsedPixelsBeforeExceedingByteBudget()
    {
        ThumbnailCache<string> cache = new(64, 10);
        var first = new ThumbnailCacheKey("first.png", 1, 100);
        var second = new ThumbnailCacheKey("second.png", 1, 100);
        var third = new ThumbnailCacheKey("third.png", 1, 100);
        long generation = cache.Generation;

        Assert.True(cache.Store(first, "first", 4, generation));
        Assert.True(cache.Store(second, "second", 4, generation));
        Assert.True(cache.TryGet(first, out _));
        Assert.True(cache.Store(third, "third", 4, generation));

        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(first, out _));
        Assert.True(cache.TryGet(third, out _));
        Assert.False(cache.Store(second, "too large", 11, generation));
        Assert.True(cache.TryGet(first, out _));
    }

    [Fact]
    public void EntryLimitAndFileStampPreventStaleThumbnailHits()
    {
        ThumbnailCache<string> cache = new(1, 100);
        var original = new ThumbnailCacheKey("image.png", 1, 100);
        var modified = original with { LastWriteTimeUtcTicks = 2 };
        var resized = original with { FileLength = 101 };
        long generation = cache.Generation;

        Assert.True(cache.Store(original, "original", 4, generation));
        Assert.True(cache.TryGet(original with { Path = "IMAGE.PNG" }, out _));
        Assert.False(cache.TryGet(modified, out _));
        Assert.False(cache.TryGet(resized, out _));
        Assert.True(cache.Store(modified, "modified", 4, generation));
        Assert.False(cache.TryGet(original, out _));
        Assert.True(cache.TryGet(modified, out string? value));
        Assert.Equal("modified", value);
    }

    [Fact]
    public void ClearRejectsCompletionsFromEarlierGeneration()
    {
        ThumbnailCache<string> cache = new(64, 100);
        var key = new ThumbnailCacheKey("image.png", 1, 100);
        long oldGeneration = cache.Generation;
        Assert.True(cache.Store(key, "old", 4, oldGeneration));

        cache.Clear();

        Assert.False(cache.TryGet(key, out _));
        Assert.False(cache.Store(key, "late decode", 4, oldGeneration));
        Assert.True(cache.Store(key, "new", 4, cache.Generation));
        Assert.True(cache.TryGet(key, out string? value));
        Assert.Equal("new", value);
    }
}
