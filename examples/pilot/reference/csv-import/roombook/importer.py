"""Bulk import of bookings from a CSV export (reference solution)."""

from __future__ import annotations

import csv
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

from .errors import BookingError
from .models import Booking
from .notifications import Outbox
from .timeutil import from_room_local

COLUMNS = ("room", "user", "start", "end", "title")


@dataclass(frozen=True)
class ImportErrorRow:
    line: int
    message: str


@dataclass
class ImportResult:
    imported: list[Booking] = field(default_factory=list)
    errors: list[ImportErrorRow] = field(default_factory=list)


class _Rollback(Exception):
    pass


def _wall_clock(text: str) -> datetime:
    try:
        return datetime.strptime(text.strip(), "%Y-%m-%d %H:%M")
    except ValueError as exc:
        raise BookingError(f"not a 'YYYY-MM-DD HH:MM' time: {text!r}") from exc


def import_bookings(service, path, partial: bool = False) -> ImportResult:
    result = ImportResult()
    staging = Outbox()
    real_outbox = service.outbox
    # Excel writes a UTF-8 byte order mark; utf-8-sig removes it so the first header is "room", not "﻿room".
    with open(Path(path), newline="", encoding="utf-8-sig") as handle:
        reader = csv.reader(handle)
        header = [h.strip().lower() for h in next(reader, [])]
        missing = [c for c in COLUMNS if c not in header]
        if missing:
            result.errors.append(ImportErrorRow(1, f"missing column(s): {', '.join(missing)}"))
            return result
        rows = [(reader.line_num, row) for row in reader if any(cell.strip() for cell in row)]

    service.outbox = staging
    try:
        with service.db.transaction():
            for line, row in rows:
                if len(row) < len(header):
                    result.errors.append(ImportErrorRow(line, f"expected {len(header)} fields, found {len(row)}"))
                    continue
                values = dict(zip(header, row))
                try:
                    room = service.rooms.get(values["room"].strip())
                    start = from_room_local(_wall_clock(values["start"]), room)
                    end = from_room_local(_wall_clock(values["end"]), room)
                    booking = service.book(values["user"].strip(), room.id, start, end, values["title"])
                except BookingError as exc:
                    result.errors.append(ImportErrorRow(line, str(exc)))
                    continue
                result.imported.append(booking)
            if result.errors and not partial:
                raise _Rollback()
    except _Rollback:
        result.imported = []
        staging.sent.clear()
    finally:
        service.outbox = real_outbox
    real_outbox.sent.extend(staging.sent)
    return result
