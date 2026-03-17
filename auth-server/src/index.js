require('dotenv').config();
const express = require('express');
const cors    = require('cors');
const { initDB } = require('./db');
const authRoutes = require('./routes/auth');

const app = express();

app.use(cors());
app.use(express.json());
app.use(express.static('src/public'));
app.use('/auth', authRoutes);

// Ruta de health check
app.get('/health', (req, res) => res.json({ status: 'ok' }));

// Inicia DB y luego el servidor
initDB().then(() => {
  app.listen(process.env.PORT, () => {
    console.log(`Servidor corriendo en http://localhost:${process.env.PORT}`);
  });
});