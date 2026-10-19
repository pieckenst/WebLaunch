
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
        `LumexUI package was not found in the NuGet global package cache:\n${lumexRoot}\n\n` +
        'Run "dotnet restore" and verify that the LaunchApp project references the LumexUI package.'
    );
}

/* Find the newest installed LumexUI version containing theme/theme.css. - NUGET RESTORED PACKAGE GRAPH ALWAYS CAUSES BUILD TO  FAIL WITH 

------ Сборка начата: проект: LaunchApp, Конфигурация: Debug Any CPU ------
1>
1>  > build:css
1>  > node scripts/build-css.mjs
1>
1>  file:///E:/moredownloads/october2026/WebLaunch/LaunchApp/scripts/build-css.mjs:28
1>      throw new Error(
1>            ^
1>
1>EXEC : error : LumexUI was not found in this project's restored package graph:
1>  E:\moredownloads\october2026\WebLaunch\LaunchApp\obj\project.assets.json
1>
1>  Verify the LaunchApp project references LumexUI and run "dotnet restore".
1>      at file:///E:/moredownloads/october2026/WebLaunch/LaunchApp/scripts/build-css.mjs:28:11
1>      at ModuleJob.run (node:internal/modules/esm/module_job:430:25)
1>      at async node:internal/modules/esm/loader:639:26
1>      at async asyncRunEntryPointWithESMLoader (node:internal/modules/run_main:101:5)
1>
1>  Node.js v25.8.1
SO WE FIND GLOBALLY BECAUSE OTHERWISE BUILD DIES
*/
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
        `theme / theme.css was found:\n${lumexRoot}`
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
        `Tailwind CLI was not found:\n${tailwindCli}\n\n` +
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
