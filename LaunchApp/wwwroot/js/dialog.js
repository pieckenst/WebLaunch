// Native modal dialogs provide focus containment, an inert backdrop and focus restoration.
export function open(dialog, reference) {
    dialog.oncancel = event => {
        event.preventDefault();
        reference.invokeMethodAsync('CloseSettings');
    };
    if (!dialog.open) dialog.showModal();
}
export function close(dialog) {
    dialog.oncancel = null;
    if (dialog.open) dialog.close();
}
