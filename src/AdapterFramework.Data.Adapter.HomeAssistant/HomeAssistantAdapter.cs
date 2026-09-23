using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AdapterFramework.Data.Adapter.HomeAssistant
{
    /// <summary>
    /// Composition root for the Home Assistant data source, independent of the framework's own
    /// hosting/composition model. AdapterFramework.Data.System.Host is expected to construct this
    /// (or an equivalent framework-base-class-derived type) from the loaded DataSourceConfiguration
    /// and DataSelection, and to route <see cref="EmitAsync"/> into the framework's ingestion API.
    ///
    /// IMPORTANT: This class intentionally does NOT inherit any AdapterCommon base class, because
    /// the exact base class/interface for a push/event-driven collector could not be confirmed from
    /// the (JS-rendered) documentation during the research pass behind this repo - see prep notes §9.1.
    /// Before first build: locate the correct base type/interface in AdapterFramework.Data.Framework.AdapterCommon
    /// (likely something like an `IDataCollector`/`AdapterBase` with a `StartAsync`/`Stop` lifecycle and
    /// a method to push values into the pipeline) and adapt this class to implement/derive from it -
    /// the internal logic (discovery, WS subscribe, backfill, filtering) should not need to change,
    /// only the outer shell that plugs into the framework's lifecycle and ingestion call.
    /// </summary>
    public class HomeAssistantAdapter : IAsyncDisposable
    {
        private readonly DataSourceConfiguration _config;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;

        private HomeAssistantRestClient? _restClient;
        private HomeAssistantWebSocketClient? _wsClient;
        private HistoryBackfillService? _backfillService;
        private StateChangeIngestor? _ingestor;
        private CancellationTokenSource? _runCts;
        private Task? _runTask;

        /// <summary>
        /// Called for every point value produced (live or backfilled). Wire this to the framework's
        /// ingestion/publish API (see prep notes §9.1) instead of the placeholder Console logging below.
        /// </summary>
        public Func<HomeAssistantPointValue, Task> OnPointValue { get; set; } = _ => Task.CompletedTask;

        public HomeAssistantAdapter(DataSourceConfiguration config, ILoggerFactory loggerFactory)
        {
            _config = config;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<HomeAssistantAdapter>();
        }

        /// <summary>Runs a discovery pass. See <see cref="DiscoveryService"/> and prep notes §4/§5.1.</summary>
        public async Task<DiscoveryResult> RunDiscoveryAsync(string query, bool autoSelect, CancellationToken ct = default)
        {
            using var restClient = new HomeAssistantRestClient(_config);
            var discovery = new DiscoveryService(restClient, _config, _loggerFactory.CreateLogger<DiscoveryService>());
            return await discovery.RunAsync(query, autoSelect, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts live collection for the given DataSelection items: runs an initial history backfill,
        /// then subscribes to live state_changed events, backfilling again after any reconnect gap.
        /// <paramref name="dataSelection"/> may include unselected candidate points (the standard
        /// AVEVA DataSelection resource holds every discovered item, selected or not) - only items
        /// with <see cref="HomeAssistantDataSelectionItem.Selected"/> == true are actually collected;
        /// filtering happens inside <see cref="HistoryBackfillService"/>/<see cref="StateChangeIngestor"/>.
        /// </summary>
        public Task StartAsync(IReadOnlyCollection<HomeAssistantDataSelectionItem> dataSelection, string checkpointFilePath, CancellationToken ct)
        {
            _restClient = new HomeAssistantRestClient(_config);
            _wsClient = new HomeAssistantWebSocketClient(_config, _loggerFactory.CreateLogger<HomeAssistantWebSocketClient>());
            var checkpointStore = new FileCheckpointStore(checkpointFilePath);
            _backfillService = new HistoryBackfillService(_restClient, _config, checkpointStore, OnPointValue, _loggerFactory.CreateLogger<HistoryBackfillService>());
            _ingestor = new StateChangeIngestor(dataSelection, OnPointValue, _loggerFactory.CreateLogger<StateChangeIngestor>());

            _wsClient.OnConnected += async () =>
            {
                _logger.LogInformation("WebSocket (re)connected - running gap/startup backfill.");
                await _backfillService.BackfillAsync(dataSelection, ct).ConfigureAwait(false);
            };

            _wsClient.OnStateChanged += wsEvent => _ingestor.HandleAsync(wsEvent, ct);

            _wsClient.OnDisconnected += () =>
            {
                _logger.LogWarning("WebSocket disconnected - will reconnect and backfill the gap.");
                return Task.CompletedTask;
            };

            _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runTask = _wsClient.RunAsync(_runCts.Token);
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            _runCts?.Cancel();
            if (_runTask is not null)
            {
                try { await _runTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            if (_wsClient is not null) await _wsClient.DisposeAsync().ConfigureAwait(false);
            _restClient?.Dispose();
            _runCts?.Dispose();
        }
    }
}
