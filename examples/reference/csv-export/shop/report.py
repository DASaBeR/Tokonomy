"""Order reports."""

HEADER = ("id", "customer", "total")


def _field(value) -> str:
    text = "" if value is None else str(value)
    if any(ch in text for ch in (",", '"', "\r", "\n")):
        return '"' + text.replace('"', '""') + '"'
    return text


def orders_to_csv(orders: list) -> str:
    lines = [",".join(HEADER)]
    for order in orders:
        lines.append(",".join([_field(order["id"]), _field(order.get("customer")), f"{order['total']:.2f}"]))
    return "".join(line + "\r\n" for line in lines)


def write_csv(orders: list, path: str) -> int:
    with open(path, "w", encoding="utf-8", newline="") as handle:
        handle.write(orders_to_csv(orders))
    return len(orders)
