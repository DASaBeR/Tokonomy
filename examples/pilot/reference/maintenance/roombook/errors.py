"""Errors raised by the booking service. The CLI turns any BookingError into `error: <message>` and exit code 1."""

from __future__ import annotations


class BookingError(Exception):
    """Base class for every expected, user-facing failure."""


class ValidationError(BookingError):
    """The request itself is malformed (bad times, bad attendee count, unknown format)."""


class NotFoundError(BookingError):
    """A room, user or booking id does not exist."""


class PermissionDeniedError(BookingError):
    """The acting user may not do this."""


class PolicyError(BookingError):
    """The request is well-formed but breaks a booking rule. `code` identifies the rule."""

    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


class MaintenanceError(BookingError):
    """The room is out of service for part of the requested time."""


class ConflictError(BookingError):
    """The room is already booked for part of the requested time."""

    def __init__(self, conflicts):
        self.conflicts = list(conflicts)
        ids = ", ".join(str(b.id) for b in self.conflicts)
        super().__init__(f"room is already booked (conflicts with booking {ids})")
