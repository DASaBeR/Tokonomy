# roombook

Meeting room bookings for offices in several time zones. Python 3.11+, standard library only, SQLite storage.

```sh
python -m roombook rooms
python -m roombook book --user bob --room borealis --start 2026-12-02T09:00:00+01:00 --end 2026-12-02T10:00:00+01:00
python -m roombook free --room dune --day 2026-12-02 --minutes 60
python -m roombook report --day 2026-12-02
python -m unittest discover -s tests -t .
```

## Layout

| Module | Role |
|---|---|
| `roombook/models.py` | `Room`, `User`, `Booking` (frozen dataclasses) |
| `roombook/timeutil.py` | UTC instants, room-local wall-clock time, interval helpers |
| `roombook/storage.py` | SQLite schema, migrations, repositories, `Database.transaction()` |
| `roombook/policy.py` | Rules that need no other bookings (length, granularity, opening hours, advance window) |
| `roombook/service.py` | `BookingService`: booking, cancelling, queries, free slots |
| `roombook/notifications.py` | `Outbox` of user notifications |
| `roombook/reports.py` | Utilisation report |
| `roombook/cli.py` | Command line (exit codes: 0 ok, 1 refused, 2 usage) |
| `data/seed.json` | Demo rooms and users |
| `samples/` | Files exported by other systems |

## Conventions

- Every instant is an aware `datetime` in UTC. Room-local time is only used for display, opening hours and calendar
  days (`Room.utc_offset_minutes`).
- Expected failures raise a subclass of `roombook.errors.BookingError`; the CLI prints `error: <message>` and exits 1.
- Cancelled bookings are kept with `status = cancelled`.
- Schema changes are appended to `storage.MIGRATIONS`; never edit an applied migration.
- Tests use `tests/helpers.py` (in-memory database, demo seed, fixed clock on 2026-12-01 06:00 UTC).
