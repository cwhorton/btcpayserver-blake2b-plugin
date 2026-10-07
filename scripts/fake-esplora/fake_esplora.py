#!/usr/bin/env python3
"""A fake XBT chain for local testing of the plugin, served both as an Esplora-style
explorer (HTTP) and as an Electrum server (plain TCP). State is in memory. Transactions are
real serialized transactions, so their ids are their double-SHA256 like on the real chain.

Read API (the subset the plugin uses):
  GET  /api/blocks/tip/height
  GET  /api/block-height/<height>
  GET  /api/address/<address>
  GET  /api/address/<address>/txs[?after_txid=<txid>]

Electrum methods: server.version, blockchain.headers.subscribe, blockchain.block.header,
blockchain.scripthash.get_history, blockchain.transaction.get

Control API (JSON bodies):
  POST /dev/pay    {"address": "tb1...", "sats": 1000, "nonce": optional}  -> unconfirmed tx
                   (the same address, amount and nonce always make the same transaction)
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
import socketserver
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

PAGE_SIZE = 10
CHANGE_ADDRESS = "tb1qfakechange"
CHANGE_SCRIPT = bytes([0x00, 0x14]) + bytes(20)

# --- Bech32/bech32m (BIP173/BIP350) decoding, enough for segwit addresses ---
CHARSET = "qpzry9x8gf2tvdw0s3jn54khce6mua7l"


def _polymod(values):
    gen = [0x3B6A57B2, 0x26508E6D, 0x1EA119FA, 0x3D4233DD, 0x2A1462B3]
    chk = 1
    for v in values:
        top = chk >> 25
        chk = (chk & 0x1FFFFFF) << 5 ^ v
        for i in range(5):
            chk ^= gen[i] if ((top >> i) & 1) else 0
    return chk


def script_for(address):
    address = address.lower()
    hrp, data = address[:address.rindex("1")], [CHARSET.index(c) for c in address[address.rindex("1") + 1:]]
    if _polymod([ord(c) >> 5 for c in hrp] + [0] + [ord(c) & 31 for c in hrp] + data) not in (1, 0x2BC830A3):
        raise ValueError(f"bad checksum: {address}")
    version, acc, bits, prog = data[0], 0, 0, []
    for v in data[1:-6]:
        acc = (acc << 5) | v
        bits += 5
        while bits >= 8:
            bits -= 8
            prog.append((acc >> bits) & 0xFF)
    return bytes([version + 0x50 if version else 0, len(prog)]) + bytes(prog)


def varint(n):
    return bytes([n]) if n < 0xFD else b"\xfd" + n.to_bytes(2, "little")


def make_tx(address, sats, nonce):
    """A one-input transaction: output 0 is change, output 1 pays the address."""
    prevout = hashlib.sha256(f"{address}/{sats}/{nonce}".encode()).digest() + (0).to_bytes(4, "little")
    outputs = [(12345, CHANGE_SCRIPT), (sats, script_for(address))]
    raw = (2).to_bytes(4, "little") + varint(1) + prevout + varint(0) + b"\xff\xff\xff\xff" + varint(len(outputs))
    for value, script in outputs:
        raw += value.to_bytes(8, "little") + varint(len(script)) + script
    raw += (0).to_bytes(4, "little")
    txid = hashlib.sha256(hashlib.sha256(raw).digest()).digest()[::-1].hex()
    return txid, raw.hex(), script_for(address)


class State:
    def __init__(self, tip, checkpoint_height, checkpoint_hash, checkpoint_header):
        self.lock = threading.Lock()
        self.tip = tip
        self.checkpoint_height = checkpoint_height
        self.checkpoint_hash = checkpoint_hash
        self.checkpoint_header = checkpoint_header
        self.failing = False
        self.txs = {}  # txid -> {"address", "sats", "height", "seq", "raw", "scripthash"}
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
                    txid, raw, script = make_tx(body["address"], int(body["sats"]), body.get("nonce") or os.urandom(8).hex())
                    state.seq += 1
                    state.txs[txid] = {"address": body["address"], "sats": int(body["sats"]), "height": None, "seq": state.seq,
                                       "raw": raw, "scripthash": hashlib.sha256(script).digest()[::-1].hex()}
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


def make_electrum_handler(state):
    class ElectrumHandler(socketserver.StreamRequestHandler):
        def handle(self):
            for line in self.rfile:
                if not line.strip():
                    continue
                request = json.loads(line)
                try:
                    result = self.call(request["method"], request.get("params", []))
                    response = {"jsonrpc": "2.0", "id": request["id"], "result": result}
                except Exception as e:
                    response = {"jsonrpc": "2.0", "id": request["id"], "error": {"code": 1, "message": str(e)}}
                self.wfile.write((json.dumps(response) + "\n").encode())
                self.wfile.flush()

        def call(self, method, params):
            with state.lock:
                if state.failing:
                    raise Exception("unavailable")
                if method == "server.version":
                    return ["fake-electrum", "1.8"]
                if method == "blockchain.headers.subscribe":
                    return {"height": state.tip, "hex": ""}
                if method == "blockchain.block.header":
                    height = int(params[0])
                    if height > state.tip:
                        raise Exception(f"height {height} out of range")
                    return state.checkpoint_header if height == state.checkpoint_height else hashlib.sha256(str(height).encode()).hexdigest() * 5
                if method == "blockchain.scripthash.get_history":
                    return [{"tx_hash": txid, "height": tx["height"] or 0} for txid, tx in state.txs.items() if tx["scripthash"] == params[0]]
                if method == "blockchain.transaction.get":
                    return state.txs[params[0]]["raw"]
            raise Exception(f"unknown method {method}")

    return ElectrumHandler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=3002)
    parser.add_argument("--tip", type=int, default=160000)
    parser.add_argument("--checkpoint-height", type=int, required=True)
    parser.add_argument("--checkpoint-hash", required=True)
    parser.add_argument("--checkpoint-header", required=True)
    parser.add_argument("--electrum-port", type=int, default=50001)
    args = parser.parse_args()
    state = State(args.tip, args.checkpoint_height, args.checkpoint_hash, args.checkpoint_header)
    socketserver.ThreadingTCPServer.allow_reuse_address = True
    electrum = socketserver.ThreadingTCPServer(("0.0.0.0", args.electrum_port), make_electrum_handler(state))
    threading.Thread(target=electrum.serve_forever, daemon=True).start()
    ThreadingHTTPServer(("0.0.0.0", args.port), make_handler(state)).serve_forever()


if __name__ == "__main__":
    main()
