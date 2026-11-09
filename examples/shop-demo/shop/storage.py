"""Loading orders from JSON."""

import json


def load_order(text: str) -> dict:
    """Load an order from JSON text."""
    data = json.loads(text)
    return {"id": data["id"], "items": data["items"]}
