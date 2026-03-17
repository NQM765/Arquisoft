# Multiplayer Game — Setup Guide

> Auth + Matchmaking + Unity Netcode for GameObjects

## Stack

| Componente | Tecnología |
|---|---|
| Backend auth | Node.js + Express |
| Base de datos | PostgreSQL |
| Autenticación | JWT (bcrypt) |
| Matchmaking | Node.js + WebSocket (ws) |
| Red en juego | Netcode for GameObjects (NGO) |

---

## Requisitos

- Node.js 18+
- npm 9+
- PostgreSQL 14+


**Paquetes de Unity:**
- NativeWebSocket — `https://github.com/endel/NativeWebSocket.git#upm`
- Netcode for GameObjects — `com.unity.netcode.gameobjects`

---


## 1. Configurar PostgreSQL

Descarga e instala PostgreSQL desde [postgresql.org/download](https://postgresql.org/download). Durante la instalación anota el puerto (default 5432) y la contraseña del usuario `postgres`.

Luego crea la base de datos:

```bash
psql -U postgres
CREATE DATABASE gameauth;
\q
```

---

## 2. Backend de autenticación

```bash
cd auth-server
npm install
```

Crea un archivo `.env` en la carpeta `server/`:

```env
PORT=3000
DB_HOST=localhost
DB_PORT=5432
DB_NAME=gameauth
DB_USER=postgres
DB_PASSWORD=tu_password_aqui
JWT_SECRET=una_clave_muy_larga_y_random_aqui
JWT_EXPIRES_IN=7d
```

Corre el servidor:

```bash
npm run dev
# Servidor corriendo en http://localhost:3000
```

**Verificar que funciona:**

```bash
# Registro
curl -X POST http://localhost:3000/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"testuser","email":"test@test.com","password":"12345678"}'

# Login
curl -X POST http://localhost:3000/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"test@test.com","password":"12345678"}'

# Verificar token
curl http://localhost:3000/auth/verify \
  -H "Authorization: Bearer <token_obtenido_en_login>"
```

---

## 4. Servidor de matchmaking

Abre una segunda terminal:

```bash
npm run matchmaking
# Matchmaking server en ws://localhost:3001
```

> Los dos servidores deben estar corriendo simultáneamente para que el juego funcione.

Para correrlos con un solo comando:

```bash
npm run dev:all
```

---

## 5. Proyecto Unity

### Instalar paquetes

1. `Window` → `Package Manager` → **+** → `Add package from git URL`
2. Pega: `https://github.com/endel/NativeWebSocket.git#upm` → Add
3. Repite con: `com.unity.netcode.gameobjects`

### Estructura de escenas

| Escena | Descripción |
|---|---|
| `Login` | Abre el navegador para autenticarse. Contiene `AuthManager`. |
| `MainMenu` | Pantalla principal con el botón Buscar Partida. Contiene `MatchmakingManager`. |
| `Game` | Escena de juego. Contiene `NetworkManager` y `GameSceneManager`. |

### Estructura de scripts

```
Assets/
├── Scripts/
│   ├── Auth/
│   │   └── AuthManager.cs
│   ├── Matchmaking/
│   │   ├── MatchmakingManager.cs
│   │   └── GameSession.cs
│   └── Game/
│       ├── GameSceneManager.cs
│       └── MapSpawner.cs
├── Scenes/
│   ├── Login.unity
│   ├── MainMenu.unity
│   └── Game.unity
└── Prefabs/
    ├── NetworkPrefabsList
    └── Cube.prefab
```


## 6. Probar localmente

### Prueba de matchmaking y objetos en red

1. Levanta los dos servidores Node (secciones 3 y 4)
2. Haz una build de Unity: `File` → `Build And Run`
3. Presiona Play en el editor de Unity — esta es la segunda instancia
4. En cada instancia haz login con **usuarios distintos**
5. Ambos llegan a `MainMenu` → presiona **Buscar Partida** en cada uno
6. El servidor los empareja y ambos cargan la escena `Game`

En la terminal del matchmaking deberías ver:

```
jugador1 busca partida...
jugador2 busca partida...
Match creado: jugador1 vs jugador2 en room-1234567890
```

7. Una vez en la escena `Game`, haz **click en cualquier punto del plano**
8. Debe aparecer un cubo en ese punto **en las dos pantallas simultáneamente**
9. Prueba desde la otra instancia — los cubos también deben aparecer en ambas

---

## 7. Probar en dos computadores (misma red)

Obtén la IP local de la máquina que tiene los servidores Node:

```bash
# Mac/Linux
ipconfig getifaddr en0

# Windows
ipconfig  # busca "Dirección IPv4"
```

En el Inspector de Unity cambia `localhost` por esa IP:

- `AuthManager` → campo `Auth Server Url`: `http://192.168.1.X:3000`
- `MatchmakingManager` → campo `Matchmaking Url`: `ws://192.168.1.X:3001`

> Ambos computadores deben estar en la misma red WiFi/LAN. El firewall debe permitir los puertos **3000**, **3001** y **7777**.

---

## 8. Solución de problemas

| Síntoma | Causa probable |
|---|---|
| `Token inválido` en la terminal | `JWT_SECRET` diferente entre los dos servidores |
| `WebSocket error` en Unity | `matchmaking-server.js` no está corriendo o la URL está mal |
| Se queda en cola (`queued`) | Solo un jugador buscó partida, falta el segundo |
| La escena `Game` no carga | El nombre en `SceneManager.LoadScene()` no coincide con el archivo |
| CPU alto en Unity Editor | El prefab está registrado dos veces en el `NetworkManager` — revisa duplicados en el `Network Prefabs List` |
| Warning `CS0618 ServerRpc` | Reemplazar `[ServerRpc]` por `[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]` |
| Los cubos no aparecen en ambas pantallas | El prefab `Cube` no tiene el componente `Network Object` o no está registrado en el `Network Prefabs List` |
