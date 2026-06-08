import atexit
import logging
import os
import signal

import requests

CONSUL_ADDR = os.getenv("CONSUL_HTTP_ADDR", "http://consul:8500")
HOSTNAME = os.uname().nodename

logger = logging.getLogger(__name__)

_registered_ids: list[str] = []


def register_service(
    name: str,
    port: int,
    health_endpoint: str = "/health",
    tags: list[str] | None = None,
) -> str:
    service_id = f"{name}-{HOSTNAME}"
    payload = {
        "Name": name,
        "ID": service_id,
        "Address": HOSTNAME,
        "Port": port,
        "Check": {
            "HTTP": f"http://{HOSTNAME}:{port}{health_endpoint}",
            "Interval": "10s",
            "DeregisterCriticalServiceAfter": "30s",
        },
        "Tags": tags or [os.getenv("ENVIRONMENT", "production")],
    }

    resp = requests.put(
        f"{CONSUL_ADDR}/v1/agent/service/register",
        json=payload,
        timeout=5,
    )
    resp.raise_for_status()
    logger.info("Registered %s in Consul", service_id)
    _registered_ids.append(service_id)
    return service_id


def deregister_service(service_id: str) -> None:
    try:
        resp = requests.put(
            f"{CONSUL_ADDR}/v1/agent/service/deregister/{service_id}",
            timeout=5,
        )
        resp.raise_for_status()
        logger.info("Deregistered %s from Consul", service_id)
    except requests.RequestException:
        logger.warning("Failed to deregister %s", service_id)


def deregister_all() -> None:
    for sid in list(_registered_ids):
        deregister_service(sid)
        _registered_ids.remove(sid)


atexit.register(deregister_all)
signal.signal(signal.SIGTERM, lambda *_: deregister_all())
