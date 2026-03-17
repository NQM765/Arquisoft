// matchmaking-server.js
require('dotenv').config();
const WebSocket = require('ws');
const jwt = require('jsonwebtoken');

const wss = new WebSocket.Server({ port: 3001 });
const queue = []; // jugadores esperando

wss.on('connection', (ws) => {
  ws.isAlive = true;

  ws.on('message', async (raw) => {
    let msg;
    try { msg = JSON.parse(raw); } catch { return; }

    if (msg.type === 'find_match') {
      // Valida el JWT con el mismo secreto que ya usas
      try {
        const decoded = jwt.verify(msg.token, process.env.JWT_SECRET);
        ws.playerId  = decoded.id;
        ws.username  = decoded.username;
      } catch {
        ws.send(JSON.stringify({ type: 'error', message: 'Token inválido' }));
        ws.close();
        return;
      }

      console.log(`${ws.username} busca partida...`);
      queue.push(ws);
      ws.send(JSON.stringify({ type: 'queued', position: queue.length }));

      // Si hay 2+ jugadores, empareja los primeros dos
      if (queue.length >= 2) {
        const [p1, p2] = queue.splice(0, 2);
        const roomId = `room-${Date.now()}`;

        // P1 será el host (levanta el servidor NGO)
        p1.send(JSON.stringify({
          type: 'match_found',
          roomId,
          role: 'host',
          opponentUsername: p2.username,
          hostPort: 7777  // puerto fijo para pruebas locales
        }));

        // P2 se conecta al host
        p2.send(JSON.stringify({
          type: 'match_found',
          roomId,
          role: 'client',
          opponentUsername: p1.username,
          hostIp: '127.0.0.1',
          hostPort: 7777
        }));

        console.log(`Match creado: ${p1.username} vs ${p2.username} en ${roomId}`);
      }
    }

    if (msg.type === 'cancel_match') {
      const idx = queue.indexOf(ws);
      if (idx !== -1) queue.splice(idx, 1);
      ws.send(JSON.stringify({ type: 'cancelled' }));
    }
  });

  ws.on('close', () => {
    const idx = queue.indexOf(ws);
    if (idx !== -1) queue.splice(idx, 1);
  });
});

console.log('Matchmaking server en ws://localhost:3001');