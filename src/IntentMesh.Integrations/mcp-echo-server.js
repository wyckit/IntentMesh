#!/usr/bin/env node
// A real, minimal MCP server over stdio (newline-delimited JSON-RPC 2.0).
// Implements the MCP handshake (initialize / notifications/initialized), tools/list,
// and tools/call. It is intentionally a safe echo server: tools/call records the
// request and echoes it — it performs NO real side effect. The point is that the
// IntentMesh McpProxy gates intent BEFORE a call is ever forwarded here.
//
// Run standalone:  node mcp-echo-server.js   (then speak JSON-RPC on stdin)

'use strict';
const readline = require('readline');
const rl = readline.createInterface({ input: process.stdin, terminal: false });

function send(obj) { process.stdout.write(JSON.stringify(obj) + '\n'); }

const TOOLS = [
  { name: 'echo', description: 'Echo the arguments back.', inputSchema: { type: 'object' } },
  { name: 'send_email', description: 'Send an email (sandboxed echo).', inputSchema: { type: 'object' } },
  { name: 'run_command', description: 'Run a shell command (sandboxed echo).', inputSchema: { type: 'object' } },
  { name: 'read_calendar', description: 'Read the calendar (sandboxed echo).', inputSchema: { type: 'object' } },
];

rl.on('line', (line) => {
  line = line.trim();
  if (!line) return;
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  const { id, method, params } = msg;

  if (method === 'initialize') {
    send({ jsonrpc: '2.0', id, result: {
      protocolVersion: '2024-11-05',
      capabilities: { tools: {} },
      serverInfo: { name: 'mcp-echo-server', version: '1.0.0' },
    } });
  } else if (method === 'notifications/initialized') {
    // notification — no response
  } else if (method === 'tools/list') {
    send({ jsonrpc: '2.0', id, result: { tools: TOOLS } });
  } else if (method === 'tools/call') {
    const name = params && params.name;
    const args = (params && params.arguments) || {};
    if (!TOOLS.some(t => t.name === name)) {
      send({ jsonrpc: '2.0', id, error: { code: -32602, message: `unknown tool: ${name}` } });
      return;
    }
    send({ jsonrpc: '2.0', id, result: {
      content: [{ type: 'text', text: `${name} executed with ${JSON.stringify(args)}` }],
      isError: false,
    } });
  } else if (id !== undefined && id !== null) {
    send({ jsonrpc: '2.0', id, error: { code: -32601, message: `method not found: ${method}` } });
  }
});
