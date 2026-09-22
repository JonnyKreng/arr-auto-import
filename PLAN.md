# Arr Import Solver — Plan (v1: Lidarr only)

Automate the *arr "manual import" dialogs. The program intervenes **only where Lidarr's own
deterministic rule-ranking already failed** (items sitting in the manual-import view), uses the
**Laya** decision engine (Python sidecar) to resolve the ambiguous mapping, optionally rejects and
blocklists bad releases, and records every decision in a database surfaced through a minimal UI.

## Goals
- Resolve Lidarr manual-import conflicts (ambiguous/unmatched artist → album → quality mapping).
- Only act on items Lidarr's automatic import already declined or couldn't parse.
- Deterministic rules first; Laya model only for genuinely ambiguous rows.
- Every decision persisted and visible in a simple web UI.
- Actions are **auditable**: default `DryRun` logs planned actions without touching Lidarr.

## Laya in C# — decision
Laya is pure Python (PyTorch/HuggingFace transformers, ~300–400M-param checkpoints from HF Hub).
There is no .NET binding. Chosen integration: **Python sidecar** (FastAPI) that loads
`Router(preload=True)` and exposes `POST /decide` over HTTP. C# talks to it via `HttpClient`.
Alternatives rejected for v1: pythonnet (heavy, fragile in Linux container), ONNX export (not offered).

## Architecture

```
┌─────────────── C# worker (ArrImportSolver) ───────────────┐
│  Worker loop ──► LidarrClient                              │
│                    │  GET /api/v1/queue (downloadId + id)  │
│                    │  GET /api/v1/manualimport?downloadId  │
│                    ▼                                       │
│  ImportDecisionEngine (deterministic rules)                │
│     ├─ resolved/safe  ──► POST /api/v1/manualimport        │
│     └─ ambiguous      ──► LayaClient ──► [sidecar] /decide │
│                              │  choice/noul answers+conf   │
│                              ▼                             │
│                        Confident → act:                    │
│                         • import candidate                 │
│                         • DELETE /api/v1/queue/{id}?       │
│                            removeFromClient&blocklist      │
│                              (reject + block release)      │
│                        Low confidence → leave to human     │
│  DecisionStore (LiteDB) ◄── every step logged              │
│  Minimal API + index.html ◄── GET /api/decisions           │
└────────────────────────────────────────────────────────────┘
```

Two boxes:
- **C# worker** — existing .NET 10 Worker project (needs `Microsoft.AspNetCore.App` FrameworkReference
  for the minimal API UI; no extra web framework).
- **`LayaSidecar/`** — Python 3.11+, FastAPI + `laya` (`Router(preload=True)`). Separate compose service.

## Pipeline (one poll cycle, Lidarr v1 API, spec-verified)
1. `GET /api/v1/queue` (includeArtist/includeAlbum) → keep items with a `downloadId`; remember the
   queue item `id` (needed for reject/block).
2. `GET /api/v1/manualimport?downloadId={id}` → `ManualImportResource[]` — the rows behind the dialog.
3. Classify each row (deterministic):
   - `additionalFile == true` → **skip** (covers/nfo), log only.
   - Rejections empty **and** `artist + album + albumReleaseId + quality` all present → **resolved**,
     deterministic import. (If Lidarr's ranking already produced a single confident parse, no model.)
   - Otherwise → **ambiguous**, hand to the Laya sidecar.
4. Model resolve (see below) → act, or leave to human.
5. `POST /api/v1/manualimport` with `ManualImportUpdateResource[]` for the import decisions.
6. Persist every decision + outcome to the DB. UI shows the log.

## Decision layers
### Layer 1 — Deterministic (no ML)
- Skip `additionalFile` rows.
- Skip rows where Lidarr already returned non-empty `rejections` but we can't improve (model may still try).
- Auto-import only one confident parsed candidate.

### Layer 2 — Laya model (ambiguous rows only)
State sent as compact JSON: file name, parsed title/artist/album, audio tags, release group, size,
Lidarr's candidate albums (title, release id, year, label), rejections text.
Typed questions:
- `mapping` — `choice`: candidate albums `[albumA, albumB, …]` + `no_match`
- `reject` — `noul`: "Is this release wrong/garbage; should it be rejected and blocked?"
- `search_term` — `choice`: coarse category to disambiguate `no_match` cases (YAGNI stretch, only if needed)

Confidence gating: act only if `confidence >= Lidarr:Model:MinConfidence` (default `0.85`).
Below threshold → **LeaveToHuman** (no action, logged). Model's own confidence is calibrated; treat it as
advisory, not absolute.

> Model training & calibration (temperature fitting, fine-tuning, label harvesting) is a **v2** concern —
> see **PLAN-v2.md**. v1 only *collects* labeled decisions (its DB is the training data source) and ships
> Laya's stock checkpoint as-is.

### Actions the model can produce
| Model decision      | C# action                                                        |
|---------------------|------------------------------------------------------------------|
| `import:<candidate>`| `POST /api/v1/manualimport` with resolved `artistId/albumId/albumReleaseId/quality` |
| `reject_block`      | `DELETE /api/v1/queue/{queueId}?removeFromClient=true&blocklist=true` — reject download **and** blocklist the release so Lidarr won't grab it again |
| `leave_to_human`    | no action, log the low-confidence case                           |

Reject/block uses the **queue item `id`** (not `downloadId`), mapped in step 1 — same effect as the UI's
"Remove and block".

DryRun (`Lidarr:DryRun`, default `true`): all three actions are logged only, never sent to Lidarr.

## Database (decisions)
**LiteDB** (embedded, single file `data/decisions.db`, no migrations). Fallback if package compat with
net10.0 is an issue: EF Core + SQLite (same repository shape).
Collection `decisions`:
- `id`, `utcTimestamp`
- `app` (Lidarr), `downloadId`, `queueId`, `filePath`, `fileName`
- `rowId` (manual-import row id), `candidates` (json)
- `resolver` (`Deterministic` | `Laya`)
- `state` (json sent to model), `modelAnswer` (choice/score/confidence/probabilities/routing json)
- `action` (`Import` | `RejectBlock` | `Skip` | `LeaveToHuman`)
- `status` (`Pending` | `Applied` | `SkippedDryRun` | `Failed`)
- `error`, `inputSize`, `latencyMs`

## UI (simple)
- Switch Worker host to `WebApplication` (keeps `AddHostedService`). Endpoints:
  - `GET /api/decisions?limit=200` → JSON list
  - `GET /` → single static `index.html` (vanilla JS table, 10 s auto-refresh; no framework)
- Shows: time, file, candidate chosen, resolver, confidence, action, status. Read-only in v1.

## Config (`appsettings.json` + `appsettings.Development.json`)
```jsonc
"Lidarr": {
  "Url": "https://lidarr.krengel.me",
  "Key": "…",
  "DryRun": true,
  "PollIntervalSeconds": 60,
  "SidecarUrl": "http://laya-sidecar:8000",
  "Model": {
    "MinConfidence": 0.85
  }
},
"Ui": { "Port": 8080 }
```
(Lidarr `Url`/`Key` already provided in `appsettings.Development.json`; move `Key` to user-secrets later.)

## Deliverables / file layout
```
PLAN.md
PLAN-v2.md                 → model training & calibration (v2, offline only)
ArrImportSolver/
  Program.cs                  → WebApplication + DI (options, typed Lidarr client, store, worker)
  Options/LidarrOptions.cs    → Url, Key, DryRun, PollIntervalSeconds, SidecarUrl, MinConfidence
  Lidarr/LidarrClient.cs      → GetQueue, GetManualImport, Import, DeleteQueueItem(reject+block)
  Lidarr/Models/*.cs          → QueueResource, ManualImportResource, ManualImportUpdateResource,
                                QualityModel…
  Solver/IImportDecisionEngine.cs, ImportDecisionEngine.cs   → deterministic classification
  Solver/LayaDecisionClient.cs → POST /decide to sidecar
  Solver/DecisionModel.cs     → typed model questions/answers
  Store/DecisionRepository.cs → LiteDB read/write
  Ui/ (index.html)            → decisions table
  Worker.cs                   → poll loop wiring the pipeline
LayaSidecar/
  main.py                     → FastAPI, Router(preload=True), POST /decide
  requirements.txt            → laya, fastapi, uvicorn
  Dockerfile
compose.yaml                  → arrimportsolver + laya-sidecar services
```

## YAGNI — explicitly cut for v1
- Sonarr/Radarr/Readarr (multi-app later; decision store already has `app` field)
- Custom-format scoring, tag-based routing, import-mode selection
- User authentication, dashboards with mutations, SignalR/websockets
- Model training/calibration/label harvesting (→ **PLAN-v2.md**; v1 only collects decisions)
- Retries/exponential backoff beyond a fixed poll interval
- Anything but Lidarr's documented v1 API

## Risks / caveats
- **Throttling Lidarr:** keep `PollIntervalSeconds` sane (default 60) to avoid hammering the API.
- **Model weights:** `laya` downloads checkpoints on first call (~1 GB). Sidecar preloads at startup;
  boot takes seconds to minutes on first run.
- **Confidence is advisory:** `MinConfidence` + `DryRun:true` default guard against bad actions.
  Flip `DryRun` off only after reviewing real decisions in the UI.
- **Reject/block is destructive:** removes the download from the client and blocklists the release title.
  Gated behind `MinConfidence` and (during rollout) dry-run.
- **Live verification:** lidarr.krengel.me is not reachable from the dev machine; the first integration
  test must run against a reachable Lidarr instance.

## Milestones
1. **Scaffold:** options + Lidarr client + models → Worker polls queue/manualimport and *logs* rows.
2. **Deterministic solver + LiteDB + UI:** safe imports auto-applied (dry-run first), rows persisted, UI lists them.
3. **Laya sidecar:** sidecar service in compose; ambiguous rows scored; confidence-gated import/reject/block.
4. **Live validation:** run against real Lidarr with `DryRun=true`, review, then flip off.
5. **Label collection baseline:** v1 confirms every decision is persisted with its outcome (feeds v2 training).