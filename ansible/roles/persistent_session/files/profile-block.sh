# Interactive SSH logins share one tmux session that outlives the connection.
# Leaving tmux (exit or detach) disconnects, so no work runs in an unprotected shell.
# Not exec: if tmux fails or ends at once, the login shell still works (node4's only
# remote path). Skip it once with: ssh -t <host> NO_TMUX=1 bash -l
case $- in
*i*)
    if [ -n "$SSH_TTY" ] && [ -z "$TMUX" ] && [ -z "$NO_TMUX" ] && [ -t 0 ] && [ -t 1 ] &&
        command -v tmux >/dev/null 2>&1; then
        tmux_started=$(date +%s)
        if tmux -u new-session -A -s main && [ $(($(date +%s) - tmux_started)) -ge 3 ]; then
            exit
        fi
        unset tmux_started
        echo "Outside tmux: work here ends with the connection."
    fi
    ;;
esac
