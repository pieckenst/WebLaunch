// Never log error objects, URLs, request bodies, stacks or rejected values.
function report(code) {
    console.error(`[WebLaunch] ${code}. Retry the action or reload the page.`);
    const notice = document.getElementById('browser-notice');
    if (notice) notice.hidden = false;
}
window.addEventListener('error', event => {
    if (!(event instanceof ErrorEvent)) return; // An optional image failing is not an app failure.
    event.preventDefault(); report('BROWSER_SCRIPT_ERROR');
});
window.addEventListener('unhandledrejection', event => {
    event.preventDefault(); report('BROWSER_ASYNC_ERROR');
});
