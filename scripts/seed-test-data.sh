#!/bin/bash
set -e
DB="/workspaces/arm-sharp/src/ArmRipper.WebUi/data/arm.db"

python3 /dev/stdin << PYEOF
import sqlite3

conn = sqlite3.connect("$DB")
conn.execute("PRAGMA foreign_keys = OFF;")
cur = conn.cursor()

for t in ['tracks', 'config', 'jobs']:
    cur.execute(f"DELETE FROM {t}")

job_cols = ['Id','ArmVersion','CrcId','LogFile','StartTime','StopTime','JobLength',
    'Status','Stage','NoOfTitles',
    'Title','TitleAuto','TitleManual',
    'Year','YearAuto','YearManual',
    'VideoType','VideoTypeAuto','VideoTypeManual',
    'ImdbId','ImdbIdAuto','ImdbIdManual',
    'PosterUrl','PosterUrlAuto','PosterUrlManual',
    'DevPath','MountPoint','HasNiceTitle','Errors','DiscType','Label','Path','Ejected',
    'Pid','PidHash','IsIso','ManualStart','ManualMode','HasTrack99']
job_sql = f"INSERT INTO jobs ({','.join(job_cols)}) VALUES ({','.join(['?']*len(job_cols))})"

config_cols = ['SkipTranscode','MainFeature','UseFfmpeg','ManualWait','AllowDuplicates','Prevent99',
    'GetVideoTitle','GetAudioTitle','AutoEject','DelRawFiles',
    'RawPath','TranscodePath','CompletedPath','LogPath','InstallPath','ExtrasSub',
    'RipMethod','MkvArgs','MinLength','MaxLength',
    'HbPresetDvd','HbPresetBd','HbArgsDvd','HbArgsBd','DestExt',
    'FfmpegCli','FfmpegPreFileArgs','FfmpegPostFileArgs',
    'NotifyRip','NotifyTranscode','PbKey','IftttKey','PoUserKey','BashScript','JsonUrl','Apprise',
    'OmdbApiKey','TmdbApiKey','ArmApiKey','MetadataProvider',
    'WebServerIp','WebServerPort','UiBaseUrl',
    'EmbyRefresh','EmbyServer','EmbyPort','EmbyApiKey',
    'MaxConcurrentTranscodes','MaxConcurrentMakemkvInfo']
all_config_cols = ['JobId'] + config_cols
config_sql = f"INSERT INTO config ({','.join(all_config_cols)}) VALUES ({','.join(['?']*len(all_config_cols))})"

track_cols = ['JobId','TrackNumber','Length','AspectRatio','Fps','MainFeature',
    'BaseName','FileName','OrigFileName','NewFileName','Ripped','Status','Error','Source','Process','Chapters','FileSize']
track_sql = f"INSERT INTO tracks ({','.join(track_cols)}) VALUES ({','.join(['?']*len(track_cols))})"

# ─── Job 1: Active rip ────────────────────────────────────────────────
cur.execute(job_sql, (
    1,'1.0.0','ABCD1234','rip_20260531_001.log','2026-05-31 10:00:00',None,None,
    'Active','Ripping',12,
    'The Matrix','The Matrix',None,
    '1999','1999',None,
    'movie','movie',None,
    'tt0133093','tt0133093',None,
    'https://m.media-amazon.com/images/M/MV5BNzQzOTk3OTAtNDQ0Zi00ZTVkLWI0MTEtMDllZjNkYzNjNTc4L2ltYWdlXkEyXkFqcGdeQXVyNjU0OTQ0OTY@._V1_SX300.jpg',
    'https://m.media-amazon.com/images/M/MV5BNzQzOTk3OTAtNDQ0Zi00ZTVkLWI0MTEtMDllZjNkYzNjNTc4L2ltYWdlXkEyXkFqcGdeQXVyNjU0OTQ0OTY@._V1_SX300.jpg',None,
    '/dev/sr0','/mnt/rip/the_matrix',1,None,'Bluray','THE_MATRIX','/home/arm/media/completed/The Matrix (1999)',0,
    12345,'abc123',0,0,0,0))
cur.execute(config_sql, (
    1,
    0,0,0,0,1,1,1,1,1,0,
    '/home/arm/media/raw','/home/arm/media/transcode','/home/arm/media/completed','/home/arm/logs','/opt/arm','Extras',
    'makemkv','',600,9999,
    'Fast 1080p30','Fast 1080p30','','','mkv',
    'ffmpeg','','',
    1,1,'','','','','','',
    'test_key','','','omdb',
    '0.0.0.0',8080,'http://localhost:8080',
    0,'',8096,'',
    1,1))
for t in [
    (1,'1',8160,'16:9',23.976,1,'matrix_t00.mkv','matrix_t00.mkv',None,None,0,'Completed',None,'main',1,32,7990147488),
    (1,'2',600,'16:9',23.976,0,'matrix_t01.mkv','matrix_t01.mkv',None,None,0,'Completed',None,'extra',1,8,524288000),
    (1,'3',120,'16:9',23.976,0,'matrix_t02.mkv','matrix_t02.mkv',None,None,0,'Completed',None,'extra',1,1,104857600),
]:
    cur.execute(track_sql, t)

# ─── Job 2: Completed success ─────────────────────────────────────────
cur.execute(job_sql, (
    2,'1.0.0','5678EFGH','rip_20260530_001.log','2026-05-30 14:00:00','2026-05-30 16:30:00','2h 30m',
    'Success','Completed',8,
    'Inception','Inception',None,
    '2010','2010',None,
    'movie','movie',None,
    'tt1375666','tt1375666',None,
    'https://m.media-amazon.com/images/M/MV5BMjAxMzY3NjcxNF5BMl5BanBnXkFtZTcwNTI5OTM0Mw@@._V1_SX300.jpg',
    'https://m.media-amazon.com/images/M/MV5BMjAxMzY3NjcxNF5BMl5BanBnXkFtZTcwNTI5OTM0Mw@@._V1_SX300.jpg',None,
    '/dev/sr0','/mnt/rip/inception',1,None,'Dvd','INCEPTION','/home/arm/media/completed/Inception (2010)',1,
    12346,'def456',0,0,0,0))
cur.execute(config_sql, (
    2,
    0,1,0,0,0,1,1,1,1,1,
    '/home/arm/media/raw','/home/arm/media/transcode','/home/arm/media/completed','/home/arm/logs','/opt/arm','Extras',
    'makemkv','',600,9999,
    'Fast 1080p30','Fast 1080p30','','','mkv',
    'ffmpeg','','',
    1,1,'','','','','','',
    'test_key','','','omdb',
    '0.0.0.0',8080,'http://localhost:8080',
    0,'',8096,'',
    1,1))
cur.execute(track_sql, (
    2,'1',8880,'16:9',23.976,1,'inception_t00.mkv','inception_t00.mkv',None,None,1,'Completed',None,'main',1,48,9996534784))

# ─── Job 3: Failed with custom title ──────────────────────────────────
cur.execute(job_sql, (
    3,'1.0.0','9012IJKL','rip_20260529_001.log','2026-05-29 08:00:00','2026-05-29 08:45:00','45m',
    'Failure','RipError',5,
    'My Custom Movie Title','Pulp Fiction','My Custom Movie Title',
    '1994','1994',None,
    'movie','movie',None,
    'tt0110912','tt0110912',None,
    'https://m.media-amazon.com/images/M/MV5BNGNhMDIzZTUtNTBlZi00MTRlLWFjM2ItYzViMjE3YzI5MjljXkEyXkFqcGc@._V1_SX300.jpg',
    'https://m.media-amazon.com/images/M/MV5BNGNhMDIzZTUtNTBlZi00MTRlLWFjM2ItYzViMjE3YzI5MjljXkEyXkFqcGc@._V1_SX300.jpg',None,
    '/dev/sr1','/mnt/rip/pulp_fiction',1,
    'MakeMKV failed: volume key not found, disc may be encrypted with unsupported protection.',
    'Bluray','PULP_FICTION','/home/arm/media/completed/My Custom Movie Title (1994)',1,
    None,None,0,1,1,1))
cur.execute(config_sql, (
    3,
    0,1,0,1,0,1,1,1,0,0,
    '/home/arm/media/raw','/home/arm/media/transcode','/home/arm/media/completed','/home/arm/logs','/opt/arm','Extras',
    'handbrake','',300,6000,
    'Fast 1080p30','Fast 1080p30','','','mp4',
    'ffmpeg','','',
    0,0,'','','','','','',
    'test_key','','','omdb',
    '0.0.0.0',8080,'http://localhost:8080',
    0,'',8096,'',
    1,1))
for t in [
    (3,'1',10200,'16:9',23.976,1,'pulp_t00.mkv','pulp_t00.mkv',None,None,1,'Completed',None,'main',1,40,8100503552),
    (3,'2',900,'4:3',29.97,0,'pulp_t01.mkv','pulp_t01.mkv',None,None,0,'Completed',None,'extra',1,5,314572800),
]:
    cur.execute(track_sql, t)

# ─── Job 4: Active transcode ──────────────────────────────────────────
cur.execute(job_sql, (
    4,'1.0.0','3456MNOP','rip_20260531_002.log','2026-05-31 12:00:00',None,None,
    'TranscodeActive','Transcoding',3,
    'Interstellar','Interstellar',None,
    '2014','2014',None,
    'movie','movie',None,
    'tt0816692','tt0816692',None,
    'https://m.media-amazon.com/images/M/MV5BZjdkOTU3MDktN2IxOS00OGEyLWFmMjktY2FiMmZkNWIyODZiXkEyXkFqcGc@._V1_SX300.jpg',
    'https://m.media-amazon.com/images/M/MV5BZjdkOTU3MDktN2IxOS00OGEyLWFmMjktY2FiMmZkNWIyODZiXkEyXkFqcGc@._V1_SX300.jpg',None,
    '/dev/sr0','/mnt/rip/interstellar',1,None,'Bluray','INTERSTELLAR','/home/arm/media/completed/Interstellar (2014)',0,
    12347,'ghi789',0,0,0,0))
cur.execute(config_sql, (
    4,
    0,0,0,0,1,1,1,1,1,0,
    '/home/arm/media/raw','/home/arm/media/transcode','/home/arm/media/completed','/home/arm/logs','/opt/arm','Extras',
    'makemkv','',600,9999,
    'Fast 1080p30','Fast 1080p30','','','mkv',
    'ffmpeg','','',
    1,1,'','','','','','',
    'test_key','','','omdb',
    '0.0.0.0',8080,'http://localhost:8080',
    0,'',8096,'',
    1,1))
for t in [
    (4,'1',10140,'16:9',23.976,1,'interstellar_t00.mkv','interstellar_t00.mkv',None,None,1,'Completed',None,'main',1,44,10418943180),
    (4,'2',3060,'16:9',23.976,0,'interstellar_t01.mkv','interstellar_t01.mkv',None,None,0,'Completed',None,'extra',1,18,1572864000),
    (4,'3',150,'16:9',23.976,0,'interstellar_t02.mkv','interstellar_t02.mkv',None,None,0,'Completed',None,'extra',1,1,83886080),
]:
    cur.execute(track_sql, t)

conn.commit()

cur.execute("SELECT Id, Title, Status, Stage FROM jobs ORDER BY Id")
print("Seeded jobs:")
for j in cur.fetchall():
    print(f"  Id={j[0]}, Title={j[1]}, Status={j[2]}, Stage={j[3]}")
cur.execute("SELECT COUNT(*) FROM tracks"); print(f"Tracks: {cur.fetchone()[0]}")
cur.execute("SELECT COUNT(*) FROM config"); print(f"Configs: {cur.fetchone()[0]}")
cur.execute("SELECT COUNT(*) FROM notifications"); print(f"Notifications: {cur.fetchone()[0]}")
conn.close()
PYEOF
