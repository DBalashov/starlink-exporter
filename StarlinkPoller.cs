using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.Options;
using StarlinkExporter.Metrics;
using StarlinkExporter.Proto;
using FieldType = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace StarlinkExporter;

/// <summary>Latest rendered /metrics payload, swapped atomically after every poll.</summary>
public sealed class MetricsSnapshot
{
    private volatile string _text = "";

    public string Text => _text;

    public void Set(string text) => _text = text;
}

/// <summary>Polls the dish every <see cref="StarlinkOptions.PollInterval"/> and renders metrics.</summary>
public sealed class StarlinkPoller(
    IOptions<StarlinkOptions> options,
    MetricsSnapshot snapshot,
    ILogger<StarlinkPoller> logger) : BackgroundService
{
    private readonly StarlinkOptions _options = options.Value;
    private readonly HashSet<string> _reportedErrors = new();
    private string[] _requests = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _requests = _options.Requests.Count > 0 ? _options.Requests.Distinct().ToArray() : ["get_status"];
        logger.LogInformation("Polling {Address} every {Interval} for [{Requests}]",
            _options.Address, _options.PollInterval, string.Join(", ", _requests));

        using var client = new StarlinkClient(_options.Address, logger);
        using var timer = new PeriodicTimer(_options.PollInterval);
        do
        {
            try
            {
                snapshot.Set(await PollAsync(client, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Poll failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<string> PollAsync(StarlinkClient client, CancellationToken ct)
    {
        var metrics = new MetricSet();
        ProtoSchema schema;
        try
        {
            schema = await client.EnsureSchemaAsync(_options.SchemaSource, _options.RequestTimeout * 3, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogOnce("schema", ex, "Cannot load API schema from the dish via server reflection");
            foreach (var request in _requests)
                metrics.Add("starlink_up", "1 if the last request to the dish succeeded", [new("request", request)], 0);
            return metrics.Render();
        }

        var flattener = new ResponseFlattener(schema, _options.MaxRepeatedElements);
        var data = new List<string>();
        foreach (var request in _requests)
        {
            var labels = new[] { new Label("request", request) };
            var sw = Stopwatch.StartNew();
            var ok = false;
            try
            {
                var response = await client.HandleAsync(request, _options.RequestTimeout, ct);
                // Separate set per request so a failure halfway through exports nothing partial.
                var payload = new MetricSet();
                FlattenResponse(response, flattener, payload);
                data.Add(payload.Render());
                ok = true;
                _reportedErrors.Remove(request);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogOnce(request, ex, $"Request '{request}' failed");
            }

            metrics.Add("starlink_up", "1 if the last request to the dish succeeded", labels, ok ? 1 : 0);
            metrics.Add("starlink_request_duration_seconds", "Duration of the last request to the dish", labels,
                sw.Elapsed.TotalSeconds);
        }

        metrics.Add("starlink_last_poll_timestamp_seconds", "Unix time of the last poll", [],
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        return metrics.Render() + string.Concat(data);
    }

    /// <summary>
    /// <c>Response</c> carries the payload in a oneof (dish_get_status, get_device_info, ...);
    /// each payload is exported as <c>starlink_{field name without "get_"}_*</c>.
    /// </summary>
    private static void FlattenResponse(DecodedMessage response, ResponseFlattener flattener, MetricSet metrics)
    {
        foreach (var field in response.Type.Proto.Field)
        {
            if (field.Type != FieldType.Message || !field.HasOneofIndex)
                continue;
            if (response.Get(field.Number) is not [.., DecodedMessage payload])
                continue;

            var prefix = "starlink_" + MetricSet.SanitizeName(field.Name.Replace("get_", ""));
            flattener.Flatten(payload, prefix, metrics);
        }
    }

    private void LogOnce(string key, Exception ex, string message)
    {
        // Full exception once, then a short line each poll until the error clears.
        if (_reportedErrors.Add(key))
            logger.LogWarning(ex, "{Message}", message);
        else
            logger.LogDebug("{Message}: {Error}", message, ex is RpcException rpc ? rpc.Status.ToString() : ex.Message);
    }
}
