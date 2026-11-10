import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { once } from "node:events";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { inspect } from "../integrations/rhino-desktop/bridge.mjs";
import { apply } from "../integrations/rhino-desktop/dsh.mjs";

test("Codex stdio and DSH use the same bounded Rhino tools", { timeout: 12000 }, async t => {
  const requests = [];
  let redirect = false;
  const server = createServer(async (req, res) => {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const request = JSON.parse(Buffer.concat(chunks));
    requests.push({ request, session: req.headers["mcp-session-id"], url: req.url });
    if (redirect) return res.writeHead(302, { Location: "/should-not-fetch" }).end();
    if (request.method === "initialize") {
      return res.writeHead(200, { "Content-Type": "application/json", "Mcp-Session-Id": "fixture" })
        .end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { protocolVersion: "2025-03-26" } }));
    }
    if (request.method === "notifications/initialized") return res.writeHead(202).end();
    res.writeHead(200, { "Content-Type": "text/event-stream" });
    res.write("data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\"}\r\n\r\n");
    res.write("data: " + JSON.stringify({ jsonrpc: "2.0", id: request.id, result: {
      content: [{ type: "text", text: "Rhino: 砖墙" }, { type: "image", data: "unused" }],
    } }) + "\r\n\r\n"); // Deliberately leave the connection open.
  });
  t.after(() => { server.closeAllConnections(); server.close(); });
  server.listen(0, "localhost");
  await once(server, "listening");
  const port = server.address().port;
  const registered = [];
  apply({ inject: (_deps, fn) => fn({
    effect: start => start(), tools: { register: tool => { registered.push(tool); return () => {}; } },
  }) }, { port });
  assert.equal(registered.length, 2);
  assert.deepEqual(await registered[1].execute({ layer: "walls", limit: 20 }), {
    content: [{ type: "text", text: "Rhino: 砖墙" }],
  });
  assert.deepEqual(requests.at(-1).request.params, {
    name: "list_objects", arguments: { layer: "walls", limit: 20, includeHidden: false },
  });
  assert.equal(requests.at(-1).session, "fixture");
  assert.equal(requests.at(-1).url, "/agent");

  const child = spawn(process.execPath, [fileURLToPath(new URL("../integrations/rhino-desktop/server.mjs", import.meta.url))],
    { env: { ...process.env, MIKI_RHINO_MCP_PORT: String(port) }, stdio: ["pipe", "pipe", "inherit"] });
  t.after(() => child.kill());
  const responses = createInterface({ input: child.stdout });
  const rpc = async (id, method, params) => {
    const line = once(responses, "line");
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
    const reply = JSON.parse((await line)[0]);
    assert.equal(reply.id, id);
    return reply.result;
  };
  assert.equal((await rpc(1, "initialize", { protocolVersion: "2025-06-18" })).protocolVersion, "2025-06-18");
  assert.deepEqual((await rpc(2, "tools/list")).tools.map(tool => tool.name), registered.map(tool => tool.name));
  assert.equal((await rpc(3, "tools/call", { name: "miki_rhino_context" })).content[0].text, "Rhino: 砖墙");
  const count = requests.length;
  assert.equal((await rpc(4, "tools/call", { name: "run_python" })).isError, true);
  assert.equal((await inspect("miki_rhino_objects", { limit: 101 }, { port })).isError, true);
  assert.equal(requests.length, count);
  redirect = true;
  assert.equal((await inspect("miki_rhino_context", {}, { port })).isError, true);
  assert.equal(requests.length, count + 1);
});
