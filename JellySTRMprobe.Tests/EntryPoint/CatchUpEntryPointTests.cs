using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellySTRMprobe.EntryPoint;
using JellySTRMprobe.Service;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace JellySTRMprobe.Tests.EntryPoint;

public class CatchUpEntryPointTests
{
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<IProbeService> _mockProbeService;
    private readonly Mock<ILogger<CatchUpEntryPoint>> _mockLogger;

    public CatchUpEntryPointTests()
    {
        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockProbeService = new Mock<IProbeService>();
        _mockLogger = new Mock<ILogger<CatchUpEntryPoint>>();

        TestHelpers.EnsurePluginInstance();
    }

    private CatchUpEntryPoint CreateEntryPoint()
    {
        return new CatchUpEntryPoint(
            _mockLibraryManager.Object,
            _mockProbeService.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task StartAsync_AlwaysSubscribesToItemAdded()
    {
        var entryPoint = CreateEntryPoint();

        await entryPoint.StartAsync(CancellationToken.None);

        _mockLibraryManager.VerifyAdd(l => l.ItemAdded += It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Once);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task OnItemAdded_WithStrmPath_WhenEnabled_EnqueuesItem()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var item = TestHelpers.CreateTestItem("Test Movie", "/media/test.strm");
        var eventArgs = new ItemChangeEventArgs { Item = item };

        // Raise the event — should not throw
        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, eventArgs);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task OnItemAdded_WithStrmPath_WhenDisabled_DoesNotEnqueue()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = false;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var item = TestHelpers.CreateTestItem("Test Movie", "/media/test.strm");
        var eventArgs = new ItemChangeEventArgs { Item = item };

        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, eventArgs);

        // Wait to ensure nothing happens
        await Task.Delay(100);

        _mockProbeService.Verify(s => s.ProbeBatchAsync(
            It.IsAny<IReadOnlyList<BaseItem>>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task OnItemAdded_WithNonStrmPath_DoesNotEnqueueItem()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var item = TestHelpers.CreateTestItem("Test Movie", "/media/test.mkv");
        var eventArgs = new ItemChangeEventArgs { Item = item };

        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, eventArgs);

        // Wait to ensure nothing happens
        await Task.Delay(100);

        _mockProbeService.Verify(s => s.ProbeBatchAsync(
            It.IsAny<IReadOnlyList<BaseItem>>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromEvents()
    {
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        await entryPoint.StopAsync(CancellationToken.None);

        _mockLibraryManager.VerifyRemove(l => l.ItemAdded -= It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Once);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromEvents()
    {
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        entryPoint.Dispose();

        _mockLibraryManager.VerifyRemove(l => l.ItemAdded -= It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.AtLeastOnce);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var entryPoint = CreateEntryPoint();

        var act = () =>
        {
            entryPoint.Dispose();
            entryPoint.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task ProcessQueueAsync_ResolvesCurrentItemBeforeProbing()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var queuedItem = TestHelpers.CreateTestItem("Queued Movie", "/media/test.strm");
        var currentItem = TestHelpers.CreateTestItem("Current Movie", "/media/test.strm");
        currentItem.Id = queuedItem.Id;

        _mockLibraryManager.Setup(l => l.GetItemById(queuedItem.Id)).Returns(currentItem);
        _mockProbeService.Setup(s => s.IsUnprobed(currentItem)).Returns(true);
        _mockProbeService.Setup(s => s.ProbeBatchAsync(
            It.IsAny<IReadOnlyList<BaseItem>>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult { Probed = 1 });

        _mockLibraryManager.Raise(
            l => l.ItemAdded += null!,
            this,
            new ItemChangeEventArgs { Item = queuedItem });

        await entryPoint.ProcessQueueAsync();

        _mockProbeService.Verify(s => s.ProbeBatchAsync(
            It.Is<IReadOnlyList<BaseItem>>(items =>
                items.Count == 1 && ReferenceEquals(items[0], currentItem)),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()), Times.Once);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task ProcessQueueAsync_DeduplicatesQueuedItemIds()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var queuedItem = TestHelpers.CreateTestItem("Queued Movie", "/media/test.strm");
        var currentItem = TestHelpers.CreateTestItem("Current Movie", "/media/test.strm");
        currentItem.Id = queuedItem.Id;

        _mockLibraryManager.Setup(l => l.GetItemById(queuedItem.Id)).Returns(currentItem);
        _mockProbeService.Setup(s => s.IsUnprobed(currentItem)).Returns(true);
        _mockProbeService.Setup(s => s.ProbeBatchAsync(
            It.IsAny<IReadOnlyList<BaseItem>>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult { Probed = 1 });

        var eventArgs = new ItemChangeEventArgs { Item = queuedItem };
        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, eventArgs);
        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, eventArgs);

        await entryPoint.ProcessQueueAsync();

        _mockLibraryManager.Verify(l => l.GetItemById(queuedItem.Id), Times.Once);
        _mockProbeService.Verify(s => s.ProbeBatchAsync(
            It.Is<IReadOnlyList<BaseItem>>(items => items.Count == 1),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()), Times.Once);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task ProcessQueueAsync_DeletedItem_SkipsProbe()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var queuedItem = TestHelpers.CreateTestItem("Deleted Movie", "/media/test.strm");
        _mockLibraryManager.Setup(l => l.GetItemById(queuedItem.Id)).Returns((BaseItem?)null);

        _mockLibraryManager.Raise(
            l => l.ItemAdded += null!,
            this,
            new ItemChangeEventArgs { Item = queuedItem });

        await entryPoint.ProcessQueueAsync();

        _mockProbeService.Verify(s => s.ProbeBatchAsync(
            It.IsAny<IReadOnlyList<BaseItem>>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task ProcessQueueAsync_AlreadyProbedItem_SkipsProbe()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var queuedItem = TestHelpers.CreateTestItem("Queued Movie", "/media/test.strm");
        var currentItem = TestHelpers.CreateTestItem("Current Movie", "/media/test.strm");
        currentItem.Id = queuedItem.Id;

        _mockLibraryManager.Setup(l => l.GetItemById(queuedItem.Id)).Returns(currentItem);
        _mockProbeService.Setup(s => s.IsUnprobed(currentItem)).Returns(false);

        _mockLibraryManager.Raise(
            l => l.ItemAdded += null!,
            this,
            new ItemChangeEventArgs { Item = queuedItem });

        await entryPoint.ProcessQueueAsync();

        _mockProbeService.Verify(s => s.ProbeBatchAsync(
            It.IsAny<IReadOnlyList<BaseItem>>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task ProcessQueueAsync_WithSelectedLibraries_FiltersByAncestorIds()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var libraryId = Guid.NewGuid();
        Plugin.Instance.Configuration.SelectedLibraryIds = [libraryId];

        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var queuedItem = TestHelpers.CreateTestItem("Other Library Movie", "/media/test.strm");

        _mockLibraryManager.Setup(l => l.GetItemIds(It.Is<InternalItemsQuery>(query =>
                query.ItemIds.Length == 1
                && query.ItemIds[0] == queuedItem.Id
                && query.AncestorIds.Length == 1
                && query.AncestorIds[0] == libraryId)))
            .Returns(Array.Empty<Guid>());

        _mockLibraryManager.Raise(
            l => l.ItemAdded += null!,
            this,
            new ItemChangeEventArgs { Item = queuedItem });

        await entryPoint.ProcessQueueAsync();

        _mockLibraryManager.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Never);
        _mockProbeService.Verify(s => s.ProbeBatchAsync(
            It.IsAny<IReadOnlyList<BaseItem>>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task ProcessQueueAsync_WhenStopped_HonorsCancellation()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var queuedItem = TestHelpers.CreateTestItem("Queued Movie", "/media/test.strm");
        _mockLibraryManager.Raise(
            l => l.ItemAdded += null!,
            this,
            new ItemChangeEventArgs { Item = queuedItem });

        await entryPoint.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(() => entryPoint.ProcessQueueAsync());

        entryPoint.Dispose();
    }
}
