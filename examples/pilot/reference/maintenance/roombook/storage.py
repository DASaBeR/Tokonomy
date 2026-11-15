"""SQLite persistence. Instants are stored as normalised UTC ISO strings, so string comparison orders them correctly."""

from __future__ import annotations

import sqlite3
from contextlib import contextmanager
from datetime import datetime

from .errors import NotFoundError
from .models import Booking, BookingStatus, MaintenanceWindow, Role, Room, User
from .timeutil import UTC, require_aware

# Applied in order; the index of the last applied migration is kept in the `meta` table.
MIGRATIONS: list[str] = [
    """
    CREATE TABLE rooms (
        id TEXT PRIMARY KEY,
        name TEXT NOT NULL,
        capacity INTEGER NOT NULL,
        utc_offset_minutes INTEGER NOT NULL DEFAULT 0,
        features TEXT NOT NULL DEFAULT ''
    );
    CREATE TABLE users (
        id TEXT PRIMARY KEY,
        name TEXT NOT NULL,
        role TEXT NOT NULL DEFAULT 'member'
    );
    CREATE TABLE bookings (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        room_id TEXT NOT NULL REFERENCES rooms(id),
        user_id TEXT NOT NULL REFERENCES users(id),
        start TEXT NOT NULL,
        "end" TEXT NOT NULL,
        title TEXT NOT NULL DEFAULT '',
        attendees INTEGER NOT NULL DEFAULT 1,
        created_at TEXT
    );
    CREATE INDEX bookings_room_start ON bookings(room_id, start);
    """,
    # Bookings used to be deleted on cancellation; they are kept now so the history survives.
    """
    ALTER TABLE bookings ADD COLUMN status TEXT NOT NULL DEFAULT 'active';
    """,
    """
    CREATE TABLE maintenance (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        room_id TEXT NOT NULL REFERENCES rooms(id),
        start TEXT NOT NULL,
        "end" TEXT NOT NULL,
        reason TEXT NOT NULL DEFAULT '',
        created_by TEXT NOT NULL REFERENCES users(id)
    );
    CREATE INDEX maintenance_room_start ON maintenance(room_id, start);
    """,
]


def _iso(dt: datetime | None) -> str | None:
    return None if dt is None else require_aware(dt).astimezone(UTC).isoformat()


def _dt(text: str | None) -> datetime | None:
    return None if text is None else datetime.fromisoformat(text)


class Database:
    def __init__(self, path: str = ":memory:"):
        self.path = path
        self.conn = sqlite3.connect(path)
        self.conn.row_factory = sqlite3.Row
        self.conn.execute("PRAGMA foreign_keys = ON")
        self._depth = 0
        self.migrate()

    def migrate(self) -> None:
        self.conn.execute("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        row = self.conn.execute("SELECT value FROM meta WHERE key = 'schema_version'").fetchone()
        version = int(row["value"]) if row else 0
        for index, script in enumerate(MIGRATIONS[version:], start=version + 1):
            self.conn.executescript(script)
            self.conn.execute(
                "INSERT INTO meta (key, value) VALUES ('schema_version', ?) "
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                (str(index),),
            )
        self.conn.commit()

    @property
    def schema_version(self) -> int:
        row = self.conn.execute("SELECT value FROM meta WHERE key = 'schema_version'").fetchone()
        return int(row["value"]) if row else 0

    @contextmanager
    def transaction(self):
        """Groups writes: everything inside is committed together, or rolled back if the block raises.
        Nested use joins the outer transaction."""
        self._depth += 1
        try:
            yield self
        except BaseException:
            self._depth -= 1
            if self._depth == 0:
                self.conn.rollback()
            raise
        self._depth -= 1
        if self._depth == 0:
            self.conn.commit()

    def commit_if_idle(self) -> None:
        """Repositories call this after a write; inside transaction() the commit waits for the outer block."""
        if self._depth == 0:
            self.conn.commit()

    def close(self) -> None:
        self.conn.close()


class RoomRepository:
    def __init__(self, db: Database):
        self.db = db

    def add(self, room: Room) -> Room:
        self.db.conn.execute(
            "INSERT INTO rooms (id, name, capacity, utc_offset_minutes, features) VALUES (?, ?, ?, ?, ?)",
            (room.id, room.name, room.capacity, room.utc_offset_minutes, ",".join(room.features)),
        )
        self.db.commit_if_idle()
        return room

    def get(self, room_id: str) -> Room:
        row = self.db.conn.execute("SELECT * FROM rooms WHERE id = ?", (room_id,)).fetchone()
        if row is None:
            raise NotFoundError(f"unknown room {room_id!r}")
        return self._room(row)

    def all(self) -> list[Room]:
        return [self._room(r) for r in self.db.conn.execute("SELECT * FROM rooms ORDER BY id")]

    @staticmethod
    def _room(row) -> Room:
        features = tuple(f for f in row["features"].split(",") if f)
        return Room(row["id"], row["name"], row["capacity"], row["utc_offset_minutes"], features)


class UserRepository:
    def __init__(self, db: Database):
        self.db = db

    def add(self, user: User) -> User:
        self.db.conn.execute("INSERT INTO users (id, name, role) VALUES (?, ?, ?)", (user.id, user.name, user.role.value))
        self.db.commit_if_idle()
        return user

    def get(self, user_id: str) -> User:
        row = self.db.conn.execute("SELECT * FROM users WHERE id = ?", (user_id,)).fetchone()
        if row is None:
            raise NotFoundError(f"unknown user {user_id!r}")
        return User(row["id"], row["name"], Role(row["role"]))

    def all(self) -> list[User]:
        return [User(r["id"], r["name"], Role(r["role"])) for r in self.db.conn.execute("SELECT * FROM users ORDER BY id")]


class MaintenanceRepository:
    def __init__(self, db: Database):
        self.db = db

    def add(self, window: MaintenanceWindow) -> MaintenanceWindow:
        cur = self.db.conn.execute(
            'INSERT INTO maintenance (room_id, start, "end", reason, created_by) VALUES (?, ?, ?, ?, ?)',
            (window.room_id, _iso(window.start), _iso(window.end), window.reason, window.created_by),
        )
        self.db.commit_if_idle()
        row = self.db.conn.execute("SELECT * FROM maintenance WHERE id = ?", (cur.lastrowid,)).fetchone()
        return self._window(row)

    def for_room(self, room_id: str) -> list[MaintenanceWindow]:
        rows = self.db.conn.execute("SELECT * FROM maintenance WHERE room_id = ? ORDER BY start, id", (room_id,))
        return [self._window(r) for r in rows]

    @staticmethod
    def _window(row) -> MaintenanceWindow:
        return MaintenanceWindow(row["id"], row["room_id"], _dt(row["start"]), _dt(row["end"]), row["reason"], row["created_by"])


class BookingRepository:
    def __init__(self, db: Database):
        self.db = db

    def add(self, booking: Booking) -> Booking:
        cur = self.db.conn.execute(
            'INSERT INTO bookings (room_id, user_id, start, "end", title, attendees, created_at, status) '
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            (booking.room_id, booking.user_id, _iso(booking.start), _iso(booking.end), booking.title,
             booking.attendees, _iso(booking.created_at), booking.status.value),
        )
        self.db.commit_if_idle()
        return self.get(cur.lastrowid)

    def get(self, booking_id: int) -> Booking:
        row = self.db.conn.execute("SELECT * FROM bookings WHERE id = ?", (booking_id,)).fetchone()
        if row is None:
            raise NotFoundError(f"unknown booking {booking_id}")
        return self._booking(row)

    def set_status(self, booking_id: int, status: BookingStatus) -> Booking:
        self.db.conn.execute("UPDATE bookings SET status = ? WHERE id = ?", (status.value, booking_id))
        self.db.commit_if_idle()
        return self.get(booking_id)

    def for_room(self, room_id: str) -> list[Booking]:
        """Every booking ever made for the room, ordered by start."""
        rows = self.db.conn.execute("SELECT * FROM bookings WHERE room_id = ? ORDER BY start, id", (room_id,))
        return [self._booking(r) for r in rows]

    def for_user(self, user_id: str) -> list[Booking]:
        """Every booking the user has made, ordered by start."""
        rows = self.db.conn.execute("SELECT * FROM bookings WHERE user_id = ? ORDER BY start, id", (user_id,))
        return [self._booking(r) for r in rows]

    def starting_between(self, start: datetime, end: datetime) -> list[Booking]:
        rows = self.db.conn.execute(
            "SELECT * FROM bookings WHERE start >= ? AND start < ? ORDER BY start, id", (_iso(start), _iso(end))
        )
        return [self._booking(r) for r in rows]

    @staticmethod
    def _booking(row) -> Booking:
        return Booking(
            id=row["id"],
            room_id=row["room_id"],
            user_id=row["user_id"],
            start=_dt(row["start"]),
            end=_dt(row["end"]),
            title=row["title"],
            attendees=row["attendees"],
            status=BookingStatus(row["status"]),
            created_at=_dt(row["created_at"]),
        )
