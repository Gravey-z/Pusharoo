# Pusharoo

Pusharoo is scaffolded as a two-part application:

- `frontend/` - Angular app
- `backend/` - ASP.NET Core Web API

## Structure

```text
pusharoo/
+-- frontend/
+-- backend/
+-- docker-compose.yml
+-- README.md
```

## Run Locally

### Backend

The backend targets .NET `10.0`.

```powershell
dotnet run --project backend/backend.csproj
```

Backend API routes:

```text
POST   /api/projects
GET    /api/projects
GET    /api/projects/{projectId}
POST   /api/projects/{projectId}/artifacts
GET    /api/projects/{projectId}/artifacts
GET    /api/artifacts/{artifactId}
GET    /api/artifacts/{artifactId}/manifest
GET    /api/artifacts/{artifactId}/summary
```

Uploaded artifacts are saved in MongoDB database `Pusharoo`, collection `contractArtifacts`.

Artifact uploads expect `multipart/form-data` with `.nef` and `.manifest.json` files plus `version` and optional `notes` fields.

### Frontend

Angular 21 requires Node.js `20.19+`, `22.12+`, or `24+`.

```powershell
cd frontend
npm start
```

Angular will serve the app at `http://localhost:4200`.

## Docker

```powershell
docker compose up --build
```

The frontend is exposed on `http://localhost:4200` and the API is exposed on `http://localhost:5000`.
