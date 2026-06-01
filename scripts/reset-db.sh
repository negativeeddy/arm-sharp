#!/bin/bash
set -e
DB="/workspaces/arm-sharp/src/ArmRipper.WebUi/data/arm.db"
python3 -c "
import sqlite3
conn = sqlite3.connect('$DB')
conn.execute('PRAGMA foreign_keys = OFF;')
cur = conn.cursor()
for t in ['tracks', 'config', 'notifications', 'jobs']:
    cur.execute(f'DELETE FROM {t}')
conn.commit()
cur.execute('SELECT COUNT(*) FROM jobs'); jobs = cur.fetchone()[0]
cur.execute('SELECT COUNT(*) FROM tracks'); trk = cur.fetchone()[0]
cur.execute('SELECT COUNT(*) FROM config'); cfg = cur.fetchone()[0]
cur.execute('SELECT COUNT(*) FROM notifications'); notif = cur.fetchone()[0]
conn.close()
print(f'Cleared: {jobs} jobs, {trk} tracks, {cfg} configs, {notif} notifications')
"
