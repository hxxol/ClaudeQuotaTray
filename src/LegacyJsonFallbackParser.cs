using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexQuotaTray.Compatibility
{
    // JavaScriptSerializer accepted a small JavaScript-like superset (single quotes,
    // bare member names and signed/leading-zero numbers). Keep that legacy input
    // acceptance at the migration boundary instead of silently changing quota data.
    internal sealed class LegacyJsonFallbackParser
    {
        private readonly string _input;
        private readonly int _limit;
        private int _position;
        internal LegacyJsonFallbackParser(string input, int limit) { _input = input; _limit = limit; }
        internal object Parse()
        {
            object value = Value(1);
            WhiteSpace();
            if (_position != _input.Length) throw Error();
            return value;
        }
        private object Value(int depth)
        {
            if (depth > _limit) throw Error();
            WhiteSpace();
            if (_position == _input.Length) return null;
            char next = _input[_position];
            if (next == '{') return Object(depth);
            if (next == '[') return Array(depth);
            if (next == '\'' || next == '"')
            {
                int begin = _position;
                var text = String();
                var raw = _input.Substring(begin, _position - begin);
                if (raw.StartsWith("\"\\/Date(", StringComparison.Ordinal))
                {
                    var match = Regex.Match(raw, "^\"\\\\/Date\\((-?[0-9]+)(?:[a-zA-Z]|[+-][0-9]{4})?\\)\\\\/\"$");
                    long ms;
                    if (match.Success && long.TryParse(match.Groups[1].Value, out ms))
                        return new DateTime(checked(ms * 10000L + 621355968000000000L), DateTimeKind.Utc);
                }
                return text;
            }
            var token = Token();
            if (token == "null") return null;
            if (token == "true") return true;
            if (token == "false") return false;
            if (token.IndexOf('e') < 0 && token.IndexOf('E') < 0)
            {
                if (token.IndexOf('.') < 0)
                {
                    int integer;
                    if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return integer;
                    long large;
                    if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out large)) return large;
                }
                decimal exact;
                if (decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out exact)) return exact;
            }
            double number;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                (!double.IsInfinity(number) || token == "Infinity" || token == "-Infinity")) return number;
            throw Error();
        }
        private object Object(int depth)
        {
            _position++;
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            WhiteSpace();
            if (Take('}')) return result;
            while (true)
            {
                WhiteSpace();
                if (_position >= _input.Length) throw Error();
                var key = _input[_position] == '"' || _input[_position] == '\'' ? String() : Token();
                if (key.Length == 0) throw Error();
                WhiteSpace();
                if (!Take(':')) throw Error();
                result[key] = Value(depth + 1);
                WhiteSpace();
                if (Take('}')) return result;
                if (!Take(',')) throw Error();
            }
        }
        private object Array(int depth)
        {
            _position++;
            var result = new List<object>();
            WhiteSpace();
            if (Take(']')) return result.ToArray();
            while (true)
            {
                result.Add(Value(depth + 1));
                WhiteSpace();
                if (Take(']')) return result.ToArray();
                if (!Take(',')) throw Error();
                WhiteSpace();
                if (_position == _input.Length || _input[_position] == ']') throw Error();
            }
        }
        private string String()
        {
            char quote = _input[_position++];
            var result = new StringBuilder();
            while (_position < _input.Length)
            {
                char current = _input[_position++];
                if (current == quote) return ValidateUtf16(result.ToString());
                if (current != '\\') { result.Append(current); continue; }
                if (_position == _input.Length) throw Error();
                char escaped = _input[_position++];
                switch (escaped)
                {
                    case '\\': case '/': case '"': case '\'': result.Append(escaped); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (_position + 4 > _input.Length) throw Error();
                        result.Append((char)int.Parse(_input.Substring(_position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _position += 4;
                        break;
                    default: throw Error();
                }
            }
            throw Error();
        }
        private static string ValidateUtf16(string text)
        {
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (char.IsHighSurrogate(chars[i]))
                {
                    if (i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1])) i++;
                    else chars[i] = '\uFFFD';
                }
                else if (char.IsLowSurrogate(chars[i])) chars[i] = '\uFFFD';
            }
            return new string(chars);
        }
        private string Token()
        {
            int start = _position;
            while (_position < _input.Length)
            {
                char current = _input[_position];
                if (!char.IsLetterOrDigit(current) && current != '.' && current != '-' && current != '_' && current != '+') break;
                _position++;
            }
            return _input.Substring(start, _position - start);
        }
        private void WhiteSpace() { while (_position < _input.Length && char.IsWhiteSpace(_input[_position])) _position++; }
        private bool Take(char value) { if (_position < _input.Length && _input[_position] == value) { _position++; return true; } return false; }
        private ArgumentException Error() { return new ArgumentException("Invalid legacy JSON at character " + _position + "."); }
    }
}
