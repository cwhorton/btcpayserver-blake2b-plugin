#!/usr/bin/env python3
"""Installs the packaged plugin into the stock BTCPay (./dev.sh stock with an empty plugin
folder) the way a merchant would: Server settings > Plugins > Upload plugin.

Run in the Playwright image; see ./dev.sh upload-test. After it, restart btcpay-stock.
"""
import os
import sys

from playwright.sync_api import sync_playwright

BASE = os.environ.get("BASE_URL", "http://btcpay-stock:49392")
PACKAGE = sys.argv[1]
ENV = os.environ

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page()
    page.goto(f"{BASE}/login")
    page.fill("#Email", ENV["DEV_ADMIN_EMAIL"])
    page.fill("#Password", ENV["DEV_ADMIN_PASSWORD"])
    page.click("#LoginButton")
    page.wait_for_url(lambda url: "/login" not in url)

    page.goto(f"{BASE}/server/plugins")
    page.set_input_files("#files", PACKAGE)
    # The upload form sits in a collapsed panel; submit it as the "Upload" button would.
    with page.expect_navigation():
        page.eval_on_selector("#files", "input => input.form.submit()")
    message = page.locator(".alert").first.inner_text().strip()
    print("BTCPay says:", message)
    browser.close()
    sys.exit(0 if "restart" in message.lower() else 1)
