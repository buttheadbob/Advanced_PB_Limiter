// Keeps the keyboard inside an open dialog and gives focus back where it was when the dialog closes.
const open = new Map(); // dialog id -> { panel, previous, onKey }

const focusable = "a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])";

export function trap(id, panel) {
    if (!panel || open.has(id)) return;
    const previous = document.activeElement;
    const onKey = (e) => {
        if (e.key !== "Tab") return;
        const items = [...panel.querySelectorAll(focusable)].filter((el) => el.offsetParent !== null);
        if (items.length === 0) {
            e.preventDefault();
            panel.focus();
            return;
        }
        const first = items[0];
        const last = items[items.length - 1];
        if (e.shiftKey && (document.activeElement === first || document.activeElement === panel)) {
            e.preventDefault();
            last.focus();
        } else if (!e.shiftKey && document.activeElement === last) {
            e.preventDefault();
            first.focus();
        }
    };
    panel.addEventListener("keydown", onKey);
    open.set(id, { panel, previous, onKey });
    (panel.querySelector("[data-autofocus]") || panel).focus();
}

// Focus goes back only when no other dialog has opened meanwhile: one dialog can open another as it closes.
export function release(id) {
    const entry = open.get(id);
    if (!entry) return;
    open.delete(id);
    entry.panel.removeEventListener("keydown", entry.onKey);
    if (open.size === 0 && entry.previous && document.contains(entry.previous)) entry.previous.focus();
}
