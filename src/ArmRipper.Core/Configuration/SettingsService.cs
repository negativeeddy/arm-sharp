using ArmRipper.Core.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace ArmRipper.Core.Configuration;

/// <inheritdoc />
public sealed class SettingsService(
    ArmDbContext db,
    IOptions<ArmSettings> fileSettings) : ISettingsService
{
    /// <inheritdoc />
    public Task<ArmSettings> GetEffectiveAsync(CancellationToken ct = default)
        => SettingsHelper.GetEffectiveSettingsAsync(db, fileSettings.Value, ct);

    /// <inheritdoc />
    public Task MergeAsync(Dictionary<string, string?> fields, CancellationToken ct = default)
        => SettingsHelper.MergeIntoDbAsync(db, fields, ct);

    /// <inheritdoc />
    public Task ClearAllAsync(CancellationToken ct = default)
        => SettingsHelper.ClearAllAsync(db, ct);

    /// <inheritdoc />
    public Task EnsureSeededAsync(CancellationToken ct = default)
        => SettingsHelper.EnsureSeededAsync(db, ct);

    /// <inheritdoc />
    public Task NormalizeLegacyRowAsync(CancellationToken ct = default)
        => SettingsHelper.NormalizeLegacyRowAsync(db, fileSettings.Value, ct);
}
