"""Loading orders from JSON (format versions 1 and 2)."""

import json
import math


def _cents(price) -> int:
    return math.floor(price * 100 + 0.5 + 1e-9)


def _qty(value) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < 1:
        raise ValueError(f"invalid quantity: {value!r}")
    return value


def _items(raw, v1: bool) -> list:
    if not isinstance(raw, list):
        raise ValueError("items must be a list")
    items = []
    for entry in raw:
        if not isinstance(entry, dict) or "sku" not in entry:
            raise ValueError("item needs a sku")
        key = "price" if v1 else "unit_price_cents"
        if key not in entry:
            raise ValueError(f"item {entry['sku']!r} needs {key}")
        cents = _cents(entry[key]) if v1 else entry[key]
        items.append({"sku": entry["sku"], "unit_price_cents": cents, "qty": _qty(entry.get("qty", 1))})
    return items


def load_order(text: str) -> dict:
    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError("order must be an object")
    version = data.get("version", 1)
    if version not in (1, 2) or isinstance(version, (bool, float)) or not isinstance(version, int):
        raise ValueError(f"unsupported order version: {version!r}")
    if "id" not in data:
        raise ValueError("order needs an id")
    if "items" not in data:
        raise ValueError("order needs items")

    if version == 2:
        return {
            "version": 2,
            "id": data["id"],
            "items": _items(data["items"], v1=False),
            "coupons": [dict(c) for c in data.get("coupons", [])],
        }

    coupons = []
    if data.get("coupon_percent"):
        coupons.append({"type": "percent", "value": data["coupon_percent"]})
    if data.get("coupon_fixed"):
        coupons.append({"type": "fixed", "value": data["coupon_fixed"]})
    return {"version": 2, "id": data["id"], "items": _items(data["items"], v1=True), "coupons": coupons}
