using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Clementine.Engines.Files.Data;

/// <summary>
/// XML → JSON and XML pretty-printing, with reader settings that are safe for untrusted files: no external entities
/// or DTDs are ever fetched (XXE), and entity expansion is capped (billion laughs).
/// </summary>
internal static class XmlData
{
    public static XmlReaderSettings SafeSettings(bool keepComments) => new()
    {
        DtdProcessing = DtdProcessing.Parse,
        XmlResolver = null,
        MaxCharactersFromEntities = 10_000_000,
        IgnoreComments = !keepComments,
        IgnoreProcessingInstructions = !keepComments,
        IgnoreWhitespace = !keepComments,
        CloseInput = false,
    };

    /// <summary>
    /// Convention (as used by most converters): element → property, attributes → "@name", text next to attributes or
    /// child elements → "#text", repeated child elements → array, empty element → null.
    /// </summary>
    public static DataNode ToData(byte[] xml)
    {
        try
        {
            using var stream = new MemoryStream(xml, writable: false);
            using var reader = XmlReader.Create(stream, SafeSettings(keepComments: false));
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element)
                throw new OperationException("This XML file has no root element.");
            var root = new DataObject();
            root.Add(reader.Name, ReadElement(reader, 0));
            return root;
        }
        catch (XmlException ex)
        {
            throw Invalid(ex);
        }
    }

    static DataNode ReadElement(XmlReader reader, int depth)
    {
        if (depth > DataLimits.MaxDepth)
            throw DataLimits.TooDeep();

        var isEmpty = reader.IsEmptyElement;
        var attributes = new List<KeyValuePair<string, DataNode>>();
        if (reader.MoveToFirstAttribute())
        {
            do
                attributes.Add(new("@" + reader.Name, new DataString(reader.Value)));
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        if (isEmpty)
        {
            reader.Read();
            return attributes.Count == 0 ? DataNull.Instance : Object(attributes, [], null);
        }

        var children = new List<(string Name, DataNode Value)>();
        var text = new StringBuilder();
        reader.Read();
        while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var name = reader.Name;
                    children.Add((name, ReadElement(reader, depth + 1)));
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace:
                    text.Append(reader.Value);
                    reader.Read();
                    break;
                default:
                    reader.Read();
                    break;
            }
        }
        reader.Read(); // past </element>

        if (attributes.Count == 0 && children.Count == 0)
            return new DataString(text.ToString());
        var content = text.ToString().Trim();
        return Object(attributes, children, content.Length > 0 ? content : null);
    }

    static DataObject Object(List<KeyValuePair<string, DataNode>> attributes, List<(string Name, DataNode Value)> children, string? text)
    {
        var obj = new DataObject();
        foreach (var attribute in attributes)
            obj.Add(attribute.Key, attribute.Value);
        foreach (var group in children.GroupBy(c => c.Name, StringComparer.Ordinal))
        {
            var values = group.Select(c => c.Value).ToList();
            obj.Add(group.Key, values.Count == 1 ? values[0] : new DataArray(values));
        }
        if (text is not null)
            obj.Add("#text", new DataString(text));
        return obj;
    }

    /// <summary>
    /// Re-indents XML (2 spaces). Whitespace is only removed between elements of element-only content; mixed content
    /// ("&lt;p&gt;a &lt;b&gt;b&lt;/b&gt;&lt;/p&gt;") and xml:space="preserve" are left exactly as they were.
    /// Comments, processing instructions and the DOCTYPE are kept; the result is UTF-8.
    /// </summary>
    public static string PrettyPrint(byte[] xml)
    {
        XDocument document;
        try
        {
            using var stream = new MemoryStream(xml, writable: false);
            using var reader = XmlReader.Create(stream, SafeSettings(keepComments: true));
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw Invalid(ex);
        }

        if (document.Root is null)
            throw new OperationException("This XML file has no root element.");
        RemoveIndentation(document.Root, preserve: false, depth: 0);
        foreach (var node in document.Nodes().OfType<XText>().ToList())
            node.Remove();

        var newLine = Array.IndexOf(xml, (byte)'\r') >= 0 ? "\r\n" : "\n";
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = newLine,
            NewLineHandling = NewLineHandling.Replace,
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = document.Declaration is null,
        };
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, settings))
            document.Save(writer);
        return Encoding.UTF8.GetString(output.ToArray()) + newLine;
    }

    static void RemoveIndentation(XElement element, bool preserve, int depth)
    {
        if (depth > DataLimits.MaxDepth)
            throw DataLimits.TooDeep();
        var space = element.Attribute(XNamespace.Xml + "space")?.Value;
        preserve = space switch
        {
            "preserve" => true,
            "default" => false,
            _ => preserve,
        };
        var mixed = element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));
        if (!preserve && !mixed)
        {
            foreach (var text in element.Nodes().OfType<XText>().ToList())
                text.Remove();
        }
        foreach (var child in element.Elements())
            RemoveIndentation(child, preserve || mixed, depth + 1);
    }

    static OperationException Invalid(XmlException ex)
    {
        if (ex.Message.Contains("MaxCharactersFromEntities", StringComparison.Ordinal))
            return new OperationException("This XML expands entities to an enormous size (a \"billion laughs\" file), so Clementine stopped.", ex.Message, ex);
        var where = ex.LineNumber > 0 ? $" (line {ex.LineNumber}, column {ex.LinePosition})" : "";
        var reason = ex.Message.Split(" Line ", 2)[0].TrimEnd('.', ',');
        return new OperationException($"This isn't valid XML{where}: {reason}.", ex.Message, ex);
    }
}
