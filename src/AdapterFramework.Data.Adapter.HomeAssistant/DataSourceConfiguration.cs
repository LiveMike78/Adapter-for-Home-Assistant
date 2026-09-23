using System;
using System.Collections.Generic;
using System.Linq;

// NOTE: [Protected] is expected to come from AdapterFramework.Data.Framework.DataProtector (or
// .Abstractions) per the framework's convention for encrypting sensitive configuration values at rest.
// Confirm the exact attribute namespace against the restored package and update the using below.
// using AdapterFramework.Data.Framework.DataProtector;

namespace AdapterFramework.Data.Adapter.HomeAssistant
{
    /// <summary>
    /// Connection and behavior configuration for the Home Assistant data source.
    /// Mirrors the shape/roles described in the sample WeatherGovAdapter's DataSourceConfiguration.cs:
    /// implements Validate() and Equals(), and marks secrets with [Protected] so the framework's
    /// DataProtector encrypts them at rest.
    /// </summary>
    public class DataSourceConfiguration
    {
        /// <summary>REST base URL of the Home Assistant instance, e.g. http://homeassistant.local:8123</summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// WebSocket URL, e.g. ws://homeassistant.local:8123/api/websocket. If not set, it is derived
        /// from BaseUrl (http->ws, https->wss, path suffixed with /api/websocket).
        /// </summary>
        public string? WebSocketUrl { get; set; }

        /// <summary>Home Assistant Long-Lived Access Token. Sensitive - encrypted at rest.</summary>
        // [Protected]
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>If false, BaseUrl/WebSocketUrl must use https/wss. Default false (secure by default).</summary>
        public bool AllowInsecureBaseUrl { get; set; } = false;

        public int RequestTimeoutMs { get; set; } = 10_000;

        public int MaxRetries { get; set; } = 3;

        public int ReconnectBackoffInitialMs { get; set; } = 1_000;

        public int ReconnectBackoffMaxMs { get; set; } = 60_000;

        public bool HistoryBackfillEnabled { get; set; } = true;

        /// <summary>Safety cap used only when no prior checkpoint exists (first run).</summary>
        public int HistoryBackfillMaxLookbackHours { get; set; } = 24;

        /// <summary>Max entity IDs per /api/history/period call.</summary>
        public int HistoryBatchSize { get; set; } = 100;

        public List<string> IncludeDomains { get; set; } = new()
        {
            "sensor", "binary_sensor", "climate", "light", "switch", "cover",
            "fan", "media_player", "lock", "water_heater", "weather",
            "person", "device_tracker"
        };

        public List<string> ExcludeDomains { get; set; } = new()
        {
            "automation", "script", "update", "zone", "persistent_notification"
        };

        /// <summary>Explicit allow-list of entity_id glob patterns (e.g. "sensor.kitchen_*"). Empty = no extra restriction.</summary>
        public List<string> IncludeEntities { get; set; } = new();

        /// <summary>Explicit deny-list of entity_id glob patterns. Applied after IncludeEntities.</summary>
        public List<string> ExcludeEntities { get; set; } = new();

        public List<string> ExcludeAttributes { get; set; } = new()
        {
            "friendly_name", "icon", "entity_picture", "supported_features",
            "attribution", "context", "assumed_state"
        };

        /// <summary>MVP default off - see prep notes §5.2 for rationale.</summary>
        public bool FlattenSimpleArrayAttributes { get; set; } = false;

        /// <summary>
        /// Validates the configuration. Returns a list of human-readable errors; empty list = valid.
        /// Framework convention (per sample) is a Validate() method the host calls before accepting
        /// a configuration change - confirm exact signature/return type against AdapterCommon.
        /// </summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                errors.Add($"{nameof(BaseUrl)} is required.");
            }
            else if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri))
            {
                errors.Add($"{nameof(BaseUrl)} is not a valid absolute URL.");
            }
            else if (!AllowInsecureBaseUrl && baseUri.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add($"{nameof(BaseUrl)} must use https unless {nameof(AllowInsecureBaseUrl)} is true.");
            }

            if (string.IsNullOrWhiteSpace(AccessToken))
            {
                errors.Add($"{nameof(AccessToken)} is required.");
            }

            if (RequestTimeoutMs <= 0) errors.Add($"{nameof(RequestTimeoutMs)} must be positive.");
            if (MaxRetries < 0) errors.Add($"{nameof(MaxRetries)} cannot be negative.");
            if (ReconnectBackoffInitialMs <= 0) errors.Add($"{nameof(ReconnectBackoffInitialMs)} must be positive.");
            if (ReconnectBackoffMaxMs < ReconnectBackoffInitialMs)
                errors.Add($"{nameof(ReconnectBackoffMaxMs)} must be >= {nameof(ReconnectBackoffInitialMs)}.");
            if (HistoryBackfillMaxLookbackHours <= 0) errors.Add($"{nameof(HistoryBackfillMaxLookbackHours)} must be positive.");
            if (HistoryBatchSize <= 0) errors.Add($"{nameof(HistoryBatchSize)} must be positive.");

            return errors;
        }

        /// <summary>Resolves the effective WebSocket URL, deriving it from BaseUrl if not explicitly set.</summary>
        public string GetEffectiveWebSocketUrl()
        {
            if (!string.IsNullOrWhiteSpace(WebSocketUrl))
            {
                return WebSocketUrl;
            }

            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException($"Cannot derive WebSocket URL: {nameof(BaseUrl)} is not a valid URL.");
            }

            var scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
            var builder = new UriBuilder(baseUri)
            {
                Scheme = scheme,
                Port = baseUri.IsDefaultPort ? -1 : baseUri.Port,
                Path = "/api/websocket"
            };
            return builder.Uri.ToString();
        }

        public override bool Equals(object? obj)
        {
            if (obj is not DataSourceConfiguration other) return false;

            return BaseUrl == other.BaseUrl
                && WebSocketUrl == other.WebSocketUrl
                && AccessToken == other.AccessToken
                && AllowInsecureBaseUrl == other.AllowInsecureBaseUrl
                && RequestTimeoutMs == other.RequestTimeoutMs
                && MaxRetries == other.MaxRetries
                && ReconnectBackoffInitialMs == other.ReconnectBackoffInitialMs
                && ReconnectBackoffMaxMs == other.ReconnectBackoffMaxMs
                && HistoryBackfillEnabled == other.HistoryBackfillEnabled
                && HistoryBackfillMaxLookbackHours == other.HistoryBackfillMaxLookbackHours
                && HistoryBatchSize == other.HistoryBatchSize
                && FlattenSimpleArrayAttributes == other.FlattenSimpleArrayAttributes
                && IncludeDomains.SequenceEqual(other.IncludeDomains)
                && ExcludeDomains.SequenceEqual(other.ExcludeDomains)
                && IncludeEntities.SequenceEqual(other.IncludeEntities)
                && ExcludeEntities.SequenceEqual(other.ExcludeEntities)
                && ExcludeAttributes.SequenceEqual(other.ExcludeAttributes);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(BaseUrl);
            hash.Add(WebSocketUrl);
            hash.Add(AccessToken);
            hash.Add(AllowInsecureBaseUrl);
            hash.Add(RequestTimeoutMs);
            hash.Add(MaxRetries);
            hash.Add(HistoryBackfillEnabled);
            hash.Add(HistoryBatchSize);
            return hash.ToHashCode();
        }
    }
}
