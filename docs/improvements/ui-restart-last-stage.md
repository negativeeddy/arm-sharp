# UI / User Experience — Restart from Last Successful Stage

Add a "retry from failure" action that resumes the pipeline at the last failed stage instead of restarting from scratch. Requires each stage to checkpoint its completion state in the DB (e.g. a `Stages` table or bitfield on `Job`).
