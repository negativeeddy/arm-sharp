using System.Text.Json.Serialization;

namespace ArmRipper.Core.Models;

/// <summary>Content category for a video — determines post-processing, naming, and episode detection.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoContentType
{
    Unknown,
    Movie,
    Series,
    Tv,
    Episode
}
