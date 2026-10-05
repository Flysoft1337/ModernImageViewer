using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs.Svg;

internal static partial class SvgInputPolicy
{
    private const int MaximumInputBytes = 1_048_576;
    private const int MaximumElements = 2_048;
    private const int MaximumGeometryNumbers = 65_536;
    private const int MaximumActiveOpacityLayers = 2;
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "linearGradient", "radialGradient", "stop", "title", "desc",
    };
    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "id", "version", "viewBox", "preserveAspectRatio", "width", "height", "x", "y", "x1", "y1", "x2", "y2",
        "cx", "cy", "r", "rx", "ry", "d", "points", "transform", "style", "fill", "stroke", "color", "opacity",
        "fill-opacity", "stroke-opacity", "fill-rule", "stroke-width", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "display", "visibility",
        "gradientUnits", "gradientTransform", "spreadMethod", "fx", "fy", "fr", "offset", "stop-color", "stop-opacity",
    };
    private static readonly HashSet<string> StyleProperties = new(StringComparer.Ordinal)
    {
        "fill", "stroke", "color", "opacity", "fill-opacity", "stroke-opacity", "fill-rule", "stroke-width",
        "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset",
        "display", "visibility", "stop-color", "stop-opacity",
    };

    public static XDocument Read(Stream input, CancellationToken cancellationToken, out int maximumOpacityDepth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.CanSeek && input.Length > MaximumInputBytes)
        {
            throw new ImageSizeLimitExceededException();
        }
        using MemoryStream bounded = new();
        byte[] chunk = new byte[8_192];
        int count;
        while ((count = input.Read(chunk, 0, chunk.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bounded.Length + count > MaximumInputBytes)
            {
                throw new ImageSizeLimitExceededException();
            }
            bounded.Write(chunk, 0, count);
        }
        bounded.Position = 0;
        try
        {
            maximumOpacityDepth = Validate(bounded, cancellationToken);
            bounded.Position = 0;
            using XmlReader reader = XmlReader.Create(bounded, CreateReaderSettings());
            return XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new ImageDecodeException(ImageOpenError.CorruptFile, exception);
        }
    }

    private static XmlReaderSettings CreateReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumInputBytes,
        IgnoreComments = true,
    };

    private static int Validate(Stream input, CancellationToken cancellationToken)
    {
        using XmlReader reader = XmlReader.Create(input, CreateReaderSettings());
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        List<string> references = [];
        int elements = 0;
        int geometryNumbers = 0;
        int maximumOpacityDepth = 0;
        int[] activeOpacityAtDepth = new int[33];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.ProcessingInstruction)
            {
                throw Unsupported();
            }
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }
            if (++elements > MaximumElements || reader.Depth > 32 || reader.AttributeCount > 48)
            {
                throw new ImageSizeLimitExceededException();
            }
            string element = reader.LocalName;
            if (reader.NamespaceURI != SvgNamespace || !Elements.Contains(element)
                || (elements == 1 && element != "svg") || (elements > 1 && element == "svg"))
            {
                throw Unsupported();
            }
            string? opacity = null;
            string? inlineOpacity = null;
            while (reader.MoveToNextAttribute())
            {
                if (reader.NamespaceURI == "http://www.w3.org/2000/xmlns/")
                {
                    continue;
                }
                string name = reader.LocalName;
                string value = reader.Value;
                if (reader.NamespaceURI.Length != 0 || !Attributes.Contains(name))
                {
                    throw Unsupported();
                }
                if (value.Length > (name is "d" or "points" ? 262_144 : 4_096))
                {
                    throw new ImageSizeLimitExceededException();
                }
                if (name == "id")
                {
                    if (!IdentifierPattern().IsMatch(value) || !ids.TryAdd(value, element))
                    {
                        throw Unsupported();
                    }
                }
                else if (name == "style")
                {
                    int referenceCount = references.Count;
                    ValidateStyle(value, references, ref geometryNumbers, ref inlineOpacity);
                    if (references.Count != referenceCount && element is "linearGradient" or "radialGradient" or "stop")
                    {
                        throw Unsupported();
                    }
                }
                else
                {
                    if (name == "opacity")
                    {
                        opacity = value;
                    }
                    int referenceCount = references.Count;
                    ValidateValue(name, value, references, ref geometryNumbers);
                    if (references.Count != referenceCount && element is "linearGradient" or "radialGradient" or "stop")
                    {
                        throw Unsupported();
                    }
                }
            }
            reader.MoveToElement();
            // Opacity is not inherited. A node's own style overrides its presentation attribute,
            // while active ancestor layers remain live during traversal of its descendants.
            int activeOpacity = reader.Depth == 0 ? 0 : activeOpacityAtDepth[reader.Depth - 1];
            if (HasOpacityLayer(inlineOpacity ?? opacity))
            {
                activeOpacity++;
            }
            if (activeOpacity > MaximumActiveOpacityLayers)
            {
                throw new ImageSizeLimitExceededException();
            }
            activeOpacityAtDepth[reader.Depth] = activeOpacity;
            maximumOpacityDepth = Math.Max(maximumOpacityDepth, activeOpacity);
        }
        if (elements == 0)
        {
            throw new ImageDecodeException(ImageOpenError.CorruptFile);
        }
        foreach (string reference in references)
        {
            // Only paint-to-gradient edges exist in this subset. No use, href, clip, mask or filter edges can recurse.
            if (!ids.TryGetValue(reference, out string? target) || target is not ("linearGradient" or "radialGradient"))
            {
                throw Unsupported();
            }
        }
        return maximumOpacityDepth;
    }

    private static bool HasOpacityLayer(string? value)
    {
        if (value is null)
        {
            return false;
        }
        value = value.Trim();
        bool percentage = value.EndsWith('%');
        ReadOnlySpan<char> number = percentage ? value.AsSpan(0, value.Length - 1) : value.AsSpan();
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double opacity)
            || !double.IsFinite(opacity))
        {
            // Indirection and explicit inheritance need separate accounting, so this subset excludes them.
            throw Unsupported();
        }
        if (percentage)
        {
            opacity /= 100;
        }
        return Math.Clamp(opacity, 0, 1) < 1;
    }

    private static void ValidateStyle(string value, List<string> references, ref int geometryNumbers, ref string? opacity)
    {
        string[] declarations = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (declarations.Length > 24)
        {
            throw new ImageSizeLimitExceededException();
        }
        foreach (string declaration in declarations)
        {
            int colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw Unsupported();
            }
            string name = declaration[..colon].Trim();
            if (!StyleProperties.Contains(name))
            {
                throw Unsupported();
            }
            string propertyValue = declaration[(colon + 1)..].Trim();
            ValidateValue(name, propertyValue, references, ref geometryNumbers);
            if (name == "opacity")
            {
                opacity = propertyValue;
            }
        }
    }

    private static void ValidateValue(string name, string value, List<string> references, ref int geometryNumbers)
    {
        // CSS escaping/comments/imports and function indirection are excluded, so URL checks cannot be bypassed by CSS syntax.
        if (value.Contains('\\') || value.Contains('@') || value.Contains("/*", StringComparison.Ordinal)
            || value.Contains("var(", StringComparison.OrdinalIgnoreCase))
        {
            throw Unsupported();
        }
        if (UrlTokenPattern().IsMatch(value))
        {
            Match match = LocalPaintPattern().Match(value);
            if (name is not ("fill" or "stroke") || !match.Success)
            {
                throw Unsupported();
            }
            references.Add(match.Groups[1].Value);
            return;
        }
        if (name is "fill" or "stroke" or "color" or "stop-color" or "display" or "visibility")
        {
            return;
        }
        foreach (Match match in NumberPattern().Matches(value))
        {
            if (++geometryNumbers > MaximumGeometryNumbers)
            {
                throw new ImageSizeLimitExceededException();
            }
            if (!double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                || !double.IsFinite(number))
            {
                throw Corrupt();
            }
            if (Math.Abs(number) > 1_000_000)
            {
                throw new ImageSizeLimitExceededException();
            }
        }
        ValidateGeometry(name, value, ref geometryNumbers);
    }

    private static ImageDecodeException Unsupported() => new(ImageOpenError.UnsupportedFormat);

    private static ImageDecodeException Corrupt() => new(ImageOpenError.CorruptFile);

    private static void ValidateGeometry(string name, string value, ref int geometryNumbers)
    {
        if (name is "width" or "height" or "x" or "y" or "x1" or "y1" or "x2" or "y2"
            or "cx" or "cy" or "r" or "rx" or "ry" or "fx" or "fy" or "fr"
            or "stroke-width" or "stroke-dashoffset")
        {
            string length = value.Trim();
            Match match = LengthPattern().Match(length);
            if (!match.Success || match.Index != 0 || match.Length != length.Length)
            {
                throw Corrupt();
            }
        }
        else if (name is "points" or "viewBox")
        {
            List<GeometryToken> tokens = ReadGeometryTokens(value, allowCommands: false, ref geometryNumbers);
            if (name == "points" ? (tokens.Count & 1) != 0 : tokens.Count != 4)
            {
                throw Corrupt();
            }
            if (name == "viewBox" && (tokens[2].Number < 0 || tokens[3].Number < 0))
            {
                throw Corrupt();
            }
        }
        else if (name == "d")
        {
            ValidatePath(value, ref geometryNumbers);
        }
        else if (name is "transform" or "gradientTransform")
        {
            ValidateTransform(value, ref geometryNumbers);
        }
    }

    private readonly record struct GeometryToken(char Command, double Number, string Literal);

    private static List<GeometryToken> ReadGeometryTokens(string value, bool allowCommands, ref int geometryNumbers)
    {
        List<GeometryToken> tokens = [];
        int offset = 0;
        while (offset < value.Length)
        {
            while (offset < value.Length && char.IsWhiteSpace(value[offset]))
            {
                offset++;
            }
            if (offset == value.Length)
            {
                break;
            }
            if (value[offset] == ',')
            {
                if (tokens.Count == 0 || tokens[^1].Command != '\0')
                {
                    throw Corrupt();
                }
                offset++;
                while (offset < value.Length && char.IsWhiteSpace(value[offset]))
                {
                    offset++;
                }
                if (offset == value.Length || value[offset] == ',' || char.IsLetter(value[offset]))
                {
                    throw Corrupt();
                }
            }
            char current = value[offset];
            if (allowCommands && "MmLlHhVvCcSsQqTtAaZz".Contains(current, StringComparison.Ordinal))
            {
                if (++geometryNumbers > MaximumGeometryNumbers)
                {
                    throw new ImageSizeLimitExceededException();
                }
                tokens.Add(new GeometryToken(current, 0, string.Empty));
                offset++;
                continue;
            }
            Match match = NumberPattern().Match(value, offset);
            if (!match.Success || match.Index != offset
                || !double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                || !double.IsFinite(number))
            {
                throw Corrupt();
            }
            tokens.Add(new GeometryToken('\0', number, match.Value));
            offset += match.Length;
        }
        return tokens;
    }

    private static void ValidatePath(string value, ref int geometryNumbers)
    {
        List<GeometryToken> tokens = ReadGeometryTokens(value, allowCommands: true, ref geometryNumbers);
        if (tokens.Count == 0)
        {
            return;
        }
        if (tokens[0].Command is not ('M' or 'm'))
        {
            throw Corrupt();
        }
        int offset = 0;
        while (offset < tokens.Count)
        {
            char command = char.ToUpperInvariant(tokens[offset++].Command);
            int required = command switch
            {
                'M' or 'L' or 'T' => 2,
                'H' or 'V' => 1,
                'C' => 6,
                'S' or 'Q' => 4,
                'A' => 7,
                'Z' => 0,
                _ => throw Corrupt(),
            };
            int first = offset;
            while (offset < tokens.Count && tokens[offset].Command == '\0')
            {
                offset++;
            }
            int count = offset - first;
            if (required == 0 ? count != 0 : count == 0 || count % required != 0)
            {
                throw Corrupt();
            }
            if (command == 'A')
            {
                for (int arc = first; arc < offset; arc += 7)
                {
                    // The restricted subset requires separate literal flags, not compact SVG flag syntax.
                    if (tokens[arc + 3].Literal is not ("0" or "1") || tokens[arc + 4].Literal is not ("0" or "1"))
                    {
                        throw Corrupt();
                    }
                }
            }
        }
    }

    private static void ValidateTransform(string value, ref int geometryNumbers)
    {
        int offset = 0;
        while (offset < value.Length)
        {
            while (offset < value.Length && char.IsWhiteSpace(value[offset]))
            {
                offset++;
            }
            if (offset == value.Length)
            {
                break;
            }
            int nameStart = offset;
            while (offset < value.Length && char.IsLetter(value[offset]))
            {
                offset++;
            }
            string function = value[nameStart..offset];
            while (offset < value.Length && char.IsWhiteSpace(value[offset]))
            {
                offset++;
            }
            if (offset == value.Length || value[offset++] != '(')
            {
                throw Corrupt();
            }
            int close = value.IndexOf(')', offset);
            if (close < 0)
            {
                throw Corrupt();
            }
            int count = ReadGeometryTokens(value[offset..close], allowCommands: false, ref geometryNumbers).Count;
            bool valid = function switch
            {
                "matrix" => count == 6,
                "translate" or "scale" => count is 1 or 2,
                "rotate" => count is 1 or 3,
                "skewX" or "skewY" => count == 1,
                _ => false,
            };
            if (!valid)
            {
                throw Corrupt();
            }
            offset = close + 1;
            while (offset < value.Length && char.IsWhiteSpace(value[offset]))
            {
                offset++;
            }
            if (offset < value.Length && value[offset] == ',')
            {
                offset++;
                if (string.IsNullOrWhiteSpace(value[offset..]))
                {
                    throw Corrupt();
                }
            }
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^url\\(#([A-Za-z_][A-Za-z0-9_.-]{0,127})\\)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LocalPaintPattern();

    [GeneratedRegex("\\burl\\s*\\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex UrlTokenPattern();

    [GeneratedRegex("[+-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NumberPattern();

    [GeneratedRegex("[+-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?\\s*(?:px|pt|pc|cm|mm|in|em|ex|%)?", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LengthPattern();
}
