const express = require('express');
const bcrypt  = require('bcrypt');
const jwt     = require('jsonwebtoken');
const { pool } = require('../db');

const router = express.Router();
const SALT_ROUNDS = 12;

// ── REGISTRO ──────────────────────────────────────────────
router.post('/register', async (req, res) => {
  const { username, email, password } = req.body;

  // Validaciones básicas
  if (!username || !email || !password)
    return res.status(400).json({ error: 'Faltan campos requeridos' });

  if (password.length < 8)
    return res.status(400).json({ error: 'La contraseña debe tener al menos 8 caracteres' });

  try {
    // Verifica si el usuario o email ya existen
    const existing = await pool.query(
      'SELECT id FROM users WHERE email = $1 OR username = $2',
      [email, username]
    );
    if (existing.rows.length > 0)
      return res.status(409).json({ error: 'El usuario o email ya está registrado' });

    // Hashea la contraseña
    const hashedPassword = await bcrypt.hash(password, SALT_ROUNDS);

    // Inserta el usuario
    const result = await pool.query(
      'INSERT INTO users (username, email, password) VALUES ($1, $2, $3) RETURNING id, username, email',
      [username, email, hashedPassword]
    );
    const user = result.rows[0];

    // Genera JWT
    const token = jwt.sign(
      { id: user.id, username: user.username, email: user.email },
      process.env.JWT_SECRET,
      { expiresIn: process.env.JWT_EXPIRES_IN }
    );

    res.status(201).json({ token, user: { id: user.id, username: user.username, email: user.email } });

  } catch (err) {
    console.error(err);
    res.status(500).json({ error: 'Error interno del servidor' });
  }
});

// ── LOGIN ─────────────────────────────────────────────────
router.post('/login', async (req, res) => {
  const { email, password } = req.body;

  if (!email || !password)
    return res.status(400).json({ error: 'Faltan campos requeridos' });

  try {
    // Busca el usuario por email
    const result = await pool.query(
      'SELECT * FROM users WHERE email = $1',
      [email]
    );
    const user = result.rows[0];

    if (!user)
      return res.status(401).json({ error: 'Credenciales inválidas' });

    // Compara la contraseña
    const validPassword = await bcrypt.compare(password, user.password);
    if (!validPassword)
      return res.status(401).json({ error: 'Credenciales inválidas' });

    // Genera JWT
    const token = jwt.sign(
      { id: user.id, username: user.username, email: user.email },
      process.env.JWT_SECRET,
      { expiresIn: process.env.JWT_EXPIRES_IN }
    );

    res.json({ token, user: { id: user.id, username: user.username, email: user.email } });

  } catch (err) {
    console.error(err);
    res.status(500).json({ error: 'Error interno del servidor' });
  }
});

// ── VERIFICAR TOKEN (Unity lo usará para validar sesiones) ─
router.get('/verify', (req, res) => {
  const authHeader = req.headers['authorization'];
  const token = authHeader?.split(' ')[1]; // "Bearer <token>"

  if (!token)
    return res.status(401).json({ error: 'Token requerido' });

  try {
    const decoded = jwt.verify(token, process.env.JWT_SECRET);
    res.json({ valid: true, user: { id: decoded.id, username: decoded.username, email: decoded.email } });
  } catch {
    res.status(401).json({ valid: false, error: 'Token inválido o expirado' });
  }
});

module.exports = router;