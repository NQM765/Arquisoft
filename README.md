# RTS ArquiSoft

Proyecto RTS con cliente Unity, frontend web para login con Google y backend FastAPI para autenticacion, resumenes y matchmaking.

## Estructura

```text
.
|-- UnityProject/      # Juego en Unity
|-- backend/           # FastAPI support + matchmaking + PostgreSQL + RabbitMQ
|-- web-frontend/      # Next.js + NextAuth Google
```

## Requisitos

- Docker Desktop
- Node.js 20+ y npm, solo si vas a correr el frontend sin Docker
- Unity `6000.3.8f1`
- Dos cuentas de Google si vas a probar multiplayer local con dos instancias
- Credencial Firebase para guardar resumenes: `backend/support-service/config/serviceAccountKey.json`

## Flujo de Login

El login ya no se hace con usuario y clave dentro de Unity.

1. Unity abre el navegador en `http://localhost:3000/unity-login` con `select_account=1`.
2. La web usa NextAuth para iniciar sesion con Google.
3. NextAuth envia el `id_token` de Google al backend `support` en `POST /auth/google`.
4. El backend valida Google, crea el usuario si no existe y emite el `access_token` interno.
5. La web redirige al callback local de Unity en `127.0.0.1`.
6. Unity guarda el token en `AuthSession` y lo usa para matchmaking y resumenes.

Para multiplayer local, cada instancia de Unity recibe su propio token aunque el navegador comparta cookies. El flujo fuerza el selector de cuenta de Google para que puedas elegir una cuenta diferente por instancia.

## Puertos

Con el compose raiz segmentado, solo el proxy inverso publica puertos al host:

- Web proxy: `http://localhost:3000`
- Unity support proxy: `https://localhost:8000`
- Unity matchmaking proxy: `https://localhost:8001`

Los servicios internos quedan solo en la red privada de Docker:

- `web-frontend:3000`
- `support:8000`
- `matchmaking_1:8001`
- `matchmaking_2:8001`
- `db:5432`
- `rabbitmq:5672`

## Google OAuth

En Google Cloud Console crea un OAuth Client:

- Application type: `Web application`
- Authorized JavaScript origins:
  - `http://localhost:3000`
- Authorized redirect URIs:
  - `http://localhost:3000/api/auth/callback/google`

El `Client ID` debe ser el mismo en backend y frontend. El `Client Secret` solo va en el frontend.

## Variables de Entorno

No subas archivos `.env` ni credenciales reales al repositorio.

### `backend/.env`

```env
POSTGRES_USER=postgres
POSTGRES_PASSWORD=admin
POSTGRES_DB=arqui
DATABASE_URL=postgresql://postgres:admin@db:5432/arqui

FIREBASE_CREDENTIALS=config/serviceAccountKey.json
AUTH_TOKEN_SECRET=replace-with-a-long-random-secret

# Debe ser el mismo Client ID usado por NextAuth.
GOOGLE_CLIENT_ID=your-google-client-id.apps.googleusercontent.com

ALLOWED_ORIGINS=http://localhost:3000
```

Notas:

- `DATABASE_URL` usa host `db` porque el backend corre dentro de Docker Compose.
- `FIREBASE_CREDENTIALS` es relativo al contenedor `support-service`, por eso el valor local esperado es `config/serviceAccountKey.json`.
- `AUTH_TOKEN_SECRET` firma el token interno que Unity usa contra `matchmaking`.

### `web-frontend/.env`

```env
AUTH_SECRET=replace-with-a-long-random-secret
AUTH_GOOGLE_ID=your-google-client-id.apps.googleusercontent.com
AUTH_GOOGLE_SECRET=your-google-client-secret

NEXTAUTH_URL=http://localhost:3000
PY_BACKEND_URL=https://localhost:8000
```

Notas:

- `AUTH_GOOGLE_ID` debe ser igual a `backend/.env` -> `GOOGLE_CLIENT_ID`.
- Si corres la arquitectura segmentada con el compose raiz, `PY_BACKEND_URL` se sobreescribe como `http://support:8000` para que NextAuth llame al servicio por la red privada de Docker.
- Si corres solo `web-frontend/docker-compose.yml`, ese compose aislado usa `PY_BACKEND_DOCKER_URL` y por defecto apunta a `http://host.docker.internal:8000`.
- Para generar secretos locales puedes usar PowerShell:

```powershell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

## Levantar Backend

Opcion recomendada: levantar toda la arquitectura segmentada desde la raiz del repo:

```powershell
docker compose up --build -d
```

Verifica:

```powershell
curl.exe -k https://localhost:8000/health
curl.exe -k https://localhost:8001/health
```

Detener:

```powershell
docker compose down
```

El compose raiz crea dos redes Docker:

- `public`: contiene el reverse proxy expuesto al host.
- `private`: contiene frontend, support, matchmaking, PostgreSQL y RabbitMQ.

El contenedor Nginx actua como tres proxies logicos: browser a frontend, Unity a support, y Unity a matchmaking. TLS termina en Nginx; dentro de Docker los servicios hablan HTTP.

### Levantar solo Backend

El compose en `backend/` se mantiene como apoyo de desarrollo si necesitas correr solamente servicios backend sin la segmentacion completa.

```powershell
cd backend
docker compose up --build -d
```

## Levantar Frontend

El frontend queda incluido en el compose raiz. Si necesitas correrlo aislado para desarrollo:

```powershell
cd web-frontend
docker compose up --build -d
```

Opcion local con npm:

```powershell
cd web-frontend
npm ci
npm run dev
```

Abre:

```text
http://localhost:3000
```

## Abrir el Juego en Unity

1. Abre Unity Hub.
2. Agrega y abre `UnityProject/`.
3. Usa Unity `6000.3.8f1`.
4. Asegurate de tener corriendo:
   - Backend support en `localhost:8000`
   - Backend matchmaking en `localhost:8001`
   - Frontend en `localhost:3000`
5. Abre la escena de menu/login y presiona `Ingresar`.
6. El navegador debe mostrar selector de cuenta de Google.
7. Al terminar, Unity debe mostrar `Login correcto (...)`.

## Probar Multiplayer Local

1. Corre backend y frontend.
2. Abre el proyecto en Unity Editor.
3. Haz `Build & Run` para una segunda instancia del juego.
4. En el Editor, presiona `Ingresar` y elige la cuenta Google A.
5. En el Build, presiona `Ingresar` y elige la cuenta Google B.
6. Entra al menu multiplayer:
   - Una instancia crea partida.
   - La otra instancia busca/unirse a partida.

Cada instancia conserva su propio `access_token` en memoria. Si ves el dashboard web en una pestana vieja, cierrala y vuelve a presionar `Ingresar` desde Unity.

## Endpoints Principales

Support (`localhost:8000`):

- `GET /health`
- `POST /auth/google`
- `POST /support/session-summary`
- `POST /match/session-summary` ruta legacy

Matchmaking (`localhost:8001`):

- `GET /health`
- `POST /matchmaking/matches`
- `GET /matchmaking/queue/next`
- `POST /matchmaking/matches/{match_id}/join`
- `GET /matchmaking/matches/{match_id}`
- `POST /matchmaking/matches/{match_id}/leave`
- `POST /matchmaking/matches/{match_id}/heartbeat`
- `POST /matchmaking/matches/{match_id}/snapshot`
- `GET /matchmaking/matches/{match_id}/migration`
- `POST /matchmaking/matches/{match_id}/migration/claim`

Los endpoints de matchmaking requieren:

```http
Authorization: Bearer <access_token>
```

## Confiabilidad y Host Migration

`matchmaking` persiste las partidas en PostgreSQL y puede ejecutarse con varias replicas detras del proxy. Si despliegas dos maquinas distintas por Cloudflare Tunnel, ambas deben compartir o sincronizar de forma consistente la base usada por `DATABASE_URL`; si cada replica ve una base distinta, no hay una eleccion confiable de nuevo host.

Unity guarda heartbeat del host y snapshots periodicos del mundo. Si el host Unity se cierra o abandona, los clientes detectan que el heartbeat expiro, uno reclama el host, crea un nuevo Relay join code y restaura el ultimo snapshot. La perdida esperada de estado depende de `hostSnapshotInterval` en `MultiplayerBootstrap` y por defecto es de hasta 30 segundos.

Para un Nginx con prefijos `/api/support/` y `/api/matchmaking/`, configura Unity con:

```text
Support API baseUrl: https://cronicasciudadblanca.qzz.io/api/support
Matchmaking API baseUrl: https://cronicasciudadblanca.qzz.io/api/matchmaking
```

## Troubleshooting

- `Error 401: invalid_client`: el Client ID/Secret de Google esta mal configurado o el redirect URI no coincide exactamente.
- `Login incompleto`: Google autentico, pero NextAuth no pudo obtener token interno desde backend. Revisa que `support` este arriba y que `GOOGLE_CLIENT_ID` sea igual a `AUTH_GOOGLE_ID`.
- El navegador abre `Dashboard` en vez de selector de cuenta: cierra la pestana vieja y vuelve a iniciar desde Unity. El flujo actual usa `/google-login` con `prompt=select_account` y `max_age=0`.
- El contenedor web no alcanza al backend: con el compose raiz debe resolver `PY_BACKEND_URL=http://support:8000`; con el compose aislado de `web-frontend/`, revisa `PY_BACKEND_DOCKER_URL`.
- `support` no arranca: revisa que PostgreSQL este `healthy` y que exista `backend/support-service/config/serviceAccountKey.json`.

## Archivos que no se deben subir

- `backend/.env`
- `web-frontend/.env`
- `backend/support-service/config/serviceAccountKey.json`
- `UnityProject/Build&Run/`
- `UnityProject/Library/`, `Temp/`, `Obj/`, `Build/`, `Builds/`, `Logs/`
