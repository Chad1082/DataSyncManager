/* =============================================
   DataSync Manager — Shared JavaScript
   ============================================= */

// Auto-dismiss alerts after 5 seconds
document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.alert.alert-success, .alert.alert-info').forEach(function (el) {
        setTimeout(function () {
            const bsAlert = bootstrap.Alert.getOrCreateInstance(el);
            if (bsAlert) bsAlert.close();
        }, 5000);
    });
});

// Active sidebar link highlighting
(function () {
    const path = window.location.pathname.toLowerCase();
    document.querySelectorAll('#sidebar .nav-link').forEach(function (link) {
        const href = link.getAttribute('href');
        if (href && path.startsWith(href.toLowerCase()) && href !== '/') {
            link.classList.add('active');
        }
    });
})();

// Confirm delete helper — attach data-confirm="message" to forms
document.querySelectorAll('form[data-confirm]').forEach(function (form) {
    form.addEventListener('submit', function (e) {
        if (!confirm(form.dataset.confirm)) e.preventDefault();
    });
});

// Copy-to-clipboard helper
function copyToClipboard(text, btn) {
    navigator.clipboard.writeText(text).then(function () {
        const orig = btn.innerHTML;
        btn.innerHTML = '<i class="bi bi-check"></i> Copied!';
        btn.classList.add('btn-success');
        btn.classList.remove('btn-outline-secondary');
        setTimeout(function () {
            btn.innerHTML = orig;
            btn.classList.remove('btn-success');
            btn.classList.add('btn-outline-secondary');
        }, 2000);
    });
}

// Format numbers with commas
function formatNumber(n) {
    return n != null ? n.toLocaleString() : '—';
}

// Connection test helper (used on server pages)
function testConnection(url, statusEl) {
    statusEl.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span> Testing...';
    statusEl.className = 'text-muted';
    fetch(url)
        .then(r => r.json())
        .then(data => {
            if (data.success) {
                statusEl.innerHTML = '<i class="bi bi-check-circle-fill me-1"></i>' + (data.message || 'Connected');
                statusEl.className = 'text-success fw-semibold';
            } else {
                statusEl.innerHTML = '<i class="bi bi-x-circle-fill me-1"></i>' + (data.message || 'Failed');
                statusEl.className = 'text-danger fw-semibold';
            }
        })
        .catch(err => {
            statusEl.innerHTML = '<i class="bi bi-x-circle-fill me-1"></i>' + err.message;
            statusEl.className = 'text-danger fw-semibold';
        });
}
