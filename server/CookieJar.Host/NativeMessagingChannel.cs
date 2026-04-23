using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CookieJar.Host;

/// <summary>
/// Reads and writes Chromium native-messaging frames (4-byte little-endian length
/// prefix + UTF-8 JSON payload) over a paired stdin/stdout stream pair.
/// </summary>
public sealed class NativeMessagingChannel : IDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<JsonObject> _incoming =
        Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

    public NativeMessagingChannel(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public ChannelReader<JsonObject> Incoming => _incoming.Reader;

    public Task RunReaderAsync(CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            try
            {
                var lengthBuffer = new byte[4];
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(_input, lengthBuffer, cancellationToken))
                    {
                        break;
                    }

                    var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);
                    if (length == 0 || length > MaxMessageBytes)
                    {
                        throw new InvalidDataException(
                            $"Native messaging frame length {length} out of range.");
                    }

                    var payload = new byte[length];
                    if (!await ReadExactAsync(_input, payload, cancellationToken))
                    {
                        break;
                    }

                    var node = JsonNode.Parse(payload);
                    if (node is JsonObject obj)
                    {
                        await _incoming.Writer.WriteAsync(obj, cancellationToken);
                    }
                }
            }
            finally
            {
                _incoming.Writer.TryComplete();
            }
        }, cancellationToken);

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var json = message.ToJsonString();
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException(
                $"Native messaging payload too large ({payload.Length} bytes).");
        }

        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBuffer, (uint)payload.Length);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(lengthBuffer, cancellationToken);
            await _output.WriteAsync(payload, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(
                buffer.AsMemory(read, buffer.Length - read), cancellationToken);
            if (n == 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }

    public void Dispose() => _writeLock.Dispose();
}
