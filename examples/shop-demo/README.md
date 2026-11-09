# shop-demo

A small standard-library Python project used as a Tokonomy benchmark fixture.

```sh
python -m unittest discover -s tests -t .
```

Code lives in `shop/`:

- `money.py` — rounding and formatting helpers
- `pricing.py` — sale price and coupons
- `cart.py` — the shopping cart
- `storage.py` — loading orders from JSON

Conventions: keep functions small, no module-level mutable state, take clocks and other time sources as parameters,
and add unit tests next to the code you change (`tests/test_<module>.py`).
