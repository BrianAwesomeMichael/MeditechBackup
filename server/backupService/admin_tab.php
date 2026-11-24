<?php
/**
 * backupService/admin_tab.php -- included by admin.php for ?view=backup_service.
 * Live status from the backup PC (MeditechBackup.exe) plus its shared settings.
 * Everything talks to backupService/api.php with fetch(), so nothing here posts
 * back to admin.php.
 */
require_once(__DIR__ . '/common.php');
$mbkSet = mbk_settings();
$mbkOverrides = '';
foreach ((array)$mbkSet['job_overrides'] as $code => $o) {
    $o = (array)$o;
    $parts = array();
    if (isset($o['retention_days'])) $parts[] = (int)$o['retention_days'];
    if (!empty($o['skip'])) $parts[] = 'skip';
    $mbkOverrides .= $code . ' = ' . implode(' ', $parts) . "\n";
}
$v = function ($k) use ($mbkSet) { return htmlspecialchars((string)$mbkSet[$k]); };
?>
<link rel="stylesheet" href="backupService/status.css?v=<?php echo @filemtime(__DIR__ . '/status.css'); ?>">
<script src="backupService/status.js?v=<?php echo @filemtime(__DIR__ . '/status.js'); ?>"></script>

<h1 style="margin-top:0;">Backup Service</h1>
<p style="opacity:0.75;">Live status of <code>MeditechBackup.exe</code> on the backup PC. Every few hours it copies each
In Progress, On Hold and Pending Zip job to its own drives, one job at a time, copying only files that changed.
Jobs that reach <em>5. Complete</em> are removed from the backup drives (they are on tape by then).</p>

<div id="mbk-status"><div class="empty-queue">Loading&hellip;</div></div>

<details class="mbk" style="margin-top:28px;" id="mbk-settings-box">
<summary style="cursor:pointer; font-weight:600;">Backup settings</summary>
<p class="small muted">The backup PC loads these before each job. Drive letters and the web port are set on the backup PC itself, in
<code>MeditechBackup.json</code> next to the exe.</p>
<table class="settings-form">
<tr><th>Start a new pass every</th><td><input type="text" id="mbk-interval_hours" value="<?php echo $v('interval_hours'); ?>" size="4"> hours</td>
    <td class="hint">If a pass takes longer than this, the next one starts as soon as it finishes.</td></tr>
<tr><th>Keep old versions (production)</th><td><input type="text" id="mbk-retention_days_active" value="<?php echo $v('retention_days_active'); ?>" size="4"> days</td>
    <td class="hint">For <em>In Progress</em> and <em>On Hold</em> jobs. When a file changes or is deleted, the previous copy goes to a dated <code>_versions</code> folder on the backup drive and is deleted after this many days.</td></tr>
<tr><th>Keep old versions (pending zip)</th><td><input type="text" id="mbk-retention_days_pending" value="<?php echo $v('retention_days_pending'); ?>" size="4"> days</td>
    <td class="hint">For <em>Pending Zip</em> and later. <code>0</code> = keep only the current files and delete old versions straight away.</td></tr>
<tr><th>Keep free on each drive</th><td><input type="text" id="mbk-reserve_free_gb" value="<?php echo $v('reserve_free_gb'); ?>" size="6"> GB</td>
    <td class="hint">The service never fills a drive past this. A job that no longer fits on its drive continues on the other one.</td></tr>
<tr><th>Speed limit</th><td><input type="text" id="mbk-max_mb_per_sec" value="<?php echo $v('max_mb_per_sec'); ?>" size="6"> MB/s</td>
    <td class="hint"><code>0</code> = no limit. Set one if backups slow down the file server during the day.</td></tr>
<tr><th>Remove completed jobs</th><td><label><input type="checkbox" id="mbk-delete_completed" <?php echo $mbkSet['delete_completed'] ? 'checked' : ''; ?>> yes</label></td>
    <td class="hint">Delete a job's backup when it reaches <em>5. Complete</em> or <em>6. Archived</em>. Zipping and zipped jobs are kept (but not re-scanned) until then.</td></tr>
<tr><th>Rebalance drives</th><td><label><input type="checkbox" id="mbk-rebalance" <?php echo $mbkSet['rebalance'] ? 'checked' : ''; ?>> yes</label></td>
    <td class="hint">When one drive is nearly full and the other has room, move one whole job across at the end of a pass.</td></tr>
<tr><th>Don&rsquo;t back up</th><td colspan="2"><textarea id="mbk-exclude_patterns" rows="4" cols="40" style="font-family:monospace;"><?php echo htmlspecialchars(implode("\n", (array)$mbkSet['exclude_patterns'])); ?></textarea>
    <div class="hint">One file or folder name per line, <code>*</code> and <code>?</code> allowed. Leave empty to back up everything.
    Examples: <code>_old</code>, <code>old</code>, <code>~old</code>, <code>*.tmp</code>, <code>Thumbs.db</code>.
    Copies already on the backup drive of anything newly excluded are treated like deleted files (kept as old versions, then removed).</div></td></tr>
<tr><th>Per-job overrides</th><td colspan="2"><textarea id="mbk-overrides" rows="5" cols="40" style="font-family:monospace;"><?php echo htmlspecialchars($mbkOverrides); ?></textarea>
    <div class="hint">One job per line: <code>MTH126 = 90</code> keeps old versions 90 days for that job; <code>MTH150 = skip</code> doesn&rsquo;t back it up; <code>MTH160 = 7 skip</code> sets both.</div></td></tr>
</table>
<div class="admin-actions"><button type="button" id="mbk-save">Save Settings</button> <span id="mbk-save-msg" class="small"></span></div>
</details>

<script>
(function () {
    var API = 'backupService/api.php';
    function post(action, body) {
        return fetch(API + '?action=' + action, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
            .then(function (r) { return r.json(); });
    }
    var view = MBK.mount(document.getElementById('mbk-status'), {
        onCommand: function (cmd) { post('command', { cmd: cmd }).then(refresh); }
    });

    function refresh() {
        fetch(API + '?action=status', { cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (d) {
            if (!d.report) {
                document.getElementById('mbk-status').innerHTML = '<div class="empty-queue">The backup PC has not reported in yet. Start <code>MeditechBackup.exe</code> on it.</div>';
                return;
            }
            var ago = d.now_ts - d.received_ts;
            var r = d.report;
            var port = r.web_port || 8099;
            view.update(r, {
                offline: ago > 180,
                receivedAgo: ago,
                pending: (d.pending_commands || []).length,
                linkLocal: d.remote_addr ? 'http://' + d.remote_addr + ':' + port + '/' : ''
            });
        }).catch(function () {});
    }
    refresh();
    setInterval(refresh, 5000);

    document.getElementById('mbk-save').addEventListener('click', function () {
        var g = function (k) { return document.getElementById('mbk-' + k); };
        var ov = {};
        g('overrides').value.split(/\r?\n/).forEach(function (line) {
            var m = line.match(/^\s*([A-Za-z0-9_\-]+)\s*[=:]\s*(.*)$/);
            if (!m) return;
            var e = {}, rest = m[2].toLowerCase();
            var n = rest.match(/\d+/); if (n) e.retention_days = parseInt(n[0], 10);
            if (/skip/.test(rest)) e.skip = true;
            ov[m[1].toUpperCase()] = e;
        });
        var body = {
            interval_hours: g('interval_hours').value, retention_days_active: g('retention_days_active').value,
            retention_days_pending: g('retention_days_pending').value, reserve_free_gb: g('reserve_free_gb').value,
            max_mb_per_sec: g('max_mb_per_sec').value, delete_completed: g('delete_completed').checked,
            rebalance: g('rebalance').checked,
            exclude_patterns: g('exclude_patterns').value.split(/\r?\n/), job_overrides: ov
        };
        var msg = document.getElementById('mbk-save-msg');
        msg.textContent = 'Saving…';
        post('save_settings', body).then(function (d) {
            msg.textContent = d.ok ? 'Saved. The backup PC picks this up before its next job.' : ('Error: ' + d.error);
        }).catch(function (e) { msg.textContent = 'Error: ' + e; });
    });
})();
</script>
