#!/bin/sh
set -eu
# Provision the root with the deployment CLI first. This script only manages services.
action=${1:?install|start|stop|restart|status|uninstall}
home=${2:?absolute installation home}
role=${3:?server|worker}
account=${4:-hispania}
case "$home" in /*) ;; *) echo 'Home must be absolute' >&2; exit 2;; esac
case "$home" in *'"'*|*'%'*|*'\'*|*'
'*) echo 'Unsupported service path character' >&2; exit 2;; esac
case "$account" in *[!a-zA-Z0-9_-]*|'') echo 'Invalid service account' >&2; exit 2;; esac
case "$role" in server) directory=server; assembly=Hispania.Server;; worker) directory=worker; assembly=Hispania.WorkerAgent;; *) exit 2;; esac
name="hispania-$role.service"
case "$action" in
 install)
  id "$account" >/dev/null 2>&1 || useradd --system --user-group --no-create-home --shell /usr/sbin/nologin "$account"
  [ "$(id -u "$account")" != 0 ] || { echo 'Root service identity is rejected' >&2; exit 2; }
  [ -d "$home/app/$directory" ] && [ -f "$home/config/production.json" ]
  touch "$home/state.maintenance.lock"
  chown root:root "$home/app" "$home/config"
  chmod 0755 "$home" "$home/app" "$home/config"
  for item in state logs backups run secrets; do chown -R "$account:$account" "$home/$item"; done
  chown "$account:$account" "$home/state.maintenance.lock"
  chmod 0700 "$home/secrets"
  find "$home/secrets" -type f -exec chmod 0600 {} \;
  template=$(dirname "$0")/hispania.service.in
  # Escape replacement delimiters rather than interpolating paths as shell commands.
  escaped_home=$(printf '%s' "$home" | sed 's/[&|]/\\&/g')
  sed -e "s|@HOME@|$escaped_home|g" -e "s|@ROLE@|$role|g" -e "s|@DIRECTORY@|$directory|g" -e "s|@ASSEMBLY@|$assembly|g" -e "s|@USER@|$account|g" -e "s|@GROUP@|$account|g" "$template" > "/etc/systemd/system/$name"
  systemd-analyze verify "/etc/systemd/system/$name"
  systemctl daemon-reload
  systemctl enable "$name"
  ;;
 start|stop|restart|status) systemctl "$action" "$name";;
 uninstall) systemctl stop "$name"; systemctl disable "$name"; rm -- "/etc/systemd/system/$name"; systemctl daemon-reload;;
 *) exit 2;;
esac
