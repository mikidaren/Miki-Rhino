import { inspect, tools } from "./bridge.mjs";

export const name = "miki-rhino-dsh";
export function apply(root, config = {}) {
  root.inject(["tools"], ctx => {
    for (const tool of tools) {
      ctx.effect(() => ctx.tools.register({
        name: tool.name, description: tool.description, parameters: tool.inputSchema,
        output: { schema: { type: "object" }, render: (_args, result) => result.content },
        execute: async (args, execution) => {
          const result = await inspect(tool.name, args, { port: config.port, signal: execution?.signal });
          if (result.isError) throw new Error(result.content[0]?.text ?? "Rhino inspection failed.");
          return result;
        },
      }), tool.name);
    }
  });
}
