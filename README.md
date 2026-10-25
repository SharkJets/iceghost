# Iceghost

See and control your coding agent sessions (Claude Code, Codex, pi) from anywhere. Each PC runs a small
runner that reads the session transcripts the CLIs already write, classifies each session as `working`,
`needsInput`, `done` or `stalled`, and reports to the server. Sending a message from the web app queues a
command; the runner resumes the session headlessly in its working directory and posts the reply back.

The server (`src/Iceghost.Api`) serves the Vue web app, which is also the PWA and the Electron window.

## Deploy

1. Image: a push to `main` runs `.github/workflows/pipeline.yml`, which builds and pushes
   `skidvis/iceghost:latest` (repo secrets `DOCKER_USER` and `DOCKER_PASS`). By hand:
   `docker build -t skidvis/iceghost:latest . && docker push skidvis/iceghost:latest`.
2. Copy `deploy/kubernetes.yaml` into flux-gitops as `clusters/my-cluster/deployments/iceghost.yaml`.
3. Set the admin password: replace `REPLACE_ME` in the `iceghost-secrets` Secret with a password of at least
   12 characters. The API refuses to start without one. Do not commit the real value.
4. Cloudflare: go to **Cloudflare Zero Trust > Networks > Tunnels >** (our tunnel) **> Public Hostnames** and add:

   | Field | Value |
   |---|---|
   | Subdomain | `iceghost` |
   | Domain | `sharkjets.com` |
   | Service | the same in-cluster address the existing site hostnames already use |

   Cloudflare creates the DNS record and terminates TLS at its edge. The domain must be in the same
   Cloudflare account as the tunnel. The Ingress in `deploy/kubernetes.yaml` already declares
   `iceghost.example.com`.

## Enroll a machine

1. Open https://iceghost.example.com, log in with the admin password, go to Machines and add this PC by name.
   Copy the token it gives you.
2. Download the runner for your OS from the GitHub release (`iceghost-runner-<tag>-<rid>.zip` on Windows,
   `.tar.gz` elsewhere) and check it against `SHA256SUMS`. Put the binary at
   `%LOCALAPPDATA%\Programs\IceghostRunner\iceghost-runner.exe` on Windows, or `~/.local/bin/iceghost-runner`
   on Linux and macOS.
3. Run it once with the server and token. This saves `config.json` to the state dir and starts the loop:

   ```
   iceghost-runner run --server https://iceghost.example.com --token TOKEN
   ```

   State dir: `%LOCALAPPDATA%\Iceghost Runner` on Windows, `~/Library/Application Support/Iceghost Runner`
   on macOS, `$XDG_STATE_HOME/iceghost-runner` or `~/.local/state/iceghost-runner` on Linux. The file is
   `config.json`, mode 0600 on Unix.

Headless `claude` may need a permission flag to run unattended. Add it per runtime under `extraArgs` in
`config.json`, for example `"extraArgs": { "claude": ["--permission-mode", "acceptEdits"] }`.

## Run at startup

Each command below runs `iceghost-runner run` with the config saved by the enroll step.

**Windows** (Task Scheduler, at logon):

```
schtasks /create /tn iceghost-runner /sc onlogon /tr "\"%LOCALAPPDATA%\Programs\IceghostRunner\iceghost-runner.exe\" run"
```

**Linux** (systemd user unit). Save as `~/.config/systemd/user/iceghost-runner.service`:

```ini
[Unit]
Description=Iceghost runner

[Service]
ExecStart=%h/.local/bin/iceghost-runner run
Restart=always

[Install]
WantedBy=default.target
```

Then start it and keep user services running without a login session:

```
systemctl --user enable --now iceghost-runner
loginctl enable-linger $USER
```

**macOS** (LaunchAgent). Save as `~/Library/LaunchAgents/com.sharkjets.iceghost-runner.plist`, replacing `YOUR_USER`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>com.sharkjets.iceghost-runner</string>
  <key>ProgramArguments</key>
  <array>
    <string>/Users/YOUR_USER/.local/bin/iceghost-runner</string>
    <string>run</string>
  </array>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
</dict>
</plist>
```

Then load it:

```
launchctl load ~/Library/LaunchAgents/com.sharkjets.iceghost-runner.plist
```

## Desktop app

The Electron shell is a thin window around the web app:

```
cd electron
pnpm install
pnpm dist
```

Installers land in `electron/dist` (NSIS on Windows, DMG on macOS, AppImage on Linux).

## Phone

Open https://iceghost.example.com in the phone's browser and choose Add to Home Screen. The manifest
installs it as a standalone app.

## Limits

- Iceghost never kills a process. A hung session is fixed by resuming it headlessly; if the old TUI is
  still open, close it by hand.
- States are heuristics read from the transcript tail: `stalled` after 10 minutes without writes,
  `needsInput` when a tool call has been pending for more than 2 minutes.
- The runner scans the newest 50 transcripts modified in the last 14 days. Each session shows its last
  10 entries, each cut to 1000 characters.
- One admin password, set by `ICEGHOST_ADMIN_PASSWORD`. No invites, no new-session creation and no push
  notifications yet.
