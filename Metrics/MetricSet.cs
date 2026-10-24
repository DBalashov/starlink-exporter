using System.Globalization;
using System.Text;

namespace StarlinkExporter.Metrics;

public readonly record struct Label(string Name, string Value);

/// <summary>Collects gauge samples grouped by metric family and renders Prometheus text format 0.0.4.</summary>
public sealed class MetricSet
{
    private sealed class Family(string help)
    {
        public string Help { get; } = help;
        public List<(IReadOnlyList<Label> Labels, double Value)> Samples { get; } = new();
    }

    private readonly Dictionary<string, Family> _families = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public void Add(string name, string help, IReadOnlyList<Label> labels, double value)
    {
        if (!_families.TryGetValue(name, out var family))
        {
            _families[name] = family = new Family(help);
            _order.Add(name);
        }
        family.Samples.Add((labels, value));
    }

    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var name in _order)
        {
            var family = _families[name];
            sb.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(family.Help)).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (labels, value) in family.Samples)
            {
                var line = new StringBuilder(name);
                if (labels.Count > 0)
                {
                    line.Append('{');
                    for (var i = 0; i < labels.Count; i++)
                    {
                        if (i > 0) line.Append(',');
                        line.Append(labels[i].Name).Append("=\"").Append(EscapeLabel(labels[i].Value)).Append('"');
                    }
                    line.Append('}');
                }
                // Prometheus rejects duplicate series in one scrape.
                if (!seen.Add(line.ToString()))
                    continue;
                sb.Append(line).Append(' ').Append(FormatValue(value)).Append('\n');
            }
        }
        return sb.ToString();
    }

    public static string SanitizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? char.ToLowerInvariant(c) : '_');
        if (sb.Length == 0 || char.IsAsciiDigit(sb[0]))
            sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string FormatValue(double value) => value switch
    {
        double.PositiveInfinity => "+Inf",
        double.NegativeInfinity => "-Inf",
        double.NaN => "NaN",
        _ => value.ToString("R", CultureInfo.InvariantCulture),
    };

    private static string EscapeHelp(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n");

    private static string EscapeLabel(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
