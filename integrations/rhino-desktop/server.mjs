import { createInterface } from "node:readline";
import { inspect, tools, version } from "./bridge.mjs";

const protocols = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];
const active = new Map();
const send = (id, result, error) => process.stdout.write(JSON.stringify({
  jsonrpc: "2.0", id, ...(error ? { error } : { result }),
}) + "\n");
const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", async line => {
  let request;
  try { request = JSON.parse(line); }
  catch { send(null, null, { code: -32700, message: "Invalid JSON" }); return; }
  if (request?.jsonrpc !== "2.0" || typeof request.method !== "string") {
    send(null, null, { code: -32600, message: "Invalid request" }); return;
  }
  const { id, method, params } = request;
  if (id === undefined) {
    if (method === "notifications/cancelled") active.get(params?.requestId)?.abort();
    return;
  }
  if (typeof id !== "string" && !Number.isInteger(id)) {
    send(null, null, { code: -32600, message: "Invalid request ID" }); return;
  }
  if (method === "initialize") send(id, { protocolVersion: protocols.includes(params?.protocolVersion)
    ? params.protocolVersion : protocols.at(-1), capabilities: { tools: {} }, serverInfo: { name: "miki-rhino", version } });
  else if (method === "ping") send(id, {});
  else if (method === "tools/list") send(id, { tools });
  else if (method === "tools/call") {
    if (active.size >= 4 || active.has(id)) {
      send(id, null, { code: -32000, message: "Too many requests or duplicate request ID" }); return;
    }
    const controller = new AbortController();
    active.set(id, controller);
    try {
      const result = await inspect(params?.name, params?.arguments, { signal: controller.signal });
      if (!controller.signal.aborted) send(id, result);
    }
    finally { active.delete(id); }
  } else send(id, null, { code: -32601, message: "Unknown method" });
});
input.on("close", () => active.forEach(controller => controller.abort()));
