import logging
import time
from enum import Enum

logger = logging.getLogger(__name__)


class CircuitBreakerState(Enum):
    CLOSED = "CLOSED"
    OPEN = "OPEN"
    HALF_OPEN = "HALF_OPEN"


class CircuitBreakerOpenException(Exception):
    """Excepción cuando el Circuit Breaker está OPEN"""
    pass


class CircuitBreaker:
    """
    Circuit Breaker pattern para manejar fallos en RabbitMQ.
    
    Estados:
    - CLOSED: operación normal
    - OPEN: bloqueado, devuelve fallback
    - HALF_OPEN: intentando recuperar después del timeout
    """
    
    def __init__(self, failure_threshold: int = 3, timeout: int = 30, name: str = "default"):
        self.state = CircuitBreakerState.CLOSED
        self.failure_count = 0
        self.success_count = 0
        self.failure_threshold = failure_threshold
        self.timeout = timeout
        self.opened_at: float | None = None
        self.name = name
        logger.info(f"[CB-{name}] Initialized: threshold={failure_threshold}, timeout={timeout}s")
    
    def call(self, func, *args, **kwargs):
        """Ejecuta función protegida por circuit breaker"""
        if self.state == CircuitBreakerState.OPEN:
            if self._should_attempt_reset():
                self._transition_to(CircuitBreakerState.HALF_OPEN)
            else:
                raise CircuitBreakerOpenException(f"Circuit breaker {self.name} is OPEN")
        
        try:
            result = func(*args, **kwargs)
            self._on_success()
            return result
        except Exception as e:
            self._on_failure()
            raise
    
    def _on_success(self):
        if self.state == CircuitBreakerState.HALF_OPEN:
            self.success_count += 1
            if self.success_count >= 1:
                self._transition_to(CircuitBreakerState.CLOSED)
        else:
            self.failure_count = 0
            self.success_count = 0
    
    def _on_failure(self):
        self.failure_count += 1
        logger.warning(f"[CB-{self.name}] Failure {self.failure_count}/{self.failure_threshold}")
        
        if self.failure_count >= self.failure_threshold:
            self._transition_to(CircuitBreakerState.OPEN)
    
    def _should_attempt_reset(self) -> bool:
        if self.opened_at is None:
            return False
        return time.time() - self.opened_at >= self.timeout
    
    def _transition_to(self, new_state: CircuitBreakerState):
        old_state = self.state
        self.state = new_state
        
        if new_state == CircuitBreakerState.OPEN:
            self.opened_at = time.time()
            self.failure_count = 0
            logger.error(f"[CB-{self.name}] OPEN → Blocking calls for {self.timeout}s")
        elif new_state == CircuitBreakerState.HALF_OPEN:
            logger.info(f"[CB-{self.name}] HALF_OPEN → Attempting recovery")
        elif new_state == CircuitBreakerState.CLOSED:
            self.failure_count = 0
            self.success_count = 0
            logger.info(f"[CB-{self.name}] CLOSED → Normal operation restored")
    
    @property
    def is_open(self) -> bool:
        return self.state == CircuitBreakerState.OPEN
    
    @property
    def is_half_open(self) -> bool:
        return self.state == CircuitBreakerState.HALF_OPEN
