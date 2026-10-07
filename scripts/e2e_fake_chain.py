#!/usr/bin/env python3
"""End-to-end test of XBT payment detection against the fake explorers.

Prerequisites: ./dev.sh fake-chain (3 fake explorers, 2 must agree, 2 s polling),
./scripts/dev-seed.sh, and an XBT wallet saved for the dev store. The store's speed
policy must be the default (medium), which requires 3 confirmations.
"""
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
URL = "http://localhost:14142"
PMI = "BTCB2-CHAIN"


def env():
    values = {}
    with open(os.path.join(ROOT, ".dev.env")) as f:
        for line in f:
            if "=" in line:
                k, v = line.strip().split("=", 1)
                values[k] = v.strip('"')
    return values


ENV = env()
STORE = ENV["DEV_STORE_ID"]


def api(method, path, body=None):
    req = urllib.request.Request(URL + path, method=method, data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Authorization": "token " + ENV["DEV_API_KEY"], "Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read() or b"null")


def chain(*args):
    return subprocess.run([os.path.join(ROOT, "scripts", "fake-chain.sh"), *map(str, args)], check=True, capture_output=True, text=True).stdout.strip()


def new_invoice(amount="10"):
    invoice = api("POST", f"/api/v1/stores/{STORE}/invoices", {"amount": amount, "currency": "USD"})
    method = xbt(invoice["id"])
    assert method is not None, "XBT is not offered on the new invoice"
    return invoice["id"], method["destination"], round(float(method["due"]) * 1e8)


def xbt(invoice_id):
    methods = api("GET", f"/api/v1/stores/{STORE}/invoices/{invoice_id}/payment-methods")
    return next((m for m in methods if m["paymentMethodId"] == PMI), None)


def status(invoice_id):
    i = api("GET", f"/api/v1/stores/{STORE}/invoices/{invoice_id}")
    return i["status"], i["additionalStatus"]


def payments(invoice_id):
    return [(p["id"], p["status"]) for p in xbt(invoice_id)["payments"]]


def wait(description, check, timeout=40):
    deadline = time.time() + timeout
    while time.time() < deadline:
        value = check()
        if value:
            print(f"  ok: {description}")
            return value
        time.sleep(1)
    raise AssertionError(f"timed out waiting for: {description} (last: {check.__doc__ or ''})")


def stays(description, check, seconds=8):
    deadline = time.time() + seconds
    while time.time() < deadline:
        assert check(), f"changed unexpectedly: {description}"
        time.sleep(1)
    print(f"  ok: {description}")


def test_agreement_and_settlement():
    print("Payment needs 2 of 3 explorers, then settles after 3 confirmations")
    inv, address, sats = new_invoice()
    txid = chain("pay", address, sats, "a")
    stays("a payment seen by only one explorer is ignored", lambda: payments(inv) == [])
    chain("pay", address, sats, "b", txid)
    wait("payment recorded once two explorers agree", lambda: payments(inv) == [(f"{txid}-1", "Processing")])
    wait("invoice is processing", lambda: status(inv)[0] == "Processing")
    chain("mine", 2)
    stays("2 confirmations: still processing", lambda: status(inv)[0] == "Processing", 6)
    chain("mine", 1)
    wait("3 confirmations: settled", lambda: status(inv)[0] == "Settled")


def test_partial_payment():
    print("Partial payment, then the rest")
    inv, address, sats = new_invoice()
    chain("pay", address, sats // 2)
    wait("partial payment noticed", lambda: status(inv) == ("New", "PaidPartial"))
    chain("pay", address, sats - sats // 2)
    wait("fully paid: processing", lambda: status(inv)[0] == "Processing")
    chain("mine", 3)
    wait("settled", lambda: status(inv)[0] == "Settled")


def test_vanishing_payment():
    print("A payment that disappears stops counting")
    inv, address, sats = new_invoice()
    txid = chain("pay", address, sats)
    wait("processing", lambda: status(inv)[0] == "Processing")
    chain("mine", 2)
    chain("reorg", 2)
    stays("reorganized back to unconfirmed: still counted", lambda: status(inv)[0] == "Processing", 6)
    chain("drop", txid)
    # The Greenfield API lists only payments that count.
    wait("payment no longer counted", lambda: payments(inv) == [])
    wait("invoice back to unpaid with the full amount due", lambda: status(inv)[0] == "New" and round(float(xbt(inv)["due"]) * 1e8) == sats)


def test_outage():
    print("Too few explorers reachable")
    chain("fail", "on", "a,b")
    try:
        time.sleep(6)
        try:
            invoice = api("POST", f"/api/v1/stores/{STORE}/invoices", {"amount": "10", "currency": "USD"})
            assert xbt(invoice["id"]) is None or not xbt(invoice["id"]).get("destination"), "XBT offered while unmonitorable"
        except urllib.error.HTTPError as e:
            # XBT is this store's only payment method, so BTCPay refuses the invoice outright.
            message = e.read().decode()
            assert e.code == 400 and "Not enough XBT chain data sources are reachable" in message, message
        print("  ok: no XBT address handed out while only 1 of 3 explorers answers")
    finally:
        chain("fail", "off", "a,b")
    time.sleep(6)
    new_invoice()
    print("  ok: XBT offered again once explorers are back")


if __name__ == "__main__":
    tests = [test_agreement_and_settlement, test_partial_payment, test_vanishing_payment, test_outage]
    selected = [t for t in tests if len(sys.argv) < 2 or t.__name__ in sys.argv[1:]]
    for t in selected:
        t()
    print(f"\nAll {len(selected)} end-to-end scenarios passed")
