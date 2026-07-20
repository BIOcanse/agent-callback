using System.Globalization;

namespace AgentCallback.Cli;

internal sealed class ArgumentReader
{
    private readonly List<string> _positionals = [];
    private readonly Dictionary<string, List<string>> _options =
        new(StringComparer.OrdinalIgnoreCase);

    public ArgumentReader(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (!value.StartsWith("--", StringComparison.Ordinal))
            {
                _positionals.Add(value);
                continue;
            }

            var name = value[2..];
            if (name.Length == 0 || index + 1 >= values.Length ||
                values[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Option --{name} requires a value.");
            }

            if (!_options.TryGetValue(name, out var optionValues))
            {
                optionValues = [];
                _options.Add(name, optionValues);
            }

            optionValues.Add(values[++index]);
        }
    }

    public string Positional(int index, string name) =>
        index < _positionals.Count && !string.IsNullOrWhiteSpace(_positionals[index])
            ? _positionals[index]
            : throw new InvalidOperationException($"{name} is required.");

    public string? Optional(string name) =>
        _options.TryGetValue(name, out var values) ? values[^1] : null;

    public string Required(string name) =>
        Optional(name) ?? throw new InvalidOperationException($"--{name} is required.");

    public IReadOnlyList<string> All(string name) =>
        _options.TryGetValue(name, out var values) ? values : [];

    public int? OptionalInt32(string name)
    {
        var value = Optional(name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"--{name} must be an integer.");
    }

    public DateTimeOffset? OptionalDateTimeOffset(string name)
    {
        var value = Optional(name);
        if (value is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : throw new InvalidOperationException($"--{name} must be an ISO 8601 timestamp.");
    }
}
