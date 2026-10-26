const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const root = path.resolve(__dirname, '..');
const collection = 'lira-slab-zones-code-768';
const statePath = path.join(root, '.ragcode', 'vector-state.json');
const ollama = process.env.LIRA_OLLAMA_URL || 'http://localhost:11434';
const qdrant = process.env.LIRA_QDRANT_URL || 'http://localhost:6333';
const model = process.env.LIRA_EMBED_MODEL || 'nomic-embed-text';
const extensions = new Set(['.cs', '.xaml', '.ps1', '.csproj']);
const skipped = new Set(['.git', '.codex', '.cursor', 'bin', 'obj', 'dist', 'node_modules', '.ragcode']);

function walk(dir = root) {
  const result = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory() && !skipped.has(entry.name))
      result.push(...walk(path.join(dir, entry.name)));
    else if (entry.isFile() && extensions.has(path.extname(entry.name).toLowerCase())) {
      const file = path.join(dir, entry.name);
      if (fs.statSync(file).size < 1024 * 1024) result.push(file);
    }
  }
  return result;
}

function relative(file) { return path.relative(root, file).replaceAll('\\', '/'); }
function hash(value) { return crypto.createHash('sha256').update(value).digest('hex'); }
function pointId(value) {
  const h = hash(value);
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-a${h.slice(17, 20)}-${h.slice(20, 32)}`;
}

async function request(url, method = 'GET', body) {
  const response = await fetch(url, { method, headers: { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(120000) });
  const value = await response.json();
  if (!response.ok || value.status === 'error')
    throw new Error(`${url}: ${JSON.stringify(value).slice(0, 500)}`);
  return value;
}

async function embed(texts) {
  const value = await request(`${ollama}/api/embed`, 'POST', { model, input: texts });
  if (!Array.isArray(value.embeddings) || value.embeddings.length !== texts.length)
    throw new Error('Ollama returned an unexpected number of embeddings.');
  return value.embeddings;
}

async function ensureCollection() {
  const url = `${qdrant}/collections/${collection}`;
  const existing = await fetch(url, { signal: AbortSignal.timeout(5000) });
  if (existing.status === 404) {
    await request(url, 'PUT', { vectors: { size: 768, distance: 'Cosine' } });
  } else if (!existing.ok) {
    throw new Error(`Qdrant collection check failed: ${existing.status}`);
  } else {
    const info = await existing.json();
    if (info.result.config.params.vectors.size !== 768)
      throw new Error('Existing Qdrant collection has an incompatible vector size.');
  }
}

function chunks(file, content) {
  const lines = content.split(/\r?\n/);
  const result = [];
  const width = 48;
  const stride = 40;
  for (let start = 0; start < lines.length; start += stride) {
    const section = lines.slice(start, start + width);
    if (section.join('').trim().length < 20) continue;
    const first = start + 1;
    const last = start + section.length;
    result.push({ file, first, last, text: `File: ${file}\nLines: ${first}-${last}\n${section.join('\n')}` });
  }
  return result;
}

async function removeFile(file) {
  await request(`${qdrant}/collections/${collection}/points/delete?wait=true`, 'POST', {
    filter: { must: [{ key: 'file', match: { value: file } }] }
  });
}

async function indexCode(progress = () => {}) {
  await ensureCollection();
  fs.mkdirSync(path.dirname(statePath), { recursive: true });
  let state = {};
  if (fs.existsSync(statePath)) state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
  const files = walk();
  const seen = new Set(files.map(relative));
  let changed = 0;
  let indexedChunks = 0;
  for (const file of files) {
    const name = relative(file);
    const content = fs.readFileSync(file, 'utf8');
    const fingerprint = hash(content);
    if (state[name] === fingerprint) continue;
    const parts = chunks(name, content);
    const points = [];
    for (let i = 0; i < parts.length; i += 8) {
      const batch = parts.slice(i, i + 8);
      const vectors = await embed(batch.map(part => part.text));
      points.push(...batch.map((part, j) => ({
        id: pointId(`${name}:${part.first}`), vector: vectors[j],
        payload: { file: name, start_line: part.first, end_line: part.last, text: part.text }
      })));
    }
    await removeFile(name);
    for (let i = 0; i < points.length; i += 32)
      await request(`${qdrant}/collections/${collection}/points?wait=true`, 'PUT',
        { points: points.slice(i, i + 32) });
    state[name] = fingerprint;
    fs.writeFileSync(statePath, JSON.stringify(state, null, 2));
    changed++;
    indexedChunks += points.length;
    progress(`${name}: ${points.length} chunks`);
  }
  for (const name of Object.keys(state)) {
    if (seen.has(name)) continue;
    await removeFile(name);
    delete state[name];
    changed++;
  }
  fs.writeFileSync(statePath, JSON.stringify(state, null, 2));
  return { files: files.length, changed, indexed_chunks: indexedChunks, collection, model };
}

async function semanticSearch({ query, limit = 5, path_filter = '' }) {
  const prompt = String(query || '').trim();
  if (!prompt) throw new Error('query is required');
  await ensureCollection();
  const vocabulary = [
    [/зон/iu, 'zone'], [/армир|арматур/iu, 'reinforcement rebar'],
    [/отверст/iu, 'opening hole'], [/плит/iu, 'slab'],
    [/раздел|разрез/iu, 'split'], [/пересеч|налож/iu, 'intersect overlap'],
    [/гнут/iu, 'bent bar family'], [/сдвиг|смещ/iu, 'move shift'],
    [/подрез|обрез/iu, 'clip trim'], [/нахлест|нахлёст/iu, 'lap splice'],
    [/длин/iu, 'length'], [/шаг/iu, 'spacing step']
  ];
  const translated = vocabulary.filter(([pattern]) => pattern.test(prompt))
    .map(([, english]) => english).join(' ');
  const [vector] = await embed([translated ? `${prompt}\n${translated}` : prompt]);
  const filter = String(path_filter || '').replaceAll('\\', '/');
  const take = Math.max(1, Math.min(12, Number(limit) || 5));
  const body = { query: vector, limit: 80,
    with_payload: true, with_vector: false };
  const result = await request(`${qdrant}/collections/${collection}/points/query`, 'POST', body);
  const terms = `${prompt} ${translated}`.toLowerCase().match(/[\p{L}\p{N}_]{4,}/gu) || [];
  const ranked = result.result.points.map(point => {
    const haystack = `${point.payload.file} ${point.payload.text}`.toLowerCase();
    const lexical = terms.filter(term => haystack.includes(term) ||
      (term.length > 6 && haystack.includes(term.slice(0, -2)))).length;
    return { point, rank: point.score + Math.min(0.18, lexical * 0.045) };
  });
  ranked.sort((a, b) => b.rank - a.rank);
  return { matches: ranked.map(item => item.point)
    .filter(point => !filter || point.payload.file.toLowerCase().includes(filter.toLowerCase()))
    .slice(0, take).map(point => ({
    file: point.payload.file, start_line: point.payload.start_line,
    end_line: point.payload.end_line, score: Number(point.score.toFixed(3)),
    excerpt: point.payload.text.split('\n').slice(2, 12).join('\n').slice(0, 850)
  })), collection, model };
}

module.exports = { indexCode, semanticSearch };
if (require.main === module) {
  indexCode(line => console.error(line)).then(result => console.log(JSON.stringify(result)))
    .catch(error => { console.error(error); process.exitCode = 1; });
}
