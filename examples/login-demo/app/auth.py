"""A deliberately small login module used as a Taskrail benchmark fixture."""

import hashlib


def hash_password(password: str) -> str:
    return hashlib.sha256(password.encode("utf-8")).hexdigest()


def login(username: str, password: str, users: dict) -> str:
    """Return "ok" when the password matches the stored hash, otherwise "denied"."""
    stored = users.get(username)
    if stored is not None and stored == hash_password(password):
        return "ok"
    return "denied"
