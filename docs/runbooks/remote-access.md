# Runbook — remote access over WireGuard

**State:** prepared, not working yet: no device has been added and the router has no UDP
forward.

A device peers with every node directly. No node forwards traffic, so each stays reachable
when another is down. Over the tunnel a device reaches every overlay service and SSH on
every node, so a lost device must be removed at once.

## 1. Router (once)

Forward UDP, external port to internal address and port:

| External | To | Port |
|---|---|---|
| 51821 | node1, 192.168.0.101 | 51821 |
| 51822 | node2, 192.168.0.28 | 51821 |
| 51823 | node3, 192.168.0.103 | 51821 |
| 51824 | node4, 192.168.0.35 | 51821 |

Reserve those four addresses in DHCP as well, or a forward can point at the wrong machine.

## 2. Linux or macOS

Install WireGuard (`sudo apt install wireguard-tools`, `brew install wireguard-tools`),
then run on the device itself:

~~~bash
scp -P 2345 '<user>@<home-address>:homelab-remote/*' .
sh make-wg-client.sh linux 10.44.0.11 <home-address>      # macOS: mac 10.44.0.12
~~~

It prints a public key. Only that goes to the controller. Start it on Linux with
`sudo install -m 600 hl-linux.conf /etc/wireguard/ && sudo wg-quick up hl-linux`, on macOS
by importing the file in the WireGuard app.

## 3. iPhone

On the Mac, `sh make-wg-client.sh iphone 10.44.0.13 <home-address>`, then
`qrencode -t ansiutf8 < hl-iphone.conf` and scan it in the WireGuard app. Nobody else may
see that screen: the QR holds the private key. Delete the file afterwards.

## 4. Controller

Add each device to `wg_remote_peers` in `inventory/hosts.yml`
(see `inventory/hosts.example.yml`), then:

~~~bash
cd ~/Projects/homelab-infrastructure/ansible
~/homelab/.venv/bin/ansible-playbook playbooks/overlay.yml --check    # only wg1.conf changes
~/homelab/.venv/bin/ansible-playbook playbooks/overlay.yml
~~~

## 5. Verify from outside (mobile data, not the home Wi-Fi)

`ping 10.44.0.1` to `10.44.0.4`, `http://10.44.0.1/git/`, `ssh -p 2222 <user>@10.44.0.1`.

## Remove a device

Delete its entry and run `overlay.yml`. The reload drops the peer on every node at once,
and the role fails if it is still live.
