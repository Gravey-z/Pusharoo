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

## Wallet Key Isolation

Pusharoo never requests users' private keys, WIFs, seed phrases, mnemonics,
keystores, or wallet passwords. Wallet connections expose only the selected
public account and network. Message signatures and transaction approvals are
requested through NeoLine, OneGate, or WalletConnect and signing remains inside
the user's wallet. The optional testnet faucet relayer uses a separate operator
WIF configured on the server; it is never sent to the browser.

The browser stores the preferred wallet provider and network for reconnection,
and pending transaction/claim references for recovery. The backend stores public
wallet identity, public keys, login challenges, hashed session tokens, legacy
authorization audit fields, transaction IDs, contract hashes, and project/release
data; none of these can be used as a user's wallet private key.

## Wallet login

Connecting a wallet identifies the account in the browser; it does not sign in
to the Pusharoo API. The first protected workspace action asks for one **Sign in
to Pusharoo** message signature. Later permitted actions reuse the same login
for up to eight hours by default, even when switching between N3 TestNet and
MainNet with the same wallet. The session does not approve blockchain actions:
deployments, updates, direct faucet claims, and Relay payments still require
their own wallet transaction approval. Browsing projects and the faucet does
not require login.

Sign out revokes the server session and leaves the wallet connected; disconnecting
the wallet also signs out. If the API is unavailable, sign-out stays pending
until revocation can finish. An expired session, account change, or logout requires
a new login signature for the next protected action. A page reload can restore
an unexpired session for the same wallet. Login challenges last five minutes by
default; sessions have an absolute eight-hour lifetime with no silent extension.

The API uses a same-origin `/api` route, an HttpOnly session cookie, and an
antiforgery token for protected writes. Public HTTPS deployments use Secure,
SameSite=Lax, host-only cookies; explicit HTTP localhost development uses
separate cookie names. Keep `PUSHAROO_APP_ORIGIN` equal to the browser's exact
origin, preserve the API Data Protection key volume across restarts and replicas,
and configure only trusted reverse-proxy addresses. See the
[wallet-session rollout guide](docs/wallet-session-rollout.md) for deployment
order and acceptance checks.

## Faucet IP limit

The API reserves the public IP of each accepted sponsored claim in MongoDB.
Successful claims keep that IP reserved for 24 hours after confirmation; known
failed claims release it.

## Planned

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

Docker Compose uses production settings: the frontend is the only published
service and proxies requests to the API and event relay on the internal network.
Create a local configuration file first:

```powershell
Copy-Item .env.example .env
# Edit .env and set a strong MONGO_PASSWORD and your WalletConnect project ID.
docker compose up --build
```

The app runs at `http://localhost:8080` by default (change
`PUSHAROO_HTTP_PORT` in `.env`). MongoDB, the API, and the relay are not exposed
to the host. See [the wallet-session rollout guide](docs/wallet-session-rollout.md)
before making the service public.

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

The Relay stores subscriptions, delivery attempts, and scan checkpoints in MongoDB. Webhook and payment management goes through the Pusharoo API using the wallet login session; the Relay accepts those requests only with its private API service token. Its `/health` endpoint remains public.

For Compose, set distinct 32-character-or-longer `PUSHAROO_RELAY_TESTNET_SERVICE_TOKEN` and `PUSHAROO_RELAY_MAINNET_SERVICE_TOKEN` values before enabling the optional Relay services. The API and each Relay receive their matching token through Compose; keep the values outside source control. Start the Relays with `docker compose --profile event-relay up -d --build` after the API gateway is deployed.

By default it uses the public Neo mainnet RPC endpoint in `event-relay/appsettings.json`, polls every 15 seconds, and starts at the current chain height when no checkpoint exists. Set `NeoRpc:StartBlock` to replay from a specific block.

### Frontend

Angular 21 requires Node.js `20.19+`, `22.12+`, or `24+`.

```powershell
cd frontend
npm ci
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
POST   /api/projects/{projectId}/relay/{testnet|mainnet}/subscriptions/query
POST   /api/projects/{projectId}/relay/{testnet|mainnet}/subscriptions/usage
POST   /api/projects/{projectId}/relay/{testnet|mainnet}/subscriptions
PUT    /api/projects/{projectId}/relay/{testnet|mainnet}/subscriptions/{subscriptionId}
DELETE /api/projects/{projectId}/relay/{testnet|mainnet}/subscriptions/{subscriptionId}
POST   /api/projects/{projectId}/relay/{testnet|mainnet}/subscriptions/{subscriptionId}/deliveries/query
POST   /api/projects/{projectId}/relay/{testnet|mainnet}/subscriptions/{subscriptionId}/test
POST   /api/projects/{projectId}/relay/{testnet|mainnet}/subscriptions/{subscriptionId}/deliveries/{deliveryId}/redeliver
POST   /api/projects/{projectId}/relay/mainnet/payments/intents
POST   /api/projects/{projectId}/relay/mainnet/payments/confirm
POST   /api/projects/{projectId}/relay/mainnet/payments/history/query
```

These routes require the project owner's wallet login session. Create a subscription with:

```json
{
  "name": "Transfer events",
  "contractHash": "0x1234...",
  "eventName": "Transfer",
  "webhookUrl": "https://example.com/neo-events",
  "network": "neo3:testnet",
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
