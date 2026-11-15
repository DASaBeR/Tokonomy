"""The booking service: the only place that combines rules, storage and notifications."""

from __future__ import annotations

from datetime import date, datetime, timedelta

from .clock import SystemClock
from .errors import ConflictError, MaintenanceError, PermissionDeniedError, ValidationError
from .models import Booking, BookingStatus, MaintenanceWindow
from .notifications import Outbox, booking_cancelled, booking_confirmed
from .policy import BookingPolicy
from .storage import BookingRepository, Database, MaintenanceRepository, RoomRepository, UserRepository
from .timeutil import from_room_local, local_day_bounds, overlaps, require_aware


class BookingService:
    def __init__(self, db: Database, clock=None, policy: BookingPolicy | None = None, outbox: Outbox | None = None):
        self.db = db
        self.clock = clock or SystemClock()
        self.policy = policy or BookingPolicy()
        self.outbox = outbox if outbox is not None else Outbox()
        self.rooms = RoomRepository(db)
        self.users = UserRepository(db)
        self.bookings = BookingRepository(db)
        self.maintenance = MaintenanceRepository(db)

    # ------------------------------------------------------------------ booking

    def book(self, user_id: str, room_id: str, start: datetime, end: datetime, title: str = "", attendees: int = 1) -> Booking:
        user = self.users.get(user_id)
        room = self.rooms.get(room_id)
        start = require_aware(start, "start")
        end = require_aware(end, "end")
        if end <= start:
            raise ValidationError("end must be after start")
        if attendees < 1 or attendees > room.capacity:
            raise ValidationError(f"{room.name} holds 1 to {room.capacity} people")
        now = self.clock.now()
        self.policy.validate(room, start, end, now, user)
        blocked = [w for w in self.maintenance.for_room(room.id) if overlaps(start, end, w.start, w.end)]
        if blocked:
            raise MaintenanceError(f"{room.name} is out of service then ({blocked[0].reason or 'maintenance'})")
        conflicts = self.conflicts(room.id, start, end)
        if conflicts:
            raise ConflictError(conflicts)
        booking = self.bookings.add(Booking(None, room.id, user.id, start, end, title.strip(), attendees, created_at=now))
        self.outbox.send(user.id, "confirmed", booking_confirmed(booking, room))
        return booking

    def conflicts(self, room_id: str, start: datetime, end: datetime) -> list[Booking]:
        return [
            b for b in self.bookings.for_room(room_id)
            if b.is_active and overlaps(start, end, b.start, b.end)
        ]

    def cancel(self, booking_id: int) -> Booking:
        booking = self.bookings.get(booking_id)
        if not booking.is_active:
            raise ValidationError(f"booking {booking_id} is already cancelled")
        cancelled = self.bookings.set_status(booking_id, BookingStatus.CANCELLED)
        room = self.rooms.get(booking.room_id)
        self.outbox.send(booking.user_id, "cancelled", booking_cancelled(booking, room))
        return cancelled

    # ------------------------------------------------------------------ maintenance

    def schedule_maintenance(self, room_id: str, start: datetime, end: datetime, reason: str, actor_id: str) -> MaintenanceWindow:
        """Takes a room out of service. Admins only. Active bookings inside the window are cancelled and their
        owners told why (an assumption: the task leaves this open)."""
        actor = self.users.get(actor_id)
        if not actor.is_admin:
            raise PermissionDeniedError("only admins can schedule maintenance")
        room = self.rooms.get(room_id)
        start = require_aware(start, "start")
        end = require_aware(end, "end")
        if end <= start:
            raise ValidationError("end must be after start")
        with self.db.transaction():
            window = self.maintenance.add(MaintenanceWindow(None, room.id, start, end, reason.strip(), actor.id))
            for booking in self.conflicts(room.id, start, end):
                self.bookings.set_status(booking.id, BookingStatus.CANCELLED)
                self.outbox.send(booking.user_id, "cancelled",
                                 booking_cancelled(booking, room) + f" because the room is under maintenance ({window.reason})")
        return window

    def maintenance_for_room(self, room_id: str) -> list[MaintenanceWindow]:
        return self.maintenance.for_room(self.rooms.get(room_id).id)

    # ------------------------------------------------------------------ queries

    def bookings_on(self, room_id: str, day: date) -> list[Booking]:
        """Active bookings of the room that start on the given room-local calendar day."""
        room = self.rooms.get(room_id)
        day_start, day_end = local_day_bounds(day, room)
        return [b for b in self.bookings.for_room(room.id) if b.is_active and day_start <= b.start < day_end]

    def upcoming_for_user(self, user_id: str) -> list[Booking]:
        now = self.clock.now()
        return [b for b in self.bookings.for_user(user_id) if b.is_active and b.end > now]

    def find_free_slots(self, room_id: str, day: date, duration_minutes: int) -> list[tuple[datetime, datetime]]:
        """Start/end pairs (UTC) on the room-local day, on the policy's granularity, that could be booked now."""
        room = self.rooms.get(room_id)
        step = timedelta(minutes=self.policy.granularity_minutes)
        length = timedelta(minutes=duration_minutes)
        opens = from_room_local(datetime.combine(day, datetime.min.time()).replace(hour=self.policy.open_hour), room)
        closes = from_room_local(datetime.combine(day, datetime.min.time()).replace(hour=self.policy.close_hour), room)
        taken = [b for b in self.bookings.for_room(room.id) if b.is_active and b.end > opens and b.start < closes]
        taken += [w for w in self.maintenance.for_room(room.id) if w.end > opens and w.start < closes]
        now = self.clock.now()
        slots = []
        cursor = opens
        while cursor + length <= closes:
            end = cursor + length
            if cursor >= now and not any(overlaps(cursor, end, b.start, b.end) for b in taken):
                slots.append((cursor, end))
            cursor += step
        return slots
