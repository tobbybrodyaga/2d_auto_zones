const fs = require('node:fs');
const path = require('node:path');
const vector = require('./vector-index.cjs');

const root = path.resolve(__dirname, '..');
const extensions = new Set(['.cs', '.xaml', '.ps1', '.md', '.csproj', '.sln', '.json', '.cfg']);
const skipped = new Set(['.git', '.codex', '.cursor', 'bin', 'obj', 'dist', 'node_modules', '.ragcode']);
const maxFileBytes = 1024 * 1024;

function clamp(value, fallback, limit) {
  const n = Number(value);
  return Number.isFinite(n) ? Math.max(1, Math.min(limit, Math.floor(n))) : fallback;
}

function filesIn(dir = root) {
  const result = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (!skipped.has(entry.name)) result.push(...filesIn(path.join(dir, entry.name)));
    } else if (entry.isFile() && extensions.has(path.extname(entry.name).toLowerCase())) {
      const file = path.join(dir, entry.name);
      if (fs.statSync(file).size <= maxFileBytes) result.push(file);
    }
  }
  return result;
}

function relative(file) {
  return path.relative(root, file).replaceAll('\\', '/');
}

function safeFile(input) {
  const file = path.resolve(root, String(input || ''));
  if (!file.startsWith(root + path.sep) || !fs.statSync(file).isFile())
    throw new Error('File must be inside this project.');
  if (relative(file).split('/').some(part => skipped.has(part)))
    throw new Error('Generated files are excluded.');
  return file;
}

function findCode({ query, limit, path_filter }) {
  const words = String(query || '').toLowerCase().match(/[\p{L}\p{N}_]+/gu) || [];
  if (!words.length) throw new Error('query is required');
  const filter = String(path_filter || '').toLowerCase().replaceAll('\\', '/');
  const hits = [];
  for (const file of filesIn()) {
    const name = relative(file);
    if (filter && !name.toLowerCase().includes(filter)) continue;
    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i].toLowerCase();
      const matched = words.filter(word => line.includes(word));
      if (!matched.length) continue;
      const score = matched.length * 10 + (matched.length === words.length ? 10 : 0) +
        (words.some(word => name.toLowerCase().includes(word)) ? 2 : 0);
      hits.push({ file: name, line: i + 1, score, text: lines[i].trim().slice(0, 220) });
    }
  }
  hits.sort((a, b) => b.score - a.score || a.file.localeCompare(b.file) || a.line - b.line);
  return { matches: hits.slice(0, clamp(limit, 8, 20)), total: hits.length };
}

function listFiles({ pattern, limit }) {
  const needle = String(pattern || '').toLowerCase().replaceAll('\\', '/');
  const found = filesIn().map(relative).filter(name => name.toLowerCase().includes(needle));
  return { files: found.slice(0, clamp(limit, 30, 100)), total: found.length };
}

function readLines({ file, start_line, count }) {
  const target = safeFile(file);
  const lines = fs.readFileSync(target, 'utf8').split(/\r?\n/);
  const start = clamp(start_line, 1, lines.length);
  const take = clamp(count, 16, 60);
  return { file: relative(target), start_line: start, total_lines: lines.length,
    lines: lines.slice(start - 1, start - 1 + take).map((text, i) =>
      `${start + i}: ${text.slice(0, 400)}`) };
}

const definitions = [
  { name: 'semantic_search', description: 'Vector semantic search over indexed C#, XAML, PowerShell and project files using local Ollama and Qdrant. Returns short excerpts. Use before lexical find_code for conceptual queries.',
    inputSchema: { type: 'object', properties: { query: { type: 'string' }, path_filter: { type: 'string' }, limit: { type: 'integer' } }, required: ['query'] } },
  { name: 'index_code', description: 'Incrementally embed changed source files with local Ollama and store vectors in Qdrant. Run on first setup or after substantial code changes.',
    inputSchema: { type: 'object', properties: {} } },
  { name: 'find_code', description: 'Find exact terms in project source and return ranked, short line excerpts. Not semantic search. Use path_filter and small limit to save tokens.',
    inputSchema: { type: 'object', properties: { query: { type: 'string' }, path_filter: { type: 'string' }, limit: { type: 'integer' } }, required: ['query'] } },
  { name: 'list_files', description: 'List matching project source paths, excluding generated files.',
    inputSchema: { type: 'object', properties: { pattern: { type: 'string' }, limit: { type: 'integer' } } } },
  { name: 'read_lines', description: 'Read only a bounded line range after finding a source location.',
    inputSchema: { type: 'object', properties: { file: { type: 'string' }, start_line: { type: 'integer' }, count: { type: 'integer' } }, required: ['file', 'start_line'] } }
];

function response(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

async function handle(message) {
  if (!Object.hasOwn(message, 'id')) return;
  try {
    let result;
    if (message.method === 'initialize') {
      result = { protocolVersion: message.params?.protocolVersion || '2024-11-05',
        capabilities: { tools: {} }, serverInfo: { name: 'lira-code-search', version: '1.0.0' },
        instructions: 'Use semantic_search for conceptual C#/XAML/PowerShell questions, then read_lines. Use find_code for exact identifiers. Run index_code when the index is missing or stale.' };
    } else if (message.method === 'tools/list') {
      result = { tools: definitions };
    } else if (message.method === 'tools/call') {
      const args = message.params?.arguments || {};
      const name = message.params?.name;
      const value = name === 'semantic_search' ? await vector.semanticSearch(args) :
        name === 'index_code' ? await vector.indexCode() :
        name === 'find_code' ? findCode(args) :
        name === 'list_files' ? listFiles(args) :
        name === 'read_lines' ? readLines(args) : null;
      if (value === null) throw new Error('Unknown tool');
      result = { content: [{ type: 'text', text: JSON.stringify(value) }] };
    } else {
      response({ jsonrpc: '2.0', id: message.id, error: { code: -32601, message: 'Method not found' } });
      return;
    }
    response({ jsonrpc: '2.0', id: message.id, result });
  } catch (error) {
    response({ jsonrpc: '2.0', id: message.id, error: { code: -32602, message: error.message } });
  }
}

let pending = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => {
  pending += chunk;
  for (let end; (end = pending.indexOf('\n')) >= 0;) {
    const line = pending.slice(0, end).trim();
    pending = pending.slice(end + 1);
    if (!line) continue;
    try { handle(JSON.parse(line)); }
    catch (error) { process.stderr.write(`Invalid MCP input: ${error.message}\n`); }
  }
});
