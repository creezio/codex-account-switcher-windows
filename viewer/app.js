import { App } from '@modelcontextprotocol/ext-apps';
import { marked } from 'marked';
import DOMPurify from 'dompurify';

const app = new App({ name: 'Account Switcher shared Pages', version: '0.12.0' }, { availableDisplayModes: ['fullscreen'] });
const $ = id => document.getElementById(id);
let current, session, busy = false, editing = null, pending = null, uncertain = false;
const status = (text, error = false) => { $('status').textContent = text; $('status').className = error ? 'error' : ''; };
const dirty = () => editing !== null && $('draft').value !== editing.original;
function controls() {
  for (const b of document.querySelectorAll('button')) b.disabled = busy || !current || !session;
  $('append').disabled ||= !current?.CanEdit || editing !== null;
  $('refresh').disabled ||= dirty();
  $('save').disabled ||= !current?.CanEdit || uncertain || !dirty() || !$('draft').value.trim();
  document.querySelectorAll('[data-edit]').forEach(b => b.disabled ||= !current?.CanEdit || editing !== null);
  $('draft').disabled = busy;
}
function markup(text) {
  // No remote images, frames, styles or active HTML. Private references never leave the tunnel.
  const clean = DOMPurify.sanitize(marked.parse(text), { ALLOWED_TAGS: ['p','h1','h2','h3','h4','h5','h6','strong','em','del','code','pre','blockquote','ul','ol','li','hr','br','table','thead','tbody','tr','th','td','a'], ALLOWED_ATTR: ['href','title'], ALLOW_DATA_ATTR: false });
  const host = document.createElement('div'); host.innerHTML = clean;
  host.querySelectorAll('a').forEach(a => { a.removeAttribute('href'); a.title = 'Ouvrir cette référence dans l’instance propriétaire'; });
  return host;
}
function render(value) {
  current = value;
  $('title').textContent = value.Title || 'Page sans titre';
  $('owner').textContent = `Via ${value.Owner} · ${value.CanEdit ? 'Lecture et modification' : 'Lecture seule'}`;
  $('updated').textContent = `Contenu lu le ${new Date(value.ReadAt).toLocaleString('fr-FR')}`;
  $('blocks').replaceChildren();
  for (const [index, block] of value.Blocks.entries()) {
    const card = document.createElement('article'); card.className = 'block';
    const bar = document.createElement('div'); bar.className = 'bar';
    const label = document.createElement('span'); label.textContent = block.Kind === 'agent_instructions' ? 'Instructions de la Page · lecture seule' : `Bloc ${index + 1}`; bar.append(label);
    if (value.CanEdit && block.Kind === 'markdown') {
      const edit = document.createElement('button'); edit.textContent = 'Modifier'; edit.dataset.edit = block.Id; edit.setAttribute('aria-label', `Modifier le bloc ${index + 1}`); edit.onclick = () => begin(block); bar.append(edit);
    }
    card.append(bar, markup(block.Markdown));
    if (/!\[|project-file:|library-file:|visualize:/.test(block.Markdown)) { const note = document.createElement('p'); note.className = 'reference'; note.textContent = 'Média ou pièce jointe : disponible dans l’instance propriétaire.'; card.append(note); }
    $('blocks').append(card);
  }
  if (!value.Blocks.length) $('blocks').textContent = 'Cette Page est vide.';
  controls();
}
function begin(block = null) {
  if (busy || editing !== null || !current?.CanEdit) return;
  editing = { id: block?.Id || '', original: block?.Markdown || '', readId: current.ReadId };
  pending = null; uncertain = false; $('editor').hidden = false; $('draft').value = editing.original;
  $('edit-label').textContent = block ? 'Modifier ce bloc' : 'Ajouter du texte en fin de Page'; $('draft-note').textContent = '';
  controls(); $('editor').scrollIntoView({ block: 'nearest' }); $('draft').focus();
}
function closeEditor() { editing = null; pending = null; uncertain = false; $('editor').hidden = true; $('draft').value = ''; controls(); }
async function call(name, extra = {}) {
  const result = await app.callServerTool({ name, arguments: { session, share: current.Share, page: current.Page, ...extra } });
  if (result.isError) throw new Error(result.content?.find(c => c.type === 'text')?.text || 'Opération refusée');
  return result.structuredContent || JSON.parse(result.content.find(c => c.type === 'text').text);
}
async function run(action, writing = false) {
  if (busy || !current || !session) return;
  busy = true; controls();
  try { await action(); } catch (e) {
    if (writing) { uncertain = true; $('draft-note').textContent = `Résultat incertain · référence ${pending?.id}. Brouillon conservé. Consultez la Page chez le propriétaire avant toute nouvelle écriture.`; }
    status(e.message || 'Le tunnel ne répond pas. Réessayez la lecture.', true);
  } finally { busy = false; controls(); }
}
$('refresh').onclick = () => run(async () => { if (dirty()) return; status('Lecture de la Page originale…'); render(await call('read_shared_page')); status('Vue actualisée.'); });
$('native').onclick = () => run(async () => { status('Ouverture dans l’instance propriétaire…'); const result = await call('open_shared_page_owner'); status(`Ouverture demandée dans ${result.instance}.`); });
$('append').onclick = () => begin();
$('draft').oninput = controls;
$('cancel').onclick = () => { if (!dirty() || window.confirm('Abandonner le brouillon non enregistré ?')) closeEditor(); };
$('save').onclick = () => run(async () => {
  if (!editing || uncertain || !dirty()) return;
  if (new TextEncoder().encode($('draft').value).length > 80000) throw new Error('Texte limité à 80 Ko par enregistrement.');
  pending ??= { id: crypto.randomUUID().replaceAll('-', ''), readId: editing.readId, block: editing.id, markdown: $('draft').value };
  status('Enregistrement sur la Page originale…');
  const result = await call('save_shared_page_block', pending);
  if (result.State === 'saved' || result.State === 'saved_unverified') { closeEditor(); if (result.Document) render(result.Document); else { current.CanEdit = false; } status(result.Message, result.State !== 'saved'); }
  else { uncertain = true; $('draft-note').textContent = 'Brouillon conservé. Comparez-le avec la Page originale avant de reprendre ; aucun renvoi automatique.'; status(result.Message, true); }
}, true);
window.addEventListener('beforeunload', e => { if (dirty() || busy) { e.preventDefault(); e.returnValue = ''; } });
app.ontoolinput = ({ arguments: args }) => { session = args?.session || session; controls(); };
app.ontoolresult = result => {
  if (editing !== null) { status('Une nouvelle vue est disponible. Terminez ou annulez votre brouillon avant d’actualiser.'); return; }
  session = result._meta?.viewerSession || session;
  if (result.isError) { status('Impossible de lire cette Page. Vérifiez les accès dans le Switcher.', true); return; }
  const value = result.structuredContent;
  if (value?.Blocks) { render(value); status('Page originale chargée via le tunnel.'); }
};
app.onhostcontextchanged = ctx => { if (ctx.theme) document.documentElement.style.colorScheme = ctx.theme; };
app.connect().then(async () => { const ctx = app.getHostContext(); if (ctx?.availableDisplayModes?.includes('fullscreen') && ctx.displayMode !== 'fullscreen') await app.requestDisplayMode({ mode: 'fullscreen' }); }).catch(() => status('Ouvrez cette vue depuis le plugin Creezio Relay dans Codex, ou consultez la Page dans Account Switcher.', true));
