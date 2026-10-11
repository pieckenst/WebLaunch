 
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const root = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    '..'
);

// NuGet global-packages folder.
// Respect NUGET_PACKAGES when explicitly configured.
const nugetRoot =
    process.env.NUGET_PACKAGES ||
    path.join(
        process.env.USERPROFILE || process.env.HOME || '',
        '.nuget',
        'packages'
    );

const lumexRoot = path.join(nugetRoot, 'lumexui');

if (!fs.existsSync(lumexRoot)) {
    throw new Error(
        `LumexUI package was not found in the NuGet global package cache:\n${ lumexRoot }\n\n` +
        'Run "dotnet restore" and verify that the LaunchApp project references the LumexUI package.'
    );
}

// Find the newest installed LumexUI version containing theme/theme.css.
const versions = fs
    .readdirSync(lumexRoot, { withFileTypes: true })
    .filter(entry => entry.isDirectory())
    .map(entry => entry.name)
    .sort((a, b) =>
        a.localeCompare(b, undefined, {
            numeric: true,
            sensitivity: 'base'
        })
    )
    .reverse();

const packagePath = versions
    .map(version => path.join(lumexRoot, version))
    .find(folder =>
        fs.existsSync(path.join(folder, 'theme', 'theme.css'))
    );

if (!packagePath) {
    throw new Error(
        `LumexUI is installed in the NuGet cache, but no version containing ` +
        `theme / theme.css was found:\n${ lumexRoot }`
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
 