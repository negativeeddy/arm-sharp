using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ArmRipper.Core.Infrastructure;

namespace ArmRipper.WebUi.Services;

public class HardwareEncoderInfoService(ICliProcessRunner runner) : IHardwareEncoderInfoService
{
    public async Task<IReadOnlyList<Dictionary<string, object>>> GetHardwareEncoderInfoAsync(bool includeDetailedNvidiaStats = false)
    {
        var encoders = new List<Dictionary<string, object>>();
        await AddNvidiaEncoderAsync(encoders, includeDetailedNvidiaStats);
        await AddFfmpegEncodersAsync(encoders);
        if (encoders.Count == 0)
            encoders.Add(new Dictionary<string, object> { ["available"] = false });
        return encoders;
    }

    private async Task AddNvidiaEncoderAsync(List<Dictionary<string, object>> encoders, bool includeDetailedNvidiaStats)
    {
        try
        {
            var query = includeDetailedNvidiaStats
                ? "--query-gpu=index,name,driver_version,compute_cap,utilization.encoder --format=csv,noheader"
                : "--query-gpu=index,name --format=csv,noheader";

            var gpuResult = await runner.RunAsync("nvidia-smi", query, timeoutMs: 5000);

            if (gpuResult.ExitCode != 0 || string.IsNullOrWhiteSpace(gpuResult.StdOut))
                return;

            var gpuLines = gpuResult.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var gpuLine in gpuLines)
            {
                var nv = new Dictionary<string, object>
                {
                    ["vendor"] = "NVIDIA",
                    ["type"] = "NVENC",
                    ["available"] = true
                };

                var parts = gpuLine.Split(',');
                if (parts.Length > 0) nv["index"] = parts[0].Trim();
                if (parts.Length > 1) nv["gpu"] = parts[1].Trim();
                if (includeDetailedNvidiaStats)
                {
                    if (parts.Length > 2) nv["driver"] = parts[2].Trim();
                    if (parts.Length > 3) nv["compute_cap"] = parts[3].Trim();
                    if (parts.Length > 4) nv["encoder_util"] = parts[4].Trim();
                }

                if (includeDetailedNvidiaStats && nv.TryGetValue("index", out var idx))
                {
                    var detailResult = await runner.RunAsync("nvidia-smi", $"-q -i {idx}", timeoutMs: 5000);
                    if (detailResult.ExitCode == 0)
                    {
                        var inEncoderSection = false;
                        foreach (var line in detailResult.StdOut.Split('\n'))
                        {
                            if (line.Trim() == "Encoder Stats")
                            {
                                inEncoderSection = true;
                                continue;
                            }

                            if (inEncoderSection)
                            {
                                if (string.IsNullOrWhiteSpace(line) || !line.Contains(':'))
                                {
                                    inEncoderSection = false;
                                    continue;
                                }

                                var colonIdx = line.IndexOf(':');
                                var key = line[..colonIdx].Trim();
                                var val = line[(colonIdx + 1)..].Trim();
                                switch (key)
                                {
                                    case "Active Sessions": nv["sessions"] = val; break;
                                    case "Average FPS": nv["avg_fps"] = val; break;
                                    case "Average Latency": nv["avg_latency"] = val; break;
                                }
                            }
                        }
                    }
                }

                if (includeDetailedNvidiaStats &&
                    nv.TryGetValue("compute_cap", out var cc) &&
                    cc is string cs &&
                    cs.StartsWith("6."))
                {
                    nv["note"] = "Pascal GPU - HEVC B-frames not supported (bf=0 required)";
                }

                encoders.Add(nv);
            }
        }
        catch { }
    }

    private async Task AddFfmpegEncodersAsync(List<Dictionary<string, object>> encoders)
    {
        try
        {
            var result = await runner.RunAsync("ffmpeg", "-hide_banner -encoders", timeoutMs: 10_000);
            if (result.ExitCode != 0) return;

            var seen = new HashSet<string>();
            foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("V")) continue;

                var match = Regex.Match(trimmed, @"\s(\w+)$");
                if (!match.Success) continue;

                var encoderName = match.Groups[1].Value;
                var codec = encoderName.Split('_')[0];

                if (encoderName.EndsWith("_nvenc") || encoderName == "nvenc" || encoderName.EndsWith("_nvenc_hevc"))
                {
                    if (!seen.Add("nvidia")) continue;
                    AddOrUpdateFfmpegEncoder(encoders, "NVIDIA", "NVENC", codec);
                }
                else if (encoderName.EndsWith("_qsv"))
                {
                    if (!seen.Add("intel")) continue;
                    AddOrUpdateFfmpegEncoder(encoders, "Intel", "QuickSync", codec);
                }
                else if (encoderName.EndsWith("_amf"))
                {
                    if (!seen.Add("amd")) continue;
                    AddOrUpdateFfmpegEncoder(encoders, "AMD", "AMF", codec);
                }
                else if (encoderName.EndsWith("_vaapi"))
                {
                    if (!seen.Add("vaapi")) continue;
                    AddOrUpdateFfmpegEncoder(encoders, "VA-API", "VA-API", codec);
                }
            }
        }
        catch { }
    }

    private static void AddOrUpdateFfmpegEncoder(
        List<Dictionary<string, object>> encoders,
        string vendor,
        string type,
        string codec)
    {
        var existing = encoders.FirstOrDefault(e =>
            e.TryGetValue("type", out var t) && t is string ts && ts == type);

        if (existing is null)
        {
            existing = new Dictionary<string, object>
            {
                ["vendor"] = vendor,
                ["type"] = type,
                ["available"] = true,
                ["codecs"] = new List<string>()
            };
            encoders.Add(existing);
        }

        if (existing["codecs"] is List<string> codecs && !codecs.Contains(codec))
            codecs.Add(codec);
    }
}
