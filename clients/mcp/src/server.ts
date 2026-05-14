/**
 * MCP server. Bridges the 26 mesen2-rpc tools to the Model Context
 * Protocol so Claude Code can drive the emulator directly.
 *
 * stdio transport: stdin reads MCP JSON-RPC, stdout writes responses,
 * stderr is the ONLY place we may write logs. Never console.log here —
 * it would corrupt the wire.
 */

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";

import { Mesen2Client } from "@k0b3n4irb/mesen2-rpc-client";
import { spawnIfNeeded } from "./spawn.js";
import { TOOLS } from "./tools.js";

interface Config {
  binary: string;
  port: number;
  host: string;
}

function readConfig(): Config {
  const binary = process.env.MESEN_RPC_BIN
    ?? "/home/kobenairb/workspace/Mesen2/bin/linux-arm64/Release/Mesen";
  const port = parseInt(process.env.MESEN_RPC_PORT ?? "9911", 10);
  const host = process.env.MESEN_RPC_HOST ?? "127.0.0.1";
  return { binary, port, host };
}

export async function run(): Promise<void> {
  const cfg = readConfig();

  // Spawn binary if no server is reachable. The child handle is kept so
  // we can kill it on shutdown — leaking emulator processes after Claude
  // Code disconnects would be very rude.
  const child = await spawnIfNeeded({ binary: cfg.binary, port: cfg.port });

  const client = new Mesen2Client({ host: cfg.host, port: cfg.port });
  await client.connect();
  process.stderr.write(`mesen2-mcp: connected to ${cfg.host}:${cfg.port} (${TOOLS.length} tools)\n`);

  const server = new Server(
    { name: "mesen2-rpc", version: "0.1.0" },
    { capabilities: { tools: {} } },
  );

  server.setRequestHandler(ListToolsRequestSchema, () => ({
    tools: TOOLS.map(t => ({
      name: t.name,
      description: t.description,
      inputSchema: t.inputSchema,
    })),
  }));

  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: args } = request.params;
    const tool = TOOLS.find(t => t.name === name);
    if (!tool) {
      return {
        isError: true,
        content: [{ type: "text", text: `unknown tool: ${name}` }],
      };
    }

    try {
      const result = await tool.handler(client, (args ?? {}) as Record<string, unknown>);
      return {
        content: [{ type: "text", text: JSON.stringify(result) }],
      };
    } catch (err) {
      // RpcException = server-side bounds/state error → return as a tool
      // error so Claude can see it and adjust; do NOT propagate as an MCP
      // protocol error (those abort the conversation).
      // RpcException's .message already includes "RPC <code>: <text>" —
      // don't prefix again or the message reads "RPC -32602: RPC -32602:".
      const msg = err instanceof Error ? err.message : String(err);
      return {
        isError: true,
        content: [{ type: "text", text: msg }],
      };
    }
  });

  const cleanup = () => {
    try { client.close(); } catch { /* ignore */ }
    if (child) {
      child.kill("SIGTERM");
      setTimeout(() => { if (!child.killed) child.kill("SIGKILL"); }, 1000);
    }
  };
  process.on("SIGINT", () => { cleanup(); process.exit(0); });
  process.on("SIGTERM", () => { cleanup(); process.exit(0); });
  process.on("beforeExit", cleanup);

  // server.connect() resolves once the transport is wired — it does NOT
  // block until stdio closes. The process stays alive on the event loop
  // (stdin keeps it pinned). Cleanup runs on signal / beforeExit only.
  const transport = new StdioServerTransport();
  await server.connect(transport);
}
