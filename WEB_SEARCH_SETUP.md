# Web search setup

The app uses Tavily Search through `IWebSearchService`. It searches only when a request explicitly asks for web search or is recognized as time-sensitive. Ordinary chat stays local and does not require an internet connection.

## Configure the free plan

Tavily currently advertises 1,000 API credits per month on its free plan without a payment card. Search requests require a Tavily account and API key. Its current API help lists 100 requests per minute for development keys and 1,000 per minute for production keys; the free monthly credit allowance is likely to be the practical limit for this personal app. Credit use varies by request type; this app sends basic search requests, asks for source excerpts and publication dates, and does not request a generated Tavily answer. If monthly credits are exhausted, search stops until the next reset unless the account is upgraded. Check Tavily's [current pricing](https://www.tavily.com/pricing), [rate limits](https://help.tavily.com/articles/3240802908-rate-limits), and [API usage terms](https://help.tavily.com/collections/4969763352-api) before relying on these limits.

Set the key as a per-user environment variable in PowerShell, then restart the app:

```powershell
[Environment]::SetEnvironmentVariable('TAVILY_API_KEY', 'paste-your-key-here', 'User')
```

The app reads the key from `TAVILY_API_KEY`; it does not write the key into the settings JSON. Search terms are sent to Tavily. Source excerpts are then sent to the locally running Ollama model for summarization.

## What the app returns

The service keeps at most three valid HTTP(S) sources, limits each excerpt to 1,800 characters, caps a provider response at 1 MB, and caches successful searches in memory for two minutes (up to 64 queries). Requests have a 20-second timeout and honor cancellation. The answer bubble includes clickable source links. Dates are provider-supplied and the assistant is instructed to treat them as unverified unless the source itself establishes the date.

If the network, key, or provider is unavailable, web search shows an error. You can still have ordinary local conversations. No browser automation or publisher-page scraping is used.
