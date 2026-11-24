<?php
/** Shared by backupService/api.php and admin_tab.php: data folder, settings defaults, JSON helpers. */

define('MBK_DATA', __DIR__ . DIRECTORY_SEPARATOR . 'data');
if (!is_dir(MBK_DATA)) @mkdir(MBK_DATA, 0777, true);

$MBK_DEFAULT_SETTINGS = array(
    'interval_hours'         => 4,     // start a new pass this often (a pass that runs over starts the next one straight away)
    'retention_days_active'  => 30,    // keep old versions this long for In Progress / On Hold jobs
    'retention_days_pending' => 0,     // ... and for Completed - Pending Zip (and later, until tape)
    'exclude_patterns'       => array(),// file or folder names, * and ? wildcards. Empty = back up everything
    'reserve_free_gb'        => 50,    // never fill a backup drive past this much free space
    'max_mb_per_sec'         => 0,     // copy speed cap, 0 = unlimited
    'delete_completed'       => true,  // delete the backup once a job reaches 5. Complete / 6. Archived
    'rebalance'              => true,  // move whole jobs between drives when one gets nearly full
    'job_overrides'          => array(),// "JOB123" => {"retention_days": 90, "skip": false}
    // When a drive is full (less free than cleanup_free_gb): delete the oldest things with nothing written in
    // cleanup_min_age_days, until it's back above it.
    'cleanup_free_gb'        => 500,
    'cleanup_min_age_days'   => 7,
    'cleanup_versions'       => true,  // A: old versions, sooner than their usual retention
    'cleanup_unowned'        => false, // B: folders on the backup drives that no job uses (old bvckup2 copies etc.)
    'protected_folders'      => array(),// never deleted by the cleanup: "Archive\Keep" (path under the backup folder) or "KEEP*" (folder name)
    'permanent_folders'      => array(),// always backed up, whatever the database says: "\\192.168.1.14\Projects2\Library"
);

function mbk_read_json($name, $default)
{
    $p = MBK_DATA . DIRECTORY_SEPARATOR . $name;
    if (!file_exists($p)) return $default;
    $raw = @file_get_contents($p);
    if ($raw === false) return $default;
    $d = json_decode($raw, true);
    return is_array($d) ? $d : $default;
}

function mbk_write_json($name, $data)
{
    $p = MBK_DATA . DIRECTORY_SEPARATOR . $name;
    $tmp = $p . '.' . getmypid() . '.tmp';
    if (@file_put_contents($tmp, json_encode($data, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES)) === false) return false;
    if (!@rename($tmp, $p)) { @unlink($tmp); return false; }
    return true;
}

/** Run $fn(current) -> new under an exclusive lock on data/<name>.lock. */
function mbk_locked($name, $default, $fn)
{
    $lock = fopen(MBK_DATA . DIRECTORY_SEPARATOR . $name . '.lock', 'c');
    if ($lock) flock($lock, LOCK_EX);
    $cur = mbk_read_json($name, $default);
    $res = $fn($cur);
    mbk_write_json($name, $res[0]);
    if ($lock) { flock($lock, LOCK_UN); fclose($lock); }
    return $res[1];
}

function mbk_settings()
{
    global $MBK_DEFAULT_SETTINGS;
    $s = array_merge($MBK_DEFAULT_SETTINGS, mbk_read_json('settings.json', array()));
    if (!is_array($s['job_overrides']) || empty($s['job_overrides'])) $s['job_overrides'] = new stdClass(); // JSON {} not []
    return $s;
}
