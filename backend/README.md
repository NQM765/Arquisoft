# RTS ArquiSoft Backend

Backend separado en dos servicios FastAPI y un paquete compartido:

- `support-service`: autenticacion, usuarios, resumenes de partida y Firebase.
- `matchmaking-service`: cola multiplayer, ciclo de partida, ready state, metadata Relay/Lobby y host migration.
- `shared`: CORS, seguridad, conexiones y modelos comunes.

## Estructura

```text
backend/
  support-service/
    app/
    config/
  matchmaking-service/
    app/
  shared/
    connections/
    models/
  docker-compose.yml
  Dockerfile
```

## Requisitos

- Docker Desktop
- Archivo `.env` basado en `.env.example`
- Credenciales Firebase en `support-service/config/serviceAccountKey.json`, o la ruta configurada por `FIREBASE_CREDENTIALS`

## Configuracion

`.env` minimo:

```env
POSTGRES_USER=postgres
POSTGRES_PASSWORD=admin
POSTGRES_DB=arqui
DATABASE_URL=postgresql://postgres:admin@db:5432/arqui
FIREBASE_CREDENTIALS=config/serviceAccountKey.json
AUTH_TOKEN_SECRET=replace-this-dev-secret
AUTH_GOOGLE_ID=google-oauth-client-id
ALLOWED_ORIGINS=*
```

## Levantar servicios

```bash
docker compose up --build
```

Servicios:

- `support`: `http://localhost:8000`
- `matchmaking`: `http://localhost:8001`
- `db`: PostgreSQL expuesto en `localhost:5433`

## Endpoints principales

Support:

| Metodo | Ruta | Descripcion |
|--------|------|-------------|
| `GET` | `/health` | Health check |
| `POST` | `/auth/google` | Valida el `id_token` de Google y devuelve `user_id`, `username` y `access_token` |
| `POST` | `/support/session-summary` | Guardar resumen en Firebase |
| `POST` | `/match/session-summary` | Ruta legacy para resumen |

Matchmaking:

| Metodo | Ruta | Descripcion |
|--------|------|-------------|
| `GET` | `/health` | Health check |
| `POST` | `/matchmaking/matches` | Crear partida y publicar codigo Relay |
| `GET` | `/matchmaking/queue/next` | Consumir la siguiente partida disponible |
| `POST` | `/matchmaking/matches/{match_id}/join` | Unirse a una partida |
| `GET` | `/matchmaking/matches/{match_id}` | Consultar partida |
| `POST` | `/matchmaking/matches/{match_id}/leave` | Salir de partida |
| `POST` | `/matchmaking/matches/{match_id}/heartbeat` | Heartbeat del host actual |
| `POST` | `/matchmaking/matches/{match_id}/snapshot` | Guardar snapshot de estado del juego |
| `GET` | `/matchmaking/matches/{match_id}/migration` | Consultar estado de migracion |
| `POST` | `/matchmaking/matches/{match_id}/migration/claim` | Reclamar host con un nuevo Relay join code |

Los endpoints de matchmaking requieren:

```http
Authorization: Bearer <access_token>
```

## Flujo multiplayer

1. Unity abre el frontend web en `/unity-login`.
2. El frontend inicia sesion con Google mediante NextAuth.
3. NextAuth envia el `id_token` de Google a `support` en `/auth/google`.
4. `support` valida Google, crea o encuentra el usuario y devuelve `user_id`, `username` y `access_token`.
5. El frontend redirige al callback local de Unity con el `access_token`.
6. Unity usa ese token en `Authorization: Bearer <access_token>` para matchmaking y resumenes.

Las partidas de matchmaking se persisten en PostgreSQL. Esto permite correr varias replicas de `matchmaking` siempre que compartan la misma base de datos, o bases sincronizadas de forma consistente entre maquinas.

Para host migration, Unity guarda heartbeats y snapshots periodicos. Si el host deja de enviar heartbeat, otro jugador puede crear una nueva sesion Relay, reclamar el rol de host y restaurar el ultimo snapshot guardado.
