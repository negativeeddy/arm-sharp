var arm = arm || {};

arm.jobRefreshInterval = null;
arm._jobTableDebounceTimer = null;
arm._drivesDebounceTimer = null;

// Fetch and replace active jobs table body
arm._refreshJobsTable = function () {
    var container = document.getElementById('activeJobsTable');
    if (!container) return;
    fetch('/api/jobs/active')
        .then(function (r) { return r.text(); })
        .then(function (html) {
            var tbody = container.querySelector('tbody');
            if (tbody) tbody.innerHTML = html;
        })
        .catch(function () {});
};

// Fetch and replace the drives section (resets "Ripping" badges to "Rip" buttons)
arm._refreshDrives = function () {
    var container = document.getElementById('drivesContainer');
    if (!container) return;
    fetch('/api/drives/partial')
        .then(function (r) { return r.text(); })
        .then(function (html) {
            container.innerHTML = html;
        })
        .catch(function () {});
};

// Direct DOM update: find the matching row and update title/disc type immediately
arm._applyJobUpdateToDom = function (update) {
    if (!update || !update.jobId) return;
    var container = document.getElementById('activeJobsTable');
    if (!container) return;
    var tbody = container.querySelector('tbody');
    if (!tbody) return;

    // Walk rows looking for the job ID in the first cell
    for (var i = 0; i < tbody.rows.length; i++) {
        var row = tbody.rows[i];
        if (row.cells.length > 0 && row.cells[0].textContent.trim() === String(update.jobId)) {
            // Update title cell (2nd column)
            if (update.title && row.cells[1]) {
                row.cells[1].textContent = update.title;
            }
            // Update disc type cell (3rd column)
            if (update.discType && row.cells[2]) {
                row.cells[2].textContent = update.discType;
            }
            break;
        }
    }
};

// SignalR-driven: immediate DOM update + debounced full refresh
arm._onJobUpdateForTable = function (update) {
    // Apply visible changes instantly
    arm._applyJobUpdateToDom(update);
    // Schedule a full table refresh to sync pipeline, progress, etc.
    if (arm._jobTableDebounceTimer) clearTimeout(arm._jobTableDebounceTimer);
    arm._jobTableDebounceTimer = setTimeout(arm._refreshJobsTable, 500);

    // When a job reaches a terminal state, also refresh the drives section
    // so "Ripping" badges revert to "Rip" buttons
    if (update && update.status) {
        var terminalStatuses = ['success', 'fail', 'cancelled'];
        if (terminalStatuses.indexOf(update.status) !== -1) {
            if (arm._drivesDebounceTimer) clearTimeout(arm._drivesDebounceTimer);
            arm._drivesDebounceTimer = setTimeout(arm._refreshDrives, 800);
        }
    }
};

// Start SignalR-driven table refresh (replaces polling)
arm.startJobRefresh = function (intervalMs) {
    // Register SignalR handler (primary, event-driven)
    arm.onJobUpdate(arm._onJobUpdateForTable);

    // Fallback polling at a slower rate (only if SignalR is not connected)
    intervalMs = intervalMs || 30000;
    if (arm.jobRefreshInterval) clearInterval(arm.jobRefreshInterval);
    arm.jobRefreshInterval = setInterval(function () {
        // Only poll as fallback when SignalR is disconnected
        if (arm.signalrConnection && arm.signalrConnection.state === 'Connected') return;
        arm._refreshJobsTable();
    }, intervalMs);
};

arm.stopJobRefresh = function () {
    if (arm.jobRefreshInterval) {
        clearInterval(arm.jobRefreshInterval);
        arm.jobRefreshInterval = null;
    }
};

arm.abandonJob = function (jobId) {
    if (!confirm('Abandon this job? The process will be killed and the disc ejected.')) return;
    fetch('/api/abandon/' + jobId, { method: 'POST' })
        .then(function (r) { return r.json(); })
        .then(function (data) {
            if (data.success) {
                location.reload();
            } else {
                alert('Failed to abandon job: ' + (data.message || 'unknown error'));
            }
        })
        .catch(function (err) {
            alert('Error: ' + err);
        });
};
