# Pixel MMO Web Prototype

A browser-first, isometric pixel MMORPG prototype with an authoritative .NET WebSocket server. The project is a new
game implementation. The supplied RO3Emu repository is used as architectural reference only; its game data, client
patches, extracted assets, and SQLite character database are not included or migrated.

## Project layout

- `client/` — TypeScript + Phaser browser client, including a local demo mode and two original procedural maps.
- `server/` — ASP.NET Core game server with versioned WebSocket authentication, map-scoped movement/combat/chat,
  quests, inventory, equipment, and persistent rewards.
- `server/PixelMmo.Server.Tests/` — tests for character limits, reward idempotency, autosave safety, equipment, and gates.
- `supabase/migrations/` — Postgres schema for persistent game data. Game tables have RLS enabled with no browser
  policies; only the trusted game server should write them.
- `.github/workflows/` — public-repository CI and GitHub Pages deployment.

## Run the browser demo

1. Install Node.js 24 or newer.
2. In `client/`, run `npm ci` and then `npm run dev`.
3. Open the local Vite URL and choose **Play local demo**. This mode stores progress in browser memory only.

For authenticated multiplayer, copy `client/.env.example` to `client/.env.local` and set:

```text
VITE_SUPABASE_URL=https://YOUR_PROJECT.supabase.co
VITE_SUPABASE_PUBLISHABLE_KEY=sb_publishable_...
VITE_GAME_SERVER_URL=ws://localhost:5381/ws/game
```

The publishable key is intended for browser use. Never put a database password, Supabase secret key, or service-role
key in a `VITE_` variable or GitHub Pages build. The browser and server must both use protocol version 2.

## Run the game server locally

Install the .NET 10 SDK. From `server/` run:

```powershell
dotnet run
```

The development server listens on `http://localhost:5381`. With no database URL it uses a development-only
in-memory store. To use Supabase Postgres, set these server environment variables before starting it:

```text
Supabase__Url=https://YOUR_PROJECT.supabase.co
Supabase__PublishableKey=sb_publishable_...
ConnectionStrings__GameDatabase=Host=...;Port=5432;Database=postgres;Username=...;Password=...;SSL Mode=Require
```

Back up the database, then apply `supabase/migrations/001_game_core.sql` followed by
`supabase/migrations/002_phase_one_world.sql` in the Supabase SQL editor. Migration 002 keeps existing characters
in Verdant Field and grants each one its job's starter weapon. Use the Supabase shared **session** pooler
for a persistent home-hosted process when the host network is IPv4-only; keep the connection string only on the game
server. The server verifies each first-frame access token through Supabase Auth and binds all character operations
to the returned Auth user ID.

## Google OAuth setup

In Supabase Auth, enable Google and configure the provider credentials. Add the local callback and the eventual
GitHub Pages URL to the Auth redirect allow list. The browser handles login; it then sends the access token in the
first WebSocket message over `wss://` in public deployments. Tokens are never placed in the WebSocket URL.

## Zero-cost hosting boundaries

- GitHub Pages serves the static client from the public repository. Its project site URL is based on the GitHub
  account and repository name; no paid custom domain is assumed.
- Supabase stays on the Free plan. Free quotas and availability can change; pause new testers if quotas are reached.
- The .NET game server runs on the developer's PC. Remote play works only while that PC and its internet connection
  are available. Public access also requires inbound connectivity, port forwarding, and TLS for secure WebSockets.
  If the ISP uses CGNAT or blocks inbound traffic, keep testing on the LAN until a no-cost route is available.
- Export database backups to local storage regularly; the free project is not treated as the only copy of player data.

## Phase roadmap

1. Core world: accounts, characters/jobs, Lumen Town and Verdant Field, authoritative movement/combat, Starbolt,
   Moss Slime rewards, inventory/equipment, Elder Lyra's starter quest, and map-local chat.
2. Social/economy: parties, friends, guilds, trade, mail, crafting, and equipment upgrades.
3. Long-term content: dungeons, events, PvP, guild war, more maps and jobs.

The first phase is the implementation target in this repository. Later phases should be added only after the
5–20-player acceptance checks pass.

## Validation

From `client/`: `npm ci`, `npm run check`, `npm test`, and `npm run build`.

From the project root: `dotnet build server/PixelMmo.Server.csproj --configuration Release` and
`dotnet test server/PixelMmo.Server.Tests/PixelMmo.Server.Tests.csproj --configuration Release`.

GitHub Actions runs the client/server checks and applies both migrations to a temporary PostgreSQL instance on pushes
and pull requests. It also publishes the static client to GitHub Pages from `main`; repository variables can later
provide the Supabase URL/key and public game-server WSS URL. Never use repository secrets for browser-exposed values.
