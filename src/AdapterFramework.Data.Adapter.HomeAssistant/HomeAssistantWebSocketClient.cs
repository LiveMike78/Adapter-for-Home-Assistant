using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AdapterFramework.Data.Adapter.HomeAssistant
{
    /// <summary>
    /// Live event subscriber over HA's WebSocket API (see prep notes §3.3). Handles the
    /// auth_required/auth/auth_ok handshake, subscribes to `state_changed`, and raises
    /// <see cref="OnStateChanged"/> for each event. Reconnects with exponential backoff on
    /// any disconnect; callers should treat every (re)connect as a potential gap and trigger
    /// a history backfill (see <see cref="HistoryBackfillService"/>) for the outage window.
    /// </summary>
    public class HomeAssistantWebSocketClient : IAsyncDisposable
    {
        private readonly DataSourceConfiguration _config;
        private readonly ILogger _logger;
        private ClientWebSocket? _socket;
        private long _messageId = 1;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public event Func<HomeAssistantWsEvent, Task>? OnStateChanged;
        public event Func<Task>? OnConnected;
        public event Func<Task>? OnDisconnected;

        public HomeAssistantWebSocketClient(DataSourceConfiguration config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Runs the connect -> auth -> subscribe -> receive loop forever (until ct is cancelled),
        /// reconnecting with exponential backoff on any failure or disconnect.
        /// </summary>
        public async Task RunAsync(CancellationToken ct)
        {
            var backoffMs = _config.ReconnectBackoffInitialMs;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndSubscribeAsync(ct).ConfigureAwait(false);
                    backoffMs = _config.ReconnectBackoffInitialMs; // reset after a clean session
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Home Assistant WebSocket session ended unexpectedly; reconnecting in {BackoffMs}ms", backoffMs);
                }

                if (OnDisconnected is not null)
                {
                    await OnDisconnected.Invoke().ConfigureAwait(false);
                }

                if (ct.IsCancellationRequested) break;

                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                backoffMs = Math.Min(backoffMs * 2, _config.ReconnectBackoffMaxMs);
            }
        }

        private async Task ConnectAndSubscribeAsync(CancellationToken ct)
        {
            _socket = new ClientWebSocket();
            var uri = new Uri(_config.GetEffectiveWebSocketUrl());
            _logger.LogInformation("Connecting to Home Assistant WebSocket at {Uri}", uri);
            await _socket.ConnectAsync(uri, ct).ConfigureAwait(false);

            // 1. auth_required
            var first = await ReceiveEnvelopeAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Connection closed before auth_required was received.");
            if (first.Type != "auth_required")
            {
                throw new InvalidOperationException($"Expected auth_required, got '{first.Type}'.");
            }

            // 2. send auth
            await SendAsync(new { type = "auth", access_token = _config.AccessToken }, ct).ConfigureAwait(false);

            // 3. auth_ok / auth_invalid
            var authResult = await ReceiveEnvelopeAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Connection closed during authentication.");
            if (authResult.Type == "auth_invalid")
            {
                throw new UnauthorizedAccessException("Home Assistant rejected the access token (auth_invalid).");
            }
            if (authResult.Type != "auth_ok")
            {
                throw new InvalidOperationException($"Unexpected message during auth: '{authResult.Type}'.");
            }

            _logger.LogInformation("Authenticated with Home Assistant WebSocket API.");

            // 4. subscribe_events(state_changed)
            var subscribeId = Interlocked.Increment(ref _messageId);
            await SendAsync(new { id = subscribeId, type = "subscribe_events", event_type = "state_changed" }, ct).ConfigureAwait(false);

            var subAck = await ReceiveEnvelopeAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Connection closed while subscribing to state_changed.");
            if (subAck.Success != true)
            {
                throw new InvalidOperationException("Home Assistant rejected the subscribe_events request.");
            }

            if (OnConnected is not null)
            {
                await OnConnected.Invoke().ConfigureAwait(false);
            }

            // 5. receive loop
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var envelope = await ReceiveEnvelopeAsync(ct).ConfigureAwait(false);
                if (envelope is null) break; // socket closed

                if (envelope.Type == "event" && envelope.Event is JsonElement eventElement)
                {
                    var wsEvent = eventElement.Deserialize<HomeAssistantWsEvent>(JsonOptions);
                    if (wsEvent is not null && OnStateChanged is not null)
                    {
                        await OnStateChanged.Invoke(wsEvent).ConfigureAwait(false);
                    }
                }
                // "pong" and other message types are ignored here; add handling if heartbeat pings are added.
            }
        }

        private async Task SendAsync(object message, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket!.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }

        private async Task<HomeAssistantWsEnvelope?> ReceiveEnvelopeAsync(CancellationToken ct)
        {
            using var stream = new System.IO.MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket!.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            stream.Position = 0;
            return await JsonSerializer.DeserializeAsync<HomeAssistantWsEnvelope>(stream, JsonOptions, ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket is { State: WebSocketState.Open })
            {
                try
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // best-effort close
                }
            }
            _socket?.Dispose();
        }
    }
}
