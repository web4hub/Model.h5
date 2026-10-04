!pip install playwright
!playwright install
# colab need this for async
import nest_asyncio
nest_asyncio.apply()

# start browser, not to use 'with' context
from playwright.async_api import async_playwright
playwright = await async_playwright().start()
browser = await playwright.chromium.launch()

# go to a page, print its source
page = await browser.new_page()
await page.goto("https://playwright.dev/")
print(await page.content())

# end session
await browser.close()
await playwright.stop()
