# Pusharoo

Pusharoo is a Neo smart contract artifact workspace. It helps you organize contract projects, upload compiled `.nef` files with their manifest JSON, inspect contract methods/events/permissions, and keep build artifacts in MongoDB so versions can be reviewed before deployment.

The app is split into:

- `frontend/` - Angular 21 app with the Pusharoo UI
- `backend/` - ASP.NET Core 10 Web API
- `mongo` - MongoDB storage through Docker Compose

## What It Does

- Creates and lists contract projects.
- Uploads contract artifacts as `multipart/form-data`.
- Stores the `.nef` file metadata and manifest document in MongoDB.
- Summarizes each manifest with method, event, permission, and supported standard counts.
- Shows a manifest viewer with overview, methods, events, permissions, and raw JSON tabs.
- Compares artifact versions to highlight method, event, and permission changes.

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
+-- docker-compose.yml
+-- README.md
```

## Run With Docker

```powershell
docker compose up --build
```

The frontend runs at `http://localhost:4200`.

The API runs at `http://localhost:5000`.

MongoDB runs at `mongodb://localhost:27017`.

## Run Locally

### Backend

The backend targets .NET `10.0`.

```powershell
dotnet run --project backend/backend.csproj
```

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
GET    /api/artifacts/{artifactId}

GET    /api/artifacts/{artifactId}/manifest
GET    /api/artifacts/{artifactId}/summary
```

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
