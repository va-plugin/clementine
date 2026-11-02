namespace Clementine.Engines.Files.Data;

/// <summary>
/// Format-neutral data tree shared by the JSON, YAML, CSV and XML converters. Objects keep key order (and, when the
/// source had them, duplicate keys); numbers keep their exact source text so nothing is rounded on the way through.
/// </summary>
internal abstract class DataNode;

internal sealed class DataObject : DataNode
{
    public List<KeyValuePair<string, DataNode>> Properties { get; } = [];

    public void Add(string key, DataNode value) => Properties.Add(new(key, value));

    /// <summary>Replaces the value of an existing key in place (keeping its position), or appends it.</summary>
    public void Set(string key, DataNode value)
    {
        var index = Properties.FindIndex(p => p.Key == key);
        if (index >= 0)
            Properties[index] = new(key, value);
        else
            Properties.Add(new(key, value));
    }

    public DataNode? Get(string key) => Properties.FirstOrDefault(p => p.Key == key).Value;

    public bool ContainsKey(string key) => Properties.Exists(p => p.Key == key);
}

internal sealed class DataArray : DataNode
{
    public DataArray()
    {
    }

    public DataArray(IEnumerable<DataNode> items) => Items.AddRange(items);

    public List<DataNode> Items { get; } = [];
}

internal sealed class DataString(string value) : DataNode
{
    public string Value { get; } = value;
}

/// <summary>A number as valid JSON number text ("42", "-1.5e+3").</summary>
internal sealed class DataNumber(string raw) : DataNode
{
    public string Raw { get; } = raw;
}

internal sealed class DataBool(bool value) : DataNode
{
    public static readonly DataBool True = new(true);
    public static readonly DataBool False = new(false);

    public bool Value { get; } = value;
}

internal sealed class DataNull : DataNode
{
    public static readonly DataNull Instance = new();

    DataNull()
    {
    }
}

/// <summary>Limits that keep hostile inputs (deep nesting, YAML alias bombs) from exhausting memory or the stack.</summary>
internal static class DataLimits
{
    public const int MaxDepth = 256;

    /// <summary>Nodes written when expanding YAML aliases into JSON; "billion laughs" files blow far past this.</summary>
    public const long MaxNodes = 20_000_000;

    /// <summary>Largest output we build in memory (characters).</summary>
    public const long MaxOutputChars = 512L * 1024 * 1024;

    /// <summary>Total JSON text for YAML keys that are lists or mappings (real files use a few short ones).</summary>
    public const long MaxComplexKeyChars = 1024 * 1024;

    /// <summary>Hex/octal integers longer than this stay text: converting them to decimal is superlinear.</summary>
    public const int MaxRadixDigits = 1000;

    public static OperationException TooDeep() =>
        new($"The data is nested more than {MaxDepth} levels deep, which Clementine doesn't convert.");

    public static OperationException TooLarge() =>
        new("The converted data would be enormous (YAML aliases expanding over and over), so Clementine stopped.");
}
