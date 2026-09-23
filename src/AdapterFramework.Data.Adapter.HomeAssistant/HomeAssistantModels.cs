using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdapterFramework.Data.Adapter.HomeAssistant
{
    /// <summary>Shape of one entry from GET /api/states, and of each "new_state"/"old_state" in a state_changed event.</summary>
    public class HomeAssistantState
    {
        [JsonPropertyName("entity_id")]
        public string EntityId { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("attributes")]
        public Dictionary<string, JsonElement> Attributes { get; set; } = new();

        [JsonPropertyName("last_changed")]
        public DateTimeOffset? LastChanged { get; set; }

        [JsonPropertyName("last_updated")]
        public DateTimeOffset? LastUpdated { get; set; }
    }

    public class HomeAssistantStateChangedEventData
    {
        [JsonPropertyName("entity_id")]
        public string EntityId { get; set; } = string.Empty;

        [JsonPropertyName("old_state")]
        public HomeAssistantState? OldState { get; set; }

        [JsonPropertyName("new_state")]
        public HomeAssistantState? NewState { get; set; }
    }

    public class HomeAssistantWsEnvelope
    {
        [JsonPropertyName("id")]
        public long? Id { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool? Success { get; set; }

        [JsonPropertyName("event")]
        public JsonElement? Event { get; set; }
    }

    public class HomeAssistantWsEvent
    {
        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = string.Empty;

        [JsonPropertyName("time_fired")]
        public DateTimeOffset TimeFired { get; set; }

        [JsonPropertyName("data")]
        public HomeAssistantStateChangedEventData Data { get; set; } = new();
    }

    /// <summary>
    /// A single selectable point: either an entity's `state`, or one of its attributes.
    /// Point identity string convention (see prep notes §5.1): "<entity_id>" for state,
    /// "<entity_id>.<attribute_name>" for an attribute.
    ///
    /// Every field below is present on every DataSelection item written by this adapter, matching
    /// the standard AVEVA adapter DataSelection schema (confirmed against the framework's own
    /// convention - these are not optional, they are always present and simply carry defaults):
    ///   id            - stable point identifier (this adapter's PointId)
    ///   selected      - whether this candidate point is actually collected; discovery writes
    ///                   every candidate with Selected = the discovery's autoSelect flag, and a
    ///                   user can flip individual items afterwards without re-running discovery
    ///   name          - display name; defaults to null (framework/UI falls back to id), but this
    ///                   adapter populates it from HA's friendly_name attribute when available
    ///   streamId      - target OMF stream id; defaults to null, meaning "derive from id" (see
    ///                   BuildDefaultStreamId) - only set explicitly to override the default
    ///   dataFilterId  - id of a configured data filter/transform to apply; defaults to null
    ///                   (no filter/transform applied)
    /// </summary>
    public class HomeAssistantDataSelectionItem
    {
        public string PointId { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;

        /// <summary>Null for the entity's state point; the HA attribute key for an attribute point.</summary>
        public string? AttributeName { get; set; }

        public HomeAssistantValueType ValueType { get; set; } = HomeAssistantValueType.String;

        /// <summary>From HA's unit_of_measurement attribute, when present on the source entity.</summary>
        public string? UnitOfMeasurement { get; set; }

        /// <summary>
        /// Standard AVEVA DataSelection field. Whether this point is actively collected. Discovery
        /// sets this from the discovery request's autoSelect flag; not a true optional - always
        /// present, default false so a raw discovery result collects nothing until reviewed/applied.
        /// </summary>
        public bool Selected { get; set; } = false;

        /// <summary>
        /// Standard AVEVA DataSelection field. Display name for the point. Default null (consumers
        /// fall back to PointId); this adapter fills it from HA's friendly_name attribute when the
        /// source entity has one, so discovery output is human-readable out of the box.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Standard AVEVA DataSelection field. Target OMF stream id. Default null, meaning the
        /// adapter derives one from PointId at collection time (see BuildDefaultStreamId) - set
        /// explicitly only to override that default (e.g. to match a pre-existing PI tag/AF attribute name).
        /// </summary>
        public string? StreamId { get; set; }

        /// <summary>
        /// Standard AVEVA DataSelection field. Id of a configured data filter (scaling, deadband,
        /// unit conversion, etc.) to apply to this point's values. Default null - no filter applied.
        /// </summary>
        public string? DataFilterId { get; set; }

        public bool IsStatePoint => AttributeName is null;

        public static string BuildPointId(string entityId, string? attributeName) =>
            attributeName is null ? entityId : $"{entityId}.{attributeName}";

        /// <summary>Effective OMF stream id: StreamId if explicitly set, otherwise PointId itself.</summary>
        public string GetEffectiveStreamId() => string.IsNullOrWhiteSpace(StreamId) ? PointId : StreamId;
    }

    public enum HomeAssistantValueType
    {
        String,
        Double,
        Boolean
    }

    /// <summary>A single timestamped value ready to hand to the framework's ingestion pipeline.</summary>
    public class HomeAssistantPointValue
    {
        public required string PointId { get; init; }

        /// <summary>The OMF stream id to publish under - see HomeAssistantDataSelectionItem.GetEffectiveStreamId().</summary>
        public required string StreamId { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public string? StringValue { get; init; }
        public double? DoubleValue { get; init; }
        public bool? BooleanValue { get; init; }
        public HomeAssistantValueType ValueType { get; init; }

        /// <summary>True if the source state was unknown/unavailable - callers should typically skip emitting these.</summary>
        public bool IsUnavailable { get; init; }
    }
}
