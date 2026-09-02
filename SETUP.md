# MedScribeOS — Local Setup for Testing

How to pull this repo and run the app on your own Windows laptop.

---

## 0. Before you start — read this

| | |
|---|---|
| **OS** | **Windows 10/11 only.** It's a WPF desktop app; it will not build or run on macOS/Linux. |
| **Network** | **You must be on the HFMG network (or VPN).** Login goes to the internal API at `172.22.6.188:177`. If you can't reach it, you can't get past the sign-in screen. |
| **Account** | Use your existing HFMG doctor login. Accounts are not created here. |
| **AI provider** | Pick **Profile A (OpenAI cloud)** for the quickest start, or **Profile B (fully local)** if you have no internet at the test site / want zero cloud calls. See step 4. |
| **Data** | No database. Everything is local JSON / WAV files under your `%AppData%` — see step 8. |

---

## 1. Install prerequisites

| Tool | Why | Get it | Verify |
|---|---|---|---|
| **.NET 8 SDK** | builds & runs the app | <https://dotnet.microsoft.com/download/dotnet/8.0> (SDK, x64) | `dotnet --version` → `8.0.x` |
| **Git** | pull the repo | <https://git-scm.com/download/win> | `git --version` |
| **VS Code** | edit / run | <https://code.visualstudio.com> | — |
| **C# Dev Kit** (VS Code extension) | F5 debugging | Extensions panel → search "C# Dev Kit" | — |

**Profile A (OpenAI) also needs:** an OpenAI API key (`sk-...`) from whoever owns the org account.

**Profile B (fully local) also needs:**

| Tool | Why | Get it |
|---|---|---|
| **Ollama** | local LLM for HPI/ROS extraction | <https://ollama.com/download> — then `ollama pull qwen2.5:3b` |
| **Docker Desktop** | runs the local speech-to-text server | <https://www.docker.com/products/docker-desktop> (needs WSL2 + a reboot on first install) |

Also make sure Windows lets desktop apps use the mic:
**Settings → Privacy & security → Microphone → "Let desktop apps access your microphone" = On.**

---

## 2. Get the code

```powershell
cd C:\src            # wherever you keep projects
git clone https://github.com/Chandunatakarani/MedScribeOs.git
cd MedScribeOs
git checkout locally-hosted-medscribeos
```

Open the folder in VS Code: `code .`

---

## 3. Build

```powershell
dotnet restore
dotnet build
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

> If the build fails with *"cannot access the file … MedScribeOS.exe … used by another process"* — a previous run of the app is still open. Close it (check the system tray) and build again.

---

## 4. Choose your AI setup

### Profile A — OpenAI cloud (recommended first run)

Fastest, most accurate, nothing else to install. Uses `gpt-4o` + Whisper + voice-anchored diarization. Costs ~cents per test session.

### Profile B — Fully local (offline)

No cloud calls. Extraction runs on Ollama; dictation + Voice Analyzer run on a local Whisper server. Speaker labels are inferred from turn-taking (less precise than Profile A — you fix wrong ones with the ⇄ button on each chat bubble). Slower on a typical laptop.

Set up the two local servers first:

```powershell
# 1. LLM - and keep it loaded between requests (otherwise every Analyze
#    after 5 idle minutes pays a ~1 min model reload)
ollama pull qwen2.5:3b
[Environment]::SetEnvironmentVariable('OLLAMA_KEEP_ALIVE','2h','User')
# then quit Ollama from the tray and relaunch it so the setting takes effect

# 2. Speech-to-text server (in a NEW PowerShell window, after Docker Desktop says "Engine running").
#    WHISPER__TTL keeps the model warm for an hour; the volume keeps downloaded
#    models across container recreates; --restart brings it back after a reboot.
docker run -d --name speaches -p 8000:8000 --restart unless-stopped `
  -e WHISPER__TTL=3600 -v speaches-cache:/home/ubuntu/.cache/huggingface `
  ghcr.io/speaches-ai/speaches:latest-cpu

# 3. Pre-download the Whisper model (takes 1-2 min, no progress shown).
#    Stick with faster-whisper-small: the distil variants are faster but fall
#    into repetition loops ("a, a, a…") on real visit-length audio.
Invoke-RestMethod -Method Post -Uri "http://localhost:8000/v1/models/Systran/faster-whisper-small"
```

Later sessions: `ollama serve` runs automatically; the speaches container auto-starts with Docker Desktop (or `docker start speaches` if you created it without `--restart`).

---

## 5. Create the config file

The app reads **`%AppData%\MedScribeOS\config.json`** once at launch. Create it with the block for your profile — paste the whole thing into PowerShell:

### Profile A — OpenAI

```powershell
$dir = "$env:APPDATA\MedScribeOS"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
@'
{
  "OpenAiApiKey": "sk-REPLACE_WITH_YOUR_KEY"
}
'@ | Set-Content -Path "$dir\config.json" -Encoding utf8
```

### Profile B — Fully local

```powershell
$dir = "$env:APPDATA\MedScribeOS"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
@'
{
  "ChatBaseUrl": "http://localhost:11434/v1",
  "ChatModel": "qwen2.5:3b",
  "ChatJsonMode": true,
  "AudioBaseUrl": "http://localhost:8000/v1",
  "TranscribeModel": "Systran/faster-whisper-small",
  "AudioDiarization": "off"
}
'@ | Set-Content -Path "$dir\config.json" -Encoding utf8
```

Check it:

```powershell
Get-Content "$env:APPDATA\MedScribeOS\config.json"
```

> **All keys are optional.** Anything you leave out defaults to OpenAI. Full list:
> `OpenAiApiKey`, `ChatBaseUrl`, `ChatModel`, `ChatApiKey`, `ChatJsonMode`,
> `AudioBaseUrl`, `AudioApiKey`, `TranscribeModel`, `DiarizeModel`, `AudioDiarization`,
> and `OrgApiBaseUrl` (override the login server URL — usually leave alone).
> If you launch with no config and no key, the app pops a setup message and writes
> a commented template `config.json` you can then fill in.

---

## 6. Run it

**VS Code:** press **F5** (C# Dev Kit picks up the project automatically).

**Terminal:**

```powershell
dotnet run
```

---

## 7. First-run walkthrough

1. **Sign in** with your HFMG email + password. (Must be on the HFMG network.)
2. The main window opens. Go to the **Voice Analyzer** tab.
3. **Templates** (top-right) → the doctor starts with one "Standard HPI / ROS" template. Create or tweak one if you like; the picker in Voice Analyzer must have a template selected before you can start.
4. **Profile A only:** click **🎙️ Enroll Doctor Voice** once and read a sentence for ~8 seconds. (Profile B skips this.)
5. Click **● Start Conversation**, speak a short mock doctor/patient exchange, then **⏹ End Conversation**. Turns appear live as chat bubbles.
6. Fix any mis-labelled speaker with the **⇄** button on a bubble.
7. Click **🤖 Analyze** — the LLM fills your template's fields. Review/edit them.
8. **Inject** puts HPI into the focused eCW HPI box and ROS into the ROS box (eCW must be open). Custom sections copy to the clipboard instead.

**Voice Dictation tab:** toggle the mic, click into any eCW field, speak — each phrase transcribes and types itself in.

---

## 8. Where your data lives

| Path | What |
|---|---|
| `%AppData%\MedScribeOS\config.json` | provider config (step 5) |
| `%AppData%\MedScribeOS\doctor_voice_reference.wav` | your voice enrollment sample |
| `%LocalAppData%\MedScribeOS\Templates\templates_<you>.json` | your note templates (one file per doctor) |
| `%TEMP%\medscribe_*.wav` | transient audio chunks (auto-deleted) |

Nothing syncs anywhere. Delete these files to reset.

---

## 9. Troubleshooting

| Symptom | Fix |
|---|---|
| Build error: *"MedScribeOS.exe … used by another process"* | Old app instance still running — close it from the tray, rebuild. |
| Sign-in: *"Couldn't reach the sign-in server"* | You're not on the HFMG network / VPN. |
| *"The AI provider isn't configured"* on startup | `config.json` missing an API key for an OpenAI endpoint. Do step 5. |
| Analyze fails: *"Chat request failed … @ http://localhost:11434/v1"* | Ollama isn't running or the model isn't pulled — `ollama pull qwen2.5:3b`, then `ollama list`. |
| Live transcription fails: *"Model '…' is not installed locally"* | Run the `Invoke-RestMethod -Method Post …/v1/models/<the model in your config.json>` from step 4. |
| Live transcription fails: connection refused on `:8000` | Speaches container isn't up — `docker ps`; if missing, `docker start speaches` or re-run `docker run …`. |
| `docker : term not recognized` | Open a **new** terminal after installing Docker Desktop; make sure it says "Engine running". |
| First dictation/analysis hangs ~30–60 s | Local Whisper model downloading on first use, or first Ollama load. One-time. |
| Injection says *"Focused field is not inside eCW"* | Click into the actual eCW field first; make sure eCW is the front window. |
| No audio captured | Windows mic privacy setting (see step 1) or wrong default mic in Windows Sound settings. |

---

## 10. Switching profiles / cleanup

- **Switch A ↔ B:** just edit `config.json` and relaunch the app. No rebuild.
- **Stop local servers when done:** `docker stop speaches` (Ollama can stay).
- **Reset the app state:** delete the files in step 8.
- **Update the code:** `git pull` on the `locally-hosted-medscribeos` branch, then `dotnet build`.
