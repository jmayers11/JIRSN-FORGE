# JIRSN Forge — Sprint 6 MRP Visual & Planner UX Polish Specification

## Executive Summary

JIRSN Forge's deterministic Material Requirements Planning engine (`src/Forge.Host/wwwroot/js/pages/mrp.js`) already provides rare mathematical transparency: every recommendation is backed by an input/output fingerprint, pegging records, approved alternate substitution logs, and a 1-click `Why?` drawer. However, because `mrp.js` was largely untouched during Sprints 1–5, its user interface still renders as flat text tables without visual balance indicators, role-based filter chips, sortable columns, actionable exception links, or supplier/source lookups.

This **Sprint 6 MRP Visual & Planner UX Polish Specification** elevates `#/material-plan` for daily Materials Planners, Manufacturing Engineers, Buyers, and Nonprofit/Food-Bank Distribution Coordinators using **progressive disclosure**—keeping the main workspace clean and scannable while adding rich visual netting math and time-phased balance bars inside slide-out drawers. All changes are strictly frontend-only (`wwwroot/js/pages/mrp.js`, `wwwroot/js/forms.js`, and `wwwroot/css/forge.css`), require zero backend C# changes, and preserve 100% compatibility with `tests/ui/*.test.mjs`.

| Task | Target File(s) (`src/Forge.Host/wwwroot/...`) | Planner / Engineer Friction in Current Build (`v3`) | Visual & Functional Upgrade | Effort | Risk |
|---|---|---|---|---|---|
| **6.1** | `js/pages/mrp.js`, `css/forge.css` | Clicking `Why?` (`mrp.js:175–185`) opens a plain text paragraph above a raw 5-column numbers table (`bucketsTable`), forcing the user to mentally calculate how stock, receipts, and safety stock net out. | Add a **Visual Netting Formula Strip** (`[Available] + [Scheduled In] − [Gross Need] + [Safety Stock] → [Planned Order]`) and **Inline Sawtooth Balance Bars** inside `bucketsTable()` color-coded against safety stock. | 30 min | Zero |
| **6.2** | `js/pages/mrp.js`, `css/forge.css` | On the `Items` tab (`mrp.js:99–113`), planners only see a single `Ends at` number. If an item stocks out on Day 3 and replenishes on Day 10, `Ends at` looks healthy, hiding mid-horizon stockouts. | Add a compact **Horizon Health Spark-Strip** (`14`-bucket mini status bar) and a visual quarantine pill (`Insp./hold`) in `itemsTab()`, plus quick filter pills (`All`, `Below safety stock`, `In inspection/hold`, `Make`, `Buy`). | 30 min | Zero |
| **6.3** | `js/pages/mrp.js` | `plannedTab()` (`mrp.js:46–64`) mixes `Make`, `Buy`, and `Past due` orders in a static list, only `Substitutions` shows a tab count badge (`mrp.js:136`), and `enhanceTables(page)` is never called. | Add **Role Filter Chips** (`All`, `Past due`, `Make`, `Buy`) on `Recommendations`, live count badges on all 5 main tabs, and wire `enhanceTables(page)` for click-to-sort and instant table search. | 20 min | Zero |
| **6.4** | `js/pages/mrp.js` | `exceptionsTab()` (`mrp.js:90–97`) is a dead-end 3-column table (`Part \| What \| Detail`) with zero action buttons or links to fix the underlying cause. | Add a context-sensitive **Action** column to `Exceptions` (`Open in Engineering →` for BOM/revision issues, `Alternates` + `Procurement →` for overdue/reschedule receipts, and `View recommendation` for past-due releases). | 20 min | Zero |
| **6.5** | `js/pages/mrp.js`, `js/forms.js` | `draftPo()` (`mrp.js:250–259`) forces buyers to type a raw supplier code into a text input, and `addDemand()` (`mrp.js:225`) asks planners to manually type `work-order:57` into `sourceKey`. | Populate a **Supplier `<select>`** in `draftPo()` prioritizing approved sources for that part, and upgrade `sourceKey` in `addDemand()` to a `lookup` with `allowCustom: true` backed by open work orders, projects, and tools. | 25 min | Low |

## Detailed Implementation Specifications for Codex

### Task 6.1 — Visual Netting Formula Strip & Inline Sawtooth Balance Bars (`wwwroot/js/pages/mrp.js` & `wwwroot/css/forge.css`)

**Problem**: When a planner clicks `Why?` on a recommendation (`mrp.js:175–185`) or clicks an item row (`itemDrawer`, `mrp.js:187–199`), the drawer displays a text paragraph and a raw number grid (`bucketsTable`, `mrp.js:168–173`). Planners have to read every row of numbers to see where inventory drops below safety stock or hits a stockout.

**Fix Part A — Add MRP Micro-Visual Styles in `wwwroot/css/forge.css`**:
Append the following compact styles at the end of `wwwroot/css/forge.css`:

```css
/* MRP Netting Formula Strip, Horizon Spark-Strip & Sawtooth Balance Bars */
.mrp-formula {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
  gap: 8px;
  margin: 10px 0 14px;
}
.mrp-formula-step {
  background: var(--panel-2);
  border: 1px solid var(--line);
  border-radius: var(--radius);
  padding: 8px 10px;
}
.mrp-formula-step.highlight {
  border-color: var(--red-tail);
  background: rgba(194, 65, 12, 0.14);
}
.mrp-formula-step .k {
  font-size: 11px;
  color: var(--muted);
  text-transform: uppercase;
  letter-spacing: .06em;
}
.mrp-formula-step .v {
  font: 700 16px/1.2 var(--font-display);
  color: var(--text);
  margin-top: 2px;
}
.mrp-spark {
  display: inline-flex;
  align-items: flex-end;
  gap: 2px;
  height: 18px;
  padding: 2px 4px;
  background: #11161C;
  border: 1px solid var(--line);
  border-radius: 4px;
  vertical-align: middle;
}
.mrp-spark-bar {
  width: 5px;
  min-height: 3px;
  border-radius: 1px;
  background: var(--ok);
}
.mrp-spark-bar.warn { background: var(--warn); }
.mrp-spark-bar.bad { background: var(--bad); }
.mrp-bal-cell {
  min-width: 150px;
}
.mrp-bal-bar {
  height: 5px;
  background: #11161C;
  border-radius: 3px;
  overflow: hidden;
  margin-top: 3px;
}
.mrp-bal-fill {
  height: 100%;
  background: var(--ok);
  border-radius: 3px;
}
.mrp-bal-fill.warn { background: var(--warn); }
.mrp-bal-fill.bad { background: var(--bad); }
```

**Fix Part B — Upgrade `bucketsTable(i)` and `why(p)` in `wwwroot/js/pages/mrp.js` (lines 168–186)**:
Update `bucketsTable(i)` so the `Projected available` column includes a visual balance bar scaled to the item's peak projected balance, turning amber (`warn`) when below `safetyStock` and red (`bad`) when `<= 0`. In `why(p)`, render the **Netting Formula Strip** at the top of the drawer:

```javascript
  function nettingFormulaHtml(p, i) {
    if (!i) return '';
    const totalGross = (i.buckets ?? []).reduce((sum, b) => sum + (b.gross ?? 0), 0);
    const totalScheduled = (i.buckets ?? []).reduce((sum, b) => sum + (b.scheduledReceipts ?? 0), 0);
    const peggedTotal = (p.pegging ?? []).reduce((sum, g) => sum + (g.quantity ?? 0), 0);
    return `<div class="mrp-formula" role="region" aria-label="MRP netting summary">
      <div class="mrp-formula-step"><div class="k">Nettable stock</div><div class="v">${qty(i.available)} ${esc(i.unitOfMeasure)}</div><div class="faint">${i.inspection + i.hold ? `${qty(i.inspection + i.hold)} in insp./hold excluded` : 'Ready now'}</div></div>
      <div class="mrp-formula-step"><div class="k">Scheduled in</div><div class="v">+${qty(totalScheduled)}</div><div class="faint">${i.receipts?.length ?? 0} open receipt(s)</div></div>
      <div class="mrp-formula-step"><div class="k">Pegged need</div><div class="v">−${qty(peggedTotal || totalGross)}</div><div class="faint">Need by ${esc(fmtDate(p.dueDate))}</div></div>
      <div class="mrp-formula-step"><div class="k">Safety stock</div><div class="v">${qty(i.safetyStock ?? 0)}</div><div class="faint">${esc(policyText(i))}</div></div>
      <div class="mrp-formula-step highlight"><div class="k">Recommended ${esc(p.makeBuy)}</div><div class="v">${qty(p.quantity)} ${esc(p.unitOfMeasure)}</div><div class="faint">Release ${esc(fmtDate(p.releaseDate))}</div></div>
    </div>`;
  }

  function bucketsTable(i) {
    const peak = Math.max(1, i.available ?? 0, i.safetyStock ?? 0, ...(i.buckets ?? []).map(b => Math.max(0, b.projectedAvailable ?? 0)));
    return `<table class="table"><thead><tr><th>Date</th><th class="num">Needed</th><th class="num">Arriving</th><th class="num">Planned</th><th class="num">Projected available</th></tr></thead><tbody>
      ${i.buckets.map(b => {
        const proj = b.projectedAvailable ?? 0;
        const tone = proj <= 0 && (b.gross > 0 || i.safetyStock > 0) ? 'bad' : proj < (i.safetyStock ?? 0) ? 'warn' : 'ok';
        const pct = Math.max(4, Math.min(100, Math.round((Math.max(0, proj) / peak) * 100)));
        return `<tr><td>${esc(fmtDate(b.date))}</td><td class="num">${b.gross ? qty(b.gross) : ''}</td><td class="num">${b.scheduledReceipts ? qty(b.scheduledReceipts) : ''}</td>
          <td class="num">${b.plannedReceipts ? `<b class="c-warn">${qty(b.plannedReceipts)}</b>` : ''}</td>
          <td class="num mrp-bal-cell ${proj < i.safetyStock ? 'c-bad' : ''}"><div>${qty(proj)}${proj < i.safetyStock ? ` <span class="faint">(below ${qty(i.safetyStock)})</span>` : ''}</div><div class="mrp-bal-bar" aria-hidden="true"><div class="mrp-bal-fill ${tone}" style="width:${pct}%"></div></div></td></tr>`;
      }).join('')}
      </tbody></table>`;
  }
```

And inside `why(p)`:

```javascript
  function why(p) {
    const i = itemPlan(p.partNumber);
    drawer(`Why ${qty(p.quantity)} ${p.unitOfMeasure} of ${p.partNumber}?`, `
      ${nettingFormulaHtml(p, i)}
      <p>${esc(p.explanation)}</p>
      <h3 style="margin:0">What it covers</h3>
      <table class="table"><thead><tr><th>Requirement</th><th>Needed</th><th class="num">Qty</th></tr></thead><tbody>
        ${p.pegging.map(g => `<tr><td>${esc(g.description)}</td><td>${esc(fmtDate(g.needDate))}</td><td class="num">${qty(g.quantity)}</td></tr>`).join('')}</tbody></table>
      ${i ? `<h3 style="margin:0">${esc(p.partNumber)} day by day</h3>${bucketsTable(i)}
        <dl class="kv"><dt>Available for planning now</dt><dd>${qty(i.available)}${i.inspection + i.hold ? ` (plus ${qty(i.inspection)} in inspection, ${qty(i.hold)} on hold — not counted)` : ''}</dd>
          <dt>Planning</dt><dd>${esc(policyText(i))}; safety stock ${qty(i.safetyStock)}; lead time ${i.leadTimeDays} days</dd></dl>` : ''}`);
  }
```

### Task 6.2 — Horizon Health Spark-Strip & Item Filter Pills (`wwwroot/js/pages/mrp.js`)

**Problem**: On the `Items` tab (`itemsTab()`, lines 99–113), planners only see the final bucket's projected balance (`Ends at`). If a part dips below safety stock mid-horizon or has stock locked in inspection/hold, it is not visually highlighted.

**Fix in `wwwroot/js/pages/mrp.js`**:
Add a helper `horizonSparkHtml(i)` that renders up to 14 time-bucket bars showing the trajectory of `projectedAvailable` relative to `safetyStock`, and add quick filter buttons (`All`, `Below safety stock`, `In inspection / hold`, `Make`, `Buy`) at the top of `itemsTab()`:

```javascript
  let itemFilter = 'all';

  function horizonSparkHtml(i) {
    const buckets = (i.buckets ?? []).slice(0, 14);
    if (!buckets.length) return '<span class="faint">—</span>';
    const peak = Math.max(1, i.available ?? 0, i.safetyStock ?? 0, ...buckets.map(b => Math.max(0, b.projectedAvailable ?? 0)));
    const bars = buckets.map(b => {
      const proj = b.projectedAvailable ?? 0;
      const tone = proj < 0 || (proj === 0 && (b.gross > 0 || i.safetyStock > 0)) ? 'bad' : proj < (i.safetyStock ?? 0) ? 'warn' : '';
      const h = Math.max(3, Math.min(14, Math.round((Math.max(0, proj) / peak) * 14)));
      return `<span class="mrp-spark-bar ${tone}" style="height:${h}px" title="${esc(fmtDate(b.date))}: ${qty(proj)} ${esc(i.unitOfMeasure)}"></span>`;
    }).join('');
    return `<span class="mrp-spark" role="img" aria-label="Horizon balance trajectory for ${esc(i.partNumber)}">${bars}</span>`;
  }
```

In `itemsTab()`:
- Filter `list` according to `itemFilter` (`'all'`, `'low'` where any bucket has `projectedAvailable < i.safetyStock` or `i.available < i.safetyStock`, `'held'` where `(i.inspection + i.hold) > 0`, `'make'` where `i.makeBuy === 'Make'`, `'buy'` where `i.makeBuy === 'Buy'`).
- Render `horizonSparkHtml(i)` next to the `Ends at` quantity so planners see both the final number and the day-by-day health wave.

### Task 6.3 — Role Filter Chips on Recommendations, Tab Count Badges & `enhanceTables` (`wwwroot/js/pages/mrp.js`)

**Problem**:
1. `plannedTab()` (`lines 46–64`) mixes `Make`, `Buy`, and `Past due` recommendations together with no quick filter.
2. In `render()` (`line 136`), only `Substitutions` displays a count badge on its tab button.
3. `mrp.js` does not import or call `enhanceTables(page)` from `../ui.js`.

**Fix in `wwwroot/js/pages/mrp.js`**:
1. Import `enhanceTables` from `../ui.js` and call `enhanceTables(page);` inside `render()`.
2. Add `let plannedFilter = 'all';` (`'all'`, `'pastDue'`, `'Make'`, `'Buy'`) and render a compact filter chip bar at the top of `plannedTab()` whenever `out().plannedOrders.length > 0`:

```javascript
    const allOrders = out().plannedOrders;
    if (!allOrders.length) return emptyBlock('Nothing to order', 'Stock, open purchase orders and production orders cover every requirement and safety stock.');
    const pastDueCount = allOrders.filter(p => p.pastDue).length;
    const makeCount = allOrders.filter(p => p.makeBuy === 'Make').length;
    const buyCount = allOrders.filter(p => p.makeBuy === 'Buy').length;
    const indexed = allOrders.map((p, index) => ({ p, index })).filter(({ p }) =>
      plannedFilter === 'pastDue' ? p.pastDue
      : plannedFilter === 'Make' ? p.makeBuy === 'Make'
      : plannedFilter === 'Buy' ? p.makeBuy === 'Buy' : true);
```

*(Important: preserve the original array index `index` in `data-why="${index}"`, `data-req="${index}"`, `data-buy="${index}"`, and `data-make="${index}"` so filtered rows always reference the exact `out().plannedOrders[index]` entry!)*

3. Add count badges to all 5 segmented tabs in `render()`:
```javascript
[
  ['planned', `Recommendations${out()?.plannedOrders?.length ? ` (${out().plannedOrders.length})` : ''}`],
  ['substitutions', `Substitutions${out()?.substitutions?.length ? ` (${out().substitutions.length})` : ''}`],
  ['exceptions', `Exceptions${out()?.exceptions?.length ? ` (${out().exceptions.length})` : ''}`],
  ['items', `Items${out()?.items?.length ? ` (${out().items.length})` : ''}`],
  ['demand', `Demand${data.demand.data?.length ? ` (${data.demand.data.length})` : ''}`],
]
```

### Task 6.4 — Actionable 1-Click Exception Resolution (`wwwroot/js/pages/mrp.js`)

**Problem**: `exceptionsTab()` (`lines 90–97`) displays `Part | What | Detail` with no action column, forcing planners to manually copy the part number and navigate to Engineering, Procurement, or Recommendations.

**Fix in `exceptionsTab()` (`wwwroot/js/pages/mrp.js`, lines 90–97)**:
Add an actions column (`<th></th>`) to the Exceptions table with direct resolution actions based on `e.kind`:

```javascript
  function exceptionActionHtml(e) {
    if (['NoRevision', 'BomLoop', 'UnknownItem'].includes(e.kind)) {
      return `<a class="btn small" href="#/engineering/${encodeURIComponent(e.partNumber)}">Open in Engineering</a>`;
    }
    if (['RescheduleIn', 'OverdueReceipt'].includes(e.kind)) {
      return `<button class="btn small ghost" type="button" data-alts="${esc(e.partNumber)}">Alternates</button> <a class="btn small" href="#/procurement">Open POs</a>`;
    }
    if (e.kind === 'PastDueRelease') {
      return `<button class="btn small primary" type="button" data-jump-part="${esc(e.partNumber)}">View recommendation</button>`;
    }
    return `<button class="btn small ghost" type="button" data-alts="${esc(e.partNumber)}">Alternates</button>`;
  }
```

And in `render()`, bind `$$('[data-jump-part]', page)` so clicking `View recommendation` switches `tab = 'planned'`, sets `plannedFilter = 'all'`, re-renders, and opens the `Why?` drawer (or highlights the recommendation) for that part.

### Task 6.5 — Approved Supplier Picker in `Draft PO` & Entity Lookup for `sourceKey` (`wwwroot/js/pages/mrp.js` & `wwwroot/js/forms.js`)

**Problem**:
1. In `mrp.js` (`draftPo`, lines 250–259), `{ name: 'supplier', label: 'Supplier', required: true }` is a plain text input instead of a supplier selector showing approved part sources.
2. In `mrp.js` (`addDemand`, line 225), `{ name: 'sourceKey', label: 'What it is for, as kind:key (required from Maintenance on)', placeholder: 'work-order:57' }` requires typing raw `kind:key` strings.

**Fix Part A — Support `allowCustom` on `type: 'lookup'` in `wwwroot/js/forms.js` (lines 131–138)**:
In `readFields()` (`wwwroot/js/forms.js`, line 135), allow `type: 'lookup'` fields with `allowCustom: true` to accept either a selected `<datalist>` option's `data-key` OR a direct `kind:key` string typed by the user or an automated test:

```javascript
    else if (f.type === 'lookup') {
      const raw = el.value.trim();
      if (raw === '') out[f.name] = null;
      else {
        const option = $$('option', $(`#f-${f.name}-list`, m)).find(item => item.value === raw || item.getAttribute('data-key') === raw);
        if (!option && !f.allowCustom) throw new Error(`${f.label}: choose a listed value.`);
        out[f.name] = option ? option.getAttribute('data-key') : raw;
      }
    }
```

**Fix Part B — Supplier & Source Lookups in `wwwroot/js/pages/mrp.js`**:
1. In `load()` (`mrp.js:26–29`), also fetch `tryGet('/api/procurement/suppliers')` and `tryGet('/api/procurement/approved-sources')` alongside `/api/planning/mrp`, `/api/planning/demand`, and `/api/engineering/items`.
2. In `draftPo(p)` (`mrp.js:250–259`), build a supplier option list from active suppliers (`status === 'Active'`), placing suppliers that are approved sources for `p.partNumber` first (`★ ${s.name} (${s.code}) — Approved source`). If active suppliers exist, use `type: 'select'` (with pre-selected approved supplier); if the user lacks permission to read suppliers or none exist yet, fall back gracefully to the text input so existing tests continue to work seamlessly.
3. In `addDemand()` (`mrp.js:216–230`), fetch open work orders (`/api/maintenance/work-orders`), projects (`/api/projects`), and tools (`/api/tools`) on demand and pass them as `options` to `{ name: 'sourceKey', type: 'lookup', allowCustom: true, ... }` (e.g., `['work-order:57', 'work-order:57 — Replace spindle bearings (SL-02)']`), so planners can either pick a real work order/project from the dropdown or type `work-order:57` directly.

## Verification Checklist for Codex

1. Run `node --check src/Forge.Host/wwwroot/js/pages/mrp.js` and `node --check src/Forge.Host/wwwroot/js/forms.js`.
2. Run `node --test tests/ui/*.test.mjs` to verify all 25 UI test suites pass without regression.
3. Visit `#/material-plan` and verify:
   - All 5 main tabs display live record counts and tables support search + column sorting via `enhanceTables`.
   - Clicking `Why?` on any planned order displays the 5-card **Netting Formula Strip** at the top and **Inline Sawtooth Balance Bars** in the day-by-day table.
   - The `Items` tab displays the **Horizon Health Spark-Strip** and filter chips (`All`, `Below safety stock`, `In inspection / hold`, `Make`, `Buy`).
   - The `Exceptions` tab provides 1-click resolution links (`Open in Engineering`, `Alternates`, `Open POs`, `View recommendation`).
   - Clicking `Draft PO` offers an active Supplier selector (highlighting approved sources), and clicking `+ Demand` provides a searchable `sourceKey` lookup.
