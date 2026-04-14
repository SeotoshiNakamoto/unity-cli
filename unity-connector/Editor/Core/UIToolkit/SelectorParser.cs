using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UnityCliConnector.UIToolkit
{
    internal struct SelectorQuery
    {
        public string LabelExact;    // label=X
        public string LabelPartial;  // label~=X
        public string Id;            // id=X
        public string TypeName;      // type=X
        public string Path;          // path=X
        public int Index;            // [N], -1 = unset
    }

    internal static class SelectorParser
    {
        static readonly Regex IndexPattern = new Regex(@"^\[(\d+)\]$");

        internal static SelectorQuery Parse(string selector)
        {
            var query = new SelectorQuery { Index = -1 };
            if (string.IsNullOrEmpty(selector))
                return query;

            var tokens = TokenizeSmart(selector);

            foreach (var token in tokens)
            {
                var idxMatch = IndexPattern.Match(token);
                if (idxMatch.Success)
                {
                    query.Index = int.Parse(idxMatch.Groups[1].Value);
                    continue;
                }

                int tildeEq = token.IndexOf("~=", StringComparison.Ordinal);
                int eq = token.IndexOf('=');

                if (tildeEq >= 0)
                {
                    string key = token.Substring(0, tildeEq).ToLowerInvariant();
                    string value = token.Substring(tildeEq + 2);

                    if (key == "label")
                        query.LabelPartial = value;
                }
                else if (eq >= 0)
                {
                    string key = token.Substring(0, eq).ToLowerInvariant();
                    string value = token.Substring(eq + 1);

                    switch (key)
                    {
                        case "label": query.LabelExact = value; break;
                        case "id":    query.Id = value; break;
                        case "type":  query.TypeName = value; break;
                        case "path":  query.Path = value; break;
                    }
                }
            }

            return query;
        }

        /// <summary>
        /// Tokenize selector string by spaces, but keep "key=value with spaces" together
        /// when the value starts with a quote.
        /// Simple split for the common case (no quotes in selectors).
        /// </summary>
        static List<string> TokenizeSmart(string input)
        {
            // Simple space split — works for all current selector forms
            var result = new List<string>();
            var parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
                result.Add(part);
            return result;
        }

        internal static bool Matches(SelectorQuery query, ElementData element)
        {
            if (query.LabelExact != null)
            {
                if (!string.Equals(element.Label, query.LabelExact, StringComparison.Ordinal))
                    return false;
            }

            if (query.LabelPartial != null)
            {
                if (element.Label == null ||
                    element.Label.IndexOf(query.LabelPartial, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            if (query.Id != null)
            {
                // Match against the raw name (without "id:" prefix) or the full stable ID
                string rawName = element.Id != null && element.Id.StartsWith("id:")
                    ? element.Id.Substring(3)
                    : element.Id;

                if (!string.Equals(rawName, query.Id, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(element.Id, query.Id, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (query.TypeName != null)
            {
                if (!string.Equals(element.TypeName, query.TypeName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (query.Path != null)
            {
                if (element.Path == null ||
                    element.Path.IndexOf(query.Path, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Find all elements matching the query (ignoring Index).
        /// Caller handles Index selection.
        /// </summary>
        internal static List<ElementData> FindMatches(SelectorQuery query, List<ElementData> elements)
        {
            var matches = new List<ElementData>();
            foreach (var elem in elements)
            {
                if (Matches(query, elem))
                    matches.Add(elem);
            }
            return matches;
        }
    }
}
