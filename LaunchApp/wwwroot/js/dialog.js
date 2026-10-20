// Native modal dialogs provide focus containment, an inert backdrop and focus restoration.
const allowedCloseMethods = new Set(["CloseSettings", "CloseCredentialPrompt"]);

export function open(dialog, reference, closeMethod = "CloseSettings") {
    if (!dialog || typeof dialog.showModal !== "function")
        throw new TypeError("A native modal dialog is required.");
    if (!reference || typeof reference.invokeMethodAsync !== "function")
        throw new TypeError("A .NET dialog callback is required.");
    if (!allowedCloseMethods.has(closeMethod))
        throw new TypeError("Unsupported dialog close callback.");

    dialog.oncancel = event => {
        event.preventDefault();
        void reference.invokeMethodAsync(closeMethod).catch(() => {
            // Navigation/disposal may invalidate a callback between the keypress and dispatch.
        });
    };
    if (!dialog.open) dialog.showModal();
}

export function close(dialog) {
    if (!dialog) return;
    dialog.oncancel = null;
    if (dialog.open) dialog.close();
}
