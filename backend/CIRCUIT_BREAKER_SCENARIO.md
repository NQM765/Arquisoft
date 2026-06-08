# Circuit Breaker - Interacciones con Patrones Existentes

## Contexto
- **Servicio:** Matchmaking Service (FastAPI, puerto 8001)
- **Dependencia:** RabbitMQ (cola `matchmaking.available`)
- **Problema:** RabbitMQ down = timeout indefinido = cascada de fallos
- **Solución:** Circuit Breaker detiene llamadas rápido, usa fallback

## Matriz de Compatibilidad

| Patrón | Ubicación | Impacto | Acción |
|--------|-----------|--------|--------|
| **Health Checks (Consul)** | `consul_registration.py` | ✅ Complementario | Reportar estado "degraded" en `/health` cuando CB=OPEN |
| **Redis Cache** | `cache.py` | ✅ Ideal | Usar como fallback cuando CB=OPEN |
| **pika Connection** | `rabbit.py` | ⚠️ Conflictivo | Agregar `socket_timeout=2s` (CRÍTICO) |
| **Service Deregistration** | `consul_registration.py` | ✅ Compatible | Sin cambios necesarios |
| **Exception Handling** | Múltiple | ✅ Compatible | Capturar `CircuitBreakerOpenException` |

## Problema Clave: pika sin timeout

**SIN CB (actual):**
```
RabbitMQ offline → pika sin timeout → espera 30+ seg → error usuario
```

**CON CB (mejorado):**
```
RabbitMQ offline → pika con socket_timeout=2s → falla rápido → CB abre → fallback inmediato (<10ms)
```

**CAMBIO CRÍTICO en `rabbit.py`:**
```python
params = pika.ConnectionParameters(
    host="rabbitmq",
    socket_timeout=2,         # ← SIN ESTO NO FUNCIONA
    connection_attempts=1,     # ← Evitar reintentos lentos
)
```

## Estados del CB

| Estado | Qué hace | Cuándo pasa | Duración |
|--------|----------|-----------|----------|
| **CLOSED** | Llamadas normales a RabbitMQ | Sistema OK | indefinido |
| **OPEN** | Bloquea llamadas → fallback rápido | 3+ fallos | 30 segundos |
| **HALF_OPEN** | Prueba 1 llamada | Timeout cumplido | 1 solicitud |

## Flujo Completo

```
ESTADO NORMAL:
1. GET /queue/next → CB CLOSED
2. CB llama consume_next_available_match()
3. RabbitMQ OK → devuelve match
4. Agrega a cache → respuesta OK

ESTADO FALLO:
1. RabbitMQ down / timeout
2. Solicitud 1-3: RabbitMQ falla, contador++
3. Solicitud 4: contador >= 3 → CB.state = OPEN (30s timeout)
4. Solicitud 5+: CB.OPEN → CircuitBreakerOpenException
   → Fallback: cache_recent_matches()
   → Respuesta degraded (<10ms)

RECUPERACIÓN:
1. Después 30s: CB.state = HALF_OPEN
2. Intenta 1 solicitud a RabbitMQ
3. Si éxito → CB.state = CLOSED (normal)
4. Si falla → CB.state = OPEN (otro 30s)
```

## Parámetros

```python
CircuitBreaker(
    failure_threshold=3,      # Fallos para abrir
    timeout=30,              # Segundos antes de HALF_OPEN
    name="rabbitmq"
)
```

## Casos de Prueba

| # | Caso | Esperado |
|---|------|----------|
| 1 | RabbitMQ down + 3 solicitudes | CB=OPEN en <10s |
| 2 | 30s después + RabbitMQ OK | CB=HALF_OPEN→CLOSED |
| 3 | GET /health cuando CB=OPEN | `"status": "degraded"` |
| 4 | 10 solicitudes mientras CB=OPEN | Latencia máx <2s |

## Archivos Involucrados

| Archivo | Tipo | Cambio |
|---------|------|--------|
| `circuit_breaker.py` | ✨ Nuevo | Clase CB (100 líneas) |
| `rabbit.py` | ✏️ Modificar | +2 parámetros pika |
| `cache.py` | ✏️ Extender | +2 funciones fallback |
| `main.py` | ✏️ Extender | +1 línea /health |
| `router.py` | ✏️ Integrar | Wrapping endpoints |

## Impacto en Equipo

✅ **Cambios NO invasivos** - cada modificación es isolated o es pura adición:
- pika timeouts: solo parámetros, misma funcionalidad
- cache.py: funciones nuevas, nada reemplazado
- main.py: endpoint extendido, no reescrito
- router.py: wrapper, lógica existente intacta

✅ **Archivos NO afectados**: store.py, models.py, support-service/, security.py, cors.py
