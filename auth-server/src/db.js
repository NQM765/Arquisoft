const { Pool } = require('pg');
require('dotenv').config();

const pool = new Pool({
  host:     process.env.DB_HOST,
  port:     process.env.DB_PORT,
  database: process.env.DB_NAME,
  user:     process.env.DB_USER,
  password: process.env.DB_PASSWORD,
});

// Crea la tabla de usuarios si no existe
async function initDB() {
  await pool.query(`
    CREATE TABLE IF NOT EXISTS users (
      id         SERIAL PRIMARY KEY,
      username   VARCHAR(50)  UNIQUE NOT NULL,
      email      VARCHAR(100) UNIQUE NOT NULL,
      password   VARCHAR(255) NOT NULL,
      created_at TIMESTAMP DEFAULT NOW()
    )
  `);
  console.log('Base de datos lista');
}

module.exports = { pool, initDB };