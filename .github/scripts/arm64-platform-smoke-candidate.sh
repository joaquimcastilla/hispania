#!/usr/bin/env bash
set -Eeuo pipefail

archive="${HISPANIA_ARM64_ARTIFACT_PATH:?candidate archive path required}"
expected_sha="${HISPANIA_ARM64_ARTIFACT_SHA256:?candidate archive SHA required}"
expected_git_sha="${HISPANIA_ARM64_ARTIFACT_GIT_SHA:?candidate commit required}"
expected_manifest_sha="${HISPANIA_ARM64_ARTIFACT_MANIFEST_SHA256:?manifest SHA required}"
[[ "$(uname -m)" == aarch64 ]] || { echo 'FAIL: expected aarch64' >&2; exit 1; }
dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 10\.' || { echo 'FAIL: ASP.NET Core 10 runtime missing' >&2; exit 1; }
actual_sha="$(sha256sum "$archive" | awk '{print $1}')"
[[ "$actual_sha" == "$expected_sha" ]] || { echo 'FAIL: candidate archive SHA-256 mismatch' >&2; exit 1; }

temp_root="$(mktemp -d "$RUNNER_TEMP/hispania-pi3-smoke-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT.XXXXXX")"
[[ "$(dirname "$temp_root")" == "$(realpath -e "$RUNNER_TEMP")" && ! -L "$temp_root" ]] || { echo 'FAIL: temp root escaped runner temp' >&2; exit 1; }
package="$temp_root/package"
home="$temp_root/home"
mkdir -m 700 "$package" "$home"
log="$temp_root/server.log"
pid=''
started_ms=0
ready_ms=0
first_ready_ms=0
restart_ready_ms=0
first_login_ms=0
restart_login_ms=0
sampled_rss_kib=unavailable
sampled_cpu_percent=unavailable
port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
base="http://127.0.0.1:$port"
token="$(openssl rand -hex 32)"
password="$(openssl rand -hex 20)"

stop_server() {
  if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
    kill -TERM "$pid"
    for _ in $(seq 1 100); do kill -0 "$pid" 2>/dev/null || break; sleep 0.1; done
    kill -0 "$pid" 2>/dev/null && { kill -KILL "$pid"; wait "$pid" || true; return 1; }
    wait "$pid"
  fi
  pid=''
}
cleanup() {
  result=$?
  trap - EXIT TERM INT
  stop_server || result=1
  printf 'result=%s\nsource_sha=%s\nartifact_sha256=%s\nmanifest_sha256=%s\narchitecture=%s\nfirst_ready_ms=%s\nfirst_login_ms=%s\nrestart_ready_ms=%s\nrestart_login_ms=%s\nsampled_rss_kib=%s\nsampled_cpu_percent=%s\n' \
    "$([[ $result -eq 0 ]] && echo PASS || echo FAIL)" "$expected_git_sha" "$actual_sha" "$expected_manifest_sha" "$(uname -m)" "$first_ready_ms" "$first_login_ms" "$restart_ready_ms" "$restart_login_ms" "$sampled_rss_kib" "$sampled_cpu_percent"
  printf 'temperature_millidegrees='; cat /sys/class/thermal/thermal_zone0/temp 2>&1 || true
  printf 'memory_available='; awk '/MemAvailable:/ {print $2 " KiB"}' /proc/meminfo
  if [[ -n "$pid" && -r "/proc/$pid/status" ]]; then printf 'sampled_rss_kib='; awk '/VmRSS:/ {print $2}' "/proc/$pid/status"; fi
  if [[ -d "$home/state" ]]; then printf 'sqlite_files=%s\n' "$(find "$home/state" -type f -name '*.sqlite' | wc -l)"; else printf 'sqlite_files=0\n'; fi
  printf 'error_log_lines='; grep -Eic '(^|[[:space:]])(fail|error|exception)([[:space:]:]|$)' "$log" 2>/dev/null || true
  if [[ -s "$log" ]]; then python3 - "$log" "$token" "$password" <<'PY'
import pathlib, sys
text=pathlib.Path(sys.argv[1]).read_text(errors='replace')
for secret in sys.argv[2:]:
    if secret: text=text.replace(secret, '[REDACTED]')
print(text)
PY
  fi
  [[ "$(dirname "$temp_root")" == "$(realpath -e "$RUNNER_TEMP")" && "$(basename "$temp_root")" =~ ^hispania-pi3-smoke-[0-9]+-[0-9]+\.[A-Za-z0-9]+$ && ! -L "$temp_root" ]] || result=1
  rm -rf -- "$temp_root"
  exit "$result"
}
trap cleanup EXIT TERM INT

python3 - "$archive" "$package" "$expected_git_sha" "$expected_manifest_sha" <<'PY'
import hashlib, json, pathlib, sys, tarfile
archive, root, sha, expected_manifest_sha = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), sys.argv[3], sys.argv[4]
with tarfile.open(archive, 'r:gz') as tf:
    members=tf.getmembers()
    for member in members:
        path=pathlib.PurePosixPath(member.name)
        assert not path.is_absolute() and '..' not in path.parts, f'unsafe tar path {member.name}'
        assert member.isfile() or member.isdir(), f'unsupported tar entry {member.name}'
    tf.extractall(root, members=members)
m_path=root/'artifact-manifest.json'
assert hashlib.sha256(m_path.read_bytes()).hexdigest()==expected_manifest_sha, 'manifest digest mismatch'
m=json.loads(m_path.read_text(encoding='utf-8-sig'))
assert m.get('gitSha')==sha and m.get('sourceDirty') is False
assert m.get('sourceBranch')=='codex/hispania-0.7.0-step2-1-production-readiness'
assert m.get('targetRid')=='linux-arm64' and m.get('architecture')=='aarch64'
files=m.get('files'); assert isinstance(files,dict) and files
expected=set()
for relative,digest in files.items():
    p=pathlib.PurePosixPath(relative); assert not p.is_absolute() and '..' not in p.parts
    f=root.joinpath(*p.parts); assert f.is_file() and hashlib.sha256(f.read_bytes()).hexdigest()==digest.lower(), relative
    expected.add(f.resolve())
actual={p.resolve() for p in root.rglob('*') if p.is_file() and p!=m_path}
assert actual==expected, 'package files differ from manifest'
for forbidden in ('WindowsServices','Hispania.Media','Hispania.Inventory','FFmpeg'):
    assert not any(forbidden.casefold() in p.name.casefold() for p in root.rglob('*') if p.is_file()), forbidden
deps=(root/'server/Hispania.Platform.Server.deps.json').read_text(encoding='utf-8')
for forbidden in ('WindowsServices','Hispania.Media','Hispania.Inventory','FFmpeg'):
    assert forbidden.casefold() not in deps.casefold(), forbidden
PY

dotnet "$package/deployment/Hispania.Deployment.dll" install "$package" "$home" server
python3 - "$home/config/production.json" "$base" <<'PY'
import json, pathlib, sys
p=pathlib.Path(sys.argv[1]); cfg=json.loads(p.read_text(encoding='utf-8-sig'))
cfg['endpoint']=sys.argv[2]; cfg['bootstrapUsername']='candidate-smoke'
cfg['bootstrapSecret']='file:BOOTSTRAP_TOKEN'; cfg['bootstrapPasswordSecret']='file:BOOTSTRAP_PASSWORD'
p.write_text(json.dumps(cfg,indent=2)+'\n',encoding='utf-8')
PY
printf '%s' "$token" > "$home/secrets/BOOTSTRAP_TOKEN.secret"
printf '%s' "$password" > "$home/secrets/BOOTSTRAP_PASSWORD.secret"
chmod 600 "$home/secrets/BOOTSTRAP_TOKEN.secret" "$home/secrets/BOOTSTRAP_PASSWORD.secret"
dotnet "$home/app/server/Hispania.Platform.Server.dll" --home "$home" --check
HISPANIA_SECRET_CI_BOOTSTRAP="$token" HISPANIA_SECRET_CI_PASSWORD="$password" dotnet "$home/app/server/Hispania.Platform.Server.dll" --home "$home" --bootstrap
python3 - "$home/config/production.json" <<'PY'
import json, pathlib, sys
p=pathlib.Path(sys.argv[1]); cfg=json.loads(p.read_text(encoding='utf-8-sig'))
cfg.pop('bootstrapSecret',None); cfg.pop('bootstrapPasswordSecret',None); cfg.pop('bootstrapUsername',None)
p.write_text(json.dumps(cfg,indent=2)+'\n',encoding='utf-8')
PY
rm -f "$home/secrets/BOOTSTRAP_TOKEN.secret" "$home/secrets/BOOTSTRAP_PASSWORD.secret"

start_server() {
  dotnet "$home/app/server/Hispania.Platform.Server.dll" --home "$home" >>"$log" 2>&1 & pid=$!
  started_ms="$(date +%s%3N)"
  for _ in $(seq 1 50); do
    kill -0 "$pid" || { cat "$log" >&2; exit 1; }
    code="$(curl --silent --output /dev/null --max-time 1 --write-out '%{http_code}' "$base/readyz" || true)"
    if [[ "$code" == 200 ]]; then ready_ms=$(( $(date +%s%3N)-started_ms )); return; fi
    sleep 0.2
  done
  echo 'FAIL: readiness timeout' >&2; exit 1
}
start_server
first_ready_ms="$ready_ms"
for endpoint in /healthz /readyz /api/health/live /api/health/ready; do
  [[ "$(curl --silent --output /dev/null --max-time 3 --write-out '%{http_code}' "$base$endpoint")" == 200 ]] || { echo "FAIL: $endpoint" >&2; exit 1; }
done
body="$(python3 -c 'import json,sys; print(json.dumps({"username":"candidate-smoke","password":sys.argv[1]}))' "$password")"
login_started_ms="$(date +%s%3N)"
code="$(curl --silent --show-error --max-time 30 --output "$temp_root/login.json" --write-out '%{http_code}' -H 'Content-Type: application/json' --data "$body" "$base/api/identity/login")"
first_login_ms=$(( $(date +%s%3N)-login_started_ms ))
[[ "$code" == 200 ]] || { echo "FAIL: login returned HTTP $code" >&2; exit 1; }
if grep -Eiq 'Input/output error|database disk image is malformed|SQLITE_CORRUPT|SQLite Error 11' "$log"; then echo 'FAIL: storage or SQLite corruption error observed' >&2; exit 1; fi
db_before="$(find "$home/state" -type f -name '*.sqlite' | wc -l)"
[[ "$db_before" -gt 0 ]] || { echo 'FAIL: no isolated SQLite state created' >&2; exit 1; }
sampled_rss_kib="$(awk '/VmRSS:/ {print $2}' "/proc/$pid/status" 2>/dev/null || echo unavailable)"
sampled_cpu_percent="$(ps -p "$pid" -o %cpu= 2>/dev/null | xargs || echo unavailable)"
stop_server
start_server
restart_ready_ms="$ready_ms"
login_started_ms="$(date +%s%3N)"
code="$(curl --silent --show-error --max-time 30 --output "$temp_root/login-after-restart.json" --write-out '%{http_code}' -H 'Content-Type: application/json' --data "$body" "$base/api/identity/login")"
restart_login_ms=$(( $(date +%s%3N)-login_started_ms ))
[[ "$code" == 200 ]] || { echo "FAIL: persisted login returned HTTP $code" >&2; exit 1; }
if grep -Eiq 'Input/output error|database disk image is malformed|SQLITE_CORRUPT|SQLite Error 11' "$log"; then echo 'FAIL: storage or SQLite corruption error observed' >&2; exit 1; fi
db_after="$(find "$home/state" -type f -name '*.sqlite' | wc -l)"
[[ "$db_after" -ge "$db_before" ]] || { echo 'FAIL: SQLite state did not persist' >&2; exit 1; }
stop_server
python3 - "$home/state" <<'PY'
import pathlib, sqlite3, sys
files=list(pathlib.Path(sys.argv[1]).rglob('*.sqlite'))
assert files, 'SQLite stores missing after shutdown'
for path in files:
    db=sqlite3.connect(f'file:{path}?mode=ro', uri=True)
    try: assert db.execute('PRAGMA integrity_check').fetchone()==('ok',), str(path)
    finally: db.close()
print(f'sqlite_integrity_check=PASS files={len(files)}')
PY
printf 'PASS: Pi 3B+ candidate artifact native smoke; first_ready_ms=%s restart_ready_ms=%s sqlite_before=%s sqlite_after=%s\n' "$first_ready_ms" "$restart_ready_ms" "$db_before" "$db_after"
