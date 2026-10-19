// Copies the pinned xterm.js distribution files into the TerminalHost asset folder.
// The vendored output is committed so that building ShellFlow does not require Node.js.
import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const modules = join(root, 'node_modules', '@xterm');
const target = resolve(root, '..', 'src', 'ShellFlow.TerminalHost', 'Assets', 'terminal', 'vendor');

const files = [
  ['xterm/lib/xterm.js', 'xterm.js'],
  ['xterm/css/xterm.css', 'xterm.css'],
  ['addon-fit/lib/addon-fit.js', 'addon-fit.js'],
  ['addon-search/lib/addon-search.js', 'addon-search.js'],
  ['addon-unicode11/lib/addon-unicode11.js', 'addon-unicode11.js'],
  ['addon-webgl/lib/addon-webgl.js', 'addon-webgl.js'],
];

mkdirSync(target, { recursive: true });
for (const [from, to] of files) {
  copyFileSync(join(modules, from), join(target, to));
}

const notices = ['xterm', 'addon-fit', 'addon-search', 'addon-unicode11', 'addon-webgl'].map((name) => {
  const pkg = JSON.parse(readFileSync(join(modules, name, 'package.json'), 'utf8'));
  const license = readFileSync(join(modules, name, 'LICENSE'), 'utf8').trim();
  return `${pkg.name} ${pkg.version} (${pkg.license})\n${'-'.repeat(60)}\n${license}\n`;
});
writeFileSync(join(target, 'THIRD-PARTY-NOTICES.txt'), notices.join('\n\n'));
console.log(`Vendored ${files.length} files into ${target}`);
