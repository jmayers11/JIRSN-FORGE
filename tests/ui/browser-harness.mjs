// A small DOM surface for exercising the shipped handlers without a browser dependency.
// It models focus, selectors, event bubbling, document connectivity and controllable timers;
// it does not certify browser rendering or screen-reader announcements.
class Events {
  listeners = new Map();
  addEventListener(type, listener) {
    if (!this.listeners.has(type)) this.listeners.set(type, new Set());
    this.listeners.get(type).add(listener);
  }
  removeEventListener(type, listener) { this.listeners.get(type)?.delete(listener); }
  dispatchEvent(event) {
    event.target ??= this;
    event.preventDefault ??= () => { event.defaultPrevented = true; };
    for (const listener of [...(this.listeners.get(event.type) ?? [])]) listener(event);
    this[`on${event.type}`]?.(event);
    if (event.bubbles) (this.parentElement ?? this.ownerDocument)?.dispatchEvent(event);
    return !event.defaultPrevented;
  }
}

function splitSelectorGroups(selector) {
  const groups = [];
  let value = '', bracketDepth = 0, quote = null;
  for (const char of selector) {
    if (quote) {
      value += char;
      if (char === quote) quote = null;
    } else if ((char === '"' || char === "'") && bracketDepth > 0) {
      quote = char; value += char;
    } else if (char === '[') {
      bracketDepth++; value += char;
    } else if (char === ']') {
      bracketDepth--; value += char;
    } else if (char === ',' && bracketDepth === 0) {
      if (value.trim()) groups.push(value.trim());
      value = '';
    } else value += char;
  }
  if (value.trim()) groups.push(value.trim());
  return groups;
}

function splitSelectorChain(selector) {
  const chain = [];
  let value = '', bracketDepth = 0, quote = null;
  for (const char of selector) {
    if (quote) {
      value += char;
      if (char === quote) quote = null;
    } else if ((char === '"' || char === "'") && bracketDepth > 0) {
      quote = char; value += char;
    } else if (char === '[') {
      bracketDepth++; value += char;
    } else if (char === ']') {
      bracketDepth--; value += char;
    } else if (/\s/.test(char) && bracketDepth === 0) {
      if (value) chain.push(value);
      value = '';
    } else value += char;
  }
  if (value) chain.push(value);
  return chain;
}

class Element extends Events {
  constructor(tag, doc) {
    super(); this.tagName = tag.toLowerCase(); this.ownerDocument = doc;
    this.attributes = new Map(); this.children = []; this.parentElement = null;
    this.style = {}; this.value = ''; this.checked = false; this._text = ''; this._html = '';
    this.classList = {
      contains: name => this.className.split(/\s+/).includes(name),
      remove: name => { this.className = this.className.split(/\s+/).filter(c => c !== name).join(' '); },
      add: name => { this.className = `${this.className} ${name}`.trim(); },
    };
  }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  get id() { return this.getAttribute('id') ?? ''; }
  set id(value) { this.setAttribute('id', value); }
  get className() { return this.getAttribute('class') ?? ''; }
  set className(value) { this.setAttribute('class', value); }
  get disabled() { return this.attributes.has('disabled'); }
  set disabled(value) { if (value) this.setAttribute('disabled', ''); else this.attributes.delete('disabled'); }
  get dataset() {
    return Object.fromEntries([...this.attributes].filter(([k]) => k.startsWith('data-'))
      .map(([k, v]) => [k.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase()), v]));
  }
  get tabIndex() {
    if (this.attributes.has('tabindex')) return Number(this.getAttribute('tabindex'));
    return ['button', 'input', 'select', 'textarea'].includes(this.tagName) || (this.tagName === 'a' && this.attributes.has('href')) ? 0 : -1;
  }
  set tabIndex(value) { this.setAttribute('tabindex', value); }
  get isConnected() { return this === this.ownerDocument.body || !!this.parentElement?.isConnected; }
  append(...elements) {
    for (const element of elements) { element.remove(); element.parentElement = this; this.children.push(element); }
  }
  appendChild(element) { this.append(element); return element; }
  insertBefore(element, reference) {
    element.remove();
    const index = this.children.indexOf(reference);
    if (index < 0) return this.append(element);
    element.parentElement = this; this.children.splice(index, 0, element);
  }
  remove() {
    if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(c => c !== this);
    this.parentElement = null;
  }
  contains(element) { return element === this || this.children.some(child => child.contains(element)); }
  matches(selector) {
    if (selector === ':disabled') return this.disabled || !!this.parentElement?.closest('fieldset[disabled]');
    const tag = /^([a-z][\w-]*)/i.exec(selector)?.[1];
    if (tag && tag !== this.tagName) return false;
    const id = /#([\w-]+)/.exec(selector)?.[1];
    if (id && id !== this.id) return false;
    const cls = /\.([\w-]+)/.exec(selector)?.[1];
    if (cls && !this.classList.contains(cls)) return false;
    for (const [, name, value] of selector.matchAll(/\[([\w-]+)(?:="([^"]*)")?\]/g)) {
      if (!this.attributes.has(name) || (value !== undefined && this.getAttribute(name) !== value)) return false;
    }
    return true;
  }
  closest(selectors) {
    if (selectors.split(',').some(s => this.matches(s.trim()))) return this;
    return this.parentElement?.closest(selectors) ?? null;
  }
  querySelectorAll(selectors) {
    const parts = splitSelectorGroups(selectors), found = [];
    const matches = (element, selector) => {
      const chain = splitSelectorChain(selector);
      if (!element.matches(chain.at(-1))) return false;
      let ancestor = element.parentElement;
      for (let index = chain.length - 2; index >= 0; index--) {
        while (ancestor && !ancestor.matches(chain[index])) ancestor = ancestor.parentElement;
        if (!ancestor) return false;
        ancestor = ancestor.parentElement;
      }
      return true;
    };
    const visit = element => {
      for (const child of element.children) {
        if (parts.some(s => matches(child, s))) found.push(child);
        visit(child);
      }
    };
    visit(this); return found;
  }
  querySelector(selectors) { return this.querySelectorAll(selectors)[0] ?? null; }
  getClientRects() {
    return !this.isConnected || this.closest('[hidden],[aria-hidden="true"],.hidden') || this.getAttribute('type') === 'hidden' ? [] : [{}];
  }
  focus() {
    if (!this.isConnected || this.disabled) return;
    this.ownerDocument.activeElement = this;
    this.dispatchEvent({ type: 'focusin', bubbles: true });
  }
  get textContent() { return this._text + this.children.map(c => c.textContent).join(''); }
  set textContent(value) { for (const child of [...this.children]) child.remove(); this._text = String(value); }
  get innerHTML() { return this._html; }
  set innerHTML(html) {
    for (const child of [...this.children]) child.remove();
    this._text = ''; this._html = html;
    const stack = [this];
    for (const token of html.match(/<[^>]*>|[^<]+/g) ?? []) {
      if (token.startsWith('</')) { if (stack.length > 1) stack.pop(); continue; }
      if (token.startsWith('<')) {
        const name = /^<([\w-]+)/.exec(token)?.[1];
        if (!name) continue;
        const el = this.ownerDocument.createElement(name);
        const attrs = token.slice(name.length + 1, -1);
        for (const [, key, quoted, bare] of attrs.matchAll(/([\w-]+)(?:="([^"]*)"|=([^\s>]+))?/g)) el.setAttribute(key, quoted ?? bare ?? '');
        el.value = el.getAttribute('value') ?? '';
        el.checked = el.attributes.has('checked');
        stack.at(-1).append(el);
        if (!['input', 'img', 'br', 'hr', 'meta', 'link'].includes(name.toLowerCase())) stack.push(el);
      } else stack.at(-1)._text += token;
    }
    for (const select of this.querySelectorAll('select')) {
      const options = select.querySelectorAll('option'), option = options.find(o => o.attributes.has('selected')) ?? options[0];
      select.value = option ? option.getAttribute('value') ?? option.textContent : '';
    }
  }
  get hidden() { return this.attributes.has('hidden'); }
  set hidden(value) { if (value) this.setAttribute('hidden', ''); else this.attributes.delete('hidden'); }
}

export class FakeDocument extends Events {
  constructor() { super(); this.body = new Element('body', this); this.activeElement = this.body; }
  createElement(tag) { return new Element(tag, this); }
  querySelector(selector) { return this.body.querySelector(selector); }
  querySelectorAll(selector) { return this.body.querySelectorAll(selector); }
}

export function fakeTimers() {
  let now = 0, id = 0;
  const tasks = new Map();
  return {
    setTimeout(callback, delay) { tasks.set(++id, { callback, at: now + delay }); return id; },
    clearTimeout(handle) { tasks.delete(handle); },
    advance(ms) {
      const end = now + ms;
      while (true) {
        const due = [...tasks].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) break;
        now = due[1].at; tasks.delete(due[0]); due[1].callback();
      }
      now = end;
    },
  };
}

export function installBrowser(t) {
  const original = { document: globalThis.document, fetch: globalThis.fetch, setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout };
  const document = new FakeDocument(), timers = fakeTimers();
  globalThis.document = document;
  globalThis.setTimeout = timers.setTimeout;
  globalThis.clearTimeout = timers.clearTimeout;
  t.after(() => Object.assign(globalThis, original));
  return { document, timers };
}

export function response(body, status = 200) { return { status, ok: status >= 200 && status < 300, text: async () => JSON.stringify(body) }; }
export function deferred() {
  let resolve, reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}
export const flush = () => new Promise(resolve => setImmediate(resolve));
export function fire(element, type, extra = {}) {
  if (!element) throw new Error(`Cannot fire ${type} on a missing element.`);
  const event = { type, bubbles: true, ...extra };
  element.dispatchEvent(event);
  return event;
}
