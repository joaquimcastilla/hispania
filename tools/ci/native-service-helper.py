#!/usr/bin/python3 -I
"""Root-owned CI broker. No caller-supplied commands, accounts or unit templates."""
import os
import pathlib
import pwd
import re
import stat
import subprocess
import sys

ROOT = pathlib.Path('/opt/hispania-phase18-ci')
TRUSTED = '/usr/local/lib/hispania-phase21/install-systemd.sh'

def main():
    if os.geteuid() != 0 or len(sys.argv) != 4:
        raise ValueError('Expected privileged action, fixture home and role')
    action, supplied, role = sys.argv[1:]
    if action not in ('install', 'start', 'stop', 'restart', 'status', 'uninstall', 'journal', 'retained-secret', 'reclaim') or role not in ('server', 'worker'):
        raise ValueError('Unsupported operation')
    home = pathlib.Path(supplied)
    if home.parent != ROOT or not re.fullmatch(r'hispania-phase18 isolated ñ [0-9a-f]{32}', home.name):
        raise ValueError('Fixture outside dedicated CI root')
    for ancestor in (ROOT, home):
        if ancestor.is_symlink() or not ancestor.is_dir():
            raise ValueError('Unsafe fixture root')
    # Reject links before any privileged traversal; no generic chown/rm authority.
    for current, dirs, files in os.walk(home, followlinks=False):
        for name in dirs + files:
            item = pathlib.Path(current) / name
            info = item.lstat()
            if stat.S_ISLNK(info.st_mode) or (stat.S_ISREG(info.st_mode) and info.st_nlink != 1):
                raise ValueError('Linked fixture content rejected')
    env = {'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LANG': 'C.UTF-8'}
    unit = 'hispania-' + role + '.service'
    def run(args):
        subprocess.run(args, env=env, cwd='/', check=True, timeout=55)
    if action == 'journal':
        run(['/usr/bin/journalctl', '--unit=' + unit, '--no-pager', '--lines=80'])
    elif action == 'retained-secret':
        if not (home / 'secrets/NATIVE_CANARY.secret').is_file():
            raise ValueError('Retained secret missing')
    elif action == 'reclaim':
        runner = pwd.getpwnam('gha-hispania')
        for current, dirs, files in os.walk(home, followlinks=False):
            for item in [pathlib.Path(current)] + [pathlib.Path(current) / x for x in files]:
                os.chown(item, runner.pw_uid, runner.pw_gid, follow_symlinks=False)
                os.chmod(item, item.stat().st_mode | stat.S_IRUSR | stat.S_IWUSR | (stat.S_IXUSR if item.is_dir() else 0))
    else:
        run(['/bin/sh', TRUSTED, action, str(home), role, 'hispania'])

try:
    main()
except Exception:
    print('Restricted native CI service operation failed; no secret content emitted.', file=sys.stderr)
    sys.exit(1)
