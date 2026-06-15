using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArmRipper.Core.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "disc_metadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    VolumeLabel = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SectorCount = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_disc_metadata", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ArmVersion = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CrcId = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    LogFile = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StopTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    JobLength = table.Column<string>(type: "TEXT", maxLength: 12, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    NoOfTitles = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TitleAuto = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TitleManual = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Year = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    YearAuto = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    YearManual = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    VideoType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    VideoTypeAuto = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    VideoTypeManual = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    ImdbIdAuto = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    ImdbIdManual = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PosterUrlAuto = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PosterUrlManual = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DevPath = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    MountPoint = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    HasNiceTitle = table.Column<bool>(type: "INTEGER", nullable: false),
                    Errors = table.Column<string>(type: "TEXT", nullable: true),
                    Warnings = table.Column<string>(type: "TEXT", nullable: true),
                    DiscType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Ejected = table.Column<bool>(type: "INTEGER", nullable: false),
                    Pid = table.Column<int>(type: "INTEGER", nullable: true),
                    PidHash = table.Column<string>(type: "TEXT", nullable: true),
                    IsIso = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManualStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManualMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasTrack99 = table.Column<bool>(type: "INTEGER", nullable: false),
                    DiscFingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MakeMkvProgress = table.Column<int>(type: "INTEGER", nullable: true),
                    TranscodeProgress = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    Read = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ripper_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ripper_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "system_drives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SerialId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Maker = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Serial = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Mount = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Firmware = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Mdisc = table.Column<int>(type: "INTEGER", nullable: true),
                    ReadCd = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReadDvd = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReadBd = table.Column<bool>(type: "INTEGER", nullable: false),
                    DriveMode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Stale = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    JobIdCurrent = table.Column<int>(type: "INTEGER", nullable: true),
                    JobIdPrevious = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_drives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "system_info",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Hostname = table.Column<string>(type: "TEXT", nullable: false),
                    CpuInfo = table.Column<string>(type: "TEXT", nullable: true),
                    RamInfo = table.Column<string>(type: "TEXT", nullable: true),
                    OsInfo = table.Column<string>(type: "TEXT", nullable: true),
                    ArmVersion = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_info", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ui_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Theme = table.Column<string>(type: "TEXT", nullable: true),
                    RefreshRate = table.Column<int>(type: "INTEGER", nullable: true),
                    IconStyle = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ui_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "disc_tracks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DiscMetadataId = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackNumber = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    Length = table.Column<int>(type: "INTEGER", nullable: true),
                    Chapters = table.Column<int>(type: "INTEGER", nullable: true),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: true),
                    AspectRatio = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Fps = table.Column<double>(type: "REAL", nullable: true),
                    Resolution = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_disc_tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_disc_tracks_disc_metadata_DiscMetadataId",
                        column: x => x.DiscMetadataId,
                        principalTable: "disc_metadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "config",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    SkipTranscode = table.Column<bool>(type: "INTEGER", nullable: false),
                    MainFeature = table.Column<bool>(type: "INTEGER", nullable: false),
                    UseFfmpeg = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManualWait = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowDuplicates = table.Column<bool>(type: "INTEGER", nullable: false),
                    Prevent99 = table.Column<bool>(type: "INTEGER", nullable: false),
                    GetVideoTitle = table.Column<bool>(type: "INTEGER", nullable: false),
                    GetAudioTitle = table.Column<string>(type: "TEXT", nullable: true),
                    AutoEject = table.Column<bool>(type: "INTEGER", nullable: false),
                    DelRawFiles = table.Column<bool>(type: "INTEGER", nullable: false),
                    RawPath = table.Column<string>(type: "TEXT", nullable: true),
                    TranscodePath = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedPath = table.Column<string>(type: "TEXT", nullable: true),
                    LogPath = table.Column<string>(type: "TEXT", nullable: true),
                    DbFile = table.Column<string>(type: "TEXT", nullable: true),
                    InstallPath = table.Column<string>(type: "TEXT", nullable: true),
                    ExtrasSub = table.Column<string>(type: "TEXT", nullable: true),
                    RipMethod = table.Column<string>(type: "TEXT", nullable: true),
                    MkvArgs = table.Column<string>(type: "TEXT", nullable: true),
                    MinLength = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxLength = table.Column<int>(type: "INTEGER", nullable: true),
                    HbPresetDvd = table.Column<string>(type: "TEXT", nullable: true),
                    HbPresetBd = table.Column<string>(type: "TEXT", nullable: true),
                    HbArgsDvd = table.Column<string>(type: "TEXT", nullable: true),
                    HbArgsBd = table.Column<string>(type: "TEXT", nullable: true),
                    DestExt = table.Column<string>(type: "TEXT", nullable: true),
                    FfmpegCli = table.Column<string>(type: "TEXT", nullable: true),
                    FfmpegPreFileArgs = table.Column<string>(type: "TEXT", nullable: true),
                    FfmpegPostFileArgs = table.Column<string>(type: "TEXT", nullable: true),
                    NotifyRip = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyTranscode = table.Column<bool>(type: "INTEGER", nullable: false),
                    PbKey = table.Column<string>(type: "TEXT", nullable: true),
                    IftttKey = table.Column<string>(type: "TEXT", nullable: true),
                    PoUserKey = table.Column<string>(type: "TEXT", nullable: true),
                    BashScript = table.Column<string>(type: "TEXT", nullable: true),
                    JsonUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Apprise = table.Column<string>(type: "TEXT", nullable: true),
                    OmdbApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    TmdbApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    ArmApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    MetadataProvider = table.Column<string>(type: "TEXT", nullable: true),
                    WebServerIp = table.Column<string>(type: "TEXT", nullable: true),
                    WebServerPort = table.Column<int>(type: "INTEGER", nullable: true),
                    UiBaseUrl = table.Column<string>(type: "TEXT", nullable: true),
                    EmbyRefresh = table.Column<bool>(type: "INTEGER", nullable: false),
                    EmbyServer = table.Column<string>(type: "TEXT", nullable: true),
                    EmbyPort = table.Column<int>(type: "INTEGER", nullable: true),
                    EmbyApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    MaxConcurrentTranscodes = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxConcurrentMakemkvInfo = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_config", x => x.Id);
                    table.ForeignKey(
                        name: "FK_config_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tracks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackNumber = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Length = table.Column<int>(type: "INTEGER", nullable: true),
                    AspectRatio = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Fps = table.Column<double>(type: "REAL", nullable: true),
                    MainFeature = table.Column<bool>(type: "INTEGER", nullable: false),
                    BaseName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    OrigFileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NewFileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Ripped = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Process = table.Column<bool>(type: "INTEGER", nullable: false),
                    Chapters = table.Column<int>(type: "INTEGER", nullable: true),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracks_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "disc_track_streams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DiscTrackId = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamType = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    LanguageCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Codec = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ChannelCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Forced = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_disc_track_streams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_disc_track_streams_disc_tracks_DiscTrackId",
                        column: x => x.DiscTrackId,
                        principalTable: "disc_tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_config_JobId",
                table: "config",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_disc_metadata_Fingerprint",
                table: "disc_metadata",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_disc_track_streams_DiscTrackId",
                table: "disc_track_streams",
                column: "DiscTrackId");

            migrationBuilder.CreateIndex(
                name: "IX_disc_tracks_DiscMetadataId",
                table: "disc_tracks",
                column: "DiscMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_tracks_JobId",
                table: "tracks",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_users_Username",
                table: "users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "config");

            migrationBuilder.DropTable(
                name: "disc_track_streams");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "ripper_settings");

            migrationBuilder.DropTable(
                name: "system_drives");

            migrationBuilder.DropTable(
                name: "system_info");

            migrationBuilder.DropTable(
                name: "tracks");

            migrationBuilder.DropTable(
                name: "ui_settings");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "disc_tracks");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "disc_metadata");
        }
    }
}
