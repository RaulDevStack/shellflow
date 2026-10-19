// Minimal Chrome DevTools Protocol driver for ShellFlow's terminal renderers.
// Launch ShellFlow with WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333 first.
// Usage: node tests/e2e/cdp.mjs <command> [args...]
//   list                         list renderer pages
//   text [index]                 print the buffer of renderer #index
//   tail <index> [lines]         print only the last lines of the buffer (cheap; good for polling)
//   type <index> <text>          type text (use \r for Enter, \x03 for Ctrl+C) as real key input
//   eval <index> <expression>    evaluate JavaScript in a renderer
const port = process.env.CDP_PORT ?? '9333';

async function pages() {
  const res = await fetch(`http://127.0.0.1:${port}/json`);
  const all = await res.json();
  return all.filter((t) => t.type === 'page' && t.url.startsWith('https://terminal.shellflow.example/'));
}

async function connect(page) {
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    ws.onopen = resolve;
    ws.onerror = reject;
  });

  let id = 0;
  const pending = new Map();
  ws.onmessage = (event) => {
    const msg = JSON.parse(event.data);
    if (msg.id && pending.has(msg.id)) {
      pending.get(msg.id)(msg);
      pending.delete(msg.id);
    }
  };

  const send = (method, params = {}) =>
    new Promise((resolve) => {
      const msgId = ++id;
      pending.set(msgId, resolve);
      ws.send(JSON.stringify({ id: msgId, method, params }));
    });

  return { send, close: () => ws.close() };
}

async function evaluate(client, expression) {
  const result = await client.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (result.result?.exceptionDetails) {
    throw new Error(JSON.stringify(result.result.exceptionDetails));
  }

  return result.result?.result?.value;
}

function unescape(text) {
  return text.replace(/\\r/g, '\r').replace(/\\n/g, '\n').replace(/\\x([0-9a-fA-F]{2})/g, (_, h) => String.fromCharCode(parseInt(h, 16)));
}

async function typeText(client, text) {
  await evaluate(client, "document.querySelector('.xterm-helper-textarea').focus()");
  for (const ch of text) {
    if (ch === '\r') {
      await client.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
      await client.send('Input.dispatchKeyEvent', { type: 'char', text: '\r' });
      await client.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
    } else if (ch === '\x03') {
      await client.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: 'c', code: 'KeyC', windowsVirtualKeyCode: 67, modifiers: 2 });
      await client.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'c', code: 'KeyC', windowsVirtualKeyCode: 67, modifiers: 2 });
    } else {
      await client.send('Input.insertText', { text: ch });
    }
  }
}

const [command, ...args] = process.argv.slice(2);
const list = await pages();

if (command === 'list') {
  list.forEach((p, i) => console.log(`${i}: ${p.id} ${p.url}`));
  process.exit(0);
}

const index = Number(args[0] ?? 0);
const page = list[index];
if (!page) {
  console.error(`No renderer #${index} (found ${list.length}).`);
  process.exit(1);
}

const client = await connect(page);
try {
  if (command === 'text') {
    console.log(await evaluate(client, 'window.shellflowBufferText()'));
  } else if (command === 'tail') {
    const lines = Number(args[1] ?? 50);
    console.log(await evaluate(client, `window.shellflowBufferText(${Number.isFinite(lines) ? lines : 50})`));
  } else if (command === 'type') {
    await typeText(client, unescape(args.slice(1).join(' ')));
  } else if (command === 'key') {
    // key <index> <code> <key> <vk> <modifiers>  (modifiers: 1 Alt, 2 Ctrl, 4 Meta, 8 Shift)
    const [, code, key, vk, modifiers] = args;
    const base = { code, key, windowsVirtualKeyCode: Number(vk), modifiers: Number(modifiers ?? 0) };
    await evaluate(client, "document.querySelector('.xterm-helper-textarea').focus()");
    await client.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', ...base });
    await client.send('Input.dispatchKeyEvent', { type: 'keyUp', ...base });
  } else if (command === 'eval') {
    console.log(JSON.stringify(await evaluate(client, args.slice(1).join(' ')), null, 2));
  } else {
    console.error('Unknown command');
    process.exitCode = 1;
  }
} finally {
  client.close();
}
