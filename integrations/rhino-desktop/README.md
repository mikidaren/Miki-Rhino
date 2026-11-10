# Miki-Rhino desktop integrations

Open the intended Rhino 9 document on Windows and run `MCPStart 10529`. Confirm the port with `MCPStatus`. Keep the listener on localhost. Node.js 22+ must run on the same computer. Set `MIKI_RHINO_MCP_PORT` in the host process if the port differs.

| Host | Setup |
| --- | --- |
| Codex desktop | `codex plugin marketplace add mikidaren/Miki-Rhino --ref v0.2.0` then select Miki-Rhino in the Plugins Directory and use a local thread. Node.js must be on PATH. |
| DSH Web/TUI | `dsh plugin --profile web add 'github:mikidaren/Miki-Rhino#v0.2.0&path:integrations/rhino-desktop' --ignore-scripts`. Restart the selected profile and start a session. For Desktop, use the app's plugin manager. |

`miki_rhino_context` returns selection, viewport summary and counts. `miki_rhino_objects` returns up to 100 objects, with optional layer and type filters. Geometry creation and IFC export are not available through these adapters. The Rhino panel remains the modeling interface.

The bridge sends inspection requests to localhost. The agent host receives the result and may pass it to its configured model service. There are no credentials, install hooks or model-service requests in this package.
