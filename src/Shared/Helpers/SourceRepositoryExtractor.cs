// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Helpers;

using PackageUrl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// Extracts source repositories from explicit registry metadata fields.
/// </summary>
public static class SourceRepositoryExtractor
{
    private static readonly HashSet<string> RepositoryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "repository",
        "repository_url",
        "source",
        "source_repository",
        "source_repository_url",
        "source_code_uri",
        "code_repository",
        "scm",
        "vcs",
        "vcs_git",
        "vcs_browser",
        "git",
    };

    private static readonly Regex ExplicitTextFieldRegex = new(
        @"(?im)^\s*(?:Vcs-(?:Git|Browser)|Source-(?:Repository|URL)|Repository)\s*:\s*(?<url>\S+)",
        RegexOptions.Compiled);

    private static readonly Regex ExplicitJsonUrlRegex = new(
        "(?i)\"(?:git|repository_url|source_repository_url)\"\\s*:\\s*\"(?<url>[^\"]+)\"",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts repository URLs from JSON values under authoritative repository/VCS fields.
    /// Homepage and arbitrary URL fields are intentionally ignored.
    /// </summary>
    public static Dictionary<PackageURL, double> FromJson(PackageURL package, string metadata)
    {
        Dictionary<PackageURL, double> result = new();
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return result;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(metadata);
            VisitJson(package, document.RootElement, result, isAuthoritativeContext: false);
        }
        catch (JsonException)
        {
            // Some registry endpoints concatenate multiple JSON documents or embed a podspec
            // JSON document in HTML. Parse line-delimited documents, then retain only explicit
            // repository/VCS URL properties as a conservative fallback.
            foreach (string line in metadata.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line.Trim());
                    VisitJson(package, document.RootElement, result, isAuthoritativeContext: false);
                }
                catch (JsonException)
                {
                    // Continue with the explicit-field fallback below.
                }
            }

            foreach (Match match in ExplicitJsonUrlRegex.Matches(metadata))
            {
                AddUrl(package, match.Groups["url"].Value, result);
            }
        }
        return result;
    }

    /// <summary>
    /// Extracts repositories from Maven-style SCM XML.
    /// </summary>
    public static Dictionary<PackageURL, double> FromXml(PackageURL package, string metadata)
    {
        Dictionary<PackageURL, double> result = new();
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return result;
        }

        XDocument document = XDocument.Parse(metadata);
        foreach (XElement scm in document.Descendants().Where(element => element.Name.LocalName.Equals("scm", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (XElement child in scm.Elements())
            {
                if (child.Name.LocalName.Equals("url", StringComparison.OrdinalIgnoreCase) ||
                    child.Name.LocalName.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
                    child.Name.LocalName.Equals("developerConnection", StringComparison.OrdinalIgnoreCase))
                {
                    AddUrl(package, child.Value, result);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Extracts repositories from explicit Debian source control fields.
    /// </summary>
    public static Dictionary<PackageURL, double> FromExplicitTextFields(PackageURL package, string metadata)
    {
        Dictionary<PackageURL, double> result = new();
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return result;
        }

        foreach (Match match in ExplicitTextFieldRegex.Matches(metadata))
        {
            AddUrl(package, match.Groups["url"].Value.TrimEnd(';', ','), result);
        }
        return result;
    }

    private static void VisitJson(
        PackageURL package,
        JsonElement element,
        Dictionary<PackageURL, double> result,
        bool isAuthoritativeContext)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    bool authoritative = isAuthoritativeContext || RepositoryKeys.Contains(property.Name);
                    if (authoritative && property.Value.ValueKind == JsonValueKind.String)
                    {
                        AddUrl(package, property.Value.GetString(), result);
                    }
                    else
                    {
                        VisitJson(package, property.Value, result, authoritative);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    VisitJson(package, child, result, isAuthoritativeContext);
                }
                break;
            case JsonValueKind.String when isAuthoritativeContext:
                AddUrl(package, element.GetString(), result);
                break;
        }
    }

    private static void AddUrl(PackageURL package, string? rawUrl, Dictionary<PackageURL, double> result)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return;
        }

        string value = rawUrl.Trim().Trim('"', '\'');
        if (value.StartsWith("scm:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[(value.IndexOf(':') + 1)..];
            int separator = value.IndexOf(':');
            if (separator >= 0)
            {
                value = value[(separator + 1)..];
            }
        }
        if (value.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }
        if (value.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            value = "ssh://" + value;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string host = uri.Host.ToLowerInvariant();
        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !segment.Equals("tree", StringComparison.OrdinalIgnoreCase) &&
                              !segment.Equals("blob", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (segments.Length == 0)
        {
            return;
        }

        string type;
        if (host is "github.com" or "www.github.com")
        {
            type = "github";
        }
        else if (host is "gitlab.com" or "www.gitlab.com")
        {
            type = "gitlab";
        }
        else if (host is "bitbucket.org" or "www.bitbucket.org")
        {
            type = "bitbucket";
        }
        else
        {
            type = "generic";
        }

        string name = segments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[^1][..^4]
            : segments[^1];
        string? @namespace = segments.Length > 1
            ? string.Join('/', segments[..^1])
            : host;
        PackageURL repository = new(type, @namespace, name, null, null, null);
        if (!result.Keys.Any(existing => existing.ToString().Equals(repository.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            result[repository] = 1.0;
        }
    }
}
