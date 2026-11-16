# ◈ Nova Browser

A Windows browser prototype with its own WPF interface and Microsoft WebView2 (Chromium-based).

> **Security status: preview, not audited.** Do not rely on Nova Browser as a hardened primary browser yet.

## Features in the first prototype
- Dark desktop UI, multiple tabs, navigation controls, URL/search bar
- Ctrl+T new tab, Ctrl+W close tab, Ctrl+L address bar
- WebView2 browser state under the current user's LocalAppData directory
- Deny-by-default web permissions (camera, mic, geolocation, notifications etc.)
- Block popups and non-HTTP(S) navigation (apart from about:)
- **Small demonstration** domain blocking list, not equivalent to Brave Shields
- Password save and general autofill disabled

## Build (Windows)
Requires .NET 8 SDK and Microsoft Edge WebView2 Runtime (installed on many Windows machines).

```powershell
dotnet restore src/NovaBrowser/NovaBrowser.csproj
dotnet run --project src/NovaBrowser/NovaBrowser.csproj
```

GitHub Actions publishes a Windows x64 build artifact on each push. The application depends on the WebView2 Runtime even when .NET is packaged self-contained.

## Roadmap
1. Build/test stability and polished tab management
2. Robust privacy controls + per-site permission UI
3. Maintained blocklists, request filtering tests and exception controls
4. Downloads, bookmarks, history and a private-profile mode
5. Update system, signed installers, security reviews

### Important limitations
WebView2 uses Microsoft Edge's installed runtime; this project is **not** a fork of Brave or a standalone Chromium distribution. Blocking a few ad domains is not strong protection against tracking, fingerprinting, malicious content, or phishing. Updates of WebView2 runtime and Windows remain important. Never disable browser security protections to make a website work.
