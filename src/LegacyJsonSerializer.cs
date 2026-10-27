using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexQuotaTray.Compatibility
{
    // Compatibility boundary only. Quota source selection and parsing are unchanged.
    // Number boxing follows .NET Framework JavaScriptSerializer: int -> long -> decimal,
    // exponent forms -> double. Keep strings as strings; do not auto-convert ISO timestamps.
    internal sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; } = 2097152;
        public int RecursionLimit { get; set; } = 100;
        public object DeserializeObject(string input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Length > MaxJsonLength) throw new ArgumentException("JSON exceeds MaxJsonLength.");
            if (string.IsNullOrWhiteSpace(input)) return null;
            try
            {
                using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = RecursionLimit });
                return ConvertElement(document.RootElement, 1);
            }
            catch (JsonException) { return new LegacyJsonFallbackParser(input, RecursionLimit).Parse(); }
            catch (InvalidOperationException) { return new LegacyJsonFallbackParser(input, RecursionLimit).Parse(); }
        }

        private object ConvertElement(JsonElement element, int depth)
        {
            if (depth > RecursionLimit) throw new ArgumentException("JSON exceeds RecursionLimit.");
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                        dictionary[property.Name] = ConvertElement(property.Value, depth + 1);
                    return dictionary;
                case JsonValueKind.Array:
                    var result = new object[element.GetArrayLength()];
                    int i = 0;
                    foreach (var item in element.EnumerateArray()) result[i++] = ConvertElement(item, depth + 1);
                    return result;
                case JsonValueKind.String:
                    var raw = element.GetRawText();
                    // Framework recognizes only the escaped ASP.NET date syntax, not an ordinary /Date/ string.
                    if (raw.StartsWith("\"\\/Date(", StringComparison.Ordinal))
                    {
                        var match = Regex.Match(raw, "^\"\\\\/Date\\((-?[0-9]+)(?:[a-zA-Z]|[+-][0-9]{4})?\\)\\\\/\"$");
                        long milliseconds;
                        if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds))
                            return new DateTime(checked(milliseconds * 10000L + 621355968000000000L), DateTimeKind.Utc);
                    }
                    return element.GetString();
                case JsonValueKind.Number:
                    string number = element.GetRawText();
                    if (number.IndexOf('e') < 0 && number.IndexOf('E') < 0)
                    {
                        if (number.IndexOf('.') < 0)
                        {
                            int integer;
                            if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return integer;
                            long largeInteger;
                            if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out largeInteger)) return largeInteger;
                        }
                        decimal exact;
                        if (decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out exact)) return exact;
                    }
                    var floating = double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (double.IsInfinity(floating)) throw new ArgumentException("JSON number exceeds the legacy floating-point range.");
                    return floating;
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Null: return null;
                default: throw new ArgumentException("Unexpected JSON token.");
            }
        }
    }
}
