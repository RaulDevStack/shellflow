// ShellFlow terminal renderer.
// Runs inside WebView2 and talks to native code only through window.chrome.webview.
// Native → renderer: plain strings are shell output; objects are typed control messages.
// Renderer → native: typed objects, validated again on the native side (see BridgeProtocol.cs).
(() => {
  'use strict';

  const webview = window.chrome && window.chrome.webview;
  if (!webview) {
    document.body.textContent = 'ShellFlow terminal must be hosted by ShellFlow.';
    return;
  }

  const MAX_INPUT_CHUNK = 32 * 1024;
  const ACK_THRESHOLD = 16 * 1024;

  const container = document.getElementById('terminal');
  const statusEl = document.getElementById('status');
  const statusText = document.getElementById('status-text');

  let term = null;
  let fitAddon = null;
  let searchAddon = null;
  let webglAddon = null;
  let options = null;
  let keyBindings = new Map();
  let pendingAck = 0;
  let ackTimer = 0;
  let fitFrame = 0;
  let lastSize = { cols: 0, rows: 0 };
  let lastQuery = null;

  const post = (message) => webview.postMessage(message);

  // ── Output ────────────────────────────────────────────────────────────────
  function write(data) {
    if (!term) {
      return;
    }

    term.write(data, () => {
      pendingAck += data.length;
      if (pendingAck >= ACK_THRESHOLD) {
        flushAck();
      } else if (!ackTimer) {
        ackTimer = setTimeout(flushAck, 4);
      }
    });
  }

  function flushAck() {
    if (ackTimer) {
      clearTimeout(ackTimer);
      ackTimer = 0;
    }

    if (pendingAck > 0) {
      post({ type: 'ack', chars: pendingAck });
      pendingAck = 0;
    }
  }

  // ── Input ─────────────────────────────────────────────────────────────────
  function sendInput(data) {
    for (let i = 0; i < data.length; i += MAX_INPUT_CHUNK) {
      post({ type: 'input', data: data.slice(i, i + MAX_INPUT_CHUNK) });
    }
  }

  function chordKey(ctrl, alt, shift, code) {
    return `${ctrl ? 1 : 0}${alt ? 1 : 0}${shift ? 1 : 0}:${code}`;
  }

  function copySelection() {
    const text = term.getSelection();
    if (text) {
      post({ type: 'copy', text });
      term.clearSelection();
      return true;
    }

    return false;
  }

  function handleKey(event) {
    // The cd folder picker owns the keyboard while it is open.
    if (picker || pending) {
      if (event.type === 'keydown') {
        pickerKey(event);
      }

      event.preventDefault();
      return false;
    }

    if (completionEnabled && event.type === 'keydown' && event.key === 'Tab' && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) {
      const context = cdContext();
      if (context) {
        startPicker(context);
        event.preventDefault();
        return false;
      }
    }

    // Ctrl+C copies when there is a selection; otherwise it reaches the shell as an interrupt.
    if (event.ctrlKey && !event.altKey && !event.shiftKey && event.code === 'KeyC' && term.hasSelection()) {
      if (event.type === 'keydown') {
        copySelection();
      }

      event.preventDefault();
      return false;
    }

    const command = keyBindings.get(chordKey(event.ctrlKey, event.altKey, event.shiftKey, event.code));
    if (command) {
      if (event.type === 'keydown') {
        post({ type: 'command', id: command });
      }

      event.preventDefault();
      return false;
    }

    return true;
  }

  // ── cd folder picker ──────────────────────────────────────────────────────
  // "cd <partial>" + Tab opens a list of folders. Native code lists folders (relative paths resolve against the
  // folder the shell reported) and types the final choice into the shell; this side only draws and navigates.
  let completionEnabled = false;
  let picker = null; // { base, drives, entries, filter, index, erase, error }
  let pending = null; // { id, initial, erase }
  let requestSeq = 0;
  const pickerEl = document.getElementById('picker');
  const pickerPath = document.getElementById('picker-path');
  const pickerFilter = document.getElementById('picker-filter');
  const pickerList = document.getElementById('picker-list');
  const folderIcon = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M1.5 3.5a1 1 0 0 1 1-1h3.6l1.4 1.5h6a1 1 0 0 1 1 1v7.5a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1z"/></svg>';
  const upIcon = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 3.5 3.5 8h3v4.5h3V8h3z"/></svg>';
  const driveIcon = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M2 5.5a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1zM11.5 8.75a.75.75 0 1 0 0-1.5.75.75 0 0 0 0 1.5z"/></svg>';

  function cdContext() {
    const buffer = term.buffer.active;
    let y = buffer.baseY + buffer.cursorY;
    let line = buffer.getLine(y);
    if (!line) {
      return null;
    }

    let text = line.translateToString(false).slice(0, buffer.cursorX);
    while (line && line.isWrapped && y > 0) {
      y--;
      line = buffer.getLine(y);
      text = line.translateToString(false) + text;
    }

    const match = /(?:^|[\s;|&>(])(cd|chdir|set-location|sl|pushd)(\s+(.*))?$/i.exec(text);
    if (!match) {
      return null;
    }

    return { needsSpace: match[2] === undefined, partial: match[3] ?? '' };
  }

  function startPicker(context) {
    if (context.needsSpace) {
      sendInput(' ');
    }

    request({ partial: context.partial }, true, context.partial.length);
  }

  function request(target, initial, erase, select) {
    pending = { id: ++requestSeq, initial, erase, select };
    post({ type: 'dirList', id: pending.id, ...target });
  }

  function onListing(message) {
    if (!pending || message.id !== pending.id) {
      return;
    }

    const req = pending;
    pending = null;
    if (message.error) {
      if (req.initial) {
        // Unknown folder or nothing to list: let the shell do its own Tab completion instead.
        closePicker();
        sendInput('\t');
      } else if (picker) {
        picker.error = message.error;
        renderPicker();
      }

      return;
    }

    picker = {
      base: message.base,
      drives: message.drives,
      entries: message.entries,
      filter: req.initial ? message.prefix : '',
      index: 0,
      erase: req.initial ? req.erase : picker.erase,
      error: null,
    };

    // After going up, keep the folder we came from selected (like a file manager).
    if (req.select) {
      const at = rows().findIndex((row) => !row.up && row.name.toLowerCase() === req.select.toLowerCase());
      picker.index = at >= 0 ? at : 0;
    }

    renderPicker(req.initial);
  }

  function rows() {
    const filter = picker.filter.toLowerCase();
    const starts = [];
    const contains = [];
    for (const name of picker.entries) {
      const lower = name.toLowerCase();
      if (!filter || lower.startsWith(filter)) {
        starts.push(name);
      } else if (lower.includes(filter)) {
        contains.push(name);
      }
    }

    const list = [];
    if (!picker.drives && !filter) {
      list.push({ name: '..', up: true });
    }

    return list.concat([...starts, ...contains].map((name) => ({ name })));
  }

  function targetOf(row) {
    if (row.up) {
      return picker.base + '..';
    }

    return picker.drives ? row.name : picker.base + row.name;
  }

  function renderPicker(opening) {
    const list = rows();
    picker.index = Math.max(0, Math.min(picker.index, list.length - 1));
    // <bdi> keeps the path's own left-to-right order inside the right-to-left (start-ellipsis) container.
    const bdi = document.createElement('bdi');
    bdi.textContent = picker.drives ? 'This PC' : picker.base;
    pickerPath.replaceChildren(bdi);
    pickerFilter.textContent = picker.filter;
    pickerList.innerHTML = '';

    if (picker.error) {
      const empty = document.createElement('div');
      empty.className = 'picker-empty';
      empty.textContent = picker.error === 'access denied' ? 'Access denied' : 'Folder unavailable';
      pickerList.appendChild(empty);
    } else if (list.length === 0 || (list.length === 1 && list[0].up)) {
      const empty = document.createElement('div');
      empty.className = 'picker-empty';
      empty.textContent = picker.filter ? 'No matching folders' : 'No subfolders';
      if (list.length === 1) {
        pickerList.appendChild(rowElement(list[0], 0));
      }

      pickerList.appendChild(empty);
    } else {
      list.forEach((row, i) => pickerList.appendChild(rowElement(row, i)));
    }

    const selected = pickerList.querySelector('[aria-selected="true"]');
    if (selected) {
      selected.scrollIntoView({ block: 'nearest' });
    }

    if (opening || pickerEl.hidden) {
      pickerEl.hidden = false;
      positionPicker();
      pickerEl.classList.remove('opening');
      void pickerEl.offsetWidth;
      pickerEl.classList.add('opening');
    }
  }

  function rowElement(row, i) {
    const el = document.createElement('div');
    el.className = 'picker-row' + (row.up ? ' up' : '');
    el.setAttribute('role', 'option');
    el.setAttribute('aria-selected', i === picker.index ? 'true' : 'false');
    el.innerHTML = row.up ? upIcon : picker.drives ? driveIcon : folderIcon;
    const label = document.createElement('span');
    label.textContent = row.up ? '..' : row.name;
    el.appendChild(label);
    el.addEventListener('mousedown', (event) => {
      event.preventDefault();
      picker.index = i;
      renderPicker();
    });
    el.addEventListener('dblclick', (event) => {
      event.preventDefault();
      accept(row, true);
    });
    return el;
  }

  function positionPicker() {
    const screen = container.querySelector('.xterm-screen');
    if (!screen) {
      return;
    }

    const rect = screen.getBoundingClientRect();
    const cellWidth = rect.width / term.cols;
    const cellHeight = rect.height / term.rows;
    const buffer = term.buffer.active;
    const width = pickerEl.offsetWidth;
    const height = pickerEl.offsetHeight;
    let left = rect.left + buffer.cursorX * cellWidth;
    let top = rect.top + (buffer.cursorY + 1) * cellHeight + 4;
    if (top + height > window.innerHeight - 8) {
      top = Math.max(8, rect.top + buffer.cursorY * cellHeight - height - 4);
    }

    left = Math.max(8, Math.min(left, window.innerWidth - width - 8));
    pickerEl.style.left = `${Math.round(left)}px`;
    pickerEl.style.top = `${Math.round(top)}px`;
  }

  function accept(row, run) {
    if (!row || (picker.drives && row.up)) {
      return;
    }

    post({ type: 'dirAccept', dir: targetOf(row), erase: picker.erase, run });
    closePicker();
  }

  function pickerKey(event) {
    if (!picker) {
      if (event.key === 'Escape') {
        pending = null; // Still waiting for the first listing: cancel.
      }

      return;
    }

    const list = rows();
    const row = list[picker.index];
    switch (event.key) {
      case 'ArrowDown':
        picker.index = (picker.index + 1) % Math.max(1, list.length);
        break;
      case 'ArrowUp':
        picker.index = (picker.index - 1 + list.length) % Math.max(1, list.length);
        break;
      case 'PageDown':
        picker.index = Math.min(list.length - 1, picker.index + 8);
        break;
      case 'PageUp':
        picker.index = Math.max(0, picker.index - 8);
        break;
      case 'Home':
        picker.index = 0;
        break;
      case 'End':
        picker.index = list.length - 1;
        break;
      case 'Enter':
        if (event.ctrlKey && picker.base) {
          post({ type: 'dirAccept', dir: picker.base, erase: picker.erase, run: true });
          closePicker();
        } else {
          accept(row, !event.shiftKey);
        }

        return;
      case 'Tab':
      case 'ArrowRight':
        if (row) {
          request({ dir: targetOf(row) }, false, picker.erase);
        }

        return;
      case 'ArrowLeft':
        if (!picker.drives) {
          request({ dir: picker.base + '..' }, false, picker.erase, currentFolderName());
        }

        return;
      case 'Backspace':
        if (picker.filter) {
          picker.filter = picker.filter.slice(0, -1);
          picker.index = 0;
        } else if (!picker.drives) {
          request({ dir: picker.base + '..' }, false, picker.erase, currentFolderName());
          return;
        }

        break;
      case 'Escape':
        closePicker();
        return;
      default:
        if (event.key.length === 1 && !event.ctrlKey && !event.altKey && !event.metaKey) {
          picker.filter += event.key;
          picker.index = 0;
        } else {
          return;
        }
    }

    picker.error = null;
    renderPicker();
  }

  function currentFolderName() {
    const parts = (picker.base || '').split(/[\\/]/).filter(Boolean);
    return parts.length > 1 ? parts[parts.length - 1] : parts.length === 1 ? parts[0] + '\\' : null;
  }

  function closePicker() {
    picker = null;
    pending = null;
    pickerEl.hidden = true;
  }

  document.addEventListener('mousedown', (event) => {
    if (picker && !pickerEl.contains(event.target)) {
      closePicker();
    }
  });

  // ── Shell integration (working directory) ────────────────────────────────
  function reportCwd(path) {
    if (typeof path === 'string' && path.length > 0 && path.length <= 4096) {
      post({ type: 'cwd', path });
    }
  }

  function parseFileUri(data) {
    // OSC 7: file://host/path, percent-encoded.
    const match = /^file:\/\/[^/]*(\/.*)$/.exec(data);
    if (!match) {
      return null;
    }

    let path;
    try {
      path = decodeURIComponent(match[1]);
    } catch {
      return null;
    }

    // file:///C:/Users → C:\Users
    if (/^\/[A-Za-z]:\//.test(path)) {
      path = path.slice(1).replace(/\//g, '\\');
    }

    return path;
  }

  // ── Terminal creation and configuration ──────────────────────────────────
  function applyBindings(list) {
    keyBindings = new Map();
    for (const b of list || []) {
      keyBindings.set(chordKey(b.ctrl, b.alt, b.shift, b.code), b.command);
    }
  }

  function applyTheme(theme) {
    const root = document.documentElement.style;
    root.setProperty('--term-bg', theme.background);
    root.setProperty('--accent', theme.cursor);
    root.setProperty('--term-fg', theme.foreground);
    document.body.style.background = theme.background;
    document.documentElement.dataset.light = isLightBackground() ? 'true' : 'false';
  }

  function enableRenderer(gpu) {
    if (gpu && !webglAddon && window.WebglAddon) {
      try {
        webglAddon = new window.WebglAddon.WebglAddon();
        webglAddon.onContextLoss(() => {
          webglAddon.dispose();
          webglAddon = null;
          post({ type: 'renderer', renderer: 'dom' });
        });
        term.loadAddon(webglAddon);
        post({ type: 'renderer', renderer: 'webgl' });
        return;
      } catch (err) {
        webglAddon = null;
        post({ type: 'error', message: `WebGL renderer unavailable: ${err && err.message}` });
      }
    }

    if (!gpu && webglAddon) {
      webglAddon.dispose();
      webglAddon = null;
    }

    if (!webglAddon) {
      post({ type: 'renderer', renderer: 'dom' });
    }
  }

  function configure(next) {
    const previous = options;
    options = next;
    applyBindings(next.keyBindings);
    applyTheme(next.theme);

    const termOptions = {
      fontFamily: next.fontFamily,
      fontSize: next.fontSize,
      lineHeight: next.lineHeight,
      cursorStyle: next.cursorStyle,
      cursorBlink: next.cursorBlink,
      cursorInactiveStyle: 'outline',
      scrollback: next.scrollback,
      theme: next.theme,
    };

    if (!term) {
      create(termOptions);
    } else {
      Object.assign(term.options, termOptions);
      if (!previous || previous.gpu !== next.gpu) {
        enableRenderer(next.gpu);
      }

      scheduleFit();
    }
  }

  function create(termOptions) {
    term = new window.Terminal({
      ...termOptions,
      allowProposedApi: true,
      allowTransparency: false,
      convertEol: false,
      drawBoldTextInBrightColors: true,
      macOptionIsMeta: false,
      rightClickSelectsWord: false,
      smoothScrollDuration: 0,
      windowsPty: { backend: 'conpty' },
    });

    fitAddon = new window.FitAddon.FitAddon();
    searchAddon = new window.SearchAddon.SearchAddon({ highlightLimit: 2000 });
    term.loadAddon(fitAddon);
    term.loadAddon(searchAddon);

    const unicode = new window.Unicode11Addon.Unicode11Addon();
    term.loadAddon(unicode);
    term.unicode.activeVersion = '11';

    term.open(container);
    enableRenderer(options.gpu);

    term.attachCustomKeyEventHandler(handleKey);
    term.onData(sendInput);
    term.onBinary((data) => sendInput(data));
    term.onTitleChange((title) => post({ type: 'title', title: String(title).slice(0, 1024) }));
    term.onResize(({ cols, rows }) => reportSize('resize', cols, rows));
    term.onSelectionChange(() => {
      if (options && options.copyOnSelect && term.hasSelection()) {
        post({ type: 'copy', text: term.getSelection() });
      }
    });

    searchAddon.onDidChangeResults(({ resultIndex, resultCount }) => {
      post({ type: 'searchResult', index: resultIndex, count: resultCount });
    });

    term.parser.registerOscHandler(7, (data) => {
      const path = parseFileUri(data);
      if (path) {
        reportCwd(path);
      }

      return true;
    });

    term.parser.registerOscHandler(9, (data) => {
      // OSC 9;9;<path> — the ConEmu/Windows Terminal convention for the current directory.
      if (data.startsWith('9;')) {
        reportCwd(data.slice(2).replace(/^"|"$/g, ''));
        return true;
      }

      return false;
    });

    term.textarea.addEventListener('focus', () => post({ type: 'focus' }));
    term.textarea.addEventListener('blur', closePicker);
    term.onScroll(() => {
      if (picker) {
        positionPicker();
      }
    });

    // Right-click: copy the selection if there is one, otherwise paste (a common Windows terminal convention).
    container.addEventListener('contextmenu', (event) => {
      event.preventDefault();
      if (!copySelection()) {
        post({ type: 'command', id: 'terminal.paste' });
      }
    });

    new ResizeObserver(scheduleFit).observe(container);
    fitNow();
    container.classList.add('ready');
    reportSize('ready', term.cols, term.rows);
  }

  function reportSize(type, cols, rows) {
    if (type === 'resize' && cols === lastSize.cols && rows === lastSize.rows) {
      return;
    }

    lastSize = { cols, rows };
    post({ type, cols, rows });
  }

  function fitNow() {
    fitFrame = 0;
    if (!term || container.clientWidth < 20 || container.clientHeight < 20) {
      return; // Hidden or collapsed: keep the last size instead of shrinking the shell to nothing.
    }

    try {
      fitAddon.fit();
    } catch (err) {
      post({ type: 'error', message: `fit failed: ${err && err.message}` });
    }
  }

  function scheduleFit() {
    if (!fitFrame) {
      fitFrame = requestAnimationFrame(fitNow);
    }
  }

  // ── Search ────────────────────────────────────────────────────────────────
  function isLightBackground() {
    const hex = (options && options.theme && options.theme.background) || '#111318';
    const n = parseInt(hex.slice(1, 7), 16);
    const luminance = (0.299 * ((n >> 16) & 255) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255)) / 255;
    return luminance > 0.5;
  }

  function searchDecorations() {
    const accent = (options && options.theme && options.theme.cursor) || '#A6B7FF';
    return isLightBackground()
      ? {
          matchBackground: '#1A729926',
          matchBorder: '#1A729959',
          matchOverviewRuler: '#1A7299',
          activeMatchBackground: `${accent}40`,
          activeMatchBorder: accent,
          activeMatchColorOverviewRuler: accent,
        }
      : {
          matchBackground: '#91D8F733',
          matchBorder: '#91D8F766',
          matchOverviewRuler: '#91D8F7',
          activeMatchBackground: `${accent}66`,
          activeMatchBorder: accent,
          activeMatchColorOverviewRuler: accent,
        };
  }

  function find(message) {
    if (!searchAddon) {
      return;
    }

    if (!message.query) {
      clearFind();
      return;
    }

    const findOptions = {
      caseSensitive: message.caseSensitive,
      regex: message.regex,
      wholeWord: message.wholeWord,
      incremental: message.incremental,
      decorations: searchDecorations(),
    };

    lastQuery = message.query;
    try {
      if (message.backwards) {
        searchAddon.findPrevious(message.query, findOptions);
      } else {
        searchAddon.findNext(message.query, findOptions);
      }
    } catch (err) {
      // An invalid regular expression while typing is expected; report "no results".
      post({ type: 'searchResult', index: -1, count: 0 });
    }
  }

  function clearFind() {
    lastQuery = null;
    if (searchAddon) {
      searchAddon.clearDecorations();
      term.clearSelection();
    }
  }

  // ── Status overlay ────────────────────────────────────────────────────────
  function showStatus(kind, text) {
    if (kind === 'none' || !text) {
      statusEl.hidden = true;
      return;
    }

    statusEl.dataset.kind = kind;
    statusText.textContent = text;
    statusEl.hidden = false;
  }

  // ── Message dispatch ──────────────────────────────────────────────────────
  webview.addEventListener('message', (event) => {
    const message = event.data;
    if (typeof message === 'string') {
      write(message);
      return;
    }

    if (!message || typeof message !== 'object') {
      return;
    }

    switch (message.type) {
      case 'config':
        configure(message.options);
        break;
      case 'paste':
        if (term && typeof message.data === 'string') {
          term.paste(message.data);
        }
        break;
      case 'find':
        find(message);
        break;
      case 'clearFind':
        clearFind();
        break;
      case 'focus':
        if (term) {
          term.focus();
        }
        break;
      case 'action':
        runAction(message.action);
        break;
      case 'status':
        showStatus(message.kind, message.text);
        break;
      case 'completion':
        completionEnabled = message.enabled === true;
        if (!completionEnabled) {
          closePicker();
        }
        break;
      case 'dirs':
        onListing(message);
        break;
      default:
        break;
    }
  });

  function runAction(action) {
    if (!term) {
      return;
    }

    switch (action) {
      case 'clear':
        term.clear();
        break;
      case 'selectAll':
        term.selectAll();
        break;
      case 'copy':
        copySelection();
        break;
      case 'scrollToBottom':
        term.scrollToBottom();
        break;
      case 'blur':
        // Each pane has its own document; blur explicitly so only the active pane shows a focused cursor.
        term.blur();
        break;
      case 'motion:off':
      case 'motion:full':
      case 'motion:system':
        document.documentElement.dataset.motion = action.slice(7);
        break;
      default:
        break;
    }
  }

  // Read-only view of the visible buffer for automated UI verification (tests/e2e). Exposes nothing the page
  // could not already read, and grants no access to native code.
  window.shellflowBufferText = (maxLines) => {
    if (!term) {
      return '';
    }

    const buffer = term.buffer.active;
    const lines = [];
    let end = buffer.length;
    if (maxLines) {
      // Skip the blank rows below the prompt so "last N lines" means the last N lines of content.
      while (end > 0 && buffer.getLine(end - 1).translateToString(true).trim() === '') {
        end--;
      }
    }

    const start = maxLines ? Math.max(0, end - maxLines) : 0;
    for (let i = start; i < end; i++) {
      lines.push(buffer.getLine(i).translateToString(true));
    }

    return lines.join('\n').replace(/\s+$/, '');
  };

  window.addEventListener('error', (event) => {
    post({ type: 'error', message: String(event.message).slice(0, 2000) });
  });

  // Block in-renderer navigation attempts (drag-and-drop of files or links).
  window.addEventListener('dragover', (event) => event.preventDefault());
  window.addEventListener('drop', (event) => event.preventDefault());
})();
