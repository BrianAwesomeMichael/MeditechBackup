/*
 * Status view shared by admin.php?view=backup_service (201) and the local page
 * served by MeditechBackup.exe on the backup PC. The exe embeds this file at
 * build time, so rebuild it after editing.
 *
 *   var view = MBK.mount(element, { onCommand: function (cmd) {...} });
 *   view.update(snapshot, { offline: bool, receivedAgo: seconds, linkLocal: url });
 */
(function (global) {
    'use strict';

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    function bytes(n) {
        n = Number(n) || 0;
        var u = ['B', 'KB', 'MB', 'GB', 'TB'], i = 0;
        while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
        return (i === 0 ? n : n.toFixed(n < 10 ? 2 : n < 100 ? 1 : 0)) + ' ' + u[i];
    }
    function num(n) { return (Number(n) || 0).toLocaleString(); }
    function dur(sec) {
        sec = Math.max(0, Math.round(Number(sec) || 0));
        var d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
        if (d) return d + 'd ' + h + 'h';
        if (h) return h + 'h ' + m + 'm';
        if (m) return m + 'm ' + s + 's';
        return s + 's';
    }
    function when(iso) {
        if (!iso) return '';
        var t = new Date(iso);
        if (isNaN(t)) return '';
        var ago = (Date.now() - t.getTime()) / 1000;
        var txt = ago >= 0 ? dur(ago) + ' ago' : 'in ' + dur(-ago);
        return '<span title="' + esc(t.toLocaleString()) + '">' + txt + '</span>';
    }
    function pct(a, b) { return b > 0 ? Math.max(0, Math.min(100, a / b * 100)) : 0; }

    var CAT_LABEL = { active: 'Production', pending: 'Pending zip', held: 'Zipping / zipped', done: 'Complete', unknown: 'Not in DB', permanent: 'Permanent' };
    function day(iso) { if (!iso) return ''; var t = new Date(iso); return isNaN(t) ? '' : t.toLocaleDateString(); }

    function mount(root, opts) {
        opts = opts || {};
        root.classList.add('mbk');
        root.innerHTML =
            '<div class="bar" data-r="bar"></div>' +
            '<div class="cards" data-r="cards"></div>' +
            '<h2>Jobs</h2>' +
            '<div class="tools"><input type="search" data-r="filter" placeholder="Filter jobs (code, title, status)">' +
            '<label class="small"><input type="checkbox" data-r="probs"> Only jobs with problems</label>' +
            '<span class="small muted" data-r="count"></span></div>' +
            '<div class="tablewrap"><table class="jobs"><thead><tr>' +
            '<th data-k="code">Job</th><th data-k="category">Status</th><th data-k="last_success">Last good backup</th>' +
            '<th data-k="last_result">Last run</th><th data-k="bytes" class="num">Size</th><th data-k="files" class="num">Files</th>' +
            '<th data-k="last_copied_bytes" class="num">Copied last run</th><th data-k="version_bytes" class="num">Old versions</th>' +
            '<th data-k="drives">Drive</th></tr></thead><tbody data-r="rows"></tbody></table></div>' +
            '<h2>Cleanup when a drive is full</h2><div class="small" data-r="cleanup"></div>' +
            '<details class="cleanup"><summary data-r="cand-sum">Folders no job uses</summary>' +
            '<p class="small muted">Not any job&rsquo;s backup. With <em>Delete folders no job uses</em> on, these go oldest first when a drive is full. ' +
            'Add anything to keep to <em>Safe folders</em> in the backup settings.</p>' +
            '<div class="tablewrap"><table class="jobs"><thead><tr><th>Folder</th><th class="num">Size</th><th class="num">Files</th>' +
            '<th>Last written</th><th>Can delete</th></tr></thead><tbody data-r="cand"></tbody></table></div></details>' +
            '<details class="cleanup"><summary data-r="del-sum">Deleted by the cleanup</summary>' +
            '<div class="tablewrap"><table class="jobs"><thead><tr><th>When</th><th>What</th><th class="num">Size</th><th>Last written</th></tr></thead>' +
            '<tbody data-r="del"></tbody></table></div></details>' +
            '<h2>Log</h2><div class="log" data-r="log"></div>';

        var $ = function (r) { return root.querySelector('[data-r="' + r + '"]'); };
        var last = null, lastMeta = {}, sortKey = 'last_success', sortDir = 1;

        root.addEventListener('click', function (e) {
            var b = e.target.closest('button[data-cmd]');
            if (b && opts.onCommand) {
                if (b.dataset.cmd === 'skip' && !confirm('Stop the current job? It will be picked up again next pass.')) return;
                opts.onCommand(b.dataset.cmd);
            }
            var th = e.target.closest('th[data-k]');
            if (th) {
                if (sortKey === th.dataset.k) sortDir = -sortDir; else { sortKey = th.dataset.k; sortDir = 1; }
                renderRows();
            }
        });
        $('filter').addEventListener('input', renderRows);
        $('probs').addEventListener('change', renderRows);

        function renderBar(s, meta) {
            var st = meta.offline ? 'offline' : (s.state || 'starting');
            var label = { running: 'Backing up', waiting: 'Waiting for next pass', paused: 'Paused', error: 'Problem', offline: 'Not reporting', starting: 'Starting' }[st] || st;
            var h = '<span class="pill ' + esc(st) + '">' + esc(label) + '</span>';
            h += '<span class="grow">' + esc(s.state_message || '');
            if (meta.offline && meta.receivedAgo != null) h += ' <b>Last heard from ' + dur(meta.receivedAgo) + ' ago.</b>';
            h += '<br><span class="small muted">' + esc(s.machine || '?') + ' &middot; v' + esc(s.version || '?') +
                 ' &middot; running since ' + when(s.started_at);
            if (meta.linkLocal) h += ' &middot; <a href="' + esc(meta.linkLocal) + '" target="_blank">open page on backup PC</a>';
            h += '</span></span>';
            h += '<button class="primary" data-cmd="run_now" title="Start a pass now instead of waiting">Run now</button>';
            h += s.paused ? '<button data-cmd="resume">Resume</button>' : '<button data-cmd="pause">Pause</button>';
            if (s.current) h += '<button data-cmd="skip" title="Stop this job and move on to the next one">Skip job</button>';
            if (meta.pending) h += '<span class="small muted">' + meta.pending + ' command(s) waiting for the backup PC&hellip;</span>';
            $('bar').innerHTML = h;
        }

        function renderCards(s) {
            var h = '';
            var c = s.current;
            if (c) {
                var p = c.bytes_to_copy > 0 ? pct(c.bytes_copied, c.bytes_to_copy) : pct(c.files_done, c.files_total);
                h += '<div class="card"><h3>Now: ' + esc(c.code) + ' <span class="muted small">' + esc(c.title || '') + '</span></h3>' +
                     '<div class="kv"><b>Step</b><span>' + esc(c.phase) + '</span>' +
                     '<b>Files</b><span>' + num(c.files_done) + ' / ' + num(c.files_total) + ' checked, ' + num(c.files_copied) + ' copied</span>' +
                     '<b>Data</b><span>' + bytes(c.bytes_copied) + ' / ' + bytes(c.bytes_to_copy) + ' to copy' +
                     (c.speed_bps > 0 ? ' &middot; ' + bytes(c.speed_bps) + '/s' : '') + '</span>' +
                     '<b>Started</b><span>' + when(c.started_at) + '</span></div>' +
                     '<div class="meter"><span style="width:' + p.toFixed(1) + '%"></span></div>' +
                     '<div class="file">' + esc(c.current_file || '') + '</div></div>';
            }
            var y = s.cycle || {};
            h += '<div class="card"><h3>Pass</h3><div class="kv">';
            if (y.started_at && !y.ended_at) h += '<b>This pass</b><span>job ' + num(y.job_index) + ' of ' + num(y.job_count) + ', started ' + when(y.started_at) + '</span>';
            if (y.last_ended_at) h += '<b>Last pass</b><span>finished ' + when(y.last_ended_at) + ', took ' + dur(y.last_duration_sec) + '</span>';
            if (y.next_at && (!y.started_at || y.ended_at)) h += '<b>Next pass</b><span>' + when(y.next_at) + '</span>';
            var set = s.settings || {};
            h += '<b>Every</b><span>' + esc(set.interval_hours) + ' h</span>';
            h += '<b>Keep old versions</b><span>' + esc(set.retention_days_active) + ' days (production), ' + esc(set.retention_days_pending) + ' days (pending zip)</span>';
            h += '</div></div>';
            (s.drives || []).forEach(function (d) {
                var used = d.total_bytes - d.free_bytes, p = pct(used, d.total_bytes);
                h += '<div class="card"><h3>Drive ' + esc(d.path) + (d.label ? ' <span class="muted small">' + esc(d.label) + '</span>' : '') + '</h3>';
                if (!d.ok) h += '<div class="res error">' + esc(d.error || 'Not available') + '</div>';
                else h += '<div class="meter ' + (p > 95 ? 'bad' : p > 85 ? 'warn' : '') + '"><span style="width:' + p.toFixed(1) + '%"></span></div>' +
                          '<div class="small">' + bytes(d.free_bytes) + ' free of ' + bytes(d.total_bytes) + ' &middot; ' + num(d.jobs) + ' jobs</div>';
                h += '</div>';
            });
            $('cards').innerHTML = h;
        }

        function problem(j) {
            return j.last_result === 'error' || j.last_result === 'warnings' || j.category === 'unknown' || (j.last_message && /missing|not found/i.test(j.last_message));
        }

        function renderRows() {
            if (!last) return;
            var f = $('filter').value.trim().toLowerCase(), probs = $('probs').checked;
            var cur = last.current ? last.current.code : '';
            var jobs = (last.jobs || []).filter(function (j) {
                if (probs && !problem(j)) return false;
                if (!f) return true;
                return (j.code + ' ' + (j.title || '') + ' ' + (j.db_status || '') + ' ' + (j.last_message || '')).toLowerCase().indexOf(f) >= 0;
            });
            jobs.sort(function (a, b) {
                var x = a[sortKey], y = b[sortKey];
                if (Array.isArray(x)) x = x.join(); if (Array.isArray(y)) y = y.join();
                if (x == null || x === '') return 1; if (y == null || y === '') return -1;
                return (x < y ? -1 : x > y ? 1 : 0) * sortDir;
            });
            var h = '';
            jobs.forEach(function (j) {
                h += '<tr' + (j.code === cur ? ' class="cur"' : '') + '>' +
                     '<td><b>' + esc(j.code) + '</b><br><span class="small muted">' + esc(j.title || '') + '</span>' +
                     (j.duplicate_of ? '<br><span class="small res warnings">Same folder as ' + esc(j.duplicate_of) + '</span>' : '') + '</td>' +
                     '<td><span class="cat ' + esc(j.category) + '" title="' + esc(j.db_status) + '">' + esc(CAT_LABEL[j.category] || j.category) + '</span>' +
                     (j.skip ? '<br><span class="small muted">skipped in settings</span>' : '') +
                     '<br><span class="small muted">keeps ' + esc(j.retention_days) + ' d</span></td>' +
                     '<td>' + (when(j.last_success) || '<span class="muted">never</span>') + '</td>' +
                     '<td><span class="res ' + esc(j.last_result || 'never') + '">' + esc(j.last_result || 'never') + '</span> ' +
                     '<span class="small muted">' + when(j.last_end) + '</span>' +
                     (j.last_message ? '<br><span class="small">' + esc(j.last_message) + '</span>' : '') + '</td>' +
                     '<td class="num">' + (j.bytes ? bytes(j.bytes) : '') + '</td>' +
                     '<td class="num">' + (j.files ? num(j.files) : '') + '</td>' +
                     '<td class="num">' + (j.last_copied_files ? num(j.last_copied_files) + ' / ' + bytes(j.last_copied_bytes) : '') + '</td>' +
                     '<td class="num">' + (j.version_bytes ? bytes(j.version_bytes) + '<br><span class="small muted">' + num(j.version_sets) + ' sets</span>' : '') + '</td>' +
                     '<td>' + esc((j.drives || []).join(' + ')) +
                     (j.dest_path ? '<br><span class="small muted file" title="Backup folder">' + esc(j.dest_path) + '</span>' : '') + '</td></tr>';
            });
            $('rows').innerHTML = h || '<tr><td colspan="9" class="muted">No jobs.</td></tr>';
            $('count').textContent = jobs.length + ' of ' + (last.jobs || []).length + ' jobs';
        }

        function renderCleanup(s) {
            var set = s.settings || {}, c = s.cleanup || {};
            var on = [];
            if (set.cleanup_versions) on.push('old versions');
            if (set.cleanup_unowned) on.push('folders no job uses');
            var h = on.length
                ? 'Below <b>' + esc(set.cleanup_free_gb) + ' GB</b> free, deletes ' + esc(on.join(' and ')) +
                  ' with nothing written in ' + esc(set.cleanup_min_age_days) + ' days, oldest first.'
                : '<span class="res warnings">Off.</span> Nothing is deleted when a drive is full.';
            if (!set.cleanup_unowned && c.eligible_bytes > 0)
                h += ' <span class="res warnings">Deleting folders no job uses is off; it could free ' + bytes(c.eligible_bytes) + '.</span>';
            if (c.last_run) h += '<br>Last cleanup ' + when(c.last_run) + ': ' + esc(c.last_message || '');
            var prot = set.protected_folders || [];
            h += '<br><span class="muted">Safe folders: ' + (prot.length ? esc(prot.join(', ')) : 'none') + '</span>';
            $('cleanup').innerHTML = h;

            var cand = c.candidates || [];
            $('cand-sum').innerHTML = 'Folders no job uses: ' + num(cand.length) + ', ' + bytes(c.total_bytes) +
                ' (' + bytes(c.eligible_bytes) + ' old enough to delete)' +
                (c.surveyed_at ? ' <span class="muted small">checked ' + when(c.surveyed_at) + '</span>' : ' <span class="muted small">checked at the end of the next pass</span>');
            $('cand').innerHTML = cand.map(function (x) {
                return '<tr><td class="file">' + esc(x.path) + '</td><td class="num">' + bytes(x.bytes) + '</td><td class="num">' + num(x.files) + '</td>' +
                       '<td>' + esc(day(x.newest)) + '</td><td>' + (x.eligible ? '<span class="res ok">yes</span>' : '<span class="res never">no</span> <span class="small muted">' + esc(x.note || '') + '</span>') + '</td></tr>';
            }).join('') || '<tr><td colspan="5" class="muted">None found.</td></tr>';

            var del = c.recent || [];
            $('del-sum').textContent = 'Deleted by the cleanup: ' + (del.length ? del.length + ' most recent' : 'nothing yet');
            $('del').innerHTML = del.map(function (x) {
                return '<tr><td>' + when(x.at) + '</td><td class="file">' + esc(x.path) + (x.kind === 'versions' ? ' <span class="small muted">(old versions)</span>' : '') + '</td>' +
                       '<td class="num">' + bytes(x.bytes) + '</td><td>' + esc(day(x.newest)) + '</td></tr>';
            }).join('') || '<tr><td colspan="4" class="muted">Nothing yet.</td></tr>';
        }

        function renderLog(s) {
            var box = $('log'), atBottom = box.scrollTop + box.clientHeight >= box.scrollHeight - 5;
            box.innerHTML = (s.log || []).map(function (l) {
                var t = new Date(l.t);
                return '<div class="' + esc(l.level) + '">' + esc(t.toLocaleString()) + '  ' + esc(l.msg) + '</div>';
            }).join('');
            if (atBottom) box.scrollTop = box.scrollHeight;
        }

        return {
            update: function (snap, meta) {
                last = snap || {}; lastMeta = meta || {};
                renderBar(last, lastMeta);
                renderCards(last);
                renderRows();
                renderCleanup(last);
                renderLog(last);
            }
        };
    }

    global.MBK = { mount: mount, bytes: bytes, dur: dur };
})(window);
