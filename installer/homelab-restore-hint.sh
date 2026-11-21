# shellcheck shell=sh
# Installed by the homelab installer as /etc/profile.d/zz-homelab-restore.sh
# Reminds the admin, at login, until the controller has been restored.
if [ "$(id -u)" -ne 0 ] && [ -e /var/lib/homelab-restore/recovery.tar.gpg ] \
   && [ ! -e /var/lib/homelab-restore/restored ]; then
  printf '\n  Homelab: keys, vault and repository are not restored yet. Run:\n      sudo homelab-restore\n\n'
fi
