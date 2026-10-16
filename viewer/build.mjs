import { build } from 'esbuild';
import { readFile, writeFile, mkdir, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('../', import.meta.url));
const result = await build({ entryPoints: [root + 'viewer/app.js'], bundle: true, write: false, minify: true, format: 'iife', target: 'es2022' });
const shell = await readFile(root + 'viewer/shell.html', 'utf8');
await mkdir(root + 'plugins/creezio-relay/ui', { recursive: true });
await writeFile(root + 'plugins/creezio-relay/ui/viewer.html', shell.replace('/* BUNDLE */', () => result.outputFiles[0].text.replace(/<\/script/gi, '<\\/script')));
console.log('Bundled self-contained shared Page viewer');
let notices = 'Third-party software included in the shared Page viewer.\n\n';
// Include the license of each dependency shipped in the browser bundle.
for (const name of ['@modelcontextprotocol/ext-apps','@modelcontextprotocol/sdk','zod','zod-to-json-schema','marked','dompurify']) {
  const dir=root+'viewer/node_modules/'+name;
  try {
    const pkg=JSON.parse(await readFile(dir+'/package.json','utf8'));
    notices += `${name} ${pkg.version} — ${pkg.license}\n`;
    for (const file of (await readdir(dir)).filter(f=>/^licen[cs]e(?:\.|$)/i.test(f))) notices += await readFile(dir+'/'+file,'utf8')+'\n';
    notices += '\n';
  } catch (e) { if(e.code!=='ENOENT')throw e; }
}
await writeFile(root+'plugins/creezio-relay/ui/THIRD-PARTY-NOTICES.txt',notices.trimEnd()+'\n');
