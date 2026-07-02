var arm = arm || {};

arm.signalrConnection = null;

// --- JobUpdate callback registry ---
arm._jobUpdateHandlers = [];

arm.onJobUpdate = function (fn) {
    arm._jobUpdateHandlers.push(fn);
};

// --- Toast notifications ---
arm._showToast = function (msg) {
    var container = document.getElementById('toastContainer');
    if (!container) {
        container = document.createElement('div');
        container.id = 'toastContainer';
        container.style.cssText = 'position:fixed; bottom:1rem; right:1rem; z-index:9999; max-width:400px;';
        document.body.appendChild(container);
    }
    var toast = document.createElement('div');
    toast.className = 'alert alert-info alert-dismissible fade show py-1 px-2 mb-1 small';
    toast.style.fontSize = '11px';
    toast.style.lineHeight = '1.3';
    toast.innerHTML = '<span>' + msg + '</span>' +
        '<button type="button" class="close py-0 px-1" data-dismiss="alert" style="font-size:14px;">&times;</button>';
    container.appendChild(toast);
    setTimeout(function () {
        if (toast.parentNode) {
            toast.classList.remove('show');
            setTimeout(function () { if (toast.parentNode) toast.parentNode.removeChild(toast); }, 300);
        }
    }, 6000);
    while (container.children.length > 5) {
        container.removeChild(container.firstChild);
    }
};

arm.refreshNotifBadge = function () {
    var badge = document.getElementById('notifBadge');
    if (!badge) return;
    fetch('/api/notifications/unread')
        .then(function (r) { return r.json(); })
        .then(function (data) {
            if (data.count > 0) {
                badge.textContent = data.count;
                badge.style.display = 'inline';
            } else {
                badge.style.display = 'none';
            }
        })
        .catch(function () {});
};

arm._setSignalrStatus = function (state) {
    var dot = document.getElementById('signalrStatus');
    if (!dot) return;
    dot.className = 'signalr-dot signalr-' + state;
    var labels = { connected: 'Connected', reconnecting: 'Reconnecting...', disconnected: 'Disconnected' };
    dot.title = 'SignalR: ' + (labels[state] || state);
};

arm.startSignalR = function () {
    if (!window.signalR) return;
    arm.signalrConnection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/notifications')
        .withAutomaticReconnect()
        .build();

    arm.signalrConnection.onreconnecting(function () {
        arm._setSignalrStatus('reconnecting');
    });

    arm.signalrConnection.onreconnected(function () {
        arm._setSignalrStatus('connected');
        arm.refreshNotifBadge();
    });

    arm.signalrConnection.onclose(function () {
        arm._setSignalrStatus('disconnected');
    });

    arm.signalrConnection.on('Notification', function (notif) {
        arm.refreshNotifBadge();
        if (notif && notif.eventType) {
            arm._showToast('\u{1F514} ' + notif.eventType + ': ' + (notif.message || ''));
        }
    });

    var _lastToastKey = '';
    arm.signalrConnection.on('JobUpdate', function (update) {
        console.log('[ARM] JobUpdate:', JSON.stringify(update));

        for (var i = 0; i < arm._jobUpdateHandlers.length; i++) {
            try { arm._jobUpdateHandlers[i](update); } catch (e) {}
        }
    });

    arm.signalrConnection.start()
        .then(function () {
            arm._setSignalrStatus('connected');
            arm.refreshNotifBadge();
        })
        .catch(function () {
            arm._setSignalrStatus('disconnected');
        });
};

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

    // Initialize tooltips: Bootstrap data-toggle="tooltip" and custom data-tooltip
    if (typeof $ !== 'undefined' && $.fn.tooltip) {
        $('[data-toggle="tooltip"]').tooltip({ placement: 'top', trigger: 'hover focus' });
        $('[data-tooltip]').tooltip({
            title: function () { return this.getAttribute('data-tooltip'); },
            placement: 'top',
            trigger: 'hover focus'
        });
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
