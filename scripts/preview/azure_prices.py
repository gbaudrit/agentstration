#!/usr/bin/env python3
"""Public PAYG rate snapshot and a fixed-resource estimate; no Azure authentication."""
import argparse
from datetime import datetime, timezone
from decimal import Decimal
import json
from pathlib import Path
import re
from urllib.parse import urlencode, urlsplit
from urllib.request import urlopen

FIELDS = ('meterId', 'productName', 'skuName', 'meterName', 'armSkuName', 'armRegionName',
          'retailPrice', 'currencyCode', 'unitOfMeasure', 'effectiveStartDate')


def fetch(region, currency, expression):
    query = {'currencyCode': currency, '$filter': "armRegionName eq '" + region + "' and priceType eq 'Consumption' and " + expression}
    url = 'https://prices.azure.com/api/retail/prices?' + urlencode(query)
    rows = []
    for _ in range(5):
        parsed = urlsplit(url)
        if (parsed.scheme != 'https' or parsed.hostname != 'prices.azure.com' or parsed.port not in (None, 443)
                or parsed.username is not None or parsed.password is not None):
            raise ValueError('Unexpected retail-price pagination origin.')
        with urlopen(url, timeout=30) as response:
            payload = response.read(2 * 1024 * 1024 + 1)
        if len(payload) > 2 * 1024 * 1024:
            raise ValueError('Retail-price response exceeds its limit.')
        page = json.loads(payload)
        rows.extend(page['Items'])
        url = page.get('NextPageLink')
        if not url:
            return rows
    raise ValueError('Retail-price pagination exceeds its limit.')


def quote(rows, region, currency, hours=2):
    candidates = [r for r in rows if r.get('armRegionName') == region and r.get('currencyCode') == currency
                  and r.get('type') == 'Consumption'
                  and r.get('tierMinimumUnits') == 0 and r.get('retailPrice', 0) > 0]

    def one(predicate):
        found = [r for r in candidates if predicate(r)]
        if len(found) > 1:
            primary = [r for r in found if r.get('isPrimaryMeterRegion') is True]
            if len(primary) == 1:
                found = primary
        if len(found) != 1:
            raise ValueError('Retail meter selection must be unambiguous; inspect the public candidate snapshot.')
        return {key: found[0].get(key) for key in FIELDS}

    disks = [one(lambda r, tier=tier: r['productName'] == 'Standard SSD Managed Disks'
                 and r['meterName'] == tier + ' LRS Disk' and r['unitOfMeasure'] == '1/Month') for tier in ('E4', 'E6')]
    ip = one(lambda r: r['productName'] == 'IP Addresses' and r['meterName'] == 'Standard IPv4 Static Public IP'
             and r['unitOfMeasure'] == '1 Hour')
    estimates = {}
    for size in ('Standard_B2ms', 'Standard_B4ms'):
        vm = one(lambda r: r.get('armSkuName') == size and r['productName'] == 'Virtual Machines BS Series'
                 and r['skuName'] == size.removeprefix('Standard_') and r['unitOfMeasure'] == '1 Hour')
        hourly = Decimal(str(vm['retailPrice'])) + Decimal(str(ip['retailPrice']))
        hourly += sum(Decimal(str(d['retailPrice'])) for d in disks) / Decimal(730)
        estimates[size] = {'compute': vm, 'fixedHourlyEstimate': str(hourly.quantize(Decimal('0.000001'))),
                           'fixedEstimateForHours': str((hourly * Decimal(str(hours))).quantize(Decimal('0.01')))}
    return {'region': region, 'currency': currency, 'hours': hours, 'disks': disks, 'publicIp': ip, 'sizes': estimates,
            'assumptions': ['32 GiB OS (E4) and 64 GiB data (E6), Standard SSD LRS', '730 hours/month prorating estimate',
                            'Linux PAYG, no Spot, reservations, discounts or taxes',
                            'Excludes disk transactions, shared Blob capacity/operations and outbound transfer; these depend on measured usage',
                            'Preserve-destroy retains billable disks and public IP; budget alerts are not a spending cap']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--region', default='westeurope')
    parser.add_argument('--currency', choices=('EUR', 'USD'), default='EUR')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if not re.fullmatch(r'[a-z][a-z0-9]{1,31}', args.region):
        parser.error('Invalid region.')
    rows = []
    for expression in ("serviceName eq 'Virtual Machines' and (armSkuName eq 'Standard_B2ms' or armSkuName eq 'Standard_B4ms')",
                       "productName eq 'Standard SSD Managed Disks'", "productName eq 'IP Addresses'"):
        rows.extend(fetch(args.region, args.currency, expression))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.with_suffix('.candidates.json').write_text(json.dumps([{key: r.get(key) for key in (*FIELDS, 'type', 'isPrimaryMeterRegion', 'tierMinimumUnits')}
                                                                    for r in rows], indent=2) + '\n')
    result = quote(rows, args.region, args.currency)
    result['retrievedAt'] = datetime.now(timezone.utc).isoformat()
    args.output.write_text(json.dumps(result, indent=2) + '\n')


if __name__ == '__main__':
    main()
