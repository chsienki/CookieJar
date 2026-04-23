using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace CookieJar.Host;

/// <summary>
/// Bridges HTTP requests to the extension over the native-messaging channel.
/// Each outgoing request gets a unique id; responses are matched back to the
/// originating <see cref="TaskCompletionSource"/>.
/// </summary>
public sealed class CookieBroker
{
    private readonly NativeMessagingChannel _channel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private long _idCounter;

    public CookieBroker(NativeMessagingChannel channel)
    {
        _channel = channel;
    }

    public bool ExtensionConnected { get; private set; }

    public Task RunResponsePumpAsync(CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            await foreach (var message in _channel.Incoming.ReadAllAsync(cancellationToken))
            {
                var op = message["op"]?.GetValue<string>();
                if (op == "hello")
                {
                    ExtensionConnected = true;
                    continue;
                }

                if (op == "bye")
                {
                    ExtensionConnected = false;
                    continue;
                }

                var id = message["id"]?.GetValue<string>();
                if (id is not null && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(message);
                }
            }

            ExtensionConnected = false;
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetException(new InvalidOperationException(
                    "Extension disconnected before responding."));
            }
            _pending.Clear();
        }, cancellationToken);

    public async Task<JsonObject?> RequestAsync(
        JsonObject request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!ExtensionConnected)
        {
            return null;
        }

        var id = Interlocked.Increment(ref _idCounter).ToString();
        request["id"] = id;

        var tcs = new TaskCompletionSource<JsonObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await _channel.SendAsync(request, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var registration = cts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException(
                    "Extension did not respond within the allotted time.")));

            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }
}
