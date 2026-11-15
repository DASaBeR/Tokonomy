"""Outgoing notifications. The outbox only records them; delivery is someone else's job."""

from __future__ import annotations

from dataclasses import dataclass

from .timeutil import format_local


@dataclass(frozen=True)
class Notification:
    user_id: str
    kind: str
    message: str


class Outbox:
    def __init__(self):
        self.sent: list[Notification] = []

    def send(self, user_id: str, kind: str, message: str) -> Notification:
        note = Notification(user_id, kind, message)
        self.sent.append(note)
        return note

    def for_user(self, user_id: str) -> list[Notification]:
        return [n for n in self.sent if n.user_id == user_id]


def booking_confirmed(booking, room) -> str:
    return f"Booked {room.name} {format_local(booking.start, room)}-{format_local(booking.end, room)[11:]}" + (
        f" ({booking.title})" if booking.title else ""
    )


def booking_cancelled(booking, room) -> str:
    return f"Your booking of {room.name} on {format_local(booking.start, room)} was cancelled"
