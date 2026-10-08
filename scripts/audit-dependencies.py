"""Fail CI when the NuGet audit reports vulnerable dependencies (including transitive ones)."""
import json, subprocess, sys
result = subprocess.run(['dotnet', 'list', 'handlerlaunch.sln', 'package', '--vulnerable', '--include-transitive', '--format', 'json'], capture_output=True, text=True)
if result.returncode:
    print(result.stderr); sys.exit(result.returncode)
data = json.loads(result.stdout)
if data.get('problems') or not data.get('projects'):
    print('Dependency audit did not return a complete project result.'); sys.exit(1)
findings = []
for project in data.get('projects', []):
    for framework in project.get('frameworks', []):
        for group in ('topLevelPackages', 'transitivePackages'):
            for package in framework.get(group, []):
                if package.get('vulnerabilities'): findings.append((project['path'], package['id'], package['resolvedVersion']))
for finding in findings: print(*finding)
if findings: sys.exit(1)
print('No known vulnerable NuGet dependencies reported.')
