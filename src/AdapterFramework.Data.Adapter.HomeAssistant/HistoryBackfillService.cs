using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AdapterFramework.Data.Adapter.HomeAssistant
{
    /// <summary>
    /// Tracks "last successfully ingested timestamp" so backfill windows are the actual outage
    /// duration rather than always the max lookback (prep notes §5.5). MVP implementation persists
    /// to a local JSON file; swap for the framework's own durable config/state store if
    /// ConfigurationProvider/Registry exposes a suitable key-value API (prep notes §9.1).
    /// </summary>
    public interface ICheckpointStore
    {
        Task<DateTimeOffset?> GetLastIngestedTimestampAsync(CancellationToken ct = default);
        Task SaveLastIngestedTimestampAsync(DateTimeOffset timestamp, CancellationToken ct = default);
    }

    public class FileCheckpointStore : ICheckpointStore
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public FileCheckpointStore(string filePath)
        {
            _filePath = filePath;
        }

        public async Task<DateTimeOffset?> GetLastIngestedTimestampAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!System.IO.File.Exists(_filePath)) return null;
                var text = await System.IO.File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
                return DateTimeOffset.TryParse(text, out var ts) ? ts : null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveLastIngestedTimestampAsync(DateTimeOffset timestamp, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                await System.IO.File.WriteAllTextAsync(_filePath, timestamp.ToString("o"), ct).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    /// <summary>
    /// Backfills history for currently-selected entities on startup and after any WebSocket gap
    /// (prep notes §5.5/§6). Attribute history comes "for free" from the same /api/history/period
    /// call since HA returns full state+attributes per historical row.
    /// </summary>
    public class HistoryBackfillService
    {
        private readonly HomeAssistantRestClient _restClient;
        private readonly DataSourceConfiguration _config;
        private readonly ICheckpointStore _checkpointStore;
        private readonly Func<HomeAssistantPointValue, Task> _emit;
        private readonly ILogger _logger;

        public HistoryBackfillService(
            HomeAssistantRestClient restClient,
            DataSourceConfiguration config,
            ICheckpointStore checkpointStore,
            Func<HomeAssistantPointValue, Task> emit,
            ILogger logger)
        {
            _restClient = restClient;
            _config = config;
            _checkpointStore = checkpointStore;
            _emit = emit;
            _logger = logger;
        }

        /// <summary>
        /// Backfills [checkpoint or now-maxLookback, now) for the given selected entities, batched
        /// per <see cref="DataSourceConfiguration.HistoryBatchSize"/>, then advances the checkpoint.
        /// </summary>
        public async Task BackfillAsync(IReadOnlyCollection<HomeAssistantDataSelectionItem> selectedItems, CancellationToken ct = default)
        {
            // Standard AVEVA DataSelection items always carry Selected; only act on the ones set true.
            var activeItems = selectedItems.Where(i => i.Selected).ToList();
            if (!_config.HistoryBackfillEnabled || activeItems.Count == 0) return;

            var now = DateTimeOffset.UtcNow;
            var checkpoint = await _checkpointStore.GetLastIngestedTimestampAsync(ct).ConfigureAwait(false);
            var start = checkpoint ?? now.AddHours(-_config.HistoryBackfillMaxLookbackHours);

            if (start >= now)
            {
                _logger.LogInformation("Backfill skipped - checkpoint is already current.");
                return;
            }

            var entityIds = activeItems.Select(i => i.EntityId).Distinct().ToList();
            var itemsByEntity = activeItems.GroupBy(i => i.EntityId).ToDictionary(g => g.Key, g => g.ToList());

            _logger.LogInformation("Backfilling {EntityCount} entities from {Start} to {End}.", entityIds.Count, start, now);

            foreach (var batch in Chunk(entityIds, _config.HistoryBatchSize))
            {
                var history = await _restClient.GetHistoryAsync(start, now, batch, ct).ConfigureAwait(false);

                foreach (var entityHistory in history)
                {
                    foreach (var state in entityHistory)
                    {
                        if (!itemsByEntity.TryGetValue(state.EntityId, out var items)) continue;
                        var timestamp = state.LastUpdated ?? state.LastChanged ?? now;

                        foreach (var item in items)
                        {
                            var value = item.IsStatePoint
                                ? HomeAssistantValueConverter.ConvertState(item.PointId, item.GetEffectiveStreamId(), item.EntityId, state.State, timestamp, item.ValueType)
                                : TryGetAttributeValue(item, state, timestamp);

                            if (value is not null && !value.IsUnavailable)
                            {
                                await _emit(value).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }

            await _checkpointStore.SaveLastIngestedTimestampAsync(now, ct).ConfigureAwait(false);
        }

        private static HomeAssistantPointValue? TryGetAttributeValue(HomeAssistantDataSelectionItem item, HomeAssistantState state, DateTimeOffset timestamp)
        {
            if (item.AttributeName is null) return null;
            if (!state.Attributes.TryGetValue(item.AttributeName, out var element)) return null;

            return HomeAssistantValueConverter.TryConvertAttribute(item.PointId, item.GetEffectiveStreamId(), element, timestamp, out var value, out _)
                ? value
                : null;
        }

        private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
        {
            for (var i = 0; i < source.Count; i += size)
            {
                yield return source.Skip(i).Take(size).ToList();
            }
        }
    }
}
