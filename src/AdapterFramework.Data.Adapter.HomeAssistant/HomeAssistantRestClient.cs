using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AdapterFramework.Data.Adapter.HomeAssistant
{
    /// <summary>
    /// Thin wrapper over Home Assistant's REST API (see prep notes §3.2):
    ///   GET /api/                       - health check
    ///   GET /api/config                 - instance info
    ///   GET /api/states                 - full entity snapshot (backbone of discovery)
    ///   GET /api/history/period/...     - historical state changes (backfill)
    /// </summary>
    public class HomeAssistantRestClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly DataSourceConfiguration _config;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public HomeAssistantRestClient(DataSourceConfiguration config, HttpMessageHandler? handler = null)
        {
            _config = config;
            _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
            _httpClient.BaseAddress = new Uri(_config.BaseUrl.TrimEnd('/') + "/");
            _httpClient.Timeout = TimeSpan.FromMilliseconds(_config.RequestTimeoutMs);
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _config.AccessToken);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
        {
            using var response = await _httpClient.GetAsync("api/", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        /// <summary>Full snapshot of every entity's current state + attributes. Backbone of discovery (§4).</summary>
        public async Task<List<HomeAssistantState>> GetAllStatesAsync(CancellationToken ct = default)
        {
            using var response = await _httpClient.GetAsync("api/states", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var states = await JsonSerializer.DeserializeAsync<List<HomeAssistantState>>(stream, JsonOptions, ct).ConfigureAwait(false);
            return states ?? new List<HomeAssistantState>();
        }

        public async Task<HomeAssistantState?> GetStateAsync(string entityId, CancellationToken ct = default)
        {
            using var response = await _httpClient.GetAsync($"api/states/{Uri.EscapeDataString(entityId)}", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<HomeAssistantState>(stream, JsonOptions, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Historical state changes for backfill (§6). HA returns an array-of-arrays: one inner array
        /// per requested entity_id, in the same order they were requested (per HA docs) - callers
        /// should not assume this ordering is guaranteed across all HA versions and should key results
        /// off each returned state's own entity_id.
        /// </summary>
        public async Task<List<List<HomeAssistantState>>> GetHistoryAsync(
            DateTimeOffset start,
            DateTimeOffset end,
            IReadOnlyCollection<string> entityIds,
            CancellationToken ct = default)
        {
            if (entityIds.Count == 0) return new List<List<HomeAssistantState>>();

            var startIso = Uri.EscapeDataString(start.UtcDateTime.ToString("o"));
            var endIso = Uri.EscapeDataString(end.UtcDateTime.ToString("o"));
            var filterEntityIds = Uri.EscapeDataString(string.Join(",", entityIds));

            var url = $"api/history/period/{startIso}?end_time={endIso}&filter_entity_id={filterEntityIds}&minimal_response=false&significant_changes_only=false";

            using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<List<List<HomeAssistantState>>>(stream, JsonOptions, ct).ConfigureAwait(false);
            return result ?? new List<List<HomeAssistantState>>();
        }

        public void Dispose() => _httpClient.Dispose();
    }
}
