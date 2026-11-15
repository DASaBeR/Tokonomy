"""Command line: python -m roombook <command> ...  Exit codes: 0 ok, 1 refused (any BookingError), 2 usage error."""

from __future__ import annotations

import argparse
import sys
from datetime import date

from .errors import BookingError
from .reports import to_csv, utilisation
from .seed import load_seed
from .service import BookingService
from .storage import Database
from .timeutil import format_local, parse_instant


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="roombook", description="Meeting room bookings")
    parser.add_argument("--db", default="roombook.db", help="SQLite database file (created and seeded if new)")
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("rooms", help="list rooms")

    book = sub.add_parser("book", help="book a room")
    book.add_argument("--user", required=True)
    book.add_argument("--room", required=True)
    book.add_argument("--start", required=True, help="ISO 8601 with offset, e.g. 2026-12-01T09:00:00+01:00")
    book.add_argument("--end", required=True)
    book.add_argument("--title", default="")
    book.add_argument("--attendees", type=int, default=1)

    cancel = sub.add_parser("cancel", help="cancel a booking")
    cancel.add_argument("booking_id", type=int)
    cancel.add_argument("--as", dest="actor", required=True, help="the user who cancels (owner or admin)")

    free = sub.add_parser("free", help="list free slots of a room on a local day")
    free.add_argument("--room", required=True)
    free.add_argument("--day", required=True, type=date.fromisoformat)
    free.add_argument("--minutes", type=int, default=60)

    report = sub.add_parser("report", help="utilisation report as CSV")
    report.add_argument("--day", required=True, type=date.fromisoformat)
    return parser


def _offset(minutes: int) -> str:
    sign = "+" if minutes >= 0 else "-"
    return f"UTC{sign}{abs(minutes) // 60:02d}:{abs(minutes) % 60:02d}"


def run(args, service: BookingService, out) -> int:
    if args.command == "rooms":
        for room in service.rooms.all():
            print(f"{room.id:10} {room.name:12} {room.capacity:3}  {_offset(room.utc_offset_minutes)}", file=out)
    elif args.command == "book":
        booking = service.book(args.user, args.room, parse_instant(args.start), parse_instant(args.end), args.title, args.attendees)
        room = service.rooms.get(booking.room_id)
        print(f"booked #{booking.id} {room.name} {format_local(booking.start, room)}-{format_local(booking.end, room)[11:]}", file=out)
    elif args.command == "cancel":
        booking = service.cancel(args.booking_id, args.actor)
        print(f"cancelled #{booking.id}", file=out)
    elif args.command == "free":
        room = service.rooms.get(args.room)
        for start, end in service.find_free_slots(room.id, args.day, args.minutes):
            print(f"{format_local(start, room)[11:]}-{format_local(end, room)[11:]}", file=out)
    elif args.command == "report":
        out.write(to_csv(utilisation(service, args.day)))
    return 0


def main(argv: list[str] | None = None, service: BookingService | None = None, out=None) -> int:
    out = out or sys.stdout
    try:
        args = build_parser().parse_args(argv)
    except SystemExit as exc:  # argparse already printed the usage message
        return int(exc.code or 0)
    if service is None:
        db = Database(args.db)
        load_seed(db)
        service = BookingService(db)
    try:
        return run(args, service, out)
    except BookingError as exc:
        print(f"error: {exc}", file=out)
        return 1
