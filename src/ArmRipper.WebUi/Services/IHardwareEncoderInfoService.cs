using System.Collections.Generic;
using System.Threading.Tasks;

namespace ArmRipper.WebUi.Services;

public interface IHardwareEncoderInfoService
{
    Task<IReadOnlyList<Dictionary<string, object>>> GetHardwareEncoderInfoAsync(bool includeDetailedNvidiaStats = false);
}
