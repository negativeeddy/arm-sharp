var arm = arm || {};

arm.jobRefreshInterval = null;
arm._jobTableDebounceTimer = null;

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

// SignalR-driven: debounced refresh on any JobUpdate
arm._onJobUpdateForTable = function (update) {
    if (arm._jobTableDebounceTimer) clearTimeout(arm._jobTableDebounceTimer);
    arm._jobTableDebounceTimer = setTimeout(arm._refreshJobsTable, 500);
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
