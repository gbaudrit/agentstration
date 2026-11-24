"""Rate estimates reject ambiguous, discounted or unrelated public meters."""
import copy
from io import BytesIO
import json
import unittest
from unittest.mock import patch

import azure_prices


def meters():
    base = {'armRegionName': 'westeurope', 'currencyCode': 'EUR', 'type': 'Consumption',
            'isPrimaryMeterRegion': True, 'tierMinimumUnits': 0, 'effectiveStartDate': '2026-01-01T00:00:00Z'}
    rows = [{**base, 'armSkuName': size, 'skuName': size.removeprefix('Standard_'), 'meterName': 'Compute',
             'productName': 'Virtual Machines BS Series', 'retailPrice': price, 'unitOfMeasure': '1 Hour'}
            for size, price in [('Standard_B2ms', 0.1), ('Standard_B4ms', 0.2)]]
    rows += [{**base, 'productName': 'Standard SSD Managed Disks', 'meterName': tier + ' LRS Disk',
              'skuName': tier, 'retailPrice': price, 'unitOfMeasure': '1/Month'}
             for tier, price in [('E4', 3.65), ('E6', 7.3)]]
    rows.append({**base, 'productName': 'IP Addresses', 'skuName': 'Standard IPv4',
                 'meterName': 'Standard IPv4 Static Public IP', 'retailPrice': 0.005, 'unitOfMeasure': '1 Hour'})
    return rows


class AzurePriceTests(unittest.TestCase):
    def test_fixed_estimate_uses_linux_payg_and_both_retained_disks(self):
        rows = meters()
        for change in ({'productName': 'Virtual Machines BS Series Windows'}, {'skuName': 'B2ms Spot'},
                       {'currencyCode': 'USD'}, {'armRegionName': 'eastus'}, {'type': 'Reservation'},
                       {'tierMinimumUnits': 10}, {'isPrimaryMeterRegion': False}):
            rows.append({**rows[0], **change, 'retailPrice': 99})
        result = azure_prices.quote(rows, 'westeurope', 'EUR')
        self.assertEqual('0.120000', result['sizes']['Standard_B2ms']['fixedHourlyEstimate'])
        self.assertEqual('0.24', result['sizes']['Standard_B2ms']['fixedEstimateForHours'])
        self.assertEqual(2, len(result['disks']))
        self.assertIn('Excludes disk transactions', ' '.join(result['assumptions']))

    def test_missing_or_duplicate_meters_cannot_silently_produce_a_quote(self):
        for rows in (meters()[:-1], meters() + [copy.deepcopy(meters()[0])]):
            with self.assertRaises(ValueError):
                azure_prices.quote(rows, 'westeurope', 'EUR')

    def test_pagination_cannot_follow_an_untrusted_origin(self):
        page = {'Items': [], 'NextPageLink': 'https://untrusted.example.test/retail'}
        with patch.object(azure_prices, 'urlopen', return_value=BytesIO(json.dumps(page).encode())) as opened:
            with self.assertRaises(ValueError):
                azure_prices.fetch('westeurope', 'EUR', "productName eq 'IP Addresses'")
            self.assertEqual(1, opened.call_count)
