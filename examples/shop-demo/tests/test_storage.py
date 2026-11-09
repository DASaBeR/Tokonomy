import json
import unittest

from shop.storage import load_order


class StorageTests(unittest.TestCase):
    def test_loads_order_id(self):
        text = json.dumps({"id": "o1", "items": [{"sku": "a", "price": 1.5, "qty": 2}]})
        self.assertEqual(load_order(text)["id"], "o1")


if __name__ == "__main__":
    unittest.main()
