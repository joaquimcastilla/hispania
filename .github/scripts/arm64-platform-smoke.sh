#!/usr/bin/env bash
set -Eeuo pipefail

readonly archive="${HISPANIA_ARTIFACT_ARCHIVE:-/opt/hispania-ci/artifacts/hispania-platform-linux-arm64-step2-working-tree-20261011.tar.gz}"
readonly expected_sha="${HISPANIA_ARTIFACT_SHA256:-}"
readonly data_root="${HISPANIA_TEST_DATA_ROOT:-/var/lib/hispania-ci/platform-test}"
readonly report_root="${HISPANIA_REPORT_ROOT:-/opt/hispania-ci/tests}"
readonly app_dll="Hispania.Platform.Server.dll"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
thermal_guard() {
  local temperature available
  temperature="$(cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null || echo 0)"
  if [[ "$temperature" =~ ^[0-9]+$ && "$temperature" -gt 75000 ]]; then fail "temperature reached ${temperature}mC; stopping the smoke"; fi
  available="$(awk '/MemAvailable:/ {print $2}' /proc/meminfo)"
  [[ "$available" -ge 150000 ]] || fail "only ${available} KiB memory is available; stopping the smoke"
}
[[ "$(uname -m)" == "aarch64" ]] || fail "expected aarch64, got $(uname -m)"
dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 10\.' || fail 'ASP.NET Core Runtime 10 is missing'
[[ -r "$archive" ]] || fail "authorized artifact missing: $archive"
[[ "$expected_sha" =~ ^[[:xdigit:]]{64}$ ]] || fail 'HISPANIA_ARTIFACT_SHA256 must be a 64-character SHA-256'

actual_sha="$(sha256sum "$archive" | awk '{print $1}')"
[[ "${actual_sha,,}" == "${expected_sha,,}" ]] || fail "artifact SHA-256 mismatch: $actual_sha"

mkdir -p "$data_root" "$report_root"
run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
run_dir="$(mktemp -d "$data_root/run.XXXXXX")"
report="$report_root/arm64-platform-smoke-$run_id.txt"
package="$run_dir/package"
mkdir -p "$package"
server_log="$run_dir/server.log"
metrics="$run_dir/metrics.txt"
pid=''
port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
base="http://127.0.0.1:$port"
admin_token="$(openssl rand -hex 32)"
bootstrap_token="$(openssl rand -hex 32)"
probe_password="$(openssl rand -hex 20)"
username="arm64-smoke-$run_id"
start_ms=0
ready_ms=0

stop_server() {
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill -TERM "$pid"
    for _ in $(seq 1 100); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.1
    done
    if kill -0 "$pid" 2>/dev/null; then kill -KILL "$pid"; wait "$pid" || true; return 1; fi
    wait "$pid" || return 1
  fi
  pid=''
}

cleanup() {
  local result=$?
  stop_server || result=1
  if [[ -f "$server_log" ]]; then cp "$server_log" "$report.server.log"; fi
  {
    printf 'result=%s\nartifact_sha256=%s\n' "$([[ $result -eq 0 ]] && echo PASS || echo FAIL)" "$actual_sha"
    printf 'architecture=%s\nready_ms=%s\n' "$(uname -m)" "$ready_ms"
    [[ -f "$metrics" ]] && cat "$metrics"
    printf 'temperature_millidegrees='; cat /sys/class/thermal/thermal_zone0/temp 2>&1 || true
    printf 'error_log_lines='; (grep -Eic '(^|[[:space:]])(fail|error|exception)([[:space:]:]|$)' "$server_log" || true)
    printf 'server_log=%s\n' "$report.server.log"
  } > "$report"
  cat "$report"
  if [[ $result -ne 0 ]]; then printf 'FAIL: smoke failed; report and server log retained above\n' >&2; fi
  rm -rf -- "$run_dir"
  exit "$result"
}
trap cleanup EXIT

for entry in $(tar -tzf "$archive"); do
  [[ "$entry" != /* && "/$entry/" != *"/../"* ]] || fail "unsafe path in artifact archive: $entry"
done
tar -xzf "$archive" --no-same-owner --no-same-permissions -C "$package"
[[ -s "$package/$app_dll" && -s "$package/Hispania.Platform.Server.deps.json" ]] || fail 'Platform-only server publish files are missing'
if find "$package" -type f -printf '%f\n' | grep -Eiq 'Hispania\.(Media|Inventory)|FFmpeg'; then fail 'artifact contains Media, Inventory or FFmpeg assemblies'; fi
if grep -Eiq 'Hispania\.(Media|Inventory)|FFmpeg' "$package/Hispania.Platform.Server.deps.json"; then fail 'dependency manifest references Media, Inventory or FFmpeg'; fi
sqlite="$package/libe_sqlite3.so"
[[ -s "$sqlite" ]] || fail 'SQLite native library is missing'
readelf -h "$sqlite" | grep -q 'Machine:.*AArch64' || fail 'SQLite native library is not ELF AArch64'
ldd "$sqlite" | tee "$metrics"
if grep -q 'not found' "$metrics"; then fail 'SQLite native library dependencies are unresolved'; fi
printf 'artifact_files=%s\nartifact_bytes=%s\nsqlite_sha256=%s\n' \
  "$(find "$package" -type f | wc -l)" "$(du -sb "$package" | awk '{print $1}')" "$(sha256sum "$sqlite" | awk '{print $1}')" >> "$metrics"

start_server() {
  thermal_guard
  ASPNETCORE_URLS="$base" \
  Platform__DataPath="$run_dir/state" \
  Modules__ExtensionsPath="$run_dir/extensions" \
  Identity__Bootstrap__Token="$bootstrap_token" \
  Modules__AdminToken="$admin_token" \
  Logging__LogLevel__Default=Warning \
    dotnet "$package/$app_dll" >>"$server_log" 2>&1 &
  pid=$!
  start_ms="$(date +%s%3N)"
  for _ in $(seq 1 90); do
    thermal_guard
    kill -0 "$pid" 2>/dev/null || fail 'HISPANIA process exited before readiness'
    code="$(curl --silent --output /dev/null --max-time 1 --write-out '%{http_code}' "$base/readyz" || true)"
    if [[ "$code" == 200 ]]; then ready_ms=$(( $(date +%s%3N) - start_ms )); return; fi
    sleep 0.5
  done
  fail 'HISPANIA did not become ready within 45 seconds'
}

expect_status() {
  local expected="$1" method="$2" path="$3" body="${4:-}" auth="${5:-}"
  thermal_guard
  local args=(--silent --show-error --max-time 60 --output "$run_dir/response.json" --write-out '%{http_code}' -X "$method")
  [[ -z "$auth" ]] || args+=(-H "Authorization: $auth")
  [[ -z "$body" ]] || args+=(-H 'Content-Type: application/json' --data "$body")
  local actual
  actual="$(curl "${args[@]}" "$base$path")" || fail "request failed: $method $path"
  [[ "$actual" == "$expected" ]] || fail "$method $path returned HTTP $actual, expected $expected"
}

start_server
printf 'first_ready_ms=%s\n' "$ready_ms" >> "$metrics"
for route in /healthz /readyz /api/health/live /api/health/ready; do expect_status 200 GET "$route"; done
for route in /api/v1/media /api/inventory/items; do expect_status 404 GET "$route"; done

create_body="$(python3 -c 'import json,sys; print(json.dumps({"username":sys.argv[1],"displayName":"ARM64 smoke","password":sys.argv[2]}))' "$username" "$probe_password")"
expect_status 201 POST /api/identity/users "$create_body" "Bearer $admin_token"
user_id="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["id"]["value"])' "$run_dir/response.json")"
expect_status 200 POST /api/authorization/bootstrap-admin "{\"userId\":\"$user_id\"}" "Bootstrap $bootstrap_token"
login_body="$(python3 -c 'import json,sys; print(json.dumps({"username":sys.argv[1],"password":sys.argv[2]}))' "$username" "$probe_password")"
expect_status 200 POST /api/identity/login "$login_body"
session="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sessionToken"])' "$run_dir/response.json")"
for route in /api/platform/modules /api/platform/jobs/health /api/health/details /api/platform/observability /api/platform/admin/security/endpoints; do expect_status 200 GET "$route" '' "Session $session"; done
db_count="$(find "$run_dir/state" -type f -name '*.sqlite' | wc -l)"
[[ "$db_count" -gt 0 ]] || fail 'no SQLite database was created'
printf 'sqlite_files_before_restart=%s\n' "$db_count" >> "$metrics"
rss_kib="$(awk '/VmRSS:/ {print $2}' "/proc/$pid/status")"
cpu_pct="$(ps -p "$pid" -o %cpu= | xargs)"
printf 'sampled_rss_kib=%s\nsampled_cpu_percent=%s\n' "$rss_kib" "$cpu_pct" >> "$metrics"
stop_server || fail 'HISPANIA did not shut down cleanly'

start_server
printf 'restart_ready_ms=%s\n' "$ready_ms" >> "$metrics"
expect_status 200 POST /api/identity/login "$login_body"
db_count_after="$(find "$run_dir/state" -type f -name '*.sqlite' | wc -l)"
[[ "$db_count_after" -ge "$db_count" ]] || fail 'SQLite stores did not persist across restart'
printf 'sqlite_files_after_restart=%s\n' "$db_count_after" >> "$metrics"
stop_server || fail 'HISPANIA did not shut down cleanly after restart'
printf 'PASS: ARM64 Platform-only smoke completed\n'
