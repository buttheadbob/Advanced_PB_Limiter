const Prefix = "pbl.";

export function load(key) {
    try {
        return localStorage.getItem(Prefix + key);
    } catch {
        return null;
    }
}

export function save(key, value) {
    try {
        localStorage.setItem(Prefix + key, value);
    } catch {
        // Storage can be disabled; the page keeps its in-memory choices.
    }
}

export function remove(key) {
    try {
        localStorage.removeItem(Prefix + key);
    } catch {
        // Nothing to do.
    }
}
