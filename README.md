# Prototype1 - Pruebas de Login con API

Repositorio con cliente Unity y backend Node.js/Express para autenticacion.

## Quick Links

- [Windows](#1-preparar-backend-windows)
- [Linux](#1-preparar-backend-linux)

## Estructura

- `My project/`: cliente Unity.
- `backend/`: API de autenticacion.

## Requisitos

### Windows

- Windows 10/11
- PowerShell
- Node.js 18+ (incluye `npm`)
- Unity Hub + version de Unity del proyecto

### Linux

- Linux (cualquier distribucion)
- Terminal
- Node.js 18+ (incluye `npm`)
- Unity Hub + version de Unity del proyecto

---

## 1) Preparar backend (Windows)

1. Abre PowerShell.
2. Ve a la carpeta del backend:

```powershell
cd "C:\Users\User\Documents\U\Semestre 8\ArquiSoft\Prototype1\backend"
```

3. Sincroniza base de datos con Prisma:

```powershell
cmd /c npx prisma db push
```

4. Inicia el backend:

```powershell
cmd /c npm.cmd run start
```

Debe quedar corriendo en `http://localhost:3000`.

## 2) Probar API rapido desde terminal (Windows)

Abre otra ventana de PowerShell (deja el backend corriendo en la primera).

### Registro

```powershell
Invoke-RestMethod -Method Post -Uri 'http://localhost:3000/auth/register' -ContentType 'application/json' -Body '{"username":"user_test_1","password":"123456"}'
```

Si ese usuario ya existe, cambia `user_test_1` por otro nombre.

### Login

```powershell
$login = Invoke-RestMethod -Method Post -Uri 'http://localhost:3000/auth/login' -ContentType 'application/json' -Body '{"username":"user_test_1","password":"123456"}'
$login.accessToken
```

Debe imprimir un JWT.

### Validar token (`/auth/me`)

```powershell
Invoke-RestMethod -Method Get -Uri 'http://localhost:3000/auth/me' -Headers @{ Authorization = "Bearer $($login.accessToken)" }
```

Esperado: `success: true` y `user` con `id` y `username`.

---

## 1) Preparar backend (Linux)

1. Abre una terminal.
2. Ve a la carpeta del backend:

```bash
cd /home/javier-giraldo/Documents/Arquisoft/backend
```

3. Sincroniza base de datos con Prisma:

```bash
npx prisma db push
```

4. Inicia el backend:

```bash
npm run start
```

Debe quedar corriendo en `http://localhost:3000`.

## 2) Probar API rapido desde terminal (Linux)

Abre otra terminal (deja el backend corriendo en la primera).

### Registro

```bash
curl -X POST http://localhost:3000/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"user_test_1","password":"123456"}'
```

Si ese usuario ya existe, cambia `user_test_1` por otro nombre.

### Login

```bash
curl -X POST http://localhost:3000/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"user_test_1","password":"123456"}'
```

Debe devolver un JSON con `accessToken`. Guarda el token.

### Validar token (`/auth/me`)

```bash
curl -X GET http://localhost:3000/auth/me \
  -H "Authorization: Bearer TU_ACCESS_TOKEN"
```

Reemplaza `TU_ACCESS_TOKEN` con el token del login. Esperado: `success: true` y `user` con `id` y `username`.

---

## 3) Probar flujo en Unity

1. Abre el proyecto `My project` en Unity.
2. Abre `LoginScene`.
3. Selecciona el GameObject con `LoginController`.
4. En Inspector, confirma que `loginButton` esta asignado.
5. Presiona Play.
6. Haz login con un usuario valido.
7. Verifica que entra a `MainMenuScene`.
8. Entra a `GameScene` y valida:
   - Si hay token valido y `/auth/me` responde 200, permanece en `GameScene`.
   - Si no hay token o es invalido, `GameSessionGuard` redirige a `LoginScene`.

## 4) Validar seguridad minima

- Backend no devuelve `passwordHash` ni `salt` al cliente en respuestas de auth.
- Cliente no persiste contrasena en sesion; solo usa `CurrentUser` y `AccessToken`.
- Para produccion, usa `https://` en la API.
- En desarrollo local se permite `http://localhost` por conveniencia.

## 5) Resultado esperado del smoke test

Flujo completo de backend:

- `register` -> `success: true`
- `login` -> `success: true` + `accessToken`
- `/auth/me` con Bearer token -> `success: true`

Ejemplo real:

```json
{"RegisteredSuccess":true,"LoginSuccess":true,"HasAccessToken":true,"MeSuccess":true}
```

## Troubleshooting

### Windows

- Error `npm.ps1 cannot be loaded because running scripts is disabled`:
  usa `cmd /c npm.cmd ...` como en este README.
- `Error interno del servidor` al registrar:
  ejecuta de nuevo `cmd /c npx prisma db push`.
- `Token invalido o expirado` en `/auth/me`:
  vuelve a hacer login y usa el token nuevo.

### Linux

- `Error interno del servidor` al registrar:
  ejecuta de nuevo `npx prisma db push`.
- `Token invalido o expirado` en `/auth/me`:
  vuelve a hacer login y usa el token nuevo.
