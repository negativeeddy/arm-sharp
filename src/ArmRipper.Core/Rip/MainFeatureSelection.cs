using ArmRipper.Core.Models;

namespace ArmRipper.Core.Rip;

/// <summary>
/// Outcome of the automatic main-feature selection.
/// </summary>
/// <param name="Selected">The track that was selected, or null when there were no candidates.</param>
/// <param name="Candidates">Every track that fell inside the near-tie window, in selection order.</param>
/// <param name="TieBreakReason">
/// Which criterion separated the winner from the runner-up (for example
/// <c>file size</c>). Empty when there was nothing to break a tie against.
/// </param>
internal sealed record MainFeatureSelection(
    Track? Selected,
    IReadOnlyList<Track> Candidates,
    string TieBreakReason)
{
    /// <summary>
    /// True when more than one track was a plausible main feature, i.e. the
    /// selection was a near-tie and the user may want to pick a different one.
    /// </summary>
    public bool IsAmbiguous => Selected is not null && Candidates.Count > 1;
}
