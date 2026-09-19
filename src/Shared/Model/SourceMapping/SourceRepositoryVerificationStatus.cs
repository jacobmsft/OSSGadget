// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

namespace Microsoft.CST.OpenSource.Model.SourceMapping;

/// <summary>
/// Describes whether a source repository candidate has been verified.
/// </summary>
public enum SourceRepositoryVerificationStatus
{
    NotAttempted,
    Verified,
    Rejected,
    Inconclusive
}
