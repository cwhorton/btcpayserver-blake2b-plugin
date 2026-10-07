#!/usr/bin/env python3
"""A fake Esplora-style explorer for local testing of the plugin. State is in memory.

Read API (the subset the plugin uses):
  GET  /api/blocks/tip/height
  GET  /api/block-height/<height>
  GET  /api/address/<address>
  GET  /api/address/<address>/txs[?after_txid=<txid>]

Control API (JSON bodies):
  POST /dev/pay    {"address": "tb1...", "sats": 1000, "txid": optional}  -> unconfirmed tx
  POST /dev/mine   {"blocks": 1}       first new block confirms every unconfirmed tx
  POST /dev/drop   {"txid": "..."}     tx vanishes (replaced, double-spent or dropped)
  POST /dev/reorg  {"depth": 1}        undo the top blocks; their txs become unconfirmed
  POST /dev/fail   {"on": true}        every read request answers 503
  GET  /dev/state
"""
import argparse
import hashlib
import json
import os
import re
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

PAGE_SIZE = 10
CHANGE_ADDRESS = "tb1qfakechangefakechangefakechangefakech0000"


class State:
    def __init__(self, tip, checkpoint_height, checkpoint_hash):
        self.lock = threading.Lock()
        self.tip = tip
        self.checkpoint_height = checkpoint_height
        self.checkpoint_hash = checkpoint_hash
        self.failing = False
        self.txs = {}  # txid -> {"address", "sats", "height", "seq"}
        self.seq = 0

    def block_hash(self, height):
        if height == self.checkpoint_height:
            return self.checkpoint_hash
        return hashlib.sha256(f"fake-block-{height}".encode()).hexdigest()

    def tx_json(self, txid, tx):
        confirmed = tx["height"] is not None
        status = {"confirmed": confirmed}
        if confirmed:
            status.update(block_height=tx["height"], block_hash=self.block_hash(tx["height"]), block_time=1791380000 + tx["height"])
        # The payment is output 1; output 0 is change, so output indexes are exercised.
        return {
            "txid": txid,
            "vout": [
                {"value": 12345, "scriptpubkey_address": CHANGE_ADDRESS, "scriptpubkey_type": "v0_p2wpkh"},
                {"value": tx["sats"], "scriptpubkey_address": tx["address"], "scriptpubkey_type": "v0_p2wpkh"},
            ],
            "status": status,
        }

    def address_txs(self, address):
        mine = [(txid, tx) for txid, tx in self.txs.items() if tx["address"] == address]
        unconfirmed = sorted([t for t in mine if t[1]["height"] is None], key=lambda t: -t[1]["seq"])
        confirmed = sorted([t for t in mine if t[1]["height"] is not None], key=lambda t: (-t[1]["height"], -t[1]["seq"]))
        return unconfirmed, confirmed


def make_handler(state):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):
            pass

        def send(self, code, body, content_type="application/json"):
            data = (body if isinstance(body, str) else json.dumps(body)).encode()
            self.send_response(code)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            url = urlparse(self.path)
            path = url.path
            with state.lock:
                if path == "/dev/state":
                    return self.send(200, {"tip": state.tip, "failing": state.failing, "txs": state.txs})
                if state.failing:
                    return self.send(503, "unavailable", "text/plain")
                if path == "/api/blocks/tip/height":
                    return self.send(200, str(state.tip), "text/plain")
                m = re.fullmatch(r"/api/block-height/(\d+)", path)
                if m:
                    height = int(m.group(1))
                    if height > state.tip:
                        return self.send(404, "Block not found", "text/plain")
                    return self.send(200, state.block_hash(height), "text/plain")
                m = re.fullmatch(r"/api/address/([0-9a-zA-Z]+)", path)
                if m:
                    unconfirmed, confirmed = state.address_txs(m.group(1))
                    return self.send(200, {
                        "address": m.group(1),
                        "chain_stats": {"tx_count": len(confirmed)},
                        "mempool_stats": {"tx_count": len(unconfirmed)},
                    })
                m = re.fullmatch(r"/api/address/([0-9a-zA-Z]+)/txs", path)
                if m:
                    unconfirmed, confirmed = state.address_txs(m.group(1))
                    after = parse_qs(url.query).get("after_txid", [None])[0]
                    if after:
                        ids = [t[0] for t in confirmed]
                        page = confirmed[ids.index(after) + 1:][:PAGE_SIZE] if after in ids else []
                    else:
                        page = unconfirmed + confirmed[:PAGE_SIZE]
                    return self.send(200, [state.tx_json(txid, tx) for txid, tx in page])
            self.send(404, "not found", "text/plain")

        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)) or b"{}")
            with state.lock:
                if self.path == "/dev/pay":
                    txid = body.get("txid") or os.urandom(32).hex()
                    state.seq += 1
                    state.txs[txid] = {"address": body["address"], "sats": int(body["sats"]), "height": None, "seq": state.seq}
                    return self.send(200, {"txid": txid})
                if self.path == "/dev/mine":
                    blocks = int(body.get("blocks", 1))
                    for txid, tx in state.txs.items():
                        if tx["height"] is None:
                            tx["height"] = state.tip + 1
                    state.tip += blocks
                    return self.send(200, {"tip": state.tip})
                if self.path == "/dev/drop":
                    state.txs.pop(body["txid"], None)
                    return self.send(200, {"ok": True})
                if self.path == "/dev/reorg":
                    depth = int(body.get("depth", 1))
                    state.tip -= depth
                    for tx in state.txs.values():
                        if tx["height"] is not None and tx["height"] > state.tip:
                            tx["height"] = None
                    return self.send(200, {"tip": state.tip})
                if self.path == "/dev/fail":
                    state.failing = bool(body.get("on", True))
                    return self.send(200, {"failing": state.failing})
            self.send(404, "not found", "text/plain")

    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=3002)
    parser.add_argument("--tip", type=int, default=160000)
    parser.add_argument("--checkpoint-height", type=int, required=True)
    parser.add_argument("--checkpoint-hash", required=True)
    args = parser.parse_args()
    state = State(args.tip, args.checkpoint_height, args.checkpoint_hash)
    ThreadingHTTPServer(("0.0.0.0", args.port), make_handler(state)).serve_forever()


if __name__ == "__main__":
    main()
