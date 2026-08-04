using System.Globalization;
using System.Text.Json;
using ArmRipper.Core.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ArmRipper.Core.Configuration;

/// <summary>
/// Explicit, user-initiated import of legacy ARM settings from
/// <c>/etc/arm/config/arm.yaml</c> into the DB as override deltas.
///
/// This is the replacement for the old automatic YAML overlay at startup (the
/// "drop-in ARM replacement" goal is retired). Files are seed/import-only and
/// never override existing DB values: a key already present in the DB delta is
/// left untouched.
/// </summary>
public static class ArmSettingsImporter
{
    /// <summary>Default ARM config file path. Overridable for tests.</summary>
    public const string DefaultYamlPath = "/etc/arm/config/arm.yaml";

    /// <summary>
    /// Imports typed values from the ARM YAML file into the DB, skipping any key
    /// that is already set in the DB (DB always wins). Returns a summary of the
    /// import.
    /// </summary>
    public static async Task<ImportResult> ImportFromYamlAsync(
        ArmDbContext db,
        ArmSettings fileSettings,
        string? yamlPath = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileSettings);
        var path = yamlPath ?? DefaultYamlPath;
        var fileExists = File.Exists(path);

        var yaml = ArmYamlConfigLoader.LoadYamlValues(path);
        if (yaml.Count == 0)
            return new ImportResult(0, 0, 0, path, fileExists);

        // Current DB deltas — keys already present are never overwritten (DB wins).
        var existing = await db.RipperSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        Dictionary<string, JsonElement>? existingDict = null;
        if (existing is not null && !string.IsNullOrEmpty(existing.SettingsJson))
        {
            existingDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing.SettingsJson);
        }
        existingDict ??= new Dictionary<string, JsonElement>();

        var fields = new Dictionary<string, string?>();
        var imported = 0;
        var skipped = 0;

        foreach (var (configKey, rawValue) in yaml)
        {
            if (rawValue is null)
            {
                skipped++;
                continue;
            }

            // Only ArmSettings properties are imported (the key map produces "Arm:PropName").
            var prefix = ArmSettings.SectionName + ":";
            if (!configKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var propName = configKey[prefix.Length..];

            // DB wins — never overwrite a value already set in the DB.
            if (existingDict.ContainsKey(propName))
            {
                skipped++;
                continue;
            }

            var prop = typeof(ArmSettings).GetProperty(propName);
            if (prop is null || !prop.CanWrite)
            {
                skipped++;
                continue;
            }

            try
            {
                var converted = ConvertYamlValue(rawValue, prop.PropertyType);
                if (converted is null)
                {
                    skipped++;
                    continue;
                }

                fields[propName] = JsonSerializer.Serialize(converted, prop.PropertyType);
                imported++;
            }
            catch (Exception)
            {
                // Unparseable value for this property — skip it rather than fail the whole import.
                skipped++;
            }
        }

        if (fields.Count > 0)
            await SettingsHelper.MergeIntoDbAsync(db, fields, ct);

        return new ImportResult(imported, skipped, yaml.Count, path, fileExists);
    }

    /// <summary>
    /// Converts a raw string YAML value to the given target type, honoring
    /// nullable underlying types. Throws if the value cannot be converted.
    /// </summary>
    private static object? ConvertYamlValue(string raw, Type targetType)
    {
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (t == typeof(string))
            return raw;
        if (t == typeof(bool))
            return bool.Parse(raw);
        if (t == typeof(int))
            return int.Parse(raw, CultureInfo.InvariantCulture);
        if (t == typeof(double))
            return double.Parse(raw, CultureInfo.InvariantCulture);

        return Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
    }
}

/// <summary>Summary of an ARM settings import operation.</summary>
public readonly record struct ImportResult(
    int Imported,
    int Skipped,
    int TotalKeys,
    string Path,
    bool FileExists)
{
    public int Total => Imported + Skipped;
}
