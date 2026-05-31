var arm = arm || {};

arm.notificationPollInterval = null;

arm.startNotificationPolling = function (intervalMs) {
    intervalMs = intervalMs || 15000;
    if (arm.notificationPollInterval) clearInterval(arm.notificationPollInterval);
    arm.notificationPollInterval = setInterval(function () {
        fetch('/api/notifications/unread')
            .then(function (r) { return r.json(); })
            .then(function (data) {
                var badge = document.getElementById('notifBadge');
                if (!badge) return;
                if (data.count > 0) {
                    badge.textContent = data.count;
                    badge.style.display = 'inline';
                } else {
                    badge.style.display = 'none';
                }
            })
            .catch(function () {});
    }, intervalMs);
};

arm.stopNotificationPolling = function () {
    if (arm.notificationPollInterval) {
        clearInterval(arm.notificationPollInterval);
        arm.notificationPollInterval = null;
    }
};

arm.toggleDarkMode = function () {
    document.body.classList.toggle('dark-mode');
    var enabled = document.body.classList.contains('dark-mode');
    localStorage.setItem('arm-dark-mode', enabled ? '1' : '0');
    var btn = document.getElementById('darkModeToggle');
    if (btn) btn.textContent = enabled ? '☀️' : '🌙';
};

arm.initDarkMode = function () {
    if (localStorage.getItem('arm-dark-mode') === '1') {
        document.body.classList.add('dark-mode');
        var btn = document.getElementById('darkModeToggle');
        if (btn) btn.textContent = '☀️';
    }
};

arm.formatBytes = function (bytes, decimals) {
    if (bytes === 0) return '0 B';
    decimals = decimals || 2;
    var k = 1024;
    var sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    var i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(decimals)) + ' ' + sizes[i];
};

arm.etaText = function (seconds) {
    if (!seconds || seconds <= 0) return '--';
    var h = Math.floor(seconds / 3600);
    var m = Math.floor((seconds % 3600) / 60);
    var s = Math.floor(seconds % 60);
    if (h > 0) return h + 'h ' + m + 'm';
    if (m > 0) return m + 'm ' + s + 's';
    return s + 's';
};
