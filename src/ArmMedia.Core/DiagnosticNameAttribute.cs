namespace ArmMedia.Core;

/// <summary>
/// Marks a class with a diagnostic name that dotnet-monitor can use to filter
/// log output by category. The <see cref="Name"/> must match the logger category
/// passed to <c>ILoggerFactory.CreateLogger</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class DiagnosticNameAttribute : Attribute
{
    /// <summary>The diagnostic name / logger category.</summary>
    public string Name { get; }

    /// <summary>Creates the attribute with the given diagnostic name.</summary>
    public DiagnosticNameAttribute(string name)
    {
        Name = name;
    }
}
