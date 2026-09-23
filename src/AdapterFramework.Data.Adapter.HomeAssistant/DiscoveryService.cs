using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AdapterFramework.Data.Adapter.HomeAssistant
{
    /// <summary>
    /// Result of a discovery run - see prep notes §4/§5.1. Field names here are our own; when wiring
    /// into the framework's actual Discoveries REST resource, map these onto whatever shape
    /// AdapterCommon expects (confirm exact schema against the restored package - prep notes §9.2).
    /// </summary>
    public class DiscoveryResult
    {
        public required string Query { get; init; }
        public required bool AutoSelect { get; init; }
        public required DateTimeOffset StartTime { get; init; }
        public required DateTimeOffset EndTime { get; init; }
        public required IReadOnlyList<HomeAssistantDataSelectionItem> Items { get; init; }
    }

    /// <summary>
    /// Full discovery via a single GET /api/states call (see prep notes §4) - no MQTT-style
    /// time-boxed passive watching needed, since HA already exposes a complete entity inventory.
    /// Query string convention matches sibling adapters (RDBMS/DNP3/MQTT): "Key=Value;Key2=Value2".
    /// Supported keys (all optional, config-level include/exclude rules always apply on top):
    ///   IncludeDomains=sensor,binary_sensor   (further restricts config's IncludeDomains)
    ///   IncludeEntities=sensor.kitchen_*      (further restricts config's IncludeEntities)
    /// </summary>
    public class DiscoveryService
    {
        private readonly HomeAssistantRestClient _restClient;
        private readonly DataSourceConfiguration _config;
        private readonly EntityFilter _filter;
        private readonly ILogger _logger;

        public DiscoveryService(HomeAssistantRestClient restClient, DataSourceConfiguration config, ILogger logger)
        {
            _restClient = restClient;
            _config = config;
            _filter = new EntityFilter(config);
            _logger = logger;
        }

        public async Task<DiscoveryResult> RunAsync(string query, bool autoSelect, CancellationToken ct = default)
        {
            var start = DateTimeOffset.UtcNow;
            var scopedDomains = ParseCsvOption(query, "IncludeDomains");
            var scopedEntityGlobs = ParseCsvOption(query, "IncludeEntities");

            var allStates = await _restClient.GetAllStatesAsync(ct).ConfigureAwait(false);
            var items = new List<HomeAssistantDataSelectionItem>();

            foreach (var state in allStates)
            {
                if (!_filter.IsEntityIncluded(state.EntityId)) continue;

                var domain = EntityFilter.GetDomain(state.EntityId);
                if (scopedDomains.Count > 0 && !scopedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase)) continue;
                if (scopedEntityGlobs.Count > 0 && !scopedEntityGlobs.Any(p => EntityFilter.GlobMatch(p, state.EntityId))) continue;

                var friendlyName = TryGetFriendlyName(state);

                // State point.
                var stateValueType = HomeAssistantValueConverter.InferStateValueType(state.EntityId, state.State);
                items.Add(new HomeAssistantDataSelectionItem
                {
                    PointId = HomeAssistantDataSelectionItem.BuildPointId(state.EntityId, null),
                    EntityId = state.EntityId,
                    AttributeName = null,
                    ValueType = stateValueType,
                    UnitOfMeasurement = TryGetUnitOfMeasurement(state),
                    Selected = autoSelect,
                    Name = friendlyName,
                    StreamId = null,
                    DataFilterId = null
                });

                // Attribute points - scalar-only by default (prep notes §5.2).
                foreach (var (attrName, attrValue) in state.Attributes)
                {
                    if (!_filter.IsAttributeIncluded(attrName)) continue;

                    if (!HomeAssistantValueConverter.TryConvertAttribute(
                            HomeAssistantDataSelectionItem.BuildPointId(state.EntityId, attrName),
                            attrValue, DateTimeOffset.UtcNow, out _, out var attrValueType))
                    {
                        continue; // non-scalar (array/object) - excluded from MVP point set
                    }

                    items.Add(new HomeAssistantDataSelectionItem
                    {
                        PointId = HomeAssistantDataSelectionItem.BuildPointId(state.EntityId, attrName),
                        EntityId = state.EntityId,
                        AttributeName = attrName,
                        ValueType = attrValueType,
                        UnitOfMeasurement = attrName == "unit_of_measurement" ? null : TryGetUnitOfMeasurement(state),
                        Selected = autoSelect,
                        Name = friendlyName is null ? attrName : $"{friendlyName} {attrName}",
                        StreamId = null,
                        DataFilterId = null
                    });
                }
            }

            _logger.LogInformation("Discovery found {ItemCount} selectable points across {EntityCount} entities.", items.Count, allStates.Count);

            return new DiscoveryResult
            {
                Query = query,
                AutoSelect = autoSelect,
                StartTime = start,
                EndTime = DateTimeOffset.UtcNow,
                Items = items
            };
        }

        /// <summary>Populates the DataSelection item's standard "name" field from HA's friendly_name attribute, when present.</summary>
        private static string? TryGetFriendlyName(HomeAssistantState state)
        {
            if (state.Attributes.TryGetValue("friendly_name", out var element) &&
                element.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return element.GetString();
            }
            return null;
        }

        private static string? TryGetUnitOfMeasurement(HomeAssistantState state)
        {
            if (state.Attributes.TryGetValue("unit_of_measurement", out var element) &&
                element.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return element.GetString();
            }
            return null;
        }

        private static List<string> ParseCsvOption(string query, string key)
        {
            foreach (var pair in query.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return kv[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }
            }
            return new List<string>();
        }
    }
}
