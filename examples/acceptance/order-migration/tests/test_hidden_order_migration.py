"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import copy
import json
import unittest

from shop.storage import load_order


def dumps(**kwargs):
    return json.dumps(kwargs)


class OrderMigrationAcceptance(unittest.TestCase):
    def test_v1_is_migrated_to_v2_shape(self):
        text = dumps(
            version=1,
            id="o1",
            items=[{"sku": "a", "price": 19.99, "qty": 2}, {"sku": "b", "price": 0.3}],
            coupon_percent=10,
        )
        self.assertEqual(
            load_order(text),
            {
                "version": 2,
                "id": "o1",
                "items": [
                    {"sku": "a", "unit_price_cents": 1999, "qty": 2},
                    {"sku": "b", "unit_price_cents": 30, "qty": 1},
                ],
                "coupons": [{"type": "percent", "value": 10}],
            },
        )

    def test_missing_version_means_v1(self):
        text = dumps(id="o2", items=[{"sku": "a", "price": 5, "qty": 3}])
        order = load_order(text)
        self.assertEqual(order["version"], 2)
        self.assertEqual(order["items"], [{"sku": "a", "unit_price_cents": 500, "qty": 3}])
        self.assertEqual(order["coupons"], [])

    def test_float_prices_are_converted_without_drift(self):
        text = dumps(id="o3", items=[{"sku": "a", "price": 0.1 + 0.2}, {"sku": "b", "price": 1.15}, {"sku": "c", "price": 4.35}])
        cents = [i["unit_price_cents"] for i in load_order(text)["items"]]
        self.assertEqual(cents, [30, 115, 435])

    def test_both_coupon_kinds_percent_first(self):
        text = dumps(id="o4", items=[{"sku": "a", "price": 1}], coupon_fixed=5, coupon_percent=15)
        self.assertEqual(
            load_order(text)["coupons"], [{"type": "percent", "value": 15}, {"type": "fixed", "value": 5}]
        )

    def test_zero_or_missing_coupons_are_dropped(self):
        text = dumps(id="o5", items=[{"sku": "a", "price": 1}], coupon_percent=0, coupon_fixed=0)
        self.assertEqual(load_order(text)["coupons"], [])

    def test_v2_passes_through_unchanged_and_input_not_mutated(self):
        data = {
            "version": 2,
            "id": "o6",
            "items": [{"sku": "a", "unit_price_cents": 250, "qty": 4}],
            "coupons": [{"type": "fixed", "value": 3}],
        }
        snapshot = copy.deepcopy(data)
        self.assertEqual(load_order(json.dumps(data)), snapshot)

    def test_v2_missing_qty_defaults_to_one(self):
        data = {"version": 2, "id": "o7", "items": [{"sku": "a", "unit_price_cents": 100}], "coupons": []}
        self.assertEqual(load_order(json.dumps(data))["items"][0]["qty"], 1)

    def test_unsupported_versions_raise(self):
        for version in (0, 3, "2", 1.5):
            with self.subTest(version=version):
                text = json.dumps({"version": version, "id": "x", "items": []})
                with self.assertRaises(ValueError) as ctx:
                    load_order(text)
                self.assertIn("unsupported order version", str(ctx.exception))

    def test_invalid_orders_raise_value_error(self):
        bad = [
            "not json",
            dumps(items=[]),  # no id
            dumps(id="x"),  # no items
            dumps(id="x", items="nope"),
            dumps(id="x", items=[{"price": 1}]),  # no sku
            dumps(id="x", items=[{"sku": "a"}]),  # no price
            dumps(id="x", items=[{"sku": "a", "price": 1, "qty": 0}]),
            dumps(id="x", items=[{"sku": "a", "price": 1, "qty": -2}]),
            dumps(version=2, id="x", items=[{"sku": "a", "qty": 1}]),  # no cents
        ]
        for text in bad:
            with self.subTest(text=text):
                with self.assertRaises(ValueError):
                    load_order(text)


if __name__ == "__main__":
    unittest.main()
