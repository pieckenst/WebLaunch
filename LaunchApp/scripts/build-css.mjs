import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const assets = JSON.parse(fs.readFileSync(path.join(root, 'obj/project.assets.json'), 'utf8'));
const library = Object.entries(assets.libraries).find(([name]) => name.startsWith('LumexUI/'))?.[1];
const packagePath = library && Object.keys(assets.packageFolders)
    .map(folder => path.join(folder, library.path)).find(folder => fs.existsSync(path.join(folder, 'theme/theme.css')));
if (!packagePath) throw new Error('Restore LaunchApp packages before building Lumex styles.');
// Stage package-owned inputs beneath obj so CSS imports work across Windows drives too.
fs.cpSync(path.join(packagePath, 'theme'), path.join(root, 'obj/lumex-theme'), { recursive: true });
const input = path.join(root, 'obj/lumex-input.css');
fs.writeFileSync(input, `@layer theme, base, components, utilities;
@import "tailwindcss/theme.css" layer(theme);
@import "./lumex-theme/theme.css";
@import "tailwindcss/utilities.css" layer(utilities);
@import "../wwwroot/css/input.css";
@source "../Pages";
@source "../Shared";
@source "./lumex-theme/components";
`);
const result = spawnSync(process.execPath, [path.join(root, 'node_modules/@tailwindcss/cli/dist/index.mjs'), '-i', input,
    '-o', path.join(root, 'wwwroot/css/lumex.generated.css'), '--minify'], { cwd: root, stdio: 'inherit' });
if (result.error) throw result.error;
process.exit(result.status ?? 1);
