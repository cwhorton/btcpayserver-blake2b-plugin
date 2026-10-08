#!/usr/bin/env python3
"""Takes the README screenshots of the plugin's pages with a headless browser.

Run with ./dev.sh screenshots <phase> (Playwright in Docker). Phases:
  pages  the store wallet page, wallet setup, pricing, server settings and an open checkout
         (run with ./dev.sh mainnet, using the demo stores from .dev.env)
  paid   a paid checkout (run with ./dev.sh fake-chain, after paying PAID_INVOICE_ID)

Reads credentials and ids from the environment (.dev.env).
"""
import os
import sys

from playwright.sync_api import sync_playwright

BASE = os.environ.get("BASE_URL", "http://btcpay:14142")
OUT = os.environ.get("OUT_DIR", "/work/docs/screenshots")
ENV = os.environ
# Hide what only a development server shows: the regtest badge, notification counts, the debug
# footer and the screen-size marker.
CLEAN_CSS = """
.navbar-brand small.badge[title="Regtest"], #NotificationsBadge, footer.btcpay-footer, #MainNavTopRightHint { display: none !important; }
html::before { content: none !important; display: none !important; }
"""


def login(page):
    page.goto(f"{BASE}/login")
    page.fill("#Email", ENV["DEV_ADMIN_EMAIL"])
    page.fill("#Password", ENV["DEV_ADMIN_PASSWORD"])
    page.click("#LoginButton")
    page.wait_for_url(lambda url: "/login" not in url)


def shot(page, name, locator=None):
    page.add_style_tag(content=CLEAN_CSS)
    page.wait_for_timeout(600)
    path = f"{OUT}/{name}.png"
    if locator is not None:
        locator.screenshot(path=path)
    else:
        # Size the window to the whole page rather than stitching: fixed elements like the
        # sidebar then fill the full height.
        size = page.viewport_size
        page.set_viewport_size({"width": size["width"], "height": page.evaluate("document.documentElement.scrollHeight")})
        page.wait_for_timeout(300)
        page.screenshot(path=path)
        page.set_viewport_size(size)
    print("saved", path)


def section(page, heading_text):
    """The page row that holds a section heading."""
    return page.locator(f"h3:has-text('{heading_text}')").locator("xpath=ancestor::div[contains(@class,'row')][1]")


def admin_pages(browser):
    page = browser.new_page(viewport={"width": 1280, "height": 900}, device_scale_factor=2)
    login(page)

    page.goto(f"{BASE}/stores/{ENV['DEMO_STORE_ID']}/bitcoin-blake2b")
    page.wait_for_selector("[data-testid=BitcoinBlake2b-PriceStatus]")
    shot(page, "store-wallet")
    shot(page, "pricing", section(page, "Pricing"))

    page.goto(f"{BASE}/stores/{ENV['SETUP_STORE_ID']}/bitcoin-blake2b")
    page.fill("#WalletKey", ENV["DEV_DEMO_ZPUB"])
    page.click("#PreviewWallet")
    page.wait_for_selector("[data-testid=BitcoinBlake2b-PreviewAddresses]")
    page.check("#ConfirmAddressesMatch")
    page.check("#ConfirmDedicatedWallet")
    shot(page, "wallet-setup", section(page, "Set up your wallet"))

    page.goto(f"{BASE}/server/bitcoin-blake2b")
    page.wait_for_selector("[data-testid=BitcoinBlake2b-SourceStatus]")
    shot(page, "server-settings")
    page.close()


def checkout(browser, invoice_id, name):
    page = browser.new_page(viewport={"width": 480, "height": 1000}, device_scale_factor=2)
    page.goto(f"{BASE}/i/{invoice_id}")
    page.wait_for_selector("#Checkout, .public-page-wrap")
    page.wait_for_timeout(1500)
    shot(page, name)
    page.close()


def main(phase):
    os.makedirs(OUT, exist_ok=True)
    with sync_playwright() as p:
        browser = p.chromium.launch()
        if phase == "pages":
            admin_pages(browser)
            checkout(browser, ENV["DEMO_INVOICE_ID"], "checkout")
        elif phase == "paid":
            checkout(browser, ENV["PAID_INVOICE_ID"], "checkout-paid")
        else:
            sys.exit(f"unknown phase {phase}")
        browser.close()


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "pages")
