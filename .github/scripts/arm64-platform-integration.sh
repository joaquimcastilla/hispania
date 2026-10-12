#!/usr/bin/env bash
set -Eeuo pipefail

archive="${HISPANIA_ARM64_ARTIFACT_PATH:-/opt/hispania-ci-integration/artifacts/hispania-platform-pi4-arm64-working-tree-20261011.tar.gz}"
expected_sha="${HISPANIA_ARM64_ARTIFACT_SHA256:-b484edf386c203539708004cba8ad424db7dd52aee8da29bbbe70817c3f077b4}"
expected_git_sha="${HISPANIA_ARM64_ARTIFACT_GIT_SHA:-}"
readonly data_root=/var/lib/hispania-ci-integration/staging
readonly report_root=/opt/hispania-ci-integration/tests
app_dll=Hispania.Platform.Server.dll

safe_log_tail() {
  [[ -s "${log:-}" ]] || return 0
  python3 - "${log}" "${admin_token:-}" "${bootstrap_token:-}" "${probe_password:-}" <<'PY'
import pathlib, sys
text=pathlib.Path(sys.argv[1]).read_text(errors='replace')
for secret in sys.argv[2:]:
    if secret: text=text.replace(secret, '[REDACTED]')
print('\n'.join(text.splitlines()[-60:]))
PY
}
candidate_env() {
  local name value
  local -a unset_args=()
  while IFS='=' read -r name value; do
    case "$name" in HISPANIA__*|ASPNETCORE_URLS|ASPNETCORE_ENVIRONMENT|DOTNET_ENVIRONMENT) unset_args+=(-u "$name");; esac
  done < <(env)
  if ((${#unset_args[@]})); then env "${unset_args[@]}" "$@"; else env "$@"; fi
}
fail() { printf 'FAIL: %s\n' "$*" >&2; safe_log_tail >&2; exit 1; }
thermal_guard() {
  local temp available
  temp="$(cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null || echo 0)"
  [[ ! "$temp" =~ ^[0-9]+$ || "$temp" -le 75000 ]] || fail "temperature ${temp}mC exceeds 75C"
  available="$(awk '/MemAvailable:/ {print $2}' /proc/meminfo)"
  [[ "$available" -ge 1048576 ]] || fail "available memory below 1 GiB (${available} KiB)"
}

[[ "$(uname -m)" == aarch64 ]] || fail "expected aarch64, got $(uname -m)"
dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 10\.' || fail 'ASP.NET Core Runtime 10 is missing'
cd /
[[ -r "$archive" ]] || fail "pre-provisioned artifact missing: $archive"
actual_sha="$(sha256sum "$archive" | awk '{print $1}')"
[[ "$actual_sha" == "$expected_sha" ]] || fail "artifact SHA-256 mismatch: $actual_sha"

[[ ! -L "$data_root" && ! -L "$report_root" ]] || fail 'staging or report root must not be a symlink'
mkdir -p "$data_root" "$report_root"
[[ "$(realpath -e "$data_root")" == /var/lib/hispania-ci-integration/staging ]] || fail 'staging root resolved outside the dedicated CI path'
[[ "$(realpath -e "$report_root")" == /opt/hispania-ci-integration/tests ]] || fail 'report root resolved outside the dedicated CI path'
run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
run_dir="$(mktemp -d "$data_root/run.XXXXXX")"
[[ "$(dirname "$run_dir")" == "$data_root" && "$(basename "$run_dir")" =~ ^run\.[A-Za-z0-9]+$ ]] || fail 'temporary run directory ownership check failed'
package="$run_dir/package"
home="$run_dir/home"
mkdir -p "$package"
log="$run_dir/server.log"
metrics="$run_dir/metrics.txt"
report="$report_root/arm64-platform-integration-$run_id.txt"
pid=''
port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
base="http://127.0.0.1:$port"
admin_token="$(openssl rand -hex 32)"
bootstrap_token="$(openssl rand -hex 32)"
probe_password="$(openssl rand -hex 20)"
username="arm64-integration-$run_id"
coordinator_before="$(systemctl is-active hispania-platform.service 2>/dev/null || true)"
start_ms=0
ready_ms=0
started_utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
printf 'available_memory_kib_at_start=%s\ntemperature_millidegrees_at_start=%s\n' \
  "$(awk '/MemAvailable:/ {print $2}' /proc/meminfo)" "$(cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null || echo unavailable)" >> "$metrics"

stop_server() {
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill -TERM "$pid"
    for _ in $(seq 1 100); do kill -0 "$pid" 2>/dev/null || break; sleep 0.1; done
    if kill -0 "$pid" 2>/dev/null; then kill -KILL "$pid"; wait "$pid" || true; return 1; fi
    wait "$pid" || return 1
  fi
  pid=''
}
cleanup() {
  local result=$?
  trap - EXIT TERM INT
  stop_server || result=1
  if grep -Eiq 'Input/output error|database disk image is malformed|SQLITE_CORRUPT|SQLite Error 11' "$log" 2>/dev/null; then result=1; fi
  {
    printf 'result=%s\nartifact_sha256=%s\n' "$([[ $result -eq 0 ]] && echo PASS || echo FAIL)" "$actual_sha"
    printf 'architecture=%s\nready_ms=%s\n' "$(uname -m)" "$ready_ms"
    printf 'coordinator_state_before=%s\ncoordinator_state_after=%s\n' "$coordinator_before" "$(systemctl is-active hispania-platform.service 2>/dev/null || true)"
    [[ -f "$metrics" ]] && cat "$metrics"
    printf 'temperature_millidegrees='; cat /sys/class/thermal/thermal_zone0/temp 2>&1 || true
    printf 'throttled='; vcgencmd get_throttled 2>&1 || true
    if kernel_errors="$(journalctl -k --since "$started_utc" -p err..alert --no-pager 2>/dev/null)"; then
      printf 'kernel_error_count=%s\n' "$(printf '%s\n' "$kernel_errors" | sed '/^-- No entries --$/d' | wc -l)"
    else
      printf 'kernel_error_count=unavailable\n'
    fi
    printf 'available_memory_kib='; awk '/MemAvailable:/ {print $2}' /proc/meminfo
    printf 'disk_available='; df -h / | awk 'NR==2 {print $4}'
    printf 'error_log_lines='; (grep -Eic '(^|[[:space:]])(fail|error|exception)([[:space:]:]|$)' "$log" || true)
    printf 'server_log=%s\n' "$report.server.log"
  } > "$report"
  [[ ! -f "$log" ]] || cp "$log" "$report.server.log"
  cat "$report"
  if [[ "$(dirname "$run_dir")" == "$data_root" && "$(basename "$run_dir")" =~ ^run\.[A-Za-z0-9]+$ && ! -L "$run_dir" ]]; then
    rm -rf -- "$run_dir"
  else
    result=1
  fi
  exit "$result"
}
resource_sample() {
  local stage="$1" current max rss cpu temp
  current="$(cat /sys/fs/cgroup/memory.current 2>/dev/null || echo unavailable)"
  max="$(cat /sys/fs/cgroup/memory.max 2>/dev/null || echo unavailable)"
  rss='unavailable'; cpu='unavailable'
  if [[ -n "$pid" ]]; then
    rss="$(awk '/VmRSS:/ {print $2}' "/proc/$pid/status" 2>/dev/null || echo unavailable)"
    cpu="$(ps -p "$pid" -o %cpu= 2>/dev/null | xargs || echo unavailable)"
  fi
  temp="$(cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null || echo unavailable)"
  printf 'resource_sample stage=%s available_kib=%s cgroup_current=%s cgroup_max=%s rss_kib=%s cpu_percent=%s temperature_millidegrees=%s\n' \
    "$stage" "$(awk '/MemAvailable:/ {print $2}' /proc/meminfo)" "$current" "$max" "$rss" "$cpu" "$temp"
}
trap cleanup EXIT TERM INT

for entry in $(tar -tzf "$archive"); do
  [[ "$entry" != /* && "/$entry/" != *"/../"* ]] || fail "unsafe archive path: $entry"
done
tar -xzf "$archive" --no-same-owner --no-same-permissions -C "$package"
if [[ -n "$expected_git_sha" ]]; then
  [[ -s "$package/server/$app_dll" ]] || fail 'candidate Platform Server publish files missing'
else
  [[ -s "$package/$app_dll" ]] || fail 'Platform Server publish files missing'
fi
if [[ -n "$expected_git_sha" ]]; then
  python3 - "$package/artifact-manifest.json" "$package" "$expected_git_sha" <<'PY'
import hashlib, json, pathlib, sys
manifest_path, root, expected = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), sys.argv[3]
m = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
assert m.get('gitSha') == expected and m.get('sourceDirty') is False, 'artifact source commit/cleanliness mismatch'
assert m.get('sourceBranch') == 'codex/hispania-0.7.0-step2-1-production-readiness', 'artifact source branch mismatch'
assert m.get('targetRid') == 'linux-arm64' and m.get('architecture') == 'aarch64', 'artifact target mismatch'
expected_manifest = __import__('os').environ.get('HISPANIA_ARM64_ARTIFACT_MANIFEST_SHA256')
if expected_manifest: assert hashlib.sha256(manifest_path.read_bytes()).hexdigest() == expected_manifest, 'artifact manifest SHA mismatch'
files = m.get('files')
assert isinstance(files, dict) and files, 'manifest file list missing'
expected_paths = set()
for relative, digest in files.items():
    path = pathlib.PurePosixPath(relative)
    assert not path.is_absolute() and '..' not in path.parts, 'unsafe manifest path'
    full = root.joinpath(*path.parts)
    assert full.is_file(), f'manifest file missing: {relative}'
    assert hashlib.sha256(full.read_bytes()).hexdigest() == digest.lower(), f'manifest digest mismatch: {relative}'
    expected_paths.add(full.resolve())
actual_paths = {p.resolve() for p in root.rglob('*') if p.is_file() and p != manifest_path}
assert actual_paths == expected_paths, 'artifact contains unmanifested files or misses manifest files'
PY
  [[ -s "$package/server/Hispania.Platform.Server.deps.json" ]] || fail 'candidate dependency manifest missing'
  if find "$package" -type f -printf '%f\n' | grep -Eiq 'WindowsServices|Hispania\.(Media|Inventory)|FFmpeg'; then fail 'candidate artifact contains excluded WindowsServices, Media, Inventory or FFmpeg files'; fi
  if grep -Eiq 'WindowsServices|Hispania\.(Media|Inventory)|FFmpeg' "$package/server/Hispania.Platform.Server.deps.json"; then fail 'candidate dependency manifest references excluded components'; fi
else
  if find "$package" -type f -printf '%f\n' | grep -Eiq 'Hispania\.(Media|Inventory)|FFmpeg'; then fail 'artifact contains Media, Inventory or FFmpeg'; fi
  if grep -Eiq 'Hispania\.(Media|Inventory)|FFmpeg' "$package/Hispania.Platform.Server.deps.json"; then fail 'dependency manifest references Media, Inventory or FFmpeg'; fi
fi
sqlite_lib="$package/libe_sqlite3.so"
if [[ -n "$expected_git_sha" ]]; then sqlite_lib="$package/server/libe_sqlite3.so"; fi
[[ -s "$sqlite_lib" ]] || fail 'SQLite native library missing'
readelf -h "$sqlite_lib" | grep -q 'Machine:.*AArch64' || fail 'SQLite native library is not AArch64'
printf 'artifact_files=%s\nartifact_bytes=%s\nsqlite_sha256=%s\n' \
  "$(find "$package" -type f | wc -l)" "$(du -sb "$package" | awk '{print $1}')" "$(sha256sum "$sqlite_lib" | awk '{print $1}')" >> "$metrics"

start_server() {
  thermal_guard
  if [[ -n "$expected_git_sha" ]]; then
    candidate_env HISPANIA_CI_DIAGNOSTICS=1 dotnet "$installed_server" --home "$home" >>"$log" 2>&1 &
  else
    ASPNETCORE_URLS="$base" \
    Platform__DataPath="$run_dir/data" \
    Modules__ExtensionsPath="$run_dir/extensions" \
    Identity__Bootstrap__Token="$bootstrap_token" \
    Modules__AdminToken="$admin_token" \
    Logging__LogLevel__Default=Warning \
      dotnet "$package/$app_dll" >>"$log" 2>&1 &
  fi
  pid=$!
  start_ms="$(date +%s%3N)"
  for _ in $(seq 1 90); do
    thermal_guard
    kill -0 "$pid" 2>/dev/null || fail 'staging HISPANIA exited before readiness'
    code="$(curl --silent --output /dev/null --max-time 1 --write-out '%{http_code}' "$base/readyz" || true)"
    if [[ "$code" == 200 ]]; then ready_ms=$(( $(date +%s%3N) - start_ms )); resource_sample server-ready; return; fi
    sleep 0.5
  done
  fail 'staging HISPANIA did not become ready within 45 seconds'
}
expect_status() {
  local expected="$1" method="$2" path="$3" body="${4:-}" auth="${5:-}" actual
  thermal_guard
  local args=(--silent --show-error --max-time 60 --output "$run_dir/response.json" --write-out '%{http_code}' -X "$method")
  [[ -z "$auth" ]] || args+=(-H "Authorization: $auth")
  [[ -z "$body" ]] || args+=(-H 'Content-Type: application/json' --data "$body")
  actual="$(curl "${args[@]}" "$base$path")" || fail "request failed: $method $path"
  [[ "$actual" == "$expected" ]] || fail "$method $path returned HTTP $actual, expected $expected"
  resource_sample "${method}-${path}"
}

if [[ -n "$expected_git_sha" ]]; then
  dotnet "$package/deployment/Hispania.Deployment.dll" install "$package" "$home" server >>"$log" 2>&1 || fail 'candidate artifact staging installation failed'
  installed_server="$(realpath -e "$run_dir/home/app/server/$app_dll")"
  [[ "$installed_server" == "$run_dir/home/app/server/$app_dll" ]] || fail 'installed server path resolves outside HOME/app/server'
  for file in "$app_dll" Hispania.Hosting.dll; do
    package_sha="$(sha256sum "$package/server/$file" | awk '{print $1}')"
    installed_sha="$(sha256sum "$home/app/server/$file" | awk '{print $1}')"
    [[ "$package_sha" == "$installed_sha" ]] || fail "installed assembly differs from verified package: $file"
    printf 'installed_%s_sha256=%s\n' "${file%.dll}" "$installed_sha" >> "$metrics"
  done
  printf 'installed_server_path=%s\n' "$installed_server" >> "$metrics"
  if diagnostic_output="$(candidate_env HISPANIA_CI_DIAGNOSTICS=1 dotnet "$installed_server" --home "$run_dir/diagnostic-probe-missing" 2>&1)"; then
    fail 'invalid-home diagnostic probe unexpectedly succeeded'
  else
    diagnostic_exit=$?
  fi
  printf '%s\n' "$diagnostic_output"
  [[ "$diagnostic_output" == *'"status":"startup_diagnostic"'* ]] || fail 'opt-in startup diagnostics did not reach the candidate process'
  printf 'diagnostic_probe_exit=%s\n' "$diagnostic_exit" >> "$metrics"
  python3 - "$home/config/production.json" "$base" "$username" <<'PY'
import json, pathlib, sys
p=pathlib.Path(sys.argv[1]); cfg=json.loads(p.read_text(encoding='utf-8-sig'))
cfg['endpoint']=sys.argv[2]; cfg['bootstrapUsername']=sys.argv[3]
cfg['bootstrapSecret']='env:CI_BOOTSTRAP'; cfg['bootstrapPasswordSecret']='env:CI_PASSWORD'
p.write_text(json.dumps(cfg, indent=2)+'\n', encoding='utf-8')
PY
  if candidate_env HISPANIA_SECRET_CI_BOOTSTRAP="$bootstrap_token" HISPANIA_SECRET_CI_PASSWORD="$probe_password" \
    dotnet "$installed_server" --home "$home" --check >>"$log" 2>&1; then :; else check_exit=$?; fail "candidate installation --check failed (exit=$check_exit)"; fi
  candidate_env HISPANIA_SECRET_CI_BOOTSTRAP="$bootstrap_token" HISPANIA_SECRET_CI_PASSWORD="$probe_password" \
    dotnet "$installed_server" --home "$home" --bootstrap >>"$log" 2>&1 || fail 'candidate administrator bootstrap failed'
  python3 - "$home/config/production.json" <<'PY'
import json, pathlib, sys
p=pathlib.Path(sys.argv[1]); cfg=json.loads(p.read_text(encoding='utf-8-sig'))
cfg.pop('bootstrapSecret', None); cfg.pop('bootstrapPasswordSecret', None); cfg.pop('bootstrapUsername', None)
p.write_text(json.dumps(cfg, indent=2)+'\n', encoding='utf-8')
PY
fi
start_server
printf 'first_ready_ms=%s\n' "$ready_ms" >> "$metrics"
for route in /healthz /readyz /api/health/live /api/health/ready; do expect_status 200 GET "$route"; done
if [[ -z "$expected_git_sha" ]]; then
  create_body="$(python3 -c 'import json,sys; print(json.dumps({"username":sys.argv[1],"displayName":"ARM64 integration","password":sys.argv[2]}))' "$username" "$probe_password")"
  expect_status 201 POST /api/identity/users "$create_body" "Bearer $admin_token"
  user_id="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["id"]["value"])' "$run_dir/response.json")"
  expect_status 200 POST /api/authorization/bootstrap-admin "{\"userId\":\"$user_id\"}" "Bootstrap $bootstrap_token"
fi
login_body="$(python3 -c 'import json,sys; print(json.dumps({"username":sys.argv[1],"password":sys.argv[2]}))' "$username" "$probe_password")"
expect_status 200 POST /api/identity/login "$login_body"
session="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sessionToken"])' "$run_dir/response.json")"
for route in /api/platform/modules /api/platform/jobs/health /api/health/details /api/platform/observability /api/platform/admin/security/endpoints; do expect_status 200 GET "$route" '' "Session $session"; done
if [[ -n "$expected_git_sha" ]]; then
  for route in /api/inventory/items /api/v1/media /api/v1/media/00000000-0000-0000-0000-000000000001/stream; do
    expect_status 404 GET "$route"
  done
fi
db_root="$run_dir/data"
if [[ -n "$expected_git_sha" ]]; then db_root="$home/state"; fi
db_count="$(find "$db_root" -type f -name '*.sqlite' | wc -l)"
[[ "$db_count" -gt 0 ]] || fail 'no isolated SQLite store was created'
if grep -Eiq 'Input/output error|database disk image is malformed|SQLITE_CORRUPT|SQLite Error 11' "$log"; then fail 'staging log reports storage I/O or SQLite corruption'; fi
printf 'sqlite_files_before_restart=%s\n' "$db_count" >> "$metrics"
printf 'sampled_rss_kib=%s\nsampled_cpu_percent=%s\n' "$(awk '/VmRSS:/ {print $2}' "/proc/$pid/status")" "$(ps -p "$pid" -o %cpu= | xargs)" >> "$metrics"
stop_server || fail 'staging process did not shut down cleanly'
start_server
printf 'restart_ready_ms=%s\n' "$ready_ms" >> "$metrics"
expect_status 200 POST /api/identity/login "$login_body"
db_count_after="$(find "$db_root" -type f -name '*.sqlite' | wc -l)"
[[ "$db_count_after" -ge "$db_count" ]] || fail 'isolated SQLite stores did not persist across restart'
printf 'sqlite_files_after_restart=%s\n' "$db_count_after" >> "$metrics"
stop_server || fail 'staging process did not shut down cleanly after restart'
python3 - "$db_root" <<'PY'
import pathlib, sqlite3, sys
files=list(pathlib.Path(sys.argv[1]).rglob('*.sqlite'))
assert files, 'SQLite stores missing after shutdown'
for path in files:
    db=sqlite3.connect(f'file:{path}?mode=ro', uri=True)
    try: assert db.execute('PRAGMA integrity_check').fetchone()==('ok',), str(path)
    finally: db.close()
print(f'sqlite_integrity_check=PASS files={len(files)}')
PY
[[ "$(systemctl is-active hispania-platform.service 2>/dev/null || true)" == "$coordinator_before" ]] || fail 'Coordinator service state changed during staging smoke'
printf 'PASS: isolated ARM64 Platform integration smoke completed\n'
