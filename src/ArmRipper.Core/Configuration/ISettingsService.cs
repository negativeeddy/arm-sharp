namespace ArmRipper.Core.Configuration;

/// <summary>
/// Central, DI-friendly access point for DB-first settings.
/// All runtime reads and writes go through this service so the DB is always the
/// source of truth (DB overrides on top of file/code defaults).
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Gets the effective settings: file/code defaults overridden by any DB
    /// deltas stored in the <c>ripper_settings</c> row. DB always wins.
    /// </summary>
    Task<ArmSettings> GetEffectiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Merges specific key-value overrides into the DB delta. A <c>null</c>
    /// value removes the key (back to default). Keys not present are untouched.
    /// </summary>
    Task MergeAsync(Dictionary<string, string?> fields, CancellationToken ct = default);

    /// <summary>
    /// Clears ALL DB overrides, resetting every setting to its file/code default.
    /// This is what "Reset to defaults" does.
    /// </summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>Ensures the <c>ripper_settings</c> row exists (empty delta on a fresh install).</summary>
    Task EnsureSeededAsync(CancellationToken ct = default);

    /// <summary>
    /// Migrates a legacy full-snapshot row (from older builds) to a delta of real
    /// overrides. Idempotent — already-delta rows are left untouched.
    /// </summary>
    Task NormalizeLegacyRowAsync(CancellationToken ct = default);
}
