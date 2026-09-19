// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Utilities
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    internal class OssUtilities
    {
        private const int MAX_FIELD_LENGTH = 65535;

        public static JsonElement? GetJSONPropertyIfExists(JsonElement? element, string keyName)
        {
            if (element is JsonElement elem && !string.IsNullOrWhiteSpace(keyName))
            {
                try
                {
                    if (elem.GetProperty(keyName) is JsonElement res)
                    {
                        return res;
                    }
                }
                catch (KeyNotFoundException)
                {
                }
            }
            return null;
        }

        public static string? GetJSONPropertyStringIfExists(JsonElement? element, string keyName)
        {
            return GetJSONPropertyIfExists(element, keyName)?.ToString();
        }

        public static string GetMaxClippedLength(string str)
        {
            if (string.IsNullOrEmpty(str)) { return str; }
            int maxLength = Math.Min(str.Length, MAX_FIELD_LENGTH);
            return str[0..maxLength];
        }

        /// <summary>
        /// Convert a JSON array (or a csv string) into an POCO array
        /// </summary>
        /// <param name="jsonElement"> </param>
        /// <returns> </returns>
        public static List<string>? ConvertJSONToList(JsonElement? jsonElement)
        {
            List<string> items = new();

            if (jsonElement.ToString() is string str)
            {
                if (GetJSONEnumerator(jsonElement) is JsonElement.ArrayEnumerator enumerator)
                {
                    foreach (JsonElement itemElement in enumerator)
                    {
                        if (itemElement.GetString() is string item)
                        {
                            items.Add(item);
                        }
                    }
                }
                else if (str.Contains(","))
                {
                    // split the string as a csv
                    items = str.Split(',').ToList();
                }
                else
                {
                    return null;
                }
            }

            return items;
        }

        public static JsonElement.ArrayEnumerator? GetJSONEnumerator(JsonElement? jsonElement)
        {
            if (jsonElement is not null && jsonElement?.ToString() is string str && !string.IsNullOrEmpty(str))
            {
                if (str.StartsWith("["))
                {
                    if (jsonElement?.EnumerateArray() is JsonElement.ArrayEnumerator enumerator)
                    {
                        return enumerator;
                    }
                }
            }

            return null;
        }

        public static bool ValueExists<T>(T? item, out T retItem) where T : new()
        {
            if (item is T x && x != null)
            {
                retItem = x;
                return true;
            }
            retItem = default(T) ?? new T();
            return false;
        }

        public static string NormalizeStringForFileSystem(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string result = new((s.Select(ch => invalidChars.Contains(ch) ? '_' : ch) ?? Array.Empty<char>()).ToArray());
            return result;
        }

        /// <summary>
        /// Removes any embedded userinfo (e.g. username/password or access token) from a URL before
        /// it is written to logs, so credentials for private feeds are never leaked.
        /// </summary>
        /// <param name="url">The URL (or arbitrary string) to redact.</param>
        /// <returns>The URL with any userinfo component removed, or the original value if it isn't a valid absolute URL.</returns>
        public static string? RedactUrlCredentials(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.UserInfo))
            {
                return url;
            }

            UriBuilder builder = new(uri) { UserName = string.Empty, Password = string.Empty };
            return builder.Uri.ToString();
        }
    }
}