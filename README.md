# Meet to Manage Backend

.NET 9 Web API for the Meet to Manage LMS & Virtual Classroom platform.

## Solution structure

| Project | Responsibility |
|---|---|
| `iucs.meettomanage.api` | Web API host: controllers, DI wiring, CORS, OpenAPI, health endpoint, `CurrentUserService`. |
| `iucs.meettomanage.application` | Application layer: DTOs, services, AutoMapper profiles. |
| `iucs.meettomanage.domain` | Domain layer: entities (`Entities/<area>`), enums, `MeetToManageDbContext` + audit interceptor (`Data/`), generic repository & unit of work (`Repository/`). |

Entity design and the `BaseEntity` / `AuditEntity` split are documented in
[docs/DATABASE_SCHEMA.md](docs/DATABASE_SCHEMA.md).

## Getting started

```bash
# 1. Start PostgreSQL (host port 5433 to avoid clashing with a native install)
docker compose up -d

# 2. Run the API — in Development it applies migrations and seeds
#    the admin account + Phonics/Maths payment accounts automatically
dotnet run --project iucs.meettomanage.api --launch-profile http
```

Health check: `GET http://localhost:5288/health`. OpenAPI (dev only): `GET /openapi/v1.json`.

Development admin login (seeded, dev only — override via the `Seed` section):
`admin@meettomanage.cloud` / `Admin@12345`.

The frontend targets this API through `VITE_API_BASE_URL` in its
`.env.development`; remove that variable to run the UI in demo mode with mock
data.

Connection string `ConnectionStrings:ReaderNestDb` (key name kept as-is —
renaming it would require also updating the deployed environment's connection
string env var) lives in
`appsettings.Development.json` for local dev — use user secrets or environment
variables for real credentials, never commit them.
