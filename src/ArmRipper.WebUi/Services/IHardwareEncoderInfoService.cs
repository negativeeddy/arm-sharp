using System.Collections.Generic;
using System.Threading.Tasks;

namespace ArmRipper.WebUi.Services;

public interface IHardwareEncoderInfoService
{
    Task<IReadOnlyList<Dictionary<string, object>>> GetHardwareEncoderInfoAsync(bool includeDetailedNvidiaStats = false);

    /// <summary>Returns available NVIDIA GPUs as (index, name) pairs for the GPU selection dropdown.</summary>
    Task<IReadOnlyList<(int Index, string Name)>> GetGpuListAsync();
}
