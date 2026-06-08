# Circuit Breaker para Matchmaking Service

## ¿Qué es?
Patrón de resiliencia que bloquea llamadas a RabbitMQ cuando falla, evita cascadas y devuelve fallback rápido.

## Estados
| Estado | Qué hace | Duración |
|--------|----------|----------|
| **CLOSED** | Llamadas normales a RabbitMQ | indefinido |
| **OPEN** | Bloquea todas las llamadas, devuelve fallback | 30 segundos |
| **HALF_OPEN** | Intenta 1 llamada de prueba | 1 solicitud |

## Parámetros
```python
CircuitBreaker(
    failure_threshold=3,      # Fallos para abrir
    timeout=30,              # Segundos antes de HALF_OPEN
    name="rabbitmq"
)
```

## Cambios Necesarios

### 1. `rabbit.py` ⚠️ **OBLIGATORIO**
Agregar timeouts (RabbitMQ falla rápido, no cuelga):
```python
params = pika.ConnectionParameters(
    host="rabbitmq",
    port=5672,
    connection_attempts=1,    # ← agregar
    socket_timeout=2,         # ← agregar (CRÍTICO)
    blocked_connection_timeout=10,
)
```

### 2. `cache.py` (Opcional - mejora fallback)
Agregar 2 funciones:
```python
def cache_recent_matches(limit: int = 10) -> list[dict]:
    """Últimas partidas como fallback"""
    try:
        client = _get_client()
        cached = client.lrange("matchmaking:recent_matches", 0, limit - 1)
        return [json.loads(m) for m in cached]
    except:
        return []

def add_to_recent_matches(match: dict) -> None:
    """Agregar partida al cache"""
    try:
        client = _get_client()
        client.lpush("matchmaking:recent_matches", json.dumps(match))
        client.ltrim("matchmaking:recent_matches", 0, 9)
    except:
        pass
```

### 3. `main.py` (Para Consul observability)
Inicializar CB y actualizar `/health`:
```python
from app.circuit_breaker import CircuitBreaker

# Startup
rabbit_breaker = CircuitBreaker(failure_threshold=3, timeout=30, name="rabbitmq")

# Health endpoint
@app.get("/health")
def health():
    breaker_state = rabbit_breaker.state.value
    return {
        "status": "degraded" if breaker_state != "CLOSED" else "ok",
        "component": "matchmaking",
        "instance": socket.gethostname(),
        "circuitBreaker": breaker_state  # ← Consul lo monitorea
    }
```

### 4. `router.py` (Integración)
Importar y usar CB:
```python
from app.circuit_breaker import CircuitBreakerOpenException
from app.cache import cache_recent_matches, add_to_recent_matches

# En endpoints:
@router.get("/queue/next")
async def get_next_match():
    try:
        match = rabbit_breaker.call(consume_next_available_match)
        add_to_recent_matches(match)  # Cache del éxito
        return {"match": match, "status": "ok"}
    except CircuitBreakerOpenException:
        # Fallback: últimas partidas del cache
        cached = cache_recent_matches()
        return {
            "matches": cached,
            "status": "degraded",
            "message": "Sistema en modo reducido (RabbitMQ down)"
        }
```

## Archivos que **NO TOCAR**
- `store.py` (lógica BD intacta)
- `models.py`, `schemas.py`
- `support-service/`
- `shared/security.py`, `shared/cors.py`
- `shared/consul_registration.py` (opcional: pequeña extensión)

## Comportamiento
```
FLUJO NORMAL:
GET /queue/next → RabbitMQ ✓ → {"match": {...}, "status": "ok"}

FLUJO CON FALLO (3+ errores):
Request 1-3:    RabbitMQ falla → contador++
Request 4:      threshold! → CB.state = OPEN, timeout=30s
Request 5:      CB.OPEN → CircuitBreakerOpenException 
                → fallback con cache → {"matches": [...], "status": "degraded"}
                → respuesta en <10ms (vs 30s de timeout)
Después 30s:    CB.state = HALF_OPEN → intenta 1 solicitud
Si RabbitMQ OK: CB.state = CLOSED → vuelve a normal
```

## Testing
```bash
# Test 1: Detener RabbitMQ, enviar 3+ solicitudes
# Esperado: Circuito abre, latencia < 2s

# Test 2: Esperar 30s, reiniciar RabbitMQ
# Esperado: HALF_OPEN → CLOSED, operación normal

# Test 3: GET /health cuando CB=OPEN
# Esperado: {"status": "degraded", "circuitBreaker": "OPEN"}
# Consul lo verá y lo reportará
```

## Archivos generados
- ✅ `backend/matchmaking-service/app/circuit_breaker.py` (100 líneas, aislado)
- ✅ Git commit: "feat: implement circuit breaker pattern for matchmaking service resilience"

## Impacto en equipo
| Cambio | Archivo | Invasividad | Riesgo |
|--------|---------|-------------|--------|
| Agregar socket_timeout | `rabbit.py` | ✅ Mínima (+3 parámetros) | 🟢 Bajo |
| Agregar funciones cache | `cache.py` | ✅ Nula (solo +2 funciones) | 🟢 Bajo |
| Agregar health info | `main.py` | ✅ Mínima (+1 línea) | 🟢 Bajo |
| Wrapping endpoints | `router.py` | ✅ Mínima (wrapper) | 🟢 Bajo |
| **Todo lo demás** | **Intacto** | ✅ **Ninguna** | 🟢 **Bajo** |

**Conclusión:** CB no reemplaza trabajo existente, solo agrega orquestación cuando hay fallos.
