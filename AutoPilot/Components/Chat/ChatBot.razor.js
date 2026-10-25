// Enter submits the composer, Shift+Enter inserts a newline.
// A single delegated listener survives Blazor re-renders.
if (!window.__autopilotComposerKeys) {
    window.__autopilotComposerKeys = true;
    document.addEventListener('keydown', (e) => {
        const target = e.target;
        if (!(target instanceof HTMLTextAreaElement) || !target.matches('.composer textarea')) {
            return;
        }
        if (e.key !== 'Enter' || e.shiftKey || e.isComposing || e.keyCode === 229) {
            return;
        }
        e.preventDefault();
        const form = target.form;
        const button = form && form.querySelector('button[type="submit"]');
        // Use data-blocked rather than button.disabled: the latter depends on the draft
        // and can be stale right after the first keystroke. The server re-validates.
        if (target.value.trim() !== '' && button && button.dataset.blocked !== 'true') {
            form.requestSubmit();
        }
    });

    // Fallback auto-grow for browsers without `field-sizing: content` (e.g. Firefox).
    if (!CSS.supports('field-sizing', 'content')) {
        document.addEventListener('input', (e) => {
            const target = e.target;
            if (!(target instanceof HTMLTextAreaElement) || !target.matches('.composer textarea')) {
                return;
            }
            const style = getComputedStyle(target);
            target.style.height = 'auto';
            target.style.height = (target.scrollHeight
                + parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth)) + 'px';
        });
        // The server clears the draft on submit, which fires no input event.
        document.addEventListener('submit', (e) => {
            const form = e.target;
            if (form instanceof HTMLFormElement && form.matches('.composer')) {
                const textarea = form.querySelector('textarea');
                if (textarea) {
                    textarea.style.height = '';
                }
            }
        });
    }
}
