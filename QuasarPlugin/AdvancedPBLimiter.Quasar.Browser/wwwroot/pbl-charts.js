// uPlot charts for the PB Limiter pages. Quasar's dashboard loads uPlot (window.uPlot) and its stylesheet. Colours are
// read from the page's tokens each time a chart is drawn, so they follow the dashboard's theme and branding.

const charts = new WeakMap(); // container -> { chart, shape, observer }

export function available() {
    return typeof window.uPlot === "function";
}

// The colour a tone token resolves to here, as the canvas needs a plain colour.
function colour(el, token) {
    const probe = document.createElement("span");
    probe.style.color = `var(${token})`;
    probe.style.display = "none";
    el.appendChild(probe);
    const value = getComputedStyle(probe).color;
    probe.remove();
    return value;
}

function alpha(rgb, a) {
    const match = /rgba?\(([^)]+)\)/.exec(rgb);
    if (!match) return rgb;
    const [r, g, b] = match[1].split(/[ ,/]+/).filter(Boolean);
    return `rgba(${r}, ${g}, ${b}, ${a})`;
}

function ms(value) {
    if (value == null || Number.isNaN(value)) return "–";
    if (value === 0) return "0 ms";
    const digits = Math.abs(value) >= 10 ? 1 : Math.abs(value) >= 1 ? 2 : Math.abs(value) >= 0.01 ? 3 : 4;
    return value.toFixed(digits) + " ms";
}

function clock(seconds) {
    const d = new Date(seconds * 1000);
    const pad = (n) => (n < 10 ? "0" + n : "" + n);
    return pad(d.getHours()) + ":" + pad(d.getMinutes()) + ":" + pad(d.getSeconds());
}

// spec: {
//   kind: "runs" | "time",
//   label: accessible name,
//   height: px,
//   x: numbers (run numbers, or Unix seconds for "time"),
//   series: [{ label, tone, values, style: "bars" | "line" | "points" | "hollow" | "solid" | "ring" | "rule" }]
// }
// The marks: "points" lightly filled, "hollow" unfilled, "solid" larger and filled, and "ring" larger still and unfilled,
// so it shows round a mark at the same run. A "rule" is a level, a limit or the average, drawn dashed across the chart.
// Size and fill opacity of each mark style; a null fill draws it unfilled.
const MARKS = {
    points: { size: 9, fill: 0.35 },
    hollow: { size: 9, fill: null },
    solid: { size: 12, fill: 1 },
    ring: { size: 16, fill: null },
};

function options(el, spec, width) {
    const text = colour(el, "--pbl-muted");
    const grid = alpha(colour(el, "--pbl-text"), 0.1);
    const bars = window.uPlot.paths && window.uPlot.paths.bars ? window.uPlot.paths.bars({ size: [0.7, 24] }) : null;

    const series = [{
        label: spec.kind === "time" ? "Time" : "Run",
        value: (u, v) => v == null ? "–" : spec.kind === "time" ? clock(v) : (v === 0 ? "last run" : v === -1 ? "1 run before" : `${-v} runs before`),
    }];
    for (const s of spec.series) {
        const stroke = colour(el, `--pbl-${s.tone}`);
        const mark = MARKS[s.style];
        const item = { label: s.label, stroke, width: 2, value: (u, v) => ms(v), spanGaps: !mark };
        if (s.style === "bars" && bars) {
            item.paths = bars;
            item.fill = alpha(stroke, 0.55);
            item.width = 0;
            item.points = { show: false };
        } else if (mark) {
            item.paths = () => null;
            item.points = { show: true, size: mark.size, width: 2, stroke, fill: mark.fill === null ? "transparent" : alpha(stroke, mark.fill) };
        } else if (s.style === "rule") {
            item.dash = [6, 5];
            item.width = 1.5;
            item.points = { show: false };
        } else {
            item.fill = alpha(stroke, 0.12);
            item.points = { show: false };
        }
        series.push(item);
    }

    const axis = { stroke: text, grid: { stroke: grid, width: 1 }, ticks: { stroke: grid, width: 1 }, font: "11px system-ui, sans-serif" };
    return {
        width,
        height: spec.height || 220,
        series,
        scales: {
            x: { time: spec.kind === "time" },
            y: { range: (u, min, max) => [0, max > 0 ? max * 1.1 : 1] },
        },
        axes: [
            spec.kind === "time"
                ? { ...axis, values: (u, ticks) => ticks.map(clock) }
                : { ...axis, values: (u, ticks) => ticks.map((t) => (Number.isInteger(t) ? String(t) : "")) },
            { ...axis, size: 64, values: (u, ticks) => ticks.map((t) => ms(t)) },
        ],
        cursor: { drag: { x: false, y: false }, points: { size: 7 } },
        legend: { live: true },
        hooks: { setCursor: [(u) => { if (u.cursor.idx == null) showLatest(u); }] },
    };
}

// The legend shows the values under the pointer, and the latest ones while the pointer is off the chart.
function showLatest(chart) {
    const count = chart.data[0].length;
    if (count > 0) chart.setLegend({ idx: count - 1 });
}

function data(spec) {
    return [spec.x, ...spec.series.map((s) => s.values)];
}

// Series and theme: a change to either draws the chart again, as uPlot keeps its colours and series from the start.
function shapeOf(el, spec) {
    return colour(el, "--pbl-text") + colour(el, "--pbl-primary") + "|" + spec.kind + "|" + spec.series.map((s) => s.label + ":" + s.tone + ":" + s.style).join("|");
}

// Draws the chart in the container, or updates the one already there. False when uPlot is not on the page.
export function render(el, spec) {
    if (!available() || !el) return false;
    const width = Math.max(200, el.clientWidth);
    const shape = shapeOf(el, spec);
    const existing = charts.get(el);
    if (existing && existing.shape === shape) {
        existing.chart.setData(data(spec));
        if (existing.chart.cursor.idx == null) showLatest(existing.chart);
        return true;
    }
    dispose(el);

    const chart = new window.uPlot(options(el, spec, width), data(spec), el);
    chart.root.setAttribute("role", "img");
    chart.root.setAttribute("aria-label", spec.label || "Chart");
    showLatest(chart);
    const observer = new ResizeObserver(() => {
        const w = Math.max(200, el.clientWidth);
        if (w !== chart.width) chart.setSize({ width: w, height: chart.height });
    });
    observer.observe(el);
    charts.set(el, { chart, shape, observer });
    return true;
}

export function dispose(el) {
    const existing = el && charts.get(el);
    if (!existing) return;
    existing.observer.disconnect();
    existing.chart.destroy();
    charts.delete(el);
}
