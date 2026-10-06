using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellySTRMprobe.Service;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellySTRMprobe.EntryPoint;

/// <summary>
/// Background service that auto-probes new STRM items when they are added during library scans.
/// </summary>
public class CatchUpEntryPoint : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly IProbeService _probeService;
    private readonly ILogger<CatchUpEntryPoint> _logger;
    private readonly ConcurrentQueue<Guid> _pendingItemIds = new();

    private CancellationTokenSource? _stoppingCts;
    private Timer? _debounceTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatchUpEntryPoint"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="probeService">Instance of the <see cref="IProbeService"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{CatchUpEntryPoint}"/>.</param>
    public CatchUpEntryPoint(
        ILibraryManager libraryManager,
        IProbeService probeService,
        ILogger<CatchUpEntryPoint> logger)
    {
        _libraryManager = libraryManager;
        _probeService = probeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = new CancellationTokenSource();
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        _libraryManager.ItemAdded += OnItemAdded;

        _logger.LogInformation("STRM Probe catch-up mode initialized");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);

#pragma warning disable CA1849 // CancelAsync causes BadImageFormatException in Jellyfin's dispose pipeline
        _stoppingCts?.Cancel();
#pragma warning restore CA1849

        _logger.LogInformation("STRM Probe catch-up mode stopped");
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (!Plugin.Instance.Configuration.EnableCatchUpMode)
        {
            return;
        }

        var item = e.Item;

        if (item.Path == null || !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingItemIds.Enqueue(item.Id);
        _debounceTimer?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    // Timer callbacks must be void. The try-catch ensures exceptions from the async
    // processing do not crash the process — they are logged instead.
    private async void OnDebounceElapsed(object? state)
    {
        try
        {
            await ProcessQueueAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Catch-up queue processing was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing catch-up queue");
        }
    }

    internal async Task ProcessQueueAsync()
    {
        var queuedItemIds = new HashSet<Guid>();

        while (_pendingItemIds.TryDequeue(out var itemId))
        {
            queuedItemIds.Add(itemId);
        }

        if (queuedItemIds.Count == 0)
        {
            return;
        }

        var config = Plugin.Instance.Configuration;
        config.Validate();

        var token = _stoppingCts?.Token ?? CancellationToken.None;
        token.ThrowIfCancellationRequested();

        IEnumerable<Guid> itemIds = queuedItemIds;
        if (config.SelectedLibraryIds.Length > 0)
        {
            var query = new InternalItemsQuery
            {
                ItemIds = queuedItemIds.ToArray(),
                AncestorIds = config.SelectedLibraryIds,
            };

            itemIds = _libraryManager.GetItemIds(query);
        }

        // ItemAdded can fire before a scan finishes populating hierarchy. Resolve the
        // current repository object after the debounce instead of persisting that stale instance.
        var unprobed = new List<BaseItem>();
        foreach (var itemId in itemIds)
        {
            token.ThrowIfCancellationRequested();

            BaseItem? item;
            try
            {
                item = _libraryManager.GetItemById(itemId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Catch-up: skipping item {ItemId}: failed to resolve", itemId);
                continue;
            }

            if (item is not null && _probeService.IsUnprobed(item))
            {
                unprobed.Add(item);
            }
        }

        if (unprobed.Count == 0)
        {
            _logger.LogDebug("Catch-up: no queued STRM items need probing ({Count} queued)", queuedItemIds.Count);
            return;
        }

        _logger.LogInformation("Catch-up: probing {Count} new STRM items", unprobed.Count);

        var progress = new Progress<double>();

        var result = await _probeService.ProbeBatchAsync(
            unprobed,
            config.ProbeParallelism,
            config.ProbeTimeoutSeconds,
            config.ProbeCooldownMs,
            progress,
            token).ConfigureAwait(false);

        if (config.DeleteFailedStrms && result.FailedItems.Count > 0)
        {
            var totalProbed = result.Probed + result.Failed;
            var failurePercent = (double)result.Failed / totalProbed * 100;

            if (failurePercent > config.DeleteFailureThreshold)
            {
                _logger.LogWarning(
                    "Catch-up: failure rate {Rate:F1}% exceeds threshold {Threshold}% — skipping deletion of {Count} STRM files (provider may be down)",
                    failurePercent,
                    config.DeleteFailureThreshold,
                    result.FailedItems.Count);
            }
            else
            {
                var deleted = _probeService.DeleteStrmFiles(result.FailedItems);
                _logger.LogInformation("Catch-up: deleted {Deleted} failed STRM files ({Rate:F1}% failure rate)", deleted, failurePercent);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and optionally managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            _stoppingCts?.Cancel();
            _stoppingCts?.Dispose();
            _debounceTimer?.Dispose();
        }

        _disposed = true;
    }
}
