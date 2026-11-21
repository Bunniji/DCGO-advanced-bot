using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Minimal JSON writer and reader for the decision bridge.
///
/// Unity's JsonUtility cannot express the shapes this protocol needs (heterogeneous
/// option lists, dictionaries), and pulling in a third-party serializer for a handful
/// of messages would be a worse trade than 150 lines that we control and can debug.
///
/// The wire format is NDJSON: one complete JSON object per line, no embedded newlines.
/// That choice is deliberate - at ~100 decisions per game the bandwidth is irrelevant,
/// while being able to read the traffic with a text editor is worth a lot.
/// </summary>
public static class BridgeJson
{
    #region writing

    public sealed class Writer
    {
        readonly StringBuilder _sb = new StringBuilder(512);
        bool _needComma;

        public Writer()
        {
            _sb.Append('{');
        }

        public Writer Raw(string key, string rawJson)
        {
            Key(key);
            _sb.Append(rawJson);
            return this;
        }

        public Writer Str(string key, string value)
        {
            Key(key);
            AppendString(_sb, value);
            return this;
        }

        public Writer Num(string key, int value)
        {
            Key(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public Writer Num(string key, long value)
        {
            Key(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public Writer Bool(string key, bool value)
        {
            Key(key);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public Writer Array(string key, IEnumerable<string> rawItems)
        {
            Key(key);
            _sb.Append('[');

            bool first = true;
            foreach (string item in rawItems)
            {
                if (!first)
                {
                    _sb.Append(',');
                }

                _sb.Append(item);
                first = false;
            }

            _sb.Append(']');
            return this;
        }

        public Writer StrArray(string key, IEnumerable<string> values)
        {
            Key(key);
            _sb.Append('[');

            bool first = true;
            foreach (string value in values)
            {
                if (!first)
                {
                    _sb.Append(',');
                }

                AppendString(_sb, value);
                first = false;
            }

            _sb.Append(']');
            return this;
        }

        public Writer NumArray(string key, IEnumerable<int> values)
        {
            Key(key);
            _sb.Append('[');

            bool first = true;
            foreach (int value in values)
            {
                if (!first)
                {
                    _sb.Append(',');
                }

                _sb.Append(value.ToString(CultureInfo.InvariantCulture));
                first = false;
            }

            _sb.Append(']');
            return this;
        }

        public string Close()
        {
            _sb.Append('}');
            return _sb.ToString();
        }

        void Key(string key)
        {
            if (_needComma)
            {
                _sb.Append(',');
            }

            AppendString(_sb, key);
            _sb.Append(':');
            _needComma = true;
        }
    }

    public static void AppendString(StringBuilder sb, string value)
    {
        if (value == null)
        {
            sb.Append("null");
            return;
        }

        sb.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                    {
                        // Card names carry Japanese text in some entries. Escaping everything
                        // outside printable ASCII keeps the stream byte-safe whatever encoding
                        // the reader assumes.
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        sb.Append('"');
    }

    #endregion

    #region reading

    /// <summary>
    /// Parses one JSON value. Returns Dictionary&lt;string, object&gt;, List&lt;object&gt;,
    /// string, double, bool or null. Throws FormatException on malformed input - the caller
    /// treats that as a dead connection rather than trying to recover.
    /// </summary>
    public static object Parse(string text)
    {
        int index = 0;
        object value = ParseValue(text, ref index);
        SkipWhitespace(text, ref index);

        if (index != text.Length)
        {
            throw new FormatException($"trailing characters at {index}");
        }

        return value;
    }

    public static int GetInt(Dictionary<string, object> obj, string key, int fallback)
    {
        if (obj == null || !obj.TryGetValue(key, out object value) || value == null)
        {
            return fallback;
        }

        if (value is double d)
        {
            return (int)Math.Round(d);
        }

        if (value is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return fallback;
    }

    public static string GetString(Dictionary<string, object> obj, string key)
    {
        if (obj == null || !obj.TryGetValue(key, out object value))
        {
            return null;
        }

        return value as string;
    }

    static object ParseValue(string text, ref int index)
    {
        SkipWhitespace(text, ref index);

        if (index >= text.Length)
        {
            throw new FormatException("unexpected end of input");
        }

        char c = text[index];

        switch (c)
        {
            case '{': return ParseObject(text, ref index);
            case '[': return ParseArray(text, ref index);
            case '"': return ParseString(text, ref index);
            case 't': Expect(text, ref index, "true"); return true;
            case 'f': Expect(text, ref index, "false"); return false;
            case 'n': Expect(text, ref index, "null"); return null;
            default: return ParseNumber(text, ref index);
        }
    }

    static Dictionary<string, object> ParseObject(string text, ref int index)
    {
        var result = new Dictionary<string, object>();
        index++; // '{'

        SkipWhitespace(text, ref index);

        if (index < text.Length && text[index] == '}')
        {
            index++;
            return result;
        }

        while (true)
        {
            SkipWhitespace(text, ref index);
            string key = ParseString(text, ref index);

            SkipWhitespace(text, ref index);
            if (index >= text.Length || text[index] != ':')
            {
                throw new FormatException($"expected ':' at {index}");
            }
            index++;

            result[key] = ParseValue(text, ref index);

            SkipWhitespace(text, ref index);

            if (index >= text.Length)
            {
                throw new FormatException("unterminated object");
            }

            if (text[index] == ',')
            {
                index++;
                continue;
            }

            if (text[index] == '}')
            {
                index++;
                return result;
            }

            throw new FormatException($"expected ',' or '}}' at {index}");
        }
    }

    static List<object> ParseArray(string text, ref int index)
    {
        var result = new List<object>();
        index++; // '['

        SkipWhitespace(text, ref index);

        if (index < text.Length && text[index] == ']')
        {
            index++;
            return result;
        }

        while (true)
        {
            result.Add(ParseValue(text, ref index));

            SkipWhitespace(text, ref index);

            if (index >= text.Length)
            {
                throw new FormatException("unterminated array");
            }

            if (text[index] == ',')
            {
                index++;
                continue;
            }

            if (text[index] == ']')
            {
                index++;
                return result;
            }

            throw new FormatException($"expected ',' or ']' at {index}");
        }
    }

    static string ParseString(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '"')
        {
            throw new FormatException($"expected string at {index}");
        }

        index++;
        var sb = new StringBuilder();

        while (index < text.Length)
        {
            char c = text[index++];

            if (c == '"')
            {
                return sb.ToString();
            }

            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            if (index >= text.Length)
            {
                break;
            }

            char escape = text[index++];

            switch (escape)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (index + 4 > text.Length)
                    {
                        throw new FormatException("truncated \\u escape");
                    }
                    sb.Append((char)Convert.ToInt32(text.Substring(index, 4), 16));
                    index += 4;
                    break;
                default:
                    throw new FormatException($"unknown escape \\{escape}");
            }
        }

        throw new FormatException("unterminated string");
    }

    static double ParseNumber(string text, ref int index)
    {
        int start = index;

        while (index < text.Length && "+-.eE0123456789".IndexOf(text[index]) >= 0)
        {
            index++;
        }

        string slice = text.Substring(start, index - start);

        if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new FormatException($"bad number '{slice}' at {start}");
        }

        return value;
    }

    static void Expect(string text, ref int index, string literal)
    {
        if (index + literal.Length > text.Length ||
            string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
        {
            throw new FormatException($"expected '{literal}' at {index}");
        }

        index += literal.Length;
    }

    static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }

    #endregion
}
