using System.Text.Json;
using ArmRipper.Core.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.Core.Configuration;

/// <summary>
/// Central point for loading and persisting <see cref="ArmSettings"/>.
///
/// DB-first precedence:
///   1. DB RipperSettings row (deltas saved via UI) — highest priority
///   2. File defaults (appsettings.json + class defaults) — loaded into IOptions by startup
///
/// The DB row stores ONLY user overrides (a JSON delta), never a full snapshot.
/// If the DB has no value for a key, the file/code default applies automatically.
/// Both the Settings UI and Conductor (for creating ConfigSnapshots) use this
/// helper so they always see the same effective settings.
/// </summary>
public static class SettingsHelper
{
    /// <summary>
    /// Rows with at least this many keys are treated as legacy full snapshots
    /// (a serialized <see cref="ArmSettings"/>) rather than deltas. Only full
    /// snapshots are normalized; small delta rows are never touched.
    /// </summary>
    private const int LegacySnapshotMinKeyCount = 25;

    /// <summary>
    /// Maps backward-compatible alias property names to their canonical names.
    /// Aliases are never persisted to the DB; only the canonical names are stored.
    /// During loading, any legacy alias keys in the DB are skipped.
    /// </summary>
    private static readonly Dictionary<string, string> AliasToCanonical = new()
    {
        ["DeleteRawFiles"] = "DelRawFiles",
        ["PreventTrack99"] = "Prevent99",
        ["AudioMetadataProvider"] = "GetAudioTitle",
    };

    /// <summary>
    /// Returns the merged effective settings: file-based defaults overridden by
    /// any values stored in the DB RipperSettings row.
    /// </summary>
    public static async Task<ArmSettings> GetEffectiveSettingsAsync(
        ArmDbContext db,
        ArmSettings fileSettings,
        CancellationToken ct = default)
    {
        // Start from file defaults
        var merged = new ArmSettings();
        foreach (var prop in typeof(ArmSettings).GetProperties())
        {
            if (prop.CanWrite)
                prop.SetValue(merged, prop.GetValue(fileSettings));
        }

        // Override with DB-stored values
        var saved = await db.RipperSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (saved is not null)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(saved.SettingsJson);
            if (dict is not null)
            {
                foreach (var (key, value) in dict)
                {
                    // Skip null JSON values — safe-guard for optional fields stored as null
                    if (value.ValueKind == JsonValueKind.Null)
                        continue;

                    // Skip legacy alias keys — only canonical property names are used
                    if (AliasToCanonical.ContainsKey(key))
                        continue;

                    var prop = typeof(ArmSettings).GetProperty(key);
                    if (prop is not null && prop.CanWrite)
                    {
                        var converted = JsonSerializer.Deserialize(value.GetRawText(), prop.PropertyType);
                        if (converted is not null)
                            prop.SetValue(merged, converted);
                    }
                }
            }

            // ── Legacy-snapshot safety net: an old full-snapshot row may carry
            //    MinLength=600 (the old code default before it changed to 300). Only treat
            //    it as stale when the row is a legacy full snapshot — a small delta row
            //    holding 600 is a genuine user override and is respected verbatim.
            //    NormalizeLegacyRowAsync converts full snapshots to deltas at startup. ──
            if (dict?.Count >= LegacySnapshotMinKeyCount &&
                dict.TryGetValue("MinLength", out var minLenEl) &&
                minLenEl.ValueKind == JsonValueKind.Number &&
                minLenEl.GetInt32() == 600 &&
                fileSettings.MinLength != 600)
            {
                merged.MinLength = fileSettings.MinLength;
            }
        }

        return merged;
    }

    /// <summary>
    /// Ensures a RipperSettings row exists. On a fresh install it creates an empty
    /// delta (<c>"{}"</c>). It never copies file values into the DB — the DB holds
    /// only user overrides, so file/code defaults apply automatically.
    /// </summary>
    public static async Task EnsureSeededAsync(ArmDbContext db, CancellationToken ct = default)
    {
        if (await db.RipperSettings.AnyAsync(ct))
            return;

        db.RipperSettings.Add(new Models.RipperSettings { SettingsJson = "{}" });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One-time, idempotent migration that converts a legacy full-snapshot row
    /// (a serialized <see cref="ArmSettings"/> with many keys) into a delta row
    /// containing only keys that differ from the current file defaults — i.e. real
    /// user overrides. Rows that are already deltas are left untouched.
    /// </summary>
    public static async Task NormalizeLegacyRowAsync(
        ArmDbContext db,
        ArmSettings fileSettings,
        CancellationToken ct = default)
    {
        var row = await db.RipperSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (row is null || string.IsNullOrEmpty(row.SettingsJson))
            return;

        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.SettingsJson);
        if (dict is null || dict.Count < LegacySnapshotMinKeyCount)
            return; // already delta-style (or empty) — nothing to normalize

        var delta = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in dict)
        {
            if (value.ValueKind == JsonValueKind.Null)
                continue;

            // Skip legacy alias keys — only canonical property names are stored.
            if (AliasToCanonical.ContainsKey(key))
                continue;

            var prop = typeof(ArmSettings).GetProperty(key);
            if (prop is null || !prop.CanWrite)
                continue;

            // Stale historical default (MinLength was 600 before the code default
            // changed to 300) — drop it so the current code default applies.
            if (key == nameof(ArmSettings.MinLength) &&
                value.ValueKind == JsonValueKind.Number &&
                value.GetInt32() == 600 &&
                fileSettings.MinLength != 600)
                continue;

            // If the stored value equals the current file default, it is not an
            // override — drop it so the file/code default applies automatically.
            var dbTyped = JsonSerializer.Deserialize(value.GetRawText(), prop.PropertyType);
            if (dbTyped is not null && Equals(dbTyped, prop.GetValue(fileSettings)))
                continue;

            delta[key] = value;
        }

        row.SettingsJson = JsonSerializer.Serialize(delta, new JsonSerializerOptions { WriteIndented = false });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Clears ALL DB overrides, resetting every setting back to its file/code
    /// default. Does not touch file config. This is what "Reset to defaults" does.
    /// </summary>
    public static async Task ClearAllAsync(ArmDbContext db, CancellationToken ct = default)
    {
        var row = await db.RipperSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            db.RipperSettings.Add(new Models.RipperSettings { SettingsJson = "{}" });
        }
        else
        {
            row.SettingsJson = "{}";
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Updates the DB RipperSettings row by merging specific key-value pairs into
    /// the existing JSON. Fields not in <paramref name="fields"/> are left unchanged.
    /// This allows different UI tabs to update their own settings without clobbering
    /// settings from other tabs.
    /// </summary>
    /// <param name="fields">Dictionary of ArmSettings property names to their JSON-serialized values.</param>
    public static async Task MergeIntoDbAsync(
        ArmDbContext db,
        Dictionary<string, string?> fields,
        CancellationToken ct = default)
    {
        var existingRow = await db.RipperSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        Dictionary<string, JsonElement>? existingDict = null;

        if (existingRow is not null && !string.IsNullOrEmpty(existingRow.SettingsJson))
        {
            existingDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingRow.SettingsJson);
        }

        existingDict ??= [];

        // Apply the incoming fields (serialize each value to preserve type)
        foreach (var (key, value) in fields)
        {
            if (value is null)
            {
                existingDict.Remove(key);
            }
            else
            {
                // Parse the stringified value back to a typed JsonElement
                using var doc = JsonDocument.Parse(value);
                // Skip null values — they represent "not provided" for optional fields
                if (doc.RootElement.ValueKind == JsonValueKind.Null)
                    continue;
                existingDict[key] = doc.RootElement.Clone();
            }

            // Remove any legacy alias key that points to this canonical key
            foreach (var (alias, canonical) in AliasToCanonical)
            {
                if (canonical == key)
                    existingDict.Remove(alias);
            }
        }

        var json = JsonSerializer.Serialize(existingDict, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        if (existingRow is not null)
        {
            existingRow.SettingsJson = json;
        }
        else
        {
            db.RipperSettings.Add(new Models.RipperSettings { SettingsJson = json });
        }

        await db.SaveChangesAsync(ct);
    }
}
