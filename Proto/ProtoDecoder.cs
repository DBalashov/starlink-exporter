using Google.Protobuf;
using Google.Protobuf.Reflection;
using FieldType = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;

namespace StarlinkExporter.Proto;

/// <summary>
/// A decoded message: field number -> values in wire order. Values are <see cref="double"/>
/// (all numeric and bool fields), <see cref="long"/> (enums), <see cref="string"/>,
/// <see cref="ByteString"/> or nested <see cref="DecodedMessage"/>.
/// </summary>
public sealed class DecodedMessage(MessageType type)
{
    public MessageType Type { get; } = type;
    public Dictionary<int, List<object>> Values { get; } = new();

    public List<object>? Get(int fieldNumber) => Values.GetValueOrDefault(fieldNumber);

    internal void Add(int fieldNumber, object value)
    {
        if (!Values.TryGetValue(fieldNumber, out var list))
            Values[fieldNumber] = list = new List<object>(1);
        list.Add(value);
    }
}

/// <summary>Schema-driven decoder of the protobuf wire format (C# protobuf has no DynamicMessage).</summary>
public sealed class ProtoDecoder(ProtoSchema schema)
{
    private const int MaxDepth = 64;

    public DecodedMessage Decode(MessageType type, ReadOnlySpan<byte> data) => Decode(type, data.ToArray(), 0);

    private DecodedMessage Decode(MessageType type, byte[] data, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("Message nesting is too deep");

        var result = new DecodedMessage(type);
        var input = new CodedInputStream(data);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var number = WireFormat.GetTagFieldNumber(tag);
            var wireType = WireFormat.GetTagWireType(tag);

            if (!type.FieldsByNumber.TryGetValue(number, out var field))
            {
                input.SkipLastField();
                continue;
            }

            switch (field.Type)
            {
                case FieldType.Message when wireType == WireFormat.WireType.LengthDelimited:
                    var nested = input.ReadBytes().ToByteArray();
                    if (schema.TryGetMessage(field.TypeName, out var nestedType))
                        result.Add(number, Decode(nestedType, nested, depth + 1));
                    break;

                case FieldType.String when wireType == WireFormat.WireType.LengthDelimited:
                    result.Add(number, input.ReadString());
                    break;

                case FieldType.Bytes when wireType == WireFormat.WireType.LengthDelimited:
                    result.Add(number, input.ReadBytes());
                    break;

                case FieldType.Message or FieldType.String or FieldType.Bytes or FieldType.Group:
                    input.SkipLastField();
                    break;

                // Packed repeated scalars.
                case var _ when wireType == WireFormat.WireType.LengthDelimited:
                    var packed = new CodedInputStream(input.ReadBytes().ToByteArray());
                    while (!packed.IsAtEnd)
                        result.Add(number, ReadScalar(packed, field.Type));
                    break;

                case var _ when wireType == ExpectedWireType(field.Type):
                    result.Add(number, ReadScalar(input, field.Type));
                    break;

                default:
                    input.SkipLastField();
                    break;
            }
        }

        return result;
    }

    private static WireFormat.WireType ExpectedWireType(FieldType type) => type switch
    {
        FieldType.Double or FieldType.Fixed64 or FieldType.Sfixed64 => WireFormat.WireType.Fixed64,
        FieldType.Float or FieldType.Fixed32 or FieldType.Sfixed32 => WireFormat.WireType.Fixed32,
        _ => WireFormat.WireType.Varint,
    };

    private static object ReadScalar(CodedInputStream input, FieldType type) => type switch
    {
        FieldType.Double => input.ReadDouble(),
        FieldType.Float => (double)input.ReadFloat(),
        FieldType.Int64 => (double)input.ReadInt64(),
        FieldType.Uint64 => (double)input.ReadUInt64(),
        FieldType.Int32 => (double)(int)input.ReadInt64(),
        FieldType.Uint32 => (double)input.ReadUInt32(),
        FieldType.Fixed64 => (double)input.ReadFixed64(),
        FieldType.Fixed32 => (double)input.ReadFixed32(),
        FieldType.Sfixed64 => (double)input.ReadSFixed64(),
        FieldType.Sfixed32 => (double)input.ReadSFixed32(),
        FieldType.Sint64 => (double)input.ReadSInt64(),
        FieldType.Sint32 => (double)input.ReadSInt32(),
        FieldType.Bool => input.ReadBool() ? 1.0 : 0.0,
        FieldType.Enum => (object)input.ReadInt64(), // keep long: without the cast the switch type is double
        _ => throw new InvalidDataException($"Unexpected scalar type {type}"),
    };

    /// <summary>Encodes a <c>Request</c> with a single empty sub-message, e.g. <c>{"get_status": {}}</c>.</summary>
    public static byte[] EncodeEmptySubRequest(FieldDescriptorProto field)
    {
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        output.WriteTag(field.Number, WireFormat.WireType.LengthDelimited);
        output.WriteLength(0);
        output.Flush();
        return ms.ToArray();
    }
}
