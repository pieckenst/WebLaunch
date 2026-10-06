# Windows release acceptance

These checks require a Windows desktop. Linux cross-compilation and the synthetic
browser tests do not establish WPF, registry, native rendering, or real-game readiness.
Keep the PR draft until the release owner has these results.

- Download the CI bundle into a path containing spaces. Verify `WMconsole.exe`,
  both plugin manifests/assemblies, .NET runtimes, native dependencies and notices.
- Install the URL handler without elevation. Verify registry command quoting and
  that a second protocol invocation focuses the existing per-user host.
- Run `--console`: verify text-only pairing, redirected output and Ctrl+C cleanup.
  Pair once, restart with `--console --quiet`, and verify no WPF window/output,
  successful reconnect for the trusted browser and rejection of a new browser.
- Pair Chrome and Edge independently. Compare both codes. Reject a mismatch, let a
  prompt expire, revoke browsers and verify old sessions cannot launch.
- Deny browser local-network permission, retry after allowing, occupy the bridge
  port, and test outdated/missing handlers. No credential-URL fallback may occur.
- Use synthetic credentials first. Test optional OTP, invalid folder, failed login,
  cancellation and retry. Inspect browser/desktop logs for accidental secrets.
- With authorized game accounts/installations, verify FFXIV non-Steam and Steam
  launches, Dalamud compatibility/update/bootstrap, configured addon startup and
  cleanup on game exit. Launch acknowledgement must not wait for game exit.
- Close the browser after launch: game/addon monitoring remains in the desktop.
  Close the desktop: addon sessions clean up without terminating the game.
- Exercise Spellborn install/update in a disposable folder. Cancel downloads and
  interrupt extraction/commit; retry must recover without advancing an incomplete
  version. Test a directory without write permission.
- Open/close the optional renderer and verify it stops without killing the host.
  Test actual legacy plugin binaries used by integrators, including their private
  dependencies and any plugin-specific rendering.
- Verify legacy URLs are rejected by default, accepted only after explicit native
  opt-in, never logged, and rejected again after opt-in expires.

Real-account credentials must not be included in test fixtures, screenshots,
workflow artifacts or PR descriptions. Release the handler before deploying the
website. Pages deployment is manual and restricted to main.

The automated legacy fixture implements the original plugin API against the
current contract assembly. It does not replace testing consumers compiled against
older releases. Likewise, synthetic browser tests do not validate Square Enix,
Steam or Spellborn service availability.
