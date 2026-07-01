using System.Text.Json;
using ArmRipper.Core.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.Core.Configuration;

/// <summary>
/// Central point for loading and persisting <see cref="ArmSettings"/>.
///
/// Precedence:
///   1. DB RipperSettings row (saved via UI) — highest priority
///   2. YAML file + appsettings.json — loaded into IOptions by startup
///   3. ArmSettings class defaults — lowest priority
///
/// Both the Settings UI and Conductor (for creating ConfigSnapshots) use this
/// helper so they always see the same effective settings.
/// </summary>
public static class SettingsHelper
{
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

            // ── Default migration: if DB has MinLength=600 (the old default before it was
            //    changed to 300 in the code), ignore the DB value so the file default takes effect.
            //    This prevents stale DB-stored defaults from overriding updated code defaults. ──
            if (dict?.TryGetValue("MinLength", out var minLenEl) == true &&
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
    /// Seeds (or overwrites) the DB RipperSettings row from the current file-based
    /// <paramref name="fileSettings"/>. Skips if the row already exists unless
    /// <paramref name="force"/> is true.
    ///
    /// Call with <c>force: true</c> when <c>ARM_RESET_SETTINGS=true</c> or when
    /// the user clicks "Reset to file defaults" in the UI.
    /// </summary>
    public static async Task SeedFromFileAsync(
        ArmDbContext db,
        ArmSettings fileSettings,
        bool force = false,
        CancellationToken ct = default)
    {
        var existing = await db.RipperSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);

        if (existing is not null && !force)
            return; // already seeded, nothing to do

        var json = JsonSerializer.Serialize(fileSettings, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        if (existing is not null)
        {
            existing.SettingsJson = json;
        }
        else
        {
            db.RipperSettings.Add(new Models.RipperSettings { SettingsJson = json });
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
