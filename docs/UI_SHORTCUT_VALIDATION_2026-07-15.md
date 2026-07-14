# UI shortcut and browser validation — 2026-07-15

## Delivered

- Added the `shortcut` UI operation end-to-end (contracts, CLI and UI agent).
- Shortcut delivery first activates and verifies the requested top-level window,
  then sends the combination as one operation.  Existing low-level `key` calls
  remain available for separately issued press and release commands.
- Window activation now waits for the requested window to become foreground
  before input is sent.

## VM verification

The Windows test VM accepted the following browser workflow in its own Edge
session:

1. Put `https://cn.bing.com/search?q=长沙天气` on the VM clipboard.
2. Send `shortcut ... Control L` to the Edge window.
3. Send `shortcut ... Control V`, then separate `key Enter down` and
   `key Enter up` commands.

The resulting Bing page showed the query and weather result for `长沙天气`.
This also confirms that the shortcut path keeps foreground focus through the
paste.  Direct Unicode `type` input is not reliable in Edge's address bar;
clipboard paste is the supported browser-address-bar path.

## UI acceptance status

- Mouse movement, separate button down/up, and wheel behavior were verified in
  the UI test application.
- Keyboard entry and clipboard round-trip were verified in the VM UI test
  application.
- The UI agent and the agent service were both republished before exercising
  the newly registered shortcut operation.
