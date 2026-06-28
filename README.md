# Pusharoo

Pusharoo is a Neo smart contract artifact workspace. It helps you organize contract projects, upload compiled `.nef` files with their manifest JSON, inspect contract methods/events/permissions, and keep build artifacts in MongoDB so versions can be reviewed before deployment.

The app is split into:

- `frontend/` - Angular 21 app with the Pusharoo UI
- `backend/` - ASP.NET Core 10 Web API
- `event-relay/` - ASP.NET Core 10 service for Neo event webhooks
- `mongo` - MongoDB storage through Docker Compose

## What It Does

- Creates and lists contract projects.
- Uploads contract artifacts as `multipart/form-data`.
- Stores the `.nef` file metadata and manifest document in MongoDB.
- Summarizes each manifest with method, event, permission, and supported standard counts.
- Shows a manifest viewer with overview, methods, events, permissions, and raw JSON tabs.
- Compares artifact versions to highlight method, event, and permission changes.
- Runs an optional event relay that monitors Neo application logs and sends matching contract events to user webhooks.

## Planned

- Deployment tracking, including which artifact version is currently deployed.
- Easy deployment of previous artifact versions.
- Contract Interaction Console for invoking contract methods from the site and viewing results.
- Blockchain event subscriptions for monitoring, notifications, and webhooks.
- Public and private artifacts/contracts.

## Structure

```text
pusharoo/
+-- frontend/
+-- backend/
+-- event-relay/
+-- docker-compose.yml
+-- README.md
```

## Run With Docker

```powershell
docker compose up --build
```

The frontend runs at `http://localhost:4200`.

The API runs at `http://localhost:5000`.

The event relay runs at `http://localhost:5001`.

MongoDB runs at `mongodb://localhost:27017`.

## Run Locally

### Backend

The backend targets .NET `10.0`.

```powershell
dotnet run --project backend/backend.csproj
```

### Event Relay

```powershell
dotnet run --project event-relay/event-relay.csproj
```

The relay exposes subscription management at `http://localhost:5001/api/subscriptions` and stores subscriptions, delivery attempts, and scan checkpoints in MongoDB.

By default it uses the public Neo mainnet RPC endpoint in `event-relay/appsettings.json`, polls every 15 seconds, and starts at the current chain height when no checkpoint exists. Set `NeoRpc:StartBlock` to replay from a specific block.

### Frontend

Angular 21 requires Node.js `20.19+`, `22.12+`, or `24+`.

```powershell
cd frontend
npm install
npm start
```

Angular serves the app at `http://localhost:4200`.

## API

```text
POST   /api/projects
GET    /api/projects
GET    /api/projects/{projectId}

POST   /api/projects/{projectId}/artifacts
GET    /api/projects/{projectId}/artifacts
GET    /api/projects/{projectId}/artifacts/compare?from=v0.1.0&to=v0.1.1
POST   /api/projects/{projectId}/deployments
GET    /api/projects/{projectId}/deployments
GET    /api/artifacts/{artifactId}

GET    /api/artifacts/{artifactId}/manifest
GET    /api/artifacts/{artifactId}/nef
GET    /api/artifacts/{artifactId}/summary
```

### Event Relay API

```text
POST   /api/subscriptions
GET    /api/subscriptions
GET    /api/subscriptions/{subscriptionId}
PUT    /api/subscriptions/{subscriptionId}
DELETE /api/subscriptions/{subscriptionId}
GET    /api/subscriptions/{subscriptionId}/deliveries
GET    /health
```

Create a subscription:

```json
{
  "name": "Transfer events",
  "contractHash": "0x1234...",
  "eventName": "Transfer",
  "webhookUrl": "https://example.com/neo-events",
  "projectId": "optional-pusharoo-project-id",
  "secret": "optional-signing-secret",
  "headers": {
    "X-Integration": "pusharoo"
  },
  "isEnabled": true
}
```

Leave `eventName` empty to receive every event emitted by the contract. When `secret` is set, webhook requests include `X-Pusharoo-Signature` with an HMAC-SHA256 signature over the JSON payload.

Artifact uploads expect `multipart/form-data`:

```text
files:
- contract.nef
- contract.manifest.json

fields:
- version = 0.1.0
- notes = Initial upload
```

The compare endpoint returns added/removed methods, changed method signatures, added events, and permission changes between two stored artifact versions.
