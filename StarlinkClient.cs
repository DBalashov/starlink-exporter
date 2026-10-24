using Grpc.Core;
using Grpc.Net.Client;
using StarlinkExporter.Proto;

namespace StarlinkExporter;

public enum SchemaSource
{
    /// <summary>Server reflection; the embedded protos if the dish does not implement reflection.</summary>
    Auto,
    Reflection,
    Embedded,
}

public sealed class StarlinkOptions
{
    public string Address { get; set; } = "http://192.168.100.1:9200";
    public SchemaSource SchemaSource { get; set; } = SchemaSource.Auto;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Field names of <c>SpaceX.API.Device.Request</c> to call; empty means just get_status.</summary>
    public List<string> Requests { get; set; } = [];

    /// <summary>Repeated fields longer than this (e.g. history ring buffers) are not exported.</summary>
    public int MaxRepeatedElements { get; set; } = 32;
}

/// <summary>Calls <c>SpaceX.API.Device.Device/Handle</c> with raw bytes, using a schema from server reflection.</summary>
public sealed class StarlinkClient : IDisposable
{
    private const string ServiceName = "SpaceX.API.Device.Device";
    private const string MethodName = "Handle";

    private static readonly Marshaller<byte[]> RawMarshaller = Marshallers.Create(b => b, b => b);
    private static readonly Method<byte[], byte[]> HandleMethod =
        new(MethodType.Unary, ServiceName, MethodName, RawMarshaller, RawMarshaller);

    private readonly GrpcChannel _channel;
    private readonly CallInvoker _invoker;
    private readonly ILogger _logger;

    public StarlinkClient(string address, ILogger logger)
    {
        _logger = logger;
        _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 32 * 1024 * 1024,
        });
        _invoker = _channel.CreateCallInvoker();
    }

    public ProtoSchema? Schema { get; private set; }

    public async Task<ProtoSchema> EnsureSchemaAsync(SchemaSource source, TimeSpan timeout, CancellationToken ct)
    {
        if (Schema is not null)
            return Schema;

        if (source == SchemaSource.Embedded)
            return Schema = UseEmbedded();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return Schema = await ProtoSchema.LoadAsync(_invoker, ServiceName, MethodName, _logger, cts.Token);
        }
        // Only fall back when the dish answers but has no reflection service; if it is just
        // unreachable, keep retrying reflection so the schema matches the firmware once it is back.
        catch (RpcException ex) when (source == SchemaSource.Auto && ex.StatusCode == StatusCode.Unimplemented)
        {
            _logger.LogWarning("Dish does not support server reflection ({Status}), using embedded protos",
                ex.Status.Detail);
            return Schema = UseEmbedded();
        }
    }

    private ProtoSchema UseEmbedded()
    {
        _logger.LogInformation("Using embedded proto schema");
        return ProtoSchema.FromEmbedded(ServiceName, MethodName);
    }

    /// <summary>Sends <c>Request{ &lt;requestField&gt;: {} }</c> and returns the decoded <c>Response</c>.</summary>
    public async Task<DecodedMessage> HandleAsync(string requestField, TimeSpan timeout, CancellationToken ct)
    {
        var schema = Schema ?? throw new InvalidOperationException("Schema is not loaded");
        if (!schema.RequestType.FieldsByName.TryGetValue(requestField, out var field))
            throw new ArgumentException($"{schema.RequestType.FullName} has no field '{requestField}'", nameof(requestField));

        var request = ProtoDecoder.EncodeEmptySubRequest(field);
        var options = new CallOptions(deadline: DateTime.UtcNow + timeout, cancellationToken: ct);
        var response = await _invoker.AsyncUnaryCall(HandleMethod, null, options, request);
        return new ProtoDecoder(schema).Decode(schema.ResponseType, response);
    }

    public void Dispose() => _channel.Dispose();
}
