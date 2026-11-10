import { inspect, tools } from "./bridge.mjs";

export const name = "miki-rhino-dsh";
export function apply(root, config = {}) {
  root.inject(["tools"], ctx => {
    for (const tool of tools) {
      ctx.effect(() => ctx.tools.register({
        name: tool.name, description: tool.description, parameters: tool.inputSchema,
        output: { schema: { type: "object" }, render: (_args, result) => result.content },
        execute: (args, execution) => inspect(tool.name, args, { port: config.port, signal: execution?.signal }),
      }), tool.name);
    }
  });
}
