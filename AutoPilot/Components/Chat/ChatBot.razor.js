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

// Syntax coloring for the "Review changes" diff. Called from Blazor after a diff has rendered.
// Only highlight.js output (which escapes text) is ever assigned as HTML. Safe to call repeatedly.
if (!window.autopilotHighlightDiffs) {
    const MAX_LINES = 5000;
    const MAX_CHARS = 200000;
    const TOKEN = /<span class="[^"]*">|<\/span>|\n|[^<\n]+/g;

    // Splits highlight.js HTML into per-line HTML, closing open spans at each newline
    // and reopening them on the next line. Text is never unescaped.
    const splitHighlighted = (html) => {
        const lines = [];
        const open = [];
        let current = '';
        for (const token of html.match(TOKEN) || []) {
            if (token === '\n') {
                lines.push(current + '</span>'.repeat(open.length));
                current = open.join('');
            } else if (token === '</span>') {
                open.pop();
                current += token;
            } else if (token.startsWith('<span ')) {
                open.push(token);
                current += token;
            } else {
                current += token;
            }
        }
        lines.push(current + '</span>'.repeat(open.length));
        return lines;
    };

    const highlightSide = (language, texts) => {
        if (texts.length === 0) {
            return [];
        }
        const html = window.hljs.highlight(texts.join('\n'), { language, ignoreIllegals: true }).value;
        const lines = splitHighlighted(html);
        if (lines.length !== texts.length) {
            throw new Error('Highlighted line count mismatch');
        }
        return lines;
    };

    const highlightBlock = (block) => {
        const language = block.dataset.language;
        if (!window.hljs || !language || !window.hljs.getLanguage(language)) {
            return;
        }

        const entries = [];
        let chars = 0;
        for (const line of block.children) {
            if (!line.classList.contains('diff-line')) {
                continue;
            }
            const kind = line.classList.contains('hunk') ? 'hunk'
                : line.classList.contains('added') ? 'added'
                : line.classList.contains('removed') ? 'removed'
                : line.classList.contains('context') ? 'context' : 'meta';
            const span = kind === 'meta' || kind === 'hunk' ? null : line.querySelector(':scope > .diff-text');
            const text = span ? span.textContent : '';
            chars += text.length;
            entries.push({ kind, span, text });
        }
        if (entries.length > MAX_LINES || chars > MAX_CHARS) {
            return;
        }

        try {
            let oldSide = [];
            let newSide = [];
            const flush = () => {
                const oldHtml = highlightSide(language, oldSide.map((e) => e.text));
                const newHtml = highlightSide(language, newSide.map((e) => e.text));
                oldSide.forEach((e, i) => { if (e.kind === 'removed') { e.span.innerHTML = oldHtml[i]; } });
                newSide.forEach((e, i) => { e.span.innerHTML = newHtml[i]; });
                oldSide = [];
                newSide = [];
            };
            for (const entry of entries) {
                if (entry.kind === 'hunk') {
                    flush();
                } else if (entry.kind === 'context') {
                    oldSide.push(entry);
                    newSide.push(entry);
                } else if (entry.kind === 'removed') {
                    oldSide.push(entry);
                } else if (entry.kind === 'added') {
                    newSide.push(entry);
                }
            }
            flush();
        } catch (e) {
            for (const entry of entries) {
                if (entry.span) {
                    entry.span.textContent = entry.text;
                }
            }
        }
    };

    window.autopilotHighlightDiffs = function () {
        for (const block of document.querySelectorAll('.diff-code[data-language]:not([data-highlighted])')) {
            block.setAttribute('data-highlighted', 'true');
            try {
                highlightBlock(block);
            } catch (e) {
                // Leave this block as plain text.
            }
        }
    };
}
