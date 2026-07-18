# Startup & Recovery — Resume In-Progress Rips on Restart

Currently, if the app is restarted while a job is ripping (VideoRipping, TranscodeActive, etc.), the background task is lost and the job stays stuck. On startup, scan for jobs in non-terminal states and resume them: re-attach the MakeMKV/HandBrake process if still running, or restart the rip/transcode stage from where it left off. Requires stage-level checkpointing (which stage completed, which files were produced) so the system can pick up without re-doing completed work.
