namespace ArmRipper.Core.Rip;

[Flags]
public enum MakeMkvOutputType
{
    Drv = 1,
    Msg = 2,
    CinFo = 4,
    SinFo = 8,
    TCount = 16,
    TInfo = 32,
    PrgV = 64,
    PrgC = 128,
    PrgT = 256
}

public enum DriveVisible
{
    Empty = 0,
    Open = 1,
    Loaded = 2,
    Loading = 3,
    NotAttached = 256
}

public enum DriveType
{
    Cd = 0,
    Dvd = 1,
    BdType1 = 12,
    BdType2 = 28
}

public enum MessageId
{
    LibMkvTrace = 1002,
    VersionInfo = 1005,
    GenericInfo = 1011,
    ReadError = 2003,
    WriteError = 2019,
    ComplexMultiplex = 3024,
    TitleSkipped = 3025,
    TitleAdded = 3028,
    SubtitleSkippedIdentical = 3030,
    AudioSkippedEmpty = 3034,
    FileAdded = 3307,
    RipTitleError = 5003,
    RipCompleted = 5004,
    RipDiscOpenError = 5010,
    RipSummaryBefore = 5014,
    RipSummaryAfter = 5037,
    EvaluationExpiredInfo = 5052,
    EvaluationExpiredAppTooOld = 5021,
    EvaluationExpiredShareware = 5055,
    RipBackupFailedPre = 5096,
    RipBackupFailed = 5080
}

public record MakeMkvMessage(int Code, int Flags, int Count, string Message, string Sprintf, string[] Params);

public record MakeMkvErrorMessage(int Code, int Flags, int Count, string Message, string Sprintf, string[] Params, string Error)
    : MakeMkvMessage(Code, Flags, Count, Message, Sprintf, Params);

public record Titles(int Count);

public record CinFo(int Id, int Code, string Value);

public record TInfo(int Tid, int Id, int Code, string Value) : CinFo(Id, Code, Value);

public record SinFo(int Tid, int Sid, int Id, int Code, string Value) : TInfo(Tid, Id, Code, Value);

public record DriveInfo(int Index, bool Visible, bool Enabled, int Flags, string Info, string Disc, string Mount)
{
    public bool Loaded { get; init; }
    public bool IsOpen { get; init; }
    public bool Attached { get; init; } = true;
    public bool MediaCd { get; init; }
    public bool MediaDvd { get; init; }
    public bool MediaBd { get; init; }
}
