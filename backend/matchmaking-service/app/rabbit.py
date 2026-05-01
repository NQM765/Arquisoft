"""
Integración con RabbitMQ para el sistema de matchmaking.

Flujo:
  1. Host crea partida → publish_match_available() publica { matchId, relayJoinCode }
     en la cola 'matchmaking.available'.
  2. Cliente llama GET /matchmaking/queue/next → consume_next_available_match()
     devuelve el primer mensaje disponible (o None si no hay).

La cola se declara como durable para sobrevivir reinicios del broker.
Los mensajes se publican con delivery_mode=2 (persistentes).
"""

import json
import os

import pika

RABBITMQ_URL = os.getenv("RABBITMQ_URL", "amqp://guest:guest@rabbitmq:5672/")
QUEUE_NAME = "matchmaking.available"


def _get_channel():
    """Abre una conexión y canal nuevos. Úsalo dentro de un bloque try/finally."""
    params = pika.URLParameters(RABBITMQ_URL)
    connection = pika.BlockingConnection(params)
    channel = connection.channel()
    channel.queue_declare(queue=QUEUE_NAME, durable=True)
    return connection, channel


def publish_match_available(match_id: str, relay_join_code: str) -> None:
    """
    Publica un mensaje indicando que hay una partida disponible para unirse.
    Llamado por el host justo después de crear el match.
    """
    payload = json.dumps({"matchId": match_id, "relayJoinCode": relay_join_code})
    connection, channel = _get_channel()
    try:
        channel.basic_publish(
            exchange="",
            routing_key=QUEUE_NAME,
            body=payload,
            properties=pika.BasicProperties(delivery_mode=2),  # persistente
        )
    finally:
        connection.close()


def consume_next_available_match() -> dict | None:
    """
    Consume el primer mensaje disponible de la cola y lo devuelve como dict.
    Si no hay mensajes, devuelve None.
    El mensaje se confirma (ack) sólo si se parsea correctamente.
    """
    connection, channel = _get_channel()
    try:
        method, _props, body = channel.basic_get(queue=QUEUE_NAME, auto_ack=False)
        if method is None:
            return None  # cola vacía
        data = json.loads(body)
        channel.basic_ack(delivery_tag=method.delivery_tag)
        return data  # { matchId, relayJoinCode }
    except Exception:
        # Si algo falla no confirmamos: el mensaje vuelve a la cola
        if method is not None:
            channel.basic_nack(delivery_tag=method.delivery_tag, requeue=True)
        raise
    finally:
        connection.close()
