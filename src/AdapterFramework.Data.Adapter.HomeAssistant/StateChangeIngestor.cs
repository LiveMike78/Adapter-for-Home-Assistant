using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AdapterFramework.Data.Adapter.HomeAssistant
{
    /// <summary>
    /// Consumes live `state_changed` WebSocket events (prep notes §3.3) and emits point values for
    /// every currently-selected item on the affected entity. A single event carries the new state
    /// AND every current attribute, so one subscription covers both state and attribute points.
    /// </summary>
    public class StateChangeIngestor
    {
        private readonly Func<HomeAssistantPointValue, Task> _emit;
        private readonly ILogger _logger;

        /// <summary>Keyed by entity_id, so a lookup is O(1) per incoming event.</summary>
        private readonly Dictionary<string, List<HomeAssistantDataSelectionItem>> _selectedByEntity;

        public StateChangeIngestor(
            IReadOnlyCollection<HomeAssistantDataSelectionItem> selectedItems,
            Func<HomeAssistantPointValue, Task> emit,
            ILogger logger)
        {
            _emit = emit;
            _logger = logger;
            // Standard AVEVA DataSelection items always carry Selected; only act on the ones set true.
            _selectedByEntity = selectedItems
                .Where(i => i.Selected)
                .GroupBy(i => i.EntityId)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        public async Task HandleAsync(HomeAssistantWsEvent wsEvent, CancellationToken ct = default)
        {
            var newState = wsEvent.Data.NewState;
            if (newState is null) return; // entity removed
            if (!_selectedByEntity.TryGetValue(newState.EntityId, out var items)) return;

            var timestamp = newState.LastUpdated ?? wsEvent.TimeFired;

            foreach (var item in items)
            {
                HomeAssistantPointValue? value = item.IsStatePoint
                    ? HomeAssistantValueConverter.ConvertState(item.PointId, item.GetEffectiveStreamId(), item.EntityId, newState.State, timestamp, item.ValueType)
                    : TryGetAttributeValue(item, newState, timestamp);

                if (value is null) continue;
                if (value.IsUnavailable)
                {
                    _logger.LogDebug("Skipping unavailable value for {PointId}.", item.PointId);
                    continue;
                }

                await _emit(value).ConfigureAwait(false);
            }
        }

        private static HomeAssistantPointValue? TryGetAttributeValue(HomeAssistantDataSelectionItem item, HomeAssistantState state, DateTimeOffset timestamp)
        {
            if (item.AttributeName is null) return null;
            if (!state.Attributes.TryGetValue(item.AttributeName, out var element)) return null;

            return HomeAssistantValueConverter.TryConvertAttribute(item.PointId, item.GetEffectiveStreamId(), element, timestamp, out var value, out _)
                ? value
                : null;
        }
    }
}
