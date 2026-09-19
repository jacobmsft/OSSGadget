// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Model.SourceMapping;

/// <summary>
/// Records a condition that consumers may need to consider when using a candidate.
/// </summary>
public sealed record SourceRepositoryWarning
{
    public SourceRepositoryWarning(SourceRepositoryWarningKind kind, string? detail = null)
    {
        Kind = kind;
        Detail = SourceRepositoryEvidence.SanitizeSensitiveText(detail);
    }

    public SourceRepositoryWarningKind Kind { get; }

    public string? Detail { get; }
}
