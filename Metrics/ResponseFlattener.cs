using Google.Protobuf.Reflection;
using StarlinkExporter.Proto;
using FieldLabel = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;
using FieldType = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace StarlinkExporter.Metrics;

/// <summary>
/// Turns a decoded response message into gauges:
/// <list type="bullet">
/// <item>numeric/bool field  -> <c>{path}_{field}</c> (absent proto3 fields are exported as 0);</item>
/// <item>enum field          -> numeric gauge, its name also goes to the info metric;</item>
/// <item>string fields       -> labels of <c>{path}_info</c> with value 1;</item>
/// <item>nested message      -> recursion with <c>{path}_{field}</c> prefix;</item>
/// <item>repeated            -> one series per element with an <c>index</c> label (capped).</item>
/// </list>
/// Deprecated fields and bytes are skipped.
/// </summary>
public sealed class ResponseFlattener(ProtoSchema schema, int maxRepeatedElements)
{
    private const int MaxDepth = 16;

    public void Flatten(DecodedMessage message, string prefix, MetricSet metrics) =>
        Walk(message, prefix, [], metrics, 0);

    private void Walk(DecodedMessage message, string prefix, IReadOnlyList<Label> labels, MetricSet metrics, int depth)
    {
        if (depth > MaxDepth)
            return;

        var info = new List<Label>();
        foreach (var field in message.Type.Proto.Field)
        {
            if (field.Options?.Deprecated == true)
                continue;

            var fieldName = MetricSet.SanitizeName(field.Name);
            var name = prefix + "_" + fieldName;
            var help = $"{message.Type.FullName}.{field.Name}";
            var values = message.Get(field.Number);
            var repeated = field.Label == FieldLabel.Repeated;

            switch (field.Type)
            {
                case FieldType.Message:
                    if (values is null)
                        break;
                    if (!repeated)
                    {
                        Walk((DecodedMessage)values[^1], name, labels, metrics, depth + 1);
                        break;
                    }
                    for (var i = 0; i < values.Count && i < maxRepeatedElements; i++)
                        Walk((DecodedMessage)values[i], name, [.. labels, new Label(fieldName + "_index", i.ToString())],
                            metrics, depth + 1);
                    break;

                case FieldType.String:
                    if (values is not null)
                        info.Add(new Label(fieldName, string.Join(",", values.Cast<string>())));
                    break;

                case FieldType.Bytes or FieldType.Group:
                    break;

                case FieldType.Enum:
                    if (repeated)
                    {
                        if (values is not null)
                            info.Add(new Label(fieldName, string.Join(",", values.Select(v => EnumName(field, (long)v)))));
                        break;
                    }
                    if (values is null && field.HasOneofIndex)
                        break;
                    var enumValue = values is null ? 0L : (long)values[^1];
                    info.Add(new Label(fieldName, EnumName(field, enumValue)));
                    metrics.Add(name, help, labels, enumValue);
                    break;

                default:
                    if (repeated)
                    {
                        if (values is null || values.Count > maxRepeatedElements)
                            break;
                        for (var i = 0; i < values.Count; i++)
                            metrics.Add(name, help, [.. labels, new Label("index", i.ToString())], (double)values[i]);
                        break;
                    }
                    // Fields with explicit presence (oneof / proto3 optional) are only exported when set.
                    if (values is null && field.HasOneofIndex)
                        break;
                    metrics.Add(name, help, labels, values is null ? 0 : (double)values[^1]);
                    break;
            }
        }

        if (info.Count > 0)
            metrics.Add(prefix + "_info", $"String and enum fields of {message.Type.FullName}", [.. labels, .. info], 1);
    }

    private string EnumName(FieldDescriptorProto field, long value) =>
        schema.GetEnumName(field.TypeName, value) ?? value.ToString();
}
