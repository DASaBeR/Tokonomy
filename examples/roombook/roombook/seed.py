"""Loads the demo rooms and users from data/seed.json into an empty database."""

from __future__ import annotations

import json
from pathlib import Path

from .models import Role, Room, User
from .storage import Database, RoomRepository, UserRepository

DEFAULT_SEED = Path(__file__).resolve().parent.parent / "data" / "seed.json"


def load_seed(db: Database, path: Path | str = DEFAULT_SEED) -> None:
    rooms = RoomRepository(db)
    users = UserRepository(db)
    if rooms.all():
        return
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    with db.transaction():
        for r in data["rooms"]:
            rooms.add(Room(r["id"], r["name"], int(r["capacity"]), int(r.get("utc_offset_minutes", 0)), tuple(r.get("features", []))))
        for u in data["users"]:
            users.add(User(u["id"], u["name"], Role(u.get("role", "member"))))
