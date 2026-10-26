const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');

const server = spawn(process.execPath, [path.join(__dirname, 'code-search-mcp.cjs')],
  { stdio: ['pipe', 'pipe', 'inherit'] });
const replies = new Map();
let buffer = '';
server.stdout.setEncoding('utf8');
server.stdout.on('data', chunk => {
  buffer += chunk;
  for (let end; (end = buffer.indexOf('\n')) >= 0;) {
    const line = buffer.slice(0, end);
    buffer = buffer.slice(end + 1);
    const message = JSON.parse(line);
    const resolve = replies.get(message.id);
    if (resolve) { replies.delete(message.id); resolve(message); }
  }
});

let id = 0;
function call(method, params = {}) {
  const next = ++id;
  return new Promise(resolve => {
    replies.set(next, resolve);
    server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: next, method, params }) + '\n');
  });
}

(async () => {
  const init = await call('initialize', { protocolVersion: '2024-11-05' });
  assert.equal(init.result.serverInfo.name, 'lira-code-search');
  const tools = await call('tools/list');
  assert.deepEqual(tools.result.tools.map(tool => tool.name),
    ['semantic_search', 'index_code', 'find_code', 'list_files', 'read_lines']);
  const found = await call('tools/call', { name: 'find_code', arguments: {
    query: 'SplitOverlongZones', path_filter: 'ZoneLayoutEngine.cs', limit: 2 } });
  const hits = JSON.parse(found.result.content[0].text);
  assert(hits.matches.some(hit => hit.file.endsWith('ZoneLayoutEngine.cs')));
  assert(hits.matches.length <= 2);
  const semantic = await call('tools/call', { name: 'semantic_search', arguments: {
    query: 'split reinforcement zone at slab opening and choose bent bar', limit: 3 } });
  const vectors = JSON.parse(semantic.result.content[0].text);
  assert(vectors.matches.length > 0 && vectors.matches.length <= 3);
  assert(vectors.matches[0].score > 0);
  const read = await call('tools/call', { name: 'read_lines', arguments: {
    file: 'src/LiraSlabZones.Core/ZoneLayoutEngine.cs', start_line: 1820, count: 3 } });
  assert.equal(JSON.parse(read.result.content[0].text).lines.length, 3);
  const denied = await call('tools/call', { name: 'read_lines', arguments: {
    file: '../README.md', start_line: 1 } });
  assert(denied.error);
  console.log('PASS MCP initialize, tools, bounded C# search/read, path guard');
  server.stdin.end();
})().catch(error => { console.error(error); server.kill(); process.exitCode = 1; });
