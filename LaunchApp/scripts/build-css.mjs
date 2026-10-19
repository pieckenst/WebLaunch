 
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const root = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    '..'
);

// Resolve LumexUI from this project's NuGet restore graph. Selecting the newest
// globally cached version can silently compile different styles on different machines.
const assetsPath = path.join(root, 'obj', 'project.assets.json');
if (!fs.existsSync(assetsPath)) {
    throw new Error(
        `NuGet restore output was not found:\n${ assetsPath }\n\n` +
        'Run "dotnet restore LaunchApp/LaunchApp.csproj" before building CSS.'
    );
}

const assets = JSON.parse(fs.readFileSync(assetsPath, 'utf8'));
const libraryKey = Object.keys(assets.libraries || {}).find(key =>
    key.toLowerCase().startsWith('lumexui/')
);

if (!libraryKey) {
    throw new Error(
        `LumexUI was not found in this project's restored package graph:\n${ assetsPath }\n\n` +
        'Verify the LaunchApp project references LumexUI and run "dotnet restore".'
    );
}

const packageVersion = libraryKey.slice(libraryKey.indexOf('/') + 1);
const packageRelativePath = path.join('lumexui', packageVersion);
const packageFolders = Object.keys(assets.packageFolders || {});
const packagePath = packageFolders
    .map(folder => path.join(folder, packageRelativePath))
    .find(folder => fs.existsSync(path.join(folder, 'theme', 'theme.css')));

if (!packagePath) {
    throw new Error(
        `The restored LumexUI package (${ libraryKey }) or its theme/theme.css was not found in the package folders listed by ${ assetsPath }.`
    );
}

// Stage package-owned inputs beneath obj so CSS imports work reliably.
const stagedTheme = path.join(root, 'obj', 'lumex-theme');

fs.rmSync(stagedTheme, {
    recursive: true,
    force: true
});

fs.cpSync(
    path.join(packagePath, 'theme'),
    stagedTheme,
    { recursive: true }
);

const input = path.join(root, 'obj', 'lumex-input.css');

fs.writeFileSync(
    input,
    `@layer theme, base, components, utilities;
@import "tailwindcss/theme.css" layer(theme);
@import "./lumex-theme/theme.css";
@import "tailwindcss/utilities.css" layer(utilities);
@import "../wwwroot/css/input.css";
@source "../Pages";
@source "../Shared";
@source "./lumex-theme/components";
`,
    'utf8'
);

const tailwindCli = path.join(
    root,
    'node_modules',
    '@tailwindcss',
    'cli',
    'dist',
    'index.mjs'
);

if (!fs.existsSync(tailwindCli)) {
    throw new Error(
        `Tailwind CLI was not found:\n${ tailwindCli }\n\n` +
        'Run "npm install" before building CSS.'
    );
}

const result = spawnSync(
    process.execPath,
    [
        tailwindCli,
        '-i',
        input,
        '-o',
        path.join(root, 'wwwroot', 'css', 'lumex.generated.css'),
        '--minify'
    ],
    {
        cwd: root,
        stdio: 'inherit'
    }
);

if (result.error) {
    throw result.error;
}

process.exit(result.status ?? 1);
 