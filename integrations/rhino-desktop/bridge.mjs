const version = "0.2.0";
const rhinoProtocol = "2025-03-26";
const maxBytes = 512 * 1024;
const input = (properties = {}) => ({ type: "object", properties, additionalProperties: false });
const types = ["point", "pointset", "curve", "surface", "brep", "mesh", "annotation", "light", "block"];
const tools = [
  { name: "miki_rhino_context", description: "Read the connected Rhino 9 document's selection, viewport summary and object counts.", inputSchema: input() },
  { name: "miki_rhino_objects", description: "List up to 100 objects in the connected Rhino 9 document. Use a layer or geometry type to narrow the result.", inputSchema: input({
    layer: { type: "string", minLength: 1, maxLength: 256, description: "Exact Rhino layer full path." },
    geometryType: { type: "string", enum: types },
    limit: { type: "integer", minimum: 1, maximum: 100, default: 30 },
  }) },
].map(tool => ({ ...tool, annotations: { readOnlyHint: true, openWorldHint: false } }));
export { tools, version };

function validate(name, args) {
  if (!args || typeof args !== "object" || Array.isArray(args)) throw new Error("Arguments must be an object.");
  if (name === "miki_rhino_context" && Object.keys(args).length === 0) return ["get_context", {}];
  if (name !== "miki_rhino_objects") throw new Error("Unknown tool or invalid context arguments.");
  const { limit = 30, layer, geometryType } = args;
  if (Object.keys(args).some(key => !["limit", "layer", "geometryType"].includes(key)) ||
      !Number.isInteger(limit) || limit < 1 || limit > 100 ||
      (layer !== undefined && (typeof layer !== "string" || !layer.trim() || layer.length > 256)) ||
      (geometryType !== undefined && !types.includes(geometryType))) throw new Error("Invalid object filter or limit.");
  return ["list_objects", { limit, includeHidden: false, ...(layer === undefined ? {} : { layer }), ...(geometryType === undefined ? {} : { geometryType }) }];
}

async function readResponse(response, expected) {
  const decoder = new TextDecoder();
  let pending = "", data = [], bytes = 0;
  const sse = response.headers.get("content-type")?.split(";")[0].trim().toLowerCase() === "text/event-stream";
  if (!response.body) throw new Error("Empty Rhino MCP response.");
  for await (const chunk of response.body) {
    bytes += chunk.byteLength;
    if (bytes > maxBytes) throw new Error("Rhino response exceeded the inspection limit. Use a narrower object filter.");
    pending += decoder.decode(chunk, { stream: true });
    if (!sse) continue;
    for (;;) {
      const end = pending.indexOf("\n");
      if (end < 0) break;
      const line = pending.slice(0, end).replace(/\r$/, "");
      pending = pending.slice(end + 1);
      if (line.startsWith("data:")) data.push(line.slice(5).replace(/^ /, ""));
      else if (line === "" && data.length > 0) {
        const message = JSON.parse(data.join("\n"));
        data = [];
        if (message?.id === expected) return message;
      }
    }
  }
  pending += decoder.decode();
  if (!sse) return JSON.parse(pending);
  if (pending.startsWith("data:")) data.push(pending.slice(5).trimStart());
  if (data.length) {
    const message = JSON.parse(data.join("\n"));
    if (message?.id === expected) return message;
  }
  throw new Error("Rhino MCP ended before returning the requested result.");
}

export function failure(message) {
  return { isError: true, content: [{ type: "text", text: message }] };
}

export async function inspect(name, args = {}, options = {}) {
  try {
    const [native, parameters] = validate(name, args);
    const configuredPort = String(options.port ?? process.env.MIKI_RHINO_MCP_PORT ?? "10529");
    if (!/^\d{1,5}$/.test(configuredPort) || +configuredPort < 1024 || +configuredPort > 65535)
      throw new Error("MIKI_RHINO_MCP_PORT must be an integer from 1024 to 65535.");
    const url = `http://localhost:${+configuredPort}/agent`;
    const deadline = AbortSignal.timeout(8000);
    const signal = options.signal ? AbortSignal.any([deadline, options.signal]) : deadline;
    let session, id = 0;
    let protocol = rhinoProtocol;
    async function rpc(method, params, notification = false) {
      const requestId = notification ? undefined : ++id;
      let response;
      try {
        response = await fetch(url, {
          method: "POST", redirect: "error", signal,
          headers: { "Content-Type": "application/json", Accept: "application/json, text/event-stream",
            "MCP-Protocol-Version": protocol, ...(session ? { "Mcp-Session-Id": session } : {}) },
          body: JSON.stringify({ jsonrpc: "2.0", method, params, id: requestId }),
        });
      } catch {
        throw new Error(`Cannot connect to Rhino MCP on localhost:${+configuredPort}. Open the intended Rhino document and run MCPStart ${+configuredPort}.`);
      }
      if (!response.ok || notification) {
        await response.body?.cancel();
        if (!response.ok) throw new Error(`Rhino MCP returned HTTP ${response.status}.`);
        return;
      }
      session = response.headers.get("mcp-session-id") ?? session;
      const message = await readResponse(response, requestId);
      if (message?.jsonrpc !== "2.0" || message?.id !== requestId || message.error || !message.result)
        throw new Error("Rhino MCP did not accept the inspection request.");
      return message.result;
    }
    const initialized = await rpc("initialize", {
      protocolVersion: rhinoProtocol, capabilities: {}, clientInfo: { name: "miki-rhino-desktop", version },
    });
    if (["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"].includes(initialized.protocolVersion))
      protocol = initialized.protocolVersion;
    await rpc("notifications/initialized", {}, true);
    const result = await rpc("tools/call", { name: native, arguments: parameters });
    const content = Array.isArray(result.content) ? result.content.filter(item =>
      item?.type === "text" && typeof item.text === "string").map(({ type, text }) => ({ type, text })) : [];
    if (!content.length) return failure("Rhino did not return a text result.");
    return { content, ...(result.isError === true ? { isError: true } : {}) };
  } catch (error) {
    return failure(error instanceof SyntaxError ? "Rhino MCP returned invalid JSON."
      : error?.name === "TimeoutError" || error?.name === "AbortError" ? "Rhino inspection was interrupted or timed out."
      : error?.message || "Rhino inspection failed.");
  }
}
