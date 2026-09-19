// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Helpers;

using PackageUrl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Parses repository URLs into canonical PackageURLs.
/// </summary>
public sealed record RepositoryUrlParseResult(PackageURL PackageUrl, Uri CanonicalUri);

public static class RepositoryUrlParser
{
    private static readonly Regex CandidateRegex = new(
        @"(?<![\w.@/-])(?:(?:git\+)?(?:https?|ssh|git)://[^\s<>\u0022'`]+|git@[\w.-]+:[^\s<>\u0022'`]+|(?:www\.)?(?:github\.com|gitlab\.com|bitbucket\.org)(?:[:]\d+)?/[^\s<>\u0022'`]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> GitHubNonRepositoryPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "actions", "archive", "blob", "code", "commit", "commits", "compare", "discussions",
        "issues", "pull", "pulls", "raw", "releases", "security", "tags", "wiki"
    };

    private static readonly HashSet<string> GitLabNonRepositoryPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "archive", "blob", "issues", "merge_requests", "raw", "snippets", "uploads"
    };

    private static readonly HashSet<string> BitbucketNonRepositoryPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "branches", "commits", "downloads", "issues", "pull-requests", "raw", "wiki"
    };

    /// <summary>
    /// Attempts to parse a repository URL in a supported public forge into a canonical PackageURL.
    /// </summary>
    public static bool TryParse(string? value, out PackageURL? packageUrl)
    {
        bool parsed = TryParseRepository(value, allowGenericHosts: false, out RepositoryUrlParseResult? result);
        packageUrl = result?.PackageUrl;
        return parsed;
    }

    /// <summary>
    /// Parses a repository URL and retains its sanitized canonical URI. Generic hosts are accepted
    /// only for authoritative metadata fields.
    /// </summary>
    public static bool TryParseRepository(
        string? value,
        bool allowGenericHosts,
        out RepositoryUrlParseResult? result)
    {
        result = null;
        if (!TryCreateRepositoryUri(value, out Uri? uri, out string? embeddedSourceVersion))
        {
            return false;
        }

        string host = NormalizeHost(uri.Host);
        bool supportedForge = IsSupportedHost(host, out string type);
        if (!supportedForge && !allowGenericHosts)
        {
            return false;
        }

        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length < 2)
        {
            return false;
        }

        int repositoryIndex;
        string? sourceVersion = null;
        string? subpath = null;

        if (supportedForge && type == "gitlab")
        {
            int routeMarker = Array.FindIndex(segments, segment => segment == "-");
            repositoryIndex = routeMarker >= 0 ? routeMarker - 1 : segments.Length - 1;
            if (routeMarker >= 0 &&
                !TryParseGitLabRoute(segments[(routeMarker + 1)..], out sourceVersion, out subpath))
            {
                return false;
            }
        }
        else if (supportedForge)
        {
            repositoryIndex = 1;
            if (segments.Length > 2 &&
                !TryParseForgeRoute(type, segments[2..], out sourceVersion, out subpath))
            {
                return false;
            }
        }
        else
        {
            repositoryIndex = segments.Length - 1;
        }

        if (repositoryIndex < 1 || repositoryIndex >= segments.Length)
        {
            return false;
        }
        if (supportedForge &&
            segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string repositoryName = RemoveGitSuffix(segments[repositoryIndex]);
        if (string.IsNullOrWhiteSpace(repositoryName) ||
            repositoryName is "." or ".." ||
            repositoryName.Contains(':'))
        {
            return false;
        }

        string namespaceValue;
        if (supportedForge)
        {
            if (type is "github" or "bitbucket" &&
                segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            namespaceValue = string.Join('/', segments[..repositoryIndex]);
        }
        else
        {
            string authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
            namespaceValue = $"{authority}/{string.Join('/', segments[..repositoryIndex])}";
            type = "generic";
        }

        if (string.IsNullOrWhiteSpace(namespaceValue) || namespaceValue is "." or "..")
        {
            return false;
        }

        sourceVersion ??= embeddedSourceVersion;
        PackageURL packageUrl = new(
            type,
            namespaceValue,
            repositoryName,
            sourceVersion,
            null,
            subpath);
        Uri canonicalUri = BuildCanonicalUri(uri, segments, repositoryIndex, repositoryName);
        result = new RepositoryUrlParseResult(packageUrl, canonicalUri);
        return true;
    }

    /// <summary>
    /// Builds a browser URL for a repository PackageURL. When requested, supported forge URLs
    /// include the retained source ref and repository subpath.
    /// </summary>
    public static Uri? GetRepositoryUri(PackageURL packageUrl, bool includeSourceLocation = false)
    {
        ArgumentNullException.ThrowIfNull(packageUrl);

        string? type = packageUrl.Type?.ToLowerInvariant();
        string? repositoryNamespace = packageUrl.Namespace;
        string? repositoryName = packageUrl.Name;
        if (string.IsNullOrWhiteSpace(type) ||
            string.IsNullOrWhiteSpace(repositoryNamespace) ||
            string.IsNullOrWhiteSpace(repositoryName))
        {
            return null;
        }

        string host;
        IEnumerable<string> repositoryPath;
        if (type == "generic")
        {
            string[] namespaceSegments = repositoryNamespace
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (namespaceSegments.Length == 0)
            {
                return null;
            }

            host = namespaceSegments[0];
            if (!host.Contains('.', StringComparison.Ordinal) &&
                !host.Contains(':', StringComparison.Ordinal) &&
                !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            repositoryPath = namespaceSegments.Skip(1).Append(repositoryName);
        }
        else
        {
            host = type switch
            {
                "github" => "github.com",
                "gitlab" => "gitlab.com",
                "bitbucket" => "bitbucket.org",
                _ => string.Empty,
            };
            if (host.Length == 0)
            {
                return null;
            }

            repositoryPath = repositoryNamespace
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Append(repositoryName);
        }

        List<string> pathSegments = repositoryPath
            .Select(Uri.EscapeDataString)
            .ToList();
        if (includeSourceLocation && !string.IsNullOrWhiteSpace(packageUrl.Version))
        {
            string? routeMarker = type switch
            {
                "github" => "tree",
                "gitlab" => "-/tree",
                "bitbucket" => "src",
                _ => null,
            };
            if (routeMarker is not null)
            {
                pathSegments.AddRange(routeMarker.Split('/'));
                pathSegments.AddRange(SplitAndEscapePath(packageUrl.Version));
                pathSegments.AddRange(SplitAndEscapePath(packageUrl.Subpath));
            }
        }

        return Uri.TryCreate(
            $"https://{host}/{string.Join('/', pathSegments)}",
            UriKind.Absolute,
            out Uri? repositoryUri)
            ? repositoryUri
            : null;
    }

    /// <summary>
    /// Extracts supported repository URLs from prose or serialized metadata.
    /// </summary>
    public static IEnumerable<PackageURL> Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<PackageURL>();
        }

        return CandidateRegex.Matches(content)
            .Select(match => TryParse(match.Value, out PackageURL? packageUrl) ? packageUrl : null)
            .Where(packageUrl => packageUrl is not null)
            .Cast<PackageURL>()
            .GroupBy(packageUrl => $"{packageUrl.Type}|{packageUrl.Namespace}|{packageUrl.Name}|{packageUrl.Subpath}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsSupportedHost(string host, out string type)
    {
        type = NormalizeHost(host) switch
        {
            "github.com" => "github",
            "gitlab.com" => "gitlab",
            "bitbucket.org" => "bitbucket",
            _ => string.Empty
        };
        return type.Length != 0;
    }

    private static bool TryParseForgeRoute(
        string type,
        IReadOnlyList<string> route,
        out string? sourceVersion,
        out string? subpath)
    {
        sourceVersion = null;
        subpath = null;
        if (route.Count == 0)
        {
            return true;
        }

        string routeName = route[0];
        if (type == "github")
        {
            if (routeName.Equals("tree", StringComparison.OrdinalIgnoreCase))
            {
                if (route.Count < 2)
                {
                    return false;
                }

                sourceVersion = route[1];
                subpath = JoinRoute(route, 2);
                return true;
            }

            if (GitHubNonRepositoryPaths.Contains(routeName) ||
                routeName.Equals("badge.svg", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else if (type == "bitbucket")
        {
            if (routeName.Equals("src", StringComparison.OrdinalIgnoreCase))
            {
                if (route.Count < 2)
                {
                    return false;
                }

                sourceVersion = route[1];
                subpath = JoinRoute(route, 2);
                return true;
            }

            if (BitbucketNonRepositoryPaths.Contains(routeName))
            {
                return false;
            }
        }

        subpath = JoinRoute(route, 0);
        return true;
    }

    private static bool TryParseGitLabRoute(
        IReadOnlyList<string> route,
        out string? sourceVersion,
        out string? subpath)
    {
        sourceVersion = null;
        subpath = null;
        if (route.Count == 0)
        {
            return true;
        }

        string routeName = route[0];
        if (routeName.Equals("tree", StringComparison.OrdinalIgnoreCase))
        {
            if (route.Count < 2)
            {
                return false;
            }

            sourceVersion = route[1];
            subpath = JoinRoute(route, 2);
            return true;
        }

        return !GitLabNonRepositoryPaths.Contains(routeName);
    }

    private static string? JoinRoute(IReadOnlyList<string> route, int start)
    {
        return route.Count <= start ? null : string.Join('/', route.Skip(start));
    }

    private static bool TryCreateRepositoryUri(
        string? value,
        out Uri? uri,
        out string? sourceVersion)
    {
        uri = null;
        sourceVersion = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = TrimCandidate(value);
        bool sourceControlSyntax =
            normalized.StartsWith("scm:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("git://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("git@", StringComparison.OrdinalIgnoreCase);
        if (normalized.StartsWith("scm:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..];
            if (normalized.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[4..];
            }
        }
        if (normalized.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..];
        }

        string uriValue = normalized;
        if (TryParseScpLike(normalized, out string? scpHost, out string? scpPath))
        {
            uriValue = $"ssh://git@{scpHost}/{scpPath}";
        }
        else if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            uriValue = $"https://{normalized}";
        }

        if (!Uri.TryCreate(uriValue, UriKind.Absolute, out Uri? parsed) ||
            parsed.Scheme is not ("http" or "https" or "ssh" or "git") ||
            string.IsNullOrWhiteSpace(parsed.Host))
        {
            return false;
        }

        if (sourceControlSyntax && !string.IsNullOrWhiteSpace(parsed.Fragment))
        {
            sourceVersion = Uri.UnescapeDataString(parsed.Fragment.TrimStart('#'));
        }

        uri = parsed;
        return true;
    }

    private static IEnumerable<string> SplitAndEscapePath(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString);
    }

    private static Uri BuildCanonicalUri(
        Uri uri,
        IReadOnlyList<string> segments,
        int repositoryIndex,
        string repositoryName)
    {
        string path = string.Join(
            '/',
            segments.Take(repositoryIndex).Append(repositoryName));
        UriBuilder builder = new(uri)
        {
            Scheme = "https",
            Host = NormalizeHost(uri.Host),
            UserName = string.Empty,
            Password = string.Empty,
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }
        return builder.Uri;
    }

    private static string NormalizeHost(string host)
    {
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..].ToLowerInvariant()
            : host.ToLowerInvariant();
    }

    private static string RemoveGitSuffix(string repositoryName)
    {
        return repositoryName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repositoryName[..^4]
            : repositoryName;
    }

    private static bool TryParseScpLike(string value, out string? host, out string? path)
    {
        host = null;
        path = null;
        int separator = value.IndexOf(':');
        int at = value.IndexOf('@');
        if (separator <= 0 || at < 0 || at > separator || value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        host = value[(at + 1)..separator];
        path = value[(separator + 1)..].TrimStart('/');
        return host.Length > 0 && path.Length > 0;
    }

    private static string TrimCandidate(string value)
    {
        return value.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '"', '\'');
    }
}
