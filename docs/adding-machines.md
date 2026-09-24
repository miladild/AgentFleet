# Adding machines

A fleet is the hub plus any number of worker machines. A worker is any computer on your network that runs Ollama. The
hub sends it work over your network. Everything here is configuration; no code changes are needed.

## Which machine gets which job

Each machine ("node") has a **tier** that says what kind of work it should get:

| Tier | Meant for | A typical model |
|---|---|---|
| `heavy` | Hard problems, design, and all planning. Your strongest machine. | `qwen3-coder:30b`, or the biggest that fits |
| `standard` | Ordinary coding: bug fixes, small features, and most plan steps | `qwen2.5-coder:7b` to `14b` |
| `light` | Quick questions and trivial edits | `llama3.2:3b`, `qwen2.5-coder:3b` |

A node can also be a **vision** node, which reads screenshots and images, for example with `qwen2.5vl:7b`. It is used
only when a message contains an image.

Exactly one node is the **fallback**: it answers when routing fails, continues a conversation that is in the middle of a
tool call, and stands in when another machine is down. It also does the routing itself, with a small model. Make it
your most reliable machine.

You can leave a tier empty. With only one text node, routing is skipped entirely. With two, requests go to one or the
other. You can also have several machines in one tier; the first one that is ready gets the work.

### Hub mode

The **Hub mode** switch in the web UI (top right) decides how freely the hub's strongest model is used.
*Conservative* keeps it for complex work and leaves your GPU free for other things. *Aggressive* lets it take ordinary
requests too, which suits the night, when you are not using the machine.

## Set up a worker

### Windows worker

On the worker, in an elevated PowerShell (Run as administrator), from a clone of this repository or with the `scripts`
folder copied over:

```powershell
.\scripts\Setup-Worker.ps1 -AllowFrom 192.168.1.10
```

Replace `192.168.1.10` with the hub's private IPv4 address. This value is required: the script rejects subnet ranges,
hostnames, wildcards, and `LocalSubnet`. The script installs Ollama if needed, makes it listen on the network, adds a
firewall rule for that exact peer, and narrows Ollama's own inbound rules to the same peer. It also requires Windows
Firewall to be enabled with block-by-default inbound policy and refuses broader port rules instead of changing unrelated
firewall entries. Add `-KeepAwake` so the machine does not sleep. Use `-DryRun` to see what it would do first.

Afterwards, quit Ollama from the tray icon and start it again (or restart the computer) so it picks up the new setting.

### Linux worker

```bash
sudo ./scripts/setup-worker.sh --allow-from 192.168.1.10
```

Replace the example with the hub's private IPv4 address. CIDR networks, hostnames, wildcards, and whole-subnet rules are
rejected. The script installs Ollama with the official installer if needed, adds a systemd setting so Ollama listens on
the network, and opens the port in `ufw` or `firewalld` for that exact address. For a real run, one of those firewalls
must already be active. The script requires UFW's default incoming policy to be deny or reject and refuses existing
broader rules for the Ollama port; for firewalld it checks active zones, services, ports, and rules before proceeding.
It does not install or enable a firewall. If Ollama is missing and the script has no interactive terminal, pass `--yes`
to approve its installer. `--wifi-powersave-off` turns off Wi-Fi power saving; `--dry-run` shows the planned changes
without checking or changing firewall state.

### By hand

If you would rather not run scripts: install Ollama, set the environment variable `OLLAMA_HOST` to `0.0.0.0:11434`,
restart Ollama, and allow TCP port 11434 through the machine's firewall from the hub's address only. That is all the
scripts do.

## Add it to the fleet

On the hub:

```powershell
.\scripts\Add-FleetNode.ps1 -Name worker1 -Address 192.168.1.21 -Model qwen2.5-coder:7b -Tier standard -Pull
```

- `-Address` can be the plain address; `:11434` and `/v1` are added for you.
- `-Pull` downloads the model onto the worker over the network, so you never have to log in there.
- `-Vision` marks it as a vision node. `-Fallback` makes it the fallback.
- With two or more text nodes, the script also makes sure the small routing model is installed on the fallback node.

The script checks that the worker answers and has the model before it changes anything. If the backend is running it
adds the node through the backend, which validates the change; otherwise it edits `fleet.config.json` directly.

A running backend uses the new machine straight away; there is no restart. (If the backend was stopped, it reads the
machine when it starts.)

### Or from the web UI

**Config**, **Machines** tab, **Add a machine**: type the address (an IP or a name is enough), press **Connect**, click
one of the models it lists, choose a role, and **Add and save**. If nothing answers, the panel says what to check. Every
machine card has a status dot (green ready, amber reachable but the model is missing, red unreachable), and **Edit** opens
its address, model and a **Check and list models** button. Changing a machine's role, model or address, removing one, or
changing the fallback all apply when saved, with no restart.

## Check it

```powershell
.\scripts\Test-Fleet.ps1
.\scripts\Get-FleetModels.ps1      # every model on every machine
```

The left panel of the web UI shows a green or red dot per machine. The hub checks every machine every 10 seconds.

## Keep it reliable

A machine that is red in the morning is almost always one of these:

1. **Its address changed.** Home routers hand out addresses that can change. If the worker's firewall allows only the
   hub's address and the hub's address changes, the worker silently drops the hub. Give the hub, and each worker, a fixed
   address: a DHCP reservation in your router. `Test-Fleet.ps1` prints the address the hub uses to reach the others, and
   the backend log says when it changes.
2. **It went to sleep or its Wi-Fi did.** Use a wired connection where you can. On Windows use `-KeepAwake`; on Linux
   `--wifi-powersave-off`.
3. **Ollama stopped.** Run `ollama list` on that machine.

More in [troubleshooting.md](troubleshooting.md).

## Choosing models

Start small and go up. A model that fits in graphics memory runs many times faster than one that spills into system
RAM. As a rough guide, a 4-bit quantized model needs about 0.6 GB per billion parameters plus some working space, so a
7B model wants about 5 to 6 GB and a 30B model about 20 GB. Ollama's default downloads are already 4-bit.

`ollama pull <name>` downloads a model; the [Ollama library](https://ollama.com/library) lists what exists. Models that
handle tool calls well matter more for this fleet than raw size, so prefer ones labelled for coding and tools.
