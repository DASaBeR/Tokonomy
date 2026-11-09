"""Stock ledger with time-limited reservations."""

from dataclasses import dataclass, field


@dataclass
class ReservationResult:
    ok: bool
    code: object = None
    shortfalls: dict = field(default_factory=dict)


def _qty(qty):
    if isinstance(qty, bool) or not isinstance(qty, int) or qty < 1:
        raise ValueError("qty must be an int >= 1")
    return qty


def _copy(r):
    return ReservationResult(r.ok, r.code, dict(r.shortfalls))


class Inventory:
    def __init__(self, clock):
        self._clock = clock
        self._on_hand = {}
        self._orders = {}  # order_id -> dict(lines, expires, state)
        self._keys = {}  # key -> (payload, result)

    def _sweep(self):
        now = self._clock()
        for o in self._orders.values():
            if o["state"] == "held" and now >= o["expires"]:
                o["state"] = "expired"
        return now

    def _reserved(self, sku):
        return sum(o["lines"].get(sku, 0) for o in self._orders.values() if o["state"] == "held")

    def receive(self, sku, qty):
        _qty(qty)
        self._sweep()
        self._on_hand[sku] = self._on_hand.get(sku, 0) + qty

    def on_hand(self, sku):
        self._sweep()
        return self._on_hand.get(sku, 0)

    def reserved(self, sku):
        self._sweep()
        return self._reserved(sku)

    def available(self, sku):
        self._sweep()
        return self._on_hand.get(sku, 0) - self._reserved(sku)

    def reserve(self, order_id, lines, ttl, key=None):
        if not isinstance(lines, list) or not lines:
            raise ValueError("lines must be a non-empty list")
        merged = {}
        for sku, qty in lines:
            _qty(qty)
            merged[sku] = merged.get(sku, 0) + qty
        if isinstance(ttl, bool) or not isinstance(ttl, (int, float)) or not ttl > 0:
            raise ValueError("ttl must be > 0")
        now = self._sweep()
        payload = (order_id, tuple(sorted(merged.items())))
        if key is not None and key in self._keys:
            old_payload, old = self._keys[key]
            if old_payload == payload:
                return _copy(old)
            return ReservationResult(False, "key_conflict")
        existing = self._orders.get(order_id)
        if existing is not None and existing["state"] in ("held", "confirmed"):
            return ReservationResult(False, "duplicate_order")
        short = {}
        for sku, qty in merged.items():
            avail = self._on_hand.get(sku, 0) - self._reserved(sku)
            if qty > avail:
                short[sku] = qty - avail
        if short:
            result = ReservationResult(False, "insufficient", dict(sorted(short.items())))
        else:
            self._orders[order_id] = {"lines": merged, "expires": now + ttl, "state": "held"}
            result = ReservationResult(True, None, {})
        if key is not None:
            self._keys[key] = (payload, _copy(result))
        return result

    def confirm(self, order_id):
        self._sweep()
        o = self._orders.get(order_id)
        if o is None:
            return ReservationResult(False, "unknown_order")
        if o["state"] == "cancelled":
            return ReservationResult(False, "cancelled")
        if o["state"] == "expired":
            return ReservationResult(False, "expired")
        if o["state"] == "held":
            for sku, qty in o["lines"].items():
                self._on_hand[sku] -= qty
            o["state"] = "confirmed"
        return ReservationResult(True)

    def cancel(self, order_id):
        self._sweep()
        o = self._orders.get(order_id)
        if o is None:
            return ReservationResult(False, "unknown_order")
        if o["state"] == "confirmed":
            return ReservationResult(False, "already_confirmed")
        if o["state"] == "held":
            o["state"] = "cancelled"
        return ReservationResult(True)
