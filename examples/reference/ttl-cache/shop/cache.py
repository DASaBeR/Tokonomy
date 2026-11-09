"""A small TTL cache with LRU eviction."""

import threading
import time
from collections import OrderedDict

_MISSING = object()


class TtlCache:
    def __init__(self, ttl_seconds: float, max_size=None, clock=time.monotonic):
        self._ttl = ttl_seconds
        self._max_size = max_size
        self._clock = clock
        self._lock = threading.Lock()
        self._data = OrderedDict()  # key -> (value, stored_at), least recently used first

    def _purge(self) -> None:
        now = self._clock()
        for key in [k for k, (_, stored_at) in self._data.items() if now - stored_at >= self._ttl]:
            del self._data[key]

    def set(self, key, value) -> None:
        with self._lock:
            self._purge()
            if key in self._data:
                del self._data[key]
            elif self._max_size is not None and len(self._data) >= self._max_size:
                self._data.popitem(last=False)
            self._data[key] = (value, self._clock())

    def get(self, key, default=None):
        with self._lock:
            self._purge()
            entry = self._data.get(key, _MISSING)
            if entry is _MISSING:
                return default
            self._data.move_to_end(key)
            return entry[0]

    def delete(self, key) -> bool:
        with self._lock:
            self._purge()
            return self._data.pop(key, _MISSING) is not _MISSING

    def __contains__(self, key) -> bool:
        with self._lock:
            self._purge()
            return key in self._data

    def __len__(self) -> int:
        with self._lock:
            self._purge()
            return len(self._data)
