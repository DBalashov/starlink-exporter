using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Reflection.V1Alpha;

namespace StarlinkExporter.Proto;

/// <summary>Message type described by a <see cref="DescriptorProto"/> plus quick field lookups.</summary>
public sealed class MessageType
{
    public MessageType(string fullName, DescriptorProto proto)
    {
        FullName = fullName;
        Proto = proto;
        FieldsByNumber = proto.Field.ToDictionary(f => f.Number);
        FieldsByName = proto.Field.ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    public string FullName { get; }
    public DescriptorProto Proto { get; }
    public IReadOnlyDictionary<int, FieldDescriptorProto> FieldsByNumber { get; }
    public IReadOnlyDictionary<string, FieldDescriptorProto> FieldsByName { get; }
}

/// <summary>
/// Protobuf schema of the dish API, fetched at runtime through gRPC server reflection.
/// Normally fetched from the dish so field numbers and names match its firmware;
/// the compiled-in protos are only a fallback.
/// </summary>
public sealed class ProtoSchema
{
    private readonly Dictionary<string, MessageType> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<int, string>> _enums = new(StringComparer.Ordinal);

    private ProtoSchema(IEnumerable<FileDescriptorProto> files, string serviceName, string methodName)
    {
        ServiceDescriptorProto? service = null;
        foreach (var file in files)
        {
            var prefix = string.IsNullOrEmpty(file.Package) ? "" : file.Package + ".";
            foreach (var message in file.MessageType)
                RegisterMessage(prefix + message.Name, message);
            foreach (var e in file.EnumType)
                RegisterEnum(prefix + e.Name, e);
            service ??= file.Service.FirstOrDefault(s => prefix + s.Name == serviceName);
        }

        if (service is null)
            throw new InvalidOperationException($"Service {serviceName} not found in reflection data");
        var method = service.Method.FirstOrDefault(m => m.Name == methodName)
            ?? throw new InvalidOperationException($"Method {serviceName}/{methodName} not found");

        RequestType = GetMessage(method.InputType);
        ResponseType = GetMessage(method.OutputType);
    }

    public MessageType RequestType { get; }
    public MessageType ResponseType { get; }

    public MessageType GetMessage(string typeName) =>
        _messages.TryGetValue(typeName.TrimStart('.'), out var type)
            ? type
            : throw new KeyNotFoundException($"Message type {typeName} is unknown");

    public bool TryGetMessage(string typeName, out MessageType type) =>
        _messages.TryGetValue(typeName.TrimStart('.'), out type!);

    public string? GetEnumName(string typeName, long value) =>
        _enums.TryGetValue(typeName.TrimStart('.'), out var values) && values.TryGetValue((int)value, out var name)
            ? name
            : null;

    private void RegisterMessage(string fullName, DescriptorProto message)
    {
        _messages[fullName] = new MessageType(fullName, message);
        foreach (var nested in message.NestedType)
            RegisterMessage(fullName + "." + nested.Name, nested);
        foreach (var e in message.EnumType)
            RegisterEnum(fullName + "." + e.Name, e);
    }

    private void RegisterEnum(string fullName, EnumDescriptorProto e)
    {
        var values = new Dictionary<int, string>();
        foreach (var v in e.Value)
            values.TryAdd(v.Number, v.Name); // allow_alias: keep the first name
        _enums[fullName] = values;
    }

    /// <summary>Schema compiled in from Protos/*.proto; may lag behind the dish firmware.</summary>
    public static ProtoSchema FromEmbedded(string serviceName, string methodName)
    {
        var files = new Dictionary<string, FileDescriptorProto>(StringComparer.Ordinal);
        void Collect(FileDescriptor file)
        {
            if (files.TryAdd(file.Name, FileDescriptorProto.Parser.ParseFrom(file.SerializedData)))
                foreach (var dependency in file.Dependencies)
                    Collect(dependency);
        }

        Collect(SpaceX.API.Device.DeviceReflection.Descriptor);
        return new ProtoSchema(files.Values, serviceName, methodName);
    }

    /// <summary>Downloads the file containing <paramref name="serviceName"/> and all its dependencies.</summary>
    public static async Task<ProtoSchema> LoadAsync(
        CallInvoker invoker, string serviceName, string methodName, ILogger logger, CancellationToken ct)
    {
        var client = new ServerReflection.ServerReflectionClient(invoker);
        using var call = client.ServerReflectionInfo(cancellationToken: ct);

        async Task<IReadOnlyList<FileDescriptorProto>> AskAsync(ServerReflectionRequest request)
        {
            await call.RequestStream.WriteAsync(request, ct);
            if (!await call.ResponseStream.MoveNext(ct))
                throw new InvalidOperationException("Reflection stream closed unexpectedly");

            var response = call.ResponseStream.Current;
            if (response.MessageResponseCase == ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse)
            {
                logger.LogDebug("Reflection error for {Request}: {Message}", request, response.ErrorResponse.ErrorMessage);
                return [];
            }

            return response.FileDescriptorResponse.FileDescriptorProto
                .Select(FileDescriptorProto.Parser.ParseFrom)
                .ToList();
        }

        var files = new Dictionary<string, FileDescriptorProto>(StringComparer.Ordinal);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<FileDescriptorProto>(
            await AskAsync(new ServerReflectionRequest { FileContainingSymbol = serviceName }));
        if (queue.Count == 0)
            throw new InvalidOperationException($"Server reflection returned nothing for {serviceName}");

        while (queue.TryDequeue(out var file))
        {
            if (!files.TryAdd(file.Name, file))
                continue;
            foreach (var dependency in file.Dependency)
            {
                if (files.ContainsKey(dependency) || !requested.Add(dependency))
                    continue;
                foreach (var dep in await AskAsync(new ServerReflectionRequest { FileByFilename = dependency }))
                    queue.Enqueue(dep);
            }
        }

        await call.RequestStream.CompleteAsync();
        logger.LogInformation("Loaded {Count} proto files via server reflection", files.Count);
        return new ProtoSchema(files.Values, serviceName, methodName);
    }
}
