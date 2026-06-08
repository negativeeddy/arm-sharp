var arm = arm || {};

arm.jobRefreshInterval = null;

arm.startJobRefresh = function (intervalMs) {
    intervalMs = intervalMs || 10000;
    if (arm.jobRefreshInterval) clearInterval(arm.jobRefreshInterval);
    arm.jobRefreshInterval = setInterval(function () {
        var container = document.getElementById('activeJobsTable');
        if (!container) return;
        fetch('/api/jobs/active')
            .then(function (r) { return r.text(); })
            .then(function (html) {
                var tbody = container.querySelector('tbody');
                if (tbody) tbody.innerHTML = html;
            })
            .catch(function () {});
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
