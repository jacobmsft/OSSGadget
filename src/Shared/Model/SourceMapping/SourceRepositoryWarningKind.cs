// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Model.SourceMapping;

/// <summary>
/// Categorizes a source repository candidate warning without coupling it to scoring policy.
/// </summary>
public enum SourceRepositoryWarningKind
{
    Unknown,
    Ambiguous,
    Fork,
    Upstream,
    RootPath,
    Malformed,
    OperationalFailure
}
