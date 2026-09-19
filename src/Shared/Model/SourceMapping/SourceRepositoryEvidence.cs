// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Model.SourceMapping;

using System.Text.RegularExpressions;

/// <summary>
/// Records one reason a source repository candidate was identified.
/// </summary>
public sealed record SourceRepositoryEvidence
{
    private static readonly Regex UriCredentials = new(
        @"(?<scheme>[a-z][a-z0-9+.-]*://)[^/\s@]+@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EncodedUriCredentials = new(
        @"(?<scheme>[a-z][a-z0-9+.-]*%3a%2f%2f)[^%\s]+(?:%3a[^%\s]+)?%40",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SensitiveQueryValues = new(
        @"(?<name>[?&](?:access_token|private_token|token|sig|signature|key|password|passwd|secret)=)[^&#\s]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public SourceRepositoryEvidence(
        SourceRepositoryEvidenceKind kind,
        string? sourceField,
        string? rawValue,
        string? metadataPackageVersion = null)
    {
        Kind = kind;
        SourceField = sourceField;
        RawValue = Sanitize(rawValue);
        MetadataPackageVersion = metadataPackageVersion;
    }

    public SourceRepositoryEvidenceKind Kind { get; }

    public string? SourceField { get; }

    public string? RawValue { get; }

    public string? MetadataPackageVersion { get; }

    internal static string? Sanitize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string sanitized = UriCredentials.Replace(value, "${scheme}");
        sanitized = EncodedUriCredentials.Replace(sanitized, "${scheme}");
        return SensitiveQueryValues.Replace(sanitized, "${name}<redacted>");
    }
}
