"""Utilisation report: how much of each room's opening hours was booked on a day."""

from __future__ import annotations

import csv
import io
from dataclasses import dataclass
from datetime import date


@dataclass(frozen=True)
class UtilisationRow:
    room_id: str
    room_name: str
    booked_minutes: int
    open_minutes: int

    @property
    def percent(self) -> float:
        return 0.0 if self.open_minutes == 0 else round(100 * self.booked_minutes / self.open_minutes, 1)


def utilisation(service, day: date) -> list[UtilisationRow]:
    open_minutes = (service.policy.close_hour - service.policy.open_hour) * 60
    rows = []
    for room in service.rooms.all():
        booked = sum(b.duration_minutes for b in service.bookings_on(room.id, day))
        rows.append(UtilisationRow(room.id, room.name, booked, open_minutes))
    return rows


def to_csv(rows: list[UtilisationRow]) -> str:
    out = io.StringIO()
    writer = csv.writer(out, lineterminator="\n")
    writer.writerow(["room", "name", "booked_minutes", "open_minutes", "percent"])
    for r in rows:
        writer.writerow([r.room_id, r.room_name, r.booked_minutes, r.open_minutes, f"{r.percent:.1f}"])
    return out.getvalue()
