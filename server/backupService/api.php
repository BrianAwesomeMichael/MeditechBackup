<?php
/**
 * backupService/api.php
 *
 * The JSON API between the backup PC (MeditechBackup.exe) and this server.
 *
 *   GET  ?action=jobs            Jobs the backup PC should know about (every job
 *   POST ?action=jobs              that has not yet gone to tape). POST a JSON body
 *                                  {"codes":[...]} to also get the current status of
 *                                  those job codes. The service sends the jobs it
 *                                  holds, so it learns when one is Complete and can
 *                                  delete its copy.
 *   GET  ?action=settings        Shared service settings (edited on the admin tab).
 *   POST ?action=save_settings   Admin tab: save the settings.
 *   POST ?action=report          Backup PC: status snapshot. The reply carries any
 *                                  queued commands (run_now / pause / resume / skip).
 *   GET  ?action=status          Admin tab: the last snapshot and when it arrived.
 *   POST ?action=command         Admin tab: queue a command for the backup PC.
 *
 * Files live in backupService/data/ (status.json, settings.json, commands.json).
 * Like admin.php, there is no login: it is only reachable on the office LAN.
 */

ini_set('display_errors', 'Off');
error_reporting(E_ALL);
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

require_once(__DIR__ . '/common.php');

function mbk_out($data, $code = 200)
{
    http_response_code($code);
    echo json_encode($data, JSON_UNESCAPED_SLASHES);
    exit;
}

function mbk_body()
{
    $raw = file_get_contents('php://input');
    $d = json_decode($raw, true);
    return is_array($d) ? $d : array();
}

$action = isset($_GET['action']) ? $_GET['action'] : '';

// ---------------------------------------------------------------------
if ($action === 'jobs')
{
    require_once(__DIR__ . '/../databaseAPI/databaseAPI.php');
    $db = new databaseAPI();

    // Every status before "on tape": these jobs must be kept. The service
    // decides what to scan from the number at the start of the status.
    $keep = array(
        databaseAPI::$STATUS_IN_PROGRESS,
        databaseAPI::$STATUS_ON_HOLD,
        databaseAPI::$STATUS_COMPLETED_PENDING,
        databaseAPI::$STATUS_COMPLETED_BACKINGUP,
        databaseAPI::$STATUS_COMPLETED_ZIPPED,
        databaseAPI::$STATUS_COMPLETED_ERROR,
    );
    $in = array();
    foreach ($keep as $k) $in[] = "'" . $db->connection->real_escape_string($k) . "'";
    $where = '`job_project_status` IN (' . implode(',', $in) . ')';

    $body = mbk_body();
    $codes = array();
    if (isset($body['codes']) && is_array($body['codes'])) {
        foreach ($body['codes'] as $c) {
            $c = preg_replace('/[^A-Za-z0-9_\-]/', '', (string)$c);
            if ($c !== '') $codes[] = "'" . $c . "'";
        }
    }
    if (!empty($codes)) $where = '(' . $where . ') OR `job_code` IN (' . implode(',', $codes) . ')';

    $sql = 'SELECT `job_code`, `job_title`, `job_file_location`, `job_project_status` FROM `'
         . databaseAPI::$PROJECT_DATA . '` WHERE ' . $where . ' ORDER BY `job_code`';
    $rows = $db->runHardCodedSearch($sql);
    if (!is_array($rows)) {
        if (!empty($db->errorstatus)) mbk_out(array('ok' => false, 'error' => $db->errorstatus), 500);
        $rows = array();
    }
    mbk_out(array('ok' => true, 'generated' => date('c'), 'jobs' => $rows));
}

// ---------------------------------------------------------------------
if ($action === 'settings')
{
    mbk_out(array('ok' => true, 'settings' => mbk_settings()));
}

// ---------------------------------------------------------------------
if ($action === 'save_settings')
{
    if ($_SERVER['REQUEST_METHOD'] !== 'POST') mbk_out(array('ok' => false, 'error' => 'POST only'), 405);
    global $MBK_DEFAULT_SETTINGS;
    $in = mbk_body();
    $s = mbk_settings();
    $num = function ($k, $min, $max) use (&$s, $in) {
        if (isset($in[$k]) && is_numeric($in[$k])) $s[$k] = max($min, min($max, $in[$k] + 0));
    };
    $num('interval_hours', 0.25, 168);
    $num('retention_days_active', 0, 3650);
    $num('retention_days_pending', 0, 3650);
    $num('reserve_free_gb', 0, 100000);
    $num('max_mb_per_sec', 0, 100000);
    $num('cleanup_free_gb', 0, 100000);
    $num('cleanup_min_age_days', 1, 3650);
    foreach (array('delete_completed', 'rebalance', 'cleanup_versions', 'cleanup_unowned') as $b)
        if (isset($in[$b])) $s[$b] = (bool)$in[$b];
    foreach (array('exclude_patterns', 'protected_folders', 'permanent_folders') as $k) {
        if (!isset($in[$k]) || !is_array($in[$k])) continue;
        $p = array();
        foreach ($in[$k] as $x) { $x = trim((string)$x); if ($x !== '') $p[] = $x; }
        $s[$k] = array_values(array_unique($p));
    }
    if (isset($in['job_overrides']) && is_array($in['job_overrides'])) {
        $o = array();
        foreach ($in['job_overrides'] as $code => $v) {
            $code = strtoupper(preg_replace('/[^A-Za-z0-9_\-]/', '', (string)$code));
            if ($code === '' || !is_array($v)) continue;
            $e = array();
            if (isset($v['retention_days']) && is_numeric($v['retention_days'])) $e['retention_days'] = max(0, (int)$v['retention_days']);
            if (!empty($v['skip'])) $e['skip'] = true;
            if (!empty($e)) $o[$code] = $e;
        }
        $s['job_overrides'] = empty($o) ? new stdClass() : $o;
    }
    if (!mbk_write_json('settings.json', $s)) mbk_out(array('ok' => false, 'error' => 'Could not write data/settings.json'), 500);
    mbk_out(array('ok' => true, 'settings' => $s));
}

// ---------------------------------------------------------------------
if ($action === 'report')
{
    $snap = mbk_body();
    if (empty($snap)) mbk_out(array('ok' => false, 'error' => 'empty report'), 400);
    $wrap = array(
        'received_at' => date('c'),
        'received_ts' => time(),
        'remote_addr' => isset($_SERVER['REMOTE_ADDR']) ? $_SERVER['REMOTE_ADDR'] : '',
        'report'      => $snap,
    );
    mbk_write_json('status.json', $wrap);

    // Hand over (and clear) queued commands.
    $cmds = mbk_locked('commands.json', array(), function ($cur) { return array(array(), $cur); });
    mbk_out(array('ok' => true, 'commands' => $cmds, 'settings' => mbk_settings()));
}

// ---------------------------------------------------------------------
if ($action === 'status')
{
    $st = mbk_read_json('status.json', array());
    $st['now_ts'] = time();
    $st['pending_commands'] = mbk_read_json('commands.json', array());
    $st['settings'] = mbk_settings();
    mbk_out($st);
}

// ---------------------------------------------------------------------
if ($action === 'command')
{
    $in = mbk_body();
    $cmd = isset($in['cmd']) ? (string)$in['cmd'] : '';
    if (!in_array($cmd, array('run_now', 'pause', 'resume', 'skip'), true))
        mbk_out(array('ok' => false, 'error' => 'unknown command'), 400);
    mbk_locked('commands.json', array(), function ($cur) use ($cmd) {
        $cur[] = array('cmd' => $cmd, 'at' => date('c'));
        return array($cur, null);
    });
    mbk_out(array('ok' => true));
}

mbk_out(array('ok' => false, 'error' => 'unknown action'), 400);
