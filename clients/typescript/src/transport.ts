/**
 * JSON-RPC 2.0 transport over TCP with LSP-style Content-Length framing.
 *
 * The server side uses StreamJsonRpc's HeaderDelimitedMessageHandler, which
 * is the same wire format VS Code's DAP and LSP use:
 *
 *     Content-Length: <bytes>\r\n
 *     \r\n
 *     {"jsonrpc":"2.0","id":N,"method":"...","params":[...]}
 *
 * This module owns the byte plumbing only — there is no schema knowledge
 * here. The typed client (client.ts) layers on top.
 */

import * as net from "node:net";

export interface RpcResponse<T> {
  jsonrpc: "2.0";
  id: number;
  result?: T;
  error?: RpcError;
}

export interface RpcError {
  code: number;
  message: string;
  data?: unknown;
}

export class RpcException extends Error {
  readonly code: number;
  readonly data: unknown;
  constructor(err: RpcError) {
    super(`RPC ${err.code}: ${err.message}`);
    this.name = "RpcException";
    this.code = err.code;
    this.data = err.data;
  }
}

export interface TransportOptions {
  host?: string;
  port: number;
  /** Per-request timeout. Defaults to 30s; some calls (snap.save, run_until) can legitimately take a while. */
  defaultTimeoutMs?: number;
}

/**
 * Low-level transport. One TCP connection, one in-flight queue keyed by id.
 * Not thread-safe — caller owns serialization if it shares an instance.
 */
export class JsonRpcTransport {
  private socket: net.Socket | null = null;
  private buffer: Buffer = Buffer.alloc(0);
  private pending = new Map<number, { resolve: (r: RpcResponse<unknown>) => void; reject: (e: Error) => void; timer: NodeJS.Timeout }>();
  private nextId = 1;
  private readonly defaultTimeoutMs: number;

  constructor(private readonly opts: TransportOptions) {
    this.defaultTimeoutMs = opts.defaultTimeoutMs ?? 30_000;
  }

  connect(): Promise<void> {
    return new Promise((resolve, reject) => {
      const sock = net.createConnection(this.opts.port, this.opts.host ?? "127.0.0.1");
      sock.once("connect", () => {
        this.socket = sock;
        sock.on("data", (chunk) => this.onData(chunk));
        sock.on("error", (err) => this.onSocketError(err));
        sock.on("close", () => this.onSocketClose());
        resolve();
      });
      sock.once("error", reject);
    });
  }

  close(): void {
    if (this.socket) {
      this.socket.end();
      this.socket = null;
    }
    for (const [, p] of this.pending) {
      clearTimeout(p.timer);
      p.reject(new Error("transport closed"));
    }
    this.pending.clear();
  }

  /**
   * Issue a JSON-RPC call. `params` may be omitted for no-arg methods —
   * the server's StreamJsonRpc dispatcher accepts both `[]` and missing.
   */
  call<T = unknown>(method: string, params?: unknown[], timeoutMs?: number): Promise<T> {
    if (!this.socket) {
      return Promise.reject(new Error("transport not connected"));
    }
    const id = this.nextId++;
    const req = params !== undefined
      ? { jsonrpc: "2.0", method, params, id }
      : { jsonrpc: "2.0", method, id };
    const body = JSON.stringify(req);
    const framed = `Content-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`;

    return new Promise<T>((resolve, reject) => {
      const ms = timeoutMs ?? this.defaultTimeoutMs;
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`rpc timeout after ${ms}ms (${method})`));
      }, ms);

      this.pending.set(id, {
        resolve: (r) => {
          if (r.error) {
            reject(new RpcException(r.error));
          } else {
            resolve(r.result as T);
          }
        },
        reject,
        timer,
      });

      this.socket!.write(framed);
    });
  }

  private onData(chunk: Buffer): void {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (true) {
      const headerEnd = this.buffer.indexOf("\r\n\r\n");
      if (headerEnd === -1) return;

      const headerText = this.buffer.subarray(0, headerEnd).toString("ascii");
      const match = /Content-Length: (\d+)/i.exec(headerText);
      if (!match) {
        // Malformed frame; drop everything up to the separator and retry.
        this.buffer = this.buffer.subarray(headerEnd + 4);
        continue;
      }

      const contentLength = parseInt(match[1]!, 10);
      const totalLength = headerEnd + 4 + contentLength;
      if (this.buffer.length < totalLength) return; // need more bytes

      const bodyText = this.buffer.subarray(headerEnd + 4, totalLength).toString("utf8");
      this.buffer = this.buffer.subarray(totalLength);

      let msg: RpcResponse<unknown>;
      try {
        msg = JSON.parse(bodyText);
      } catch {
        continue;
      }

      const handler = this.pending.get(msg.id);
      if (handler) {
        clearTimeout(handler.timer);
        this.pending.delete(msg.id);
        handler.resolve(msg);
      }
    }
  }

  private onSocketError(err: Error): void {
    for (const [, p] of this.pending) {
      clearTimeout(p.timer);
      p.reject(err);
    }
    this.pending.clear();
    this.socket = null;
  }

  private onSocketClose(): void {
    if (this.pending.size > 0) {
      const err = new Error("server closed connection");
      for (const [, p] of this.pending) {
        clearTimeout(p.timer);
        p.reject(err);
      }
      this.pending.clear();
    }
    this.socket = null;
  }
}
