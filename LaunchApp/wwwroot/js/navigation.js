let observer;
let frame;
let lastLocation;
let receiver;
let reportFrame;
function reportLocation() {
    cancelAnimationFrame(reportFrame);
    reportFrame = requestAnimationFrame(() => receiver?.invokeMethodAsync('BrowserLocationChanged', location.href));
}
function linkClicked(event) {
    if (!event.defaultPrevented && event.button === 0 && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey && event.target.closest('a[href]')) reportLocation();
}
function dismissMenu(event) {
    const menu = event.target.closest('.section-menu');
    if (!menu) return;
    if (event.type === 'click' && event.target.closest('a')) menu.open = false;
    if (event.type === 'keydown' && event.key === 'Escape') { menu.open = false; menu.querySelector('summary').focus(); }
}
export function sync(route, selected, dotnet) {
    receiver = dotnet;
    document.removeEventListener('click', linkClicked, true);
    document.addEventListener('click', linkClicked, true);
    window.removeEventListener('hashchange', reportLocation);
    window.addEventListener('hashchange', reportLocation);
    window.removeEventListener('popstate', reportLocation);
    window.addEventListener('popstate', reportLocation);
    document.removeEventListener('click', dismissMenu);
    document.removeEventListener('keydown', dismissMenu);
    document.addEventListener('click', dismissMenu);
    document.addEventListener('keydown', dismissMenu);
    observer?.disconnect();
    cancelAnimationFrame(frame);
    const header = document.querySelector('.site-header');
    const bar = document.querySelector('.section-bar');
    const measure = () => {
        document.documentElement.style.setProperty('--header-height', `${header?.getBoundingClientRect().height || 0}px`);
        document.documentElement.style.setProperty('--section-bar-height', `${bar?.getBoundingClientRect().height || 0}px`);
    };
    observer = new ResizeObserver(measure);
    if (header) observer.observe(header);
    if (bar) observer.observe(bar);
    measure();
    // Keep the selected item visible in the horizontal strip without scrolling the page.
    const active = bar?.querySelector('[aria-current="location"]');
    if (active) {
        const strip = active.closest('.section-tabs');
        const left = active.offsetLeft - strip.offsetLeft;
        if (left < strip.scrollLeft) strip.scrollLeft = left;
        else if (left + active.offsetWidth > strip.scrollLeft + strip.clientWidth)
            strip.scrollLeft = left + active.offsetWidth - strip.clientWidth;
    }
    // Explicitly wait for the routed content to render. Never navigate to the home route.
    if (lastLocation !== location.href) {
        lastLocation = location.href;
        if (route && location.hash) frame = requestAnimationFrame(() => document.getElementById(selected)?.scrollIntoView({ behavior: 'instant', block: 'start' }));
    }
}
export function dispose() {
    document.removeEventListener('click', linkClicked, true);
    window.removeEventListener('hashchange', reportLocation);
    window.removeEventListener('popstate', reportLocation);
    cancelAnimationFrame(reportFrame);
    receiver = undefined;
    document.removeEventListener('click', dismissMenu);
    document.removeEventListener('keydown', dismissMenu);
    observer?.disconnect();
    cancelAnimationFrame(frame);
    lastLocation = undefined;
}
