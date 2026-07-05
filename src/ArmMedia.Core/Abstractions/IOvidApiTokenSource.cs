namespace ArmMedia.Core.Abstractions;

/// <summary>
/// Resolves the OVID API token from host configuration.
/// The token is used for authenticated operations (fingerprint registration
/// and disc submission) against the OVID community database.
/// </summary>
public interface IOvidApiTokenSource
{
    /// <summary>Gets the OVID API token, or <c>null</c> if not configured.</summary>
    string? GetToken();
}
