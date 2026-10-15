// Run: node --test tests/web-dashboard/dashboard.test.mjs
// Uses the shipped HTML with realistic read-only API fixtures. Authentication has .NET integration coverage.
import assert from 'node:assert/strict';
import { after, before, beforeEach, test } from 'node:test';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { chromium } from '../../website/node_modules/playwright/index.mjs';

const source = await readFile(new URL('../../src/TraceScope.Core/LocalWebServer.cs', import.meta.url), 'utf8');
const html = source.match(/private const string Dashboard = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1]
  .replace(/^ {8}/gm, '').replace('<!--AUTH-->', '');
const hour = 3_600_000, minimumWindow = 10_000, maximumWindow = 24 * hour;
const hop = number => ({ hop: number, address: `192.168.30.${number}`, hostname: `Hop ${number}`, averageMs: 20, minimumMs: 12, maximumMs: 42, jitterMs: 500, lossPercent: 0, received: 10, sent: 10 });
const target = id => ({ id, target: id === 'target-1' ? '8.8.8.8' : '1.1.1.1', resolvedAddress: id === 'target-1' ? '8.8.8.8' : '1.1.1.1', health: 'Healthy', options: { protocol: 'Icmp', interval: '00:00:02.5000000' }, snapshot: { destination: hop(2), hops: [hop(1), hop(2)], round: 10, samplingInterval: '00:00:02.5000000' } });
let browser, server, origin, requests, scenario, delayedHop, historyGate, historyError, fixedHistoryEnd;

function responseData(url) {
  const selectedHop = Number(url.searchParams.get('hop'));
  const from = Date.parse(url.searchParams.get('from')), to = Date.parse(url.searchParams.get('to')), duration = to - from;
  const step = Math.max(2500, Math.ceil(duration / 1000 / 2500) * 2500);
  let times = [from + duration * 0.25, from + duration * 0.25 + step, from + duration * 0.25 + step * 2];
  if (scenario === 'empty') times = [];
  if (scenario === 'single') times = [from + duration * 0.7];
  if (scenario === 'recent') times = Array.from({ length: 120 }, (_, index) => fixedHistoryEnd - (119 - index) * 2500).filter(time => time >= from && time <= to);
  if (scenario === 'dense') times = Array.from({ length: 1000 }, (_, index) => from + duration * (index + 0.5) / 1000);
  if (scenario === 'sparse') times = [from + duration * 0.1, from + duration * 0.15, from + duration * 0.9];
  if (['gap', 'loss', 'epoch'].includes(scenario)) times = [from + duration * 0.25, from + duration * 0.25 + step, from + duration * 0.75, from + duration * 0.75 + step];
  if (['loss', 'epoch'].includes(scenario)) times = Array.from({ length: 4 }, (_, index) => from + duration * 0.25 + step * index);
  const points = times.map((timestamp, index) => {
    const count = Math.max(1, Math.round(step / 2500));
    const averageMs = 20 + selectedHop + index;
    const spread = count > 1 ? 8 : 0;
    return { timestamp: new Date(timestamp).toISOString(), averageMs, minimumMs: averageMs - spread, maximumMs: averageMs + spread, jitterMs: 500 + index, count, received: count, errors: 0, lossPercent: 0, address: '192.168.30.1', measurementEpoch: scenario === 'epoch' && index > 1 ? 2 : 1 };
  });
  if (scenario === 'loss' && points.length) Object.assign(points[1], { averageMs: null, minimumMs: null, maximumMs: null, jitterMs: null, received: 0, lossPercent: 100 });
  if (scenario === 'raw') for (const point of points) Object.assign(point, { count: 1, received: 1, minimumMs: point.averageMs, maximumMs: point.averageMs });
  return { points, sampleCount: points.reduce((sum, point) => sum + point.count, 0), truncated: false };
}

before(async () => {
  browser = await chromium.launch({ headless: true });
  server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://localhost');
    response.setHeader('Content-Type', url.pathname === '/' ? 'text/html' : 'application/json');
    if (url.pathname === '/') return response.end(html);
    if (url.pathname === '/api/targets') return response.end(JSON.stringify([target('target-1'), target('target-2')]));
    if (url.pathname === '/api/sessions') return response.end('[]');
    if (url.pathname.startsWith('/api/history/')) {
      requests.push(url);
      const data = responseData(url), error = historyError;
      if (historyGate) await historyGate;
      if (Number(url.searchParams.get('hop')) === delayedHop) await new Promise(resolve => setTimeout(resolve, 300));
      if (error) { response.statusCode = 503; return response.end('{}'); }
      return response.end(JSON.stringify(data));
    }
    response.statusCode = 404;
    response.end('{}');
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
});
after(async () => { await browser?.close(); await new Promise(resolve => server?.close(resolve)); });
beforeEach(() => { requests = []; scenario = 'normal'; delayedHop = -1; historyGate = null; historyError = false; fixedHistoryEnd = Date.now() - 5000; });

async function painted(page) {
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
}
async function historyAction(page, action, predicate = () => true) {
  const pending = page.waitForResponse(response => new URL(response.url()).pathname.startsWith('/api/history/') && predicate(new URL(response.url())));
  await action();
  const response = await pending;
  await response.finished();
  await painted(page);
  return new URL(response.url());
}
async function dashboard(t, { viewport = { width: 1440, height: 1000 }, blockedStorage = false, waitForHistory = true, touch = false } = {}) {
  const context = await browser.newContext({ viewport, hasTouch: touch, isMobile: touch });
  t.after(() => context.close());
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  t.after(() => assert.deepEqual(errors, [], 'Dashboard JavaScript must not throw'));
  await page.addInitScript(({ blockedStorage }) => {
    window.setInterval = () => 0;
    if (blockedStorage) {
      Storage.prototype.getItem = () => { throw new Error('Storage blocked'); };
      Storage.prototype.setItem = () => { throw new Error('Storage blocked'); };
    }
    window.chartDraws = {};
    const canvas = CanvasRenderingContext2D.prototype;
    const paths = new WeakMap();
    for (const name of ['scale', 'setTransform', 'resetTransform', 'clearRect']) {
      const original = canvas[name];
      canvas[name] = function (...args) {
        window.chartDraws[this.canvas.id] = { strokes: [], labels: [], fills: [], arcs: [] };
        paths.set(this, []);
        return original.apply(this, args);
      };
    }
    const beginPath = canvas.beginPath;
    canvas.beginPath = function (...args) { paths.set(this, []); return beginPath.apply(this, args); };
    for (const name of ['moveTo', 'lineTo']) {
      const original = canvas[name];
      canvas[name] = function (...args) { paths.get(this)?.push({ type: name, x: args[0], y: args[1] }); return original.apply(this, args); };
    }
    const stroke = canvas.stroke, fillText = canvas.fillText, fillRect = canvas.fillRect, arc = canvas.arc;
    canvas.stroke = function (...args) { window.chartDraws[this.canvas.id]?.strokes.push({ color: this.strokeStyle, dash: this.getLineDash(), path: [...(paths.get(this) ?? [])] }); return stroke.apply(this, args); };
    canvas.fillText = function (text, ...args) { window.chartDraws[this.canvas.id]?.labels.push(String(text)); return fillText.call(this, text, ...args); };
    canvas.fillRect = function (...args) { window.chartDraws[this.canvas.id]?.fills.push({ color: this.fillStyle, bounds: args }); return fillRect.apply(this, args); };
    canvas.arc = function (...args) { window.chartDraws[this.canvas.id]?.arcs.push(args); return arc.apply(this, args); };
  }, { blockedStorage });
  await page.goto(origin);
  const choose = () => page.getByRole('button', { name: '8.8.8.8', exact: true }).click();
  if (waitForHistory) await historyAction(page, choose); else await choose();
  return page;
}
const draw = (page, id = 'chart') => page.evaluate(id => window.chartDraws[id], id);
const timeline = page => page.evaluate(() => ({ ...timeline }));
const colors = rendering => rendering.strokes.map(stroke => stroke.color);
const maximumTick = rendering => Math.max(...rendering.labels.filter(label => /^\d+(?:\.\d+)?$/.test(label)).map(Number));
function trace(rendering, color) {
  return rendering.strokes.find(stroke => stroke.color === color && stroke.path.length >= 2 && stroke.path.every((point, index, path) => index === 0 || point.x > path[index - 1].x))?.path;
}
const approximately = (actual, expected, tolerance = 5) => assert.ok(Math.abs(actual - expected) <= tolerance, `${actual} should be close to ${expected}`);
async function resizeCheck(page, width, height) {
  await page.setViewportSize({ width, height });
  await page.waitForFunction(() => ['chart', 'jitterChart'].every(id => { const canvas = document.getElementById(id); return canvas.width === Math.round(canvas.clientWidth * devicePixelRatio); }));
}

// UI, distinct series, range and preference regressions.
test('separate labeled panels have independent scales and synchronized timestamps', async t => {
  const page = await dashboard(t);
  assert.equal(await page.getByLabel('Show Jitter').isChecked(), true);
  assert.equal(await page.locator('#jitterPanel').isVisible(), true);
  assert.match(await page.locator('#detail').innerText(), /Latency \(ms\)/);
  assert.match(await page.locator('#jitterPanel').innerText(), /Jitter \(ms\)/);
  const latency = await draw(page), jitter = await draw(page, 'jitterChart');
  assert.ok(colors(latency).includes('#c9d9ca'));
  assert.ok(!colors(latency).includes('#e7a55d'));
  assert.ok(colors(jitter).includes('#e7a55d'));
  assert.ok(maximumTick(latency) >= 30 && maximumTick(latency) < 500);
  assert.ok(maximumTick(jitter) >= 502);
  const latencyPath = trace(latency, '#c9d9ca'), jitterPath = trace(jitter, '#e7a55d');
  assert.ok(latencyPath && jitterPath, 'Both average and jitter should have visible series');
  assert.deepEqual(latencyPath.map(point => point.x), jitterPath.map(point => point.x));
  assert.ok(latency.strokes.some(stroke => stroke.path.some((point, index, path) => index > 0 && point.x === path[index - 1].x && point.y !== path[index - 1].y)), 'Latency preserves min/max whiskers');
  const initialTimeline = await timeline(page), count = requests.length;
  await page.getByLabel('Show Jitter').uncheck();
  assert.equal(await page.locator('#jitterPanel').isVisible(), false);
  assert.equal(requests.length, count);
  assert.deepEqual(await timeline(page), initialTimeline);
  assert.equal(maximumTick(await draw(page)), maximumTick(latency));
  await page.getByLabel('Show Jitter').check();
  assert.equal(await page.locator('#jitterPanel').isVisible(), true);
});

test('jitter preference persists through reload and blocked storage remains usable', async t => {
  const page = await dashboard(t);
  await page.getByLabel('Show Jitter').uncheck();
  assert.deepEqual(await page.evaluate(() => Object.entries(localStorage)), [['pngr.dashboard.showJitter', 'false']]);
  await page.reload();
  await historyAction(page, () => page.getByRole('button', { name: '8.8.8.8', exact: true }).click());
  assert.equal(await page.getByLabel('Show Jitter').isChecked(), false);
  assert.equal(await page.locator('#jitterPanel').isVisible(), false);
  const blocked = await dashboard(t, { blockedStorage: true });
  assert.equal(await blocked.getByLabel('Show Jitter').isChecked(), true);
  await blocked.getByLabel('Show Jitter').uncheck();
  assert.equal(await blocked.locator('#jitterPanel').isVisible(), false);
});

for (const [name, averageMs, minimumMs, maximumMs, jitterMs] of [
  ['all-zero values', 0, 0, 0, 0],
  ['latency spike and low jitter', 20, 1, 10000, 0.05],
  ['low latency and jitter spike', 0.05, 0.01, 0.1, 10000],
]) {
  test(`${name} have independent finite axes and unclipped values`, async t => {
    const page = await dashboard(t);
    const dimensions = await page.evaluate(values => {
      const points = [0, 1, 2].map(index => ({ ...values, time: timeline.from + timeline.duration / 4 + index * 2500, count: 2, received: 2, errors: 0, lossPercent: 0, measurementEpoch: 1 }));
      chartData = { key: selectionKey(), from: timeline.from, to: timeline.to, bucketMs: timeline.duration / MAX_POINTS, points, sampleCount: 6 };
      redraw();
      return Object.fromEntries(['chart', 'jitterChart'].map(id => [id, { height: document.getElementById(id).clientHeight, width: document.getElementById(id).clientWidth }]));
    }, { averageMs, minimumMs, maximumMs, jitterMs });
    const latency = await draw(page), jitter = await draw(page, 'jitterChart');
    assert.ok(Number.isFinite(maximumTick(latency)) && maximumTick(latency) > 0);
    assert.ok(Number.isFinite(maximumTick(jitter)) && maximumTick(jitter) > 0);
    assert.ok(maximumTick(latency) >= maximumMs);
    assert.ok(maximumTick(jitter) >= jitterMs);
    if (name === 'all-zero values') {
      assert.ok(maximumTick(latency) <= 10 && maximumTick(jitter) <= 1, 'Empty magnitude uses readable small positive scales');
    } else if (maximumMs > 1) {
      assert.ok(maximumTick(jitter) < 1, 'Latency spikes do not flatten the jitter panel');
    } else {
      assert.ok(maximumTick(latency) <= 1, 'Jitter spikes do not flatten the latency panel');
    }
    for (const [id, rendering, color] of [['chart', latency, '#c9d9ca'], ['jitterChart', jitter, '#e7a55d']]) {
      const plotted = rendering.strokes.filter(stroke => stroke.color === color || (id === 'chart' && stroke.dash.length === 0 && stroke.path.length === 2 && stroke.path[0].x === stroke.path[1].x));
      assert.ok(plotted.length > 0);
      for (const stroke of plotted) for (const point of stroke.path) {
        assert.ok(Number.isFinite(point.x) && Number.isFinite(point.y));
        assert.ok(point.y >= 10 - 0.001 && point.y <= dimensions[id].height - 24 + 0.001, 'The average and extrema stay within the plot');
        assert.ok(point.x >= 56 && point.x <= dimensions[id].width - 12);
      }
    }
  });
}

test('one bounded 1000-point HTTP response feeds both graph panels', async t => {
  scenario = 'dense';
  const page = await dashboard(t);
  assert.equal(requests.length, 1, 'The two panels share one history request');
  assert.equal(requests[0].searchParams.get('maxPoints'), '1000');
  for (const [id, color] of [['chart', '#c9d9ca'], ['jitterChart', '#e7a55d']]) {
    const path = trace(await draw(page, id), color);
    assert.equal(path.length, 1000);
    assert.ok(path.every(point => Number.isFinite(point.x) && Number.isFinite(point.y)));
  }
  assert.match(await page.locator('#chartInfo').innerText(), /2000 samples/);
});

test('every history range and hop requests bounded read-only data', async t => {
  const page = await dashboard(t);
  for (const hop of ['0', '1', '2']) {
    if (hop !== '0') await historyAction(page, () => page.locator('#hop').selectOption(hop));
    for (const minutes of ['5', '30', '60', '360', '1440']) {
      const request = await historyAction(page, () => page.locator('#range').selectOption(minutes));
      assert.equal(request.searchParams.get('hop'), hop);
      assert.equal(request.searchParams.get('maxPoints'), '1000');
      approximately(Date.parse(request.searchParams.get('to')) - Date.parse(request.searchParams.get('from')), Number(minutes) * 60000);
      const view = await timeline(page);
      approximately(view.duration, Number(minutes) * 60000);
      assert.equal(view.live, true);
      const rendering = await draw(page), path = trace(rendering, '#c9d9ca');
      const height = await page.locator('#chart').evaluate(canvas => canvas.clientHeight);
      const expectedY = height - 24 - (20 + Number(hop)) / maximumTick(rendering) * (height - 24 - 10);
      approximately(path[0].y, expectedY, 0.001);
    }
  }
  await historyAction(page, () => page.getByRole('button', { name: '1.1.1.1', exact: true }).click());
  assert.equal(requests.at(-1).pathname, '/api/history/target-2');
  assert.equal(requests.at(-1).searchParams.get('hop'), '0');
});

test('button zoom, pan, live and reset share one bounded timeline', async t => {
  const page = await dashboard(t);
  const initial = await timeline(page);
  approximately(initial.duration, hour);
  assert.equal(initial.live, true);
  const afterMalformedWindows = await page.evaluate(() => {
    const now = Date.now();
    for (const [from, to] of [[NaN, now], [now - 10000, Infinity], [-Infinity, now], [2, 1]]) setWindow(from, to, false);
    return { ...timeline };
  });
  assert.deepEqual(afterMalformedWindows, initial, 'Malformed windows cannot corrupt the shared timeline');
  await historyAction(page, () => page.locator('#zoomIn').click());
  const zoomed = await timeline(page);
  approximately(zoomed.duration, hour / 2);
  approximately((zoomed.from + zoomed.to) / 2, (initial.from + initial.to) / 2, 20);
  assert.equal(zoomed.live, false);
  await historyAction(page, () => page.locator('#older').click());
  const earlier = await timeline(page);
  approximately(earlier.duration, zoomed.duration);
  approximately(earlier.to, zoomed.from);
  await historyAction(page, () => page.locator('#newer').click());
  approximately((await timeline(page)).to, zoomed.to);
  await historyAction(page, () => page.locator('#live').click());
  const live = await timeline(page);
  assert.equal(live.live, true);
  approximately(live.duration, zoomed.duration);
  approximately(live.to, Date.now(), 3000);
  await historyAction(page, () => page.locator('#resetZoom').click());
  approximately((await timeline(page)).duration, hour);
  await historyAction(page, () => page.evaluate(() => { for (let i = 0; i < 20; i++) document.getElementById('zoomIn').click(); }));
  approximately((await timeline(page)).duration, minimumWindow);
  await historyAction(page, () => page.evaluate(() => { for (let i = 0; i < 24; i++) document.getElementById('zoomOut').click(); }));
  assert.ok((await timeline(page)).duration <= maximumWindow, 'Zoom out stays inside its available selected scope');
  await historyAction(page, () => page.locator('#range').selectOption('1440'));
  approximately((await timeline(page)).duration, maximumWindow);
  const count = requests.length;
  await page.locator('#zoomOut').click();
  approximately((await timeline(page)).duration, maximumWindow);
  assert.equal(requests.length, count, 'Zoom out at the maximum does not refetch');
});

test('mouse wheel zoom anchors the cursor and drag selects a shared time interval', async t => {
  const page = await dashboard(t);
  const canvas = page.locator('#chart'), bounds = await canvas.boundingBox();
  const plotWidth = await canvas.evaluate(canvas => canvas.clientWidth - 56 - 12), cursorX = bounds.x + 56 + plotWidth * 0.3;
  await page.mouse.move(cursorX, bounds.y + 70);
  const initial = await timeline(page), anchor = initial.from + initial.duration * 0.3;
  await historyAction(page, () => page.mouse.wheel(0, -200));
  const wheeled = await timeline(page);
  assert.ok(wheeled.duration < initial.duration);
  approximately(wheeled.from + wheeled.duration * 0.3, anchor, 2000);
  const start = bounds.x + 56 + plotWidth * 0.2, finish = bounds.x + 56 + plotWidth * 0.7;
  await historyAction(page, async () => { await page.mouse.move(start, bounds.y + 100); await page.mouse.down(); await page.mouse.move(finish, bounds.y + 100, { steps: 8 }); await page.mouse.up(); });
  const dragged = await timeline(page);
  approximately(dragged.duration, wheeled.duration * 0.5, 5000);
  assert.equal(dragged.live, false);
  await historyAction(page, () => canvas.dblclick({ position: { x: 150, y: 80 } }));
  approximately((await timeline(page)).duration, hour);
});

test('short drags do not zoom and rapid wheel inputs debounce history queries', async t => {
  const page = await dashboard(t);
  const bounds = await page.locator('#chart').boundingBox(), before = await timeline(page), count = requests.length;
  await page.mouse.move(bounds.x + 120, bounds.y + 80);
  await page.mouse.down(); await page.mouse.move(bounds.x + 124, bounds.y + 80); await page.mouse.up();
  assert.deepEqual(await timeline(page), before);
  assert.equal(requests.length, count);
  await historyAction(page, () => page.evaluate(() => { const canvas = document.getElementById('chart'), rect = canvas.getBoundingClientRect(); for (let i = 0; i < 8; i++) canvas.dispatchEvent(new WheelEvent('wheel', { clientX: rect.x + 140, clientY: rect.y + 70, deltaY: -80, bubbles: true, cancelable: true })); }));
  assert.ok(requests.length - count <= 2, 'Rapid zoom events should coalesce into a bounded number of requests');
  assert.ok((await timeline(page)).duration < before.duration);
});

test('fit searches the selected range and fits sparse timestamps with padding', async t => {
  scenario = 'sparse';
  const page = await dashboard(t);
  const initial = await timeline(page);
  await historyAction(page, () => page.locator('#zoomIn').click());
  await page.locator('#fit').click();
  await page.waitForFunction(expected => Math.abs(timeline.duration - expected) < 100, hour * 0.88);
  const fitted = await timeline(page);
  approximately(fitted.to - fitted.from, hour * 0.88, 100);
  assert.equal(await page.locator('#range').inputValue(), '60');
  assert.equal(fitted.live, false);
  assert.ok(fitted.from >= initial.to - hour - 1000 && fitted.to <= initial.to + 1000);
  assert.ok(requests.some(request => Math.abs(Date.parse(request.searchParams.get('to')) - Date.parse(request.searchParams.get('from')) - hour) < 10));
});

test('fit handles a single timestamp and empty history without unbounded searches', async t => {
  scenario = 'single';
  const page = await dashboard(t);
  await page.locator('#fit').click();
  await page.waitForFunction(() => timeline.duration <= 10001);
  const single = await timeline(page);
  assert.ok(single.duration >= minimumWindow);
  scenario = 'empty';
  await historyAction(page, () => page.locator('#range').selectOption('30'));
  const beforeFit = await timeline(page), beforeRequests = requests.length;
  await page.locator('#fit').click();
  await page.waitForFunction(() => /no (samples|history|data)/i.test(document.getElementById('chartInfo').textContent));
  assert.deepEqual(await timeline(page), beforeFit);
  assert.ok(requests.length - beforeRequests <= 1);
});

test('fitting a full historical 24-hour scope keeps both endpoint samples', async t => {
  const page = await dashboard(t);
  const fitted = await page.evaluate(() => {
    const to = Date.now() - 2 * 3600000, from = to - MAX_WINDOW;
    const points = [from, to].map((time, index) => ({ time, timestamp: new Date(time).toISOString(), averageMs: 20 + index, minimumMs: 20 + index, maximumMs: 20 + index, jitterMs: 1, count: 1, received: 1, errors: 0, lossPercent: 0, measurementEpoch: 1 }));
    const data = { key: selectionKey(), from, to, bucketMs: MAX_WINDOW / MAX_POINTS, points, sampleCount: 2 };
    scope = { from, to }; scopeData = data; chartData = data;
    setWindow(from, to, false); fitData(data);
    return { ...timeline, first: points[0].time, last: points[1].time, visible: visiblePoints().length };
  });
  approximately(fitted.duration, maximumWindow);
  assert.ok(fitted.from <= fitted.first && fitted.to >= fitted.last);
  assert.equal(fitted.visible, 2, 'Fit padding must not crop either boundary');
});

test('live refresh follows now at the chosen zoom while paused inspection stays fixed', async t => {
  const page = await dashboard(t);
  await historyAction(page, () => page.locator('#zoomIn').click());
  await historyAction(page, () => page.locator('#live').click());
  const before = await timeline(page);
  await page.evaluate(() => { const fixed = Date.now() + 5000; Date.now = () => fixed; });
  await historyAction(page, () => page.evaluate(() => refresh()));
  const followed = await timeline(page);
  assert.equal(followed.live, true);
  approximately(followed.duration, before.duration);
  assert.ok(followed.to >= before.to + 4500);
  await historyAction(page, () => page.locator('#older').click());
  const paused = await timeline(page), count = requests.length;
  await page.evaluate(() => refresh());
  assert.deepEqual(await timeline(page), paused);
  assert.equal(requests.length, count, 'Inspecting a fixed window does not follow periodic refresh');
});

for (const mode of ['gap', 'loss', 'epoch']) {
  test(`${mode} boundaries break the average and jitter traces`, async t => {
    scenario = mode;
    const page = await dashboard(t);
    for (const [id, color] of [['chart', '#c9d9ca'], ['jitterChart', '#e7a55d']]) {
      const path = trace(await draw(page, id), color);
      assert.ok(path, `${id} should render a trace`);
      assert.ok(path.filter(point => point.type === 'moveTo').length >= 2, `${id} must not bridge ${mode} boundaries`);
    }
  });
}

test('missing bucket slots break traces and gap inspection never borrows a nearby point', async t => {
  const page = await dashboard(t);
  await page.evaluate(() => {
    const from = Date.now() - 1000000, to = from + 1000000;
    targets[0].snapshot.samplingInterval = '00:00:00.1000000';
    const points = [900, 2100].map((offset, index) => ({ time: from + offset, timestamp: new Date(from + offset).toISOString(), averageMs: 20 + index, minimumMs: 20 + index, maximumMs: 20 + index, jitterMs: 1 + index, count: 1, received: 1, errors: 0, lossPercent: 0, measurementEpoch: 1 }));
    chartData = { key: selectionKey(), from, to, bucketMs: 1000, points, sampleCount: 2 };
    scope = { from, to }; scopeData = null; setWindow(from, to, false); redraw();
  });
  for (const [id, color] of [['chart', '#c9d9ca'], ['jitterChart', '#e7a55d']]) {
    const path = trace(await draw(page, id), color);
    assert.equal(path.filter(point => point.type === 'moveTo').length, 2, 'A 1200ms gap may still skip an empty bucket');
    assert.equal(path.filter(point => point.type === 'lineTo').length, 0);
  }
  await page.locator('#chart').evaluate(canvas => {
    const rect = canvas.getBoundingClientRect(), d = dimensions(canvas);
    inspect({ clientX: rect.left + d.left + 1100 / timeline.duration * (d.right - d.left), clientY: rect.top + 40 }, canvas);
  });
  const tooltip = await page.locator('#inspection').innerText();
  assert.match(tooltip, /No recorded measurement/i);
  assert.match(tooltip, /Latency: Unavailable/);
  assert.doesNotMatch(tooltip, /Average latency:/);
});

test('hover inspection describes aggregated buckets and raw measurements', async t => {
  const page = await dashboard(t), bounds = await page.locator('#chart').boundingBox();
  const plotWidth = await page.locator('#chart').evaluate(canvas => canvas.clientWidth - 68);
  await page.mouse.move(bounds.x + 56 + plotWidth * 0.25, bounds.y + 100);
  await page.waitForFunction(() => /Average latency/i.test(document.getElementById('inspection').textContent));
  let text = await page.locator('#inspection').innerText();
  assert.match(text, /Average latency/i); assert.match(text, /Jitter/i); assert.match(text, /bucket/i); assert.match(text, /ms/);
  const crosshair = async id => {
    const stroke = (await draw(page, id)).strokes.find(stroke => stroke.dash.length > 0);
    assert.ok(stroke, `${id} should render an inspection crosshair`);
    return stroke.path;
  };
  const latencyCrosshair = await crosshair('chart'), jitterCrosshair = await crosshair('jitterChart');
  assert.equal(latencyCrosshair[0].x, jitterCrosshair[0].x, 'Hover inspection uses the same timestamp on both canvases');
  scenario = 'raw';
  await historyAction(page, () => page.locator('#range').selectOption('5'));
  const next = await page.locator('#chart').boundingBox();
  await page.mouse.move(next.x + 56 + plotWidth * 0.25, next.y + 100);
  await page.waitForFunction(() => /Average latency/i.test(document.getElementById('inspection').textContent));
  text = await page.locator('#inspection').innerText();
  assert.doesNotMatch(text, /aggregated bucket/i);
  assert.match(text, /20(?:\.0)?\s*ms/);
});

test('late requests never replace a newer hop and pending polling cannot starve history', async t => {
  const page = await dashboard(t);
  const aborted = [];
  page.on('requestfailed', request => { if (new URL(request.url()).pathname.startsWith('/api/history/')) aborted.push(request); });
  delayedHop = 1;
  await page.locator('#hop').selectOption('1');
  await historyAction(page, () => page.locator('#hop').selectOption('2'), url => url.searchParams.get('hop') === '2');
  const newest = await draw(page);
  await page.waitForTimeout(350);
  assert.deepEqual(await draw(page), newest);
  assert.ok(aborted.some(request => new URL(request.url()).searchParams.get('hop') === '1'), 'Superseded history requests should be aborted');
  let releaseHistory;
  historyGate = new Promise(resolve => { releaseHistory = resolve; });
  t.after(() => releaseHistory());
  await page.locator('#range').selectOption('5');
  const count = requests.length;
  await page.evaluate(() => Promise.race([Promise.all([refresh(), refresh(), refresh()]), new Promise(resolve => setTimeout(resolve, 500))]));
  assert.ok(requests.length - count <= 1, 'Polling must not replace the matching pending history request');
  releaseHistory();
  await page.waitForFunction(() => /samples/i.test(document.getElementById('chartInfo').textContent));
  assert.ok(colors(await draw(page)).includes('#c9d9ca'));
});

test('loading, empty and error states resize without stale lines and retry successfully', async t => {
  let releaseHistory;
  historyGate = new Promise(resolve => { releaseHistory = resolve; });
  t.after(() => releaseHistory());
  const page = await dashboard(t, { waitForHistory: false });
  await resizeCheck(page, 390, 844);
  assert.ok((await draw(page)).labels.some(label => /loading/i.test(label)));
  releaseHistory(); historyGate = null;
  await page.waitForFunction(() => /samples/i.test(document.getElementById('chartInfo').textContent));
  scenario = 'empty';
  await historyAction(page, () => page.locator('#range').selectOption('5'));
  assert.ok((await draw(page)).labels.some(label => /no (samples|history|data)/i.test(label)));
  historyError = true;
  await historyAction(page, () => page.locator('#range').selectOption('30'));
  assert.match(await page.locator('#chartInfo').innerText(), /not available|unavailable|error/i);
  await resizeCheck(page, 768, 1024);
  historyError = false; scenario = 'normal';
  await historyAction(page, () => page.evaluate(() => refresh()));
  assert.ok(colors(await draw(page)).includes('#c9d9ca'));
});

for (const [name, width, height] of [['desktop', 1440, 1000], ['tablet', 768, 1024], ['mobile', 390, 844]]) {
  test(`${name} controls and both panels fit and resize without refetching`, async t => {
    const page = await dashboard(t, { viewport: { width, height } });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    for (const selector of ['#showJitter', '#range', '#hop', '#chart', '#jitterChart', '#zoomIn', '#zoomOut', '#fit', '#resetZoom']) {
      const bounds = await page.locator(selector).boundingBox();
      assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width, `${selector} must fit the viewport`);
    }
    const count = requests.length;
    await resizeCheck(page, width - 50, height);
    assert.equal(requests.length, count);
    assert.ok(colors(await draw(page, 'jitterChart')).includes('#e7a55d'));
  });
}

test('touch taps inspect and zoom buttons work without hijacking page scrolling', async t => {
  const page = await dashboard(t, { viewport: { width: 390, height: 844 }, touch: true });
  const canvas = page.locator('#chart');
  assert.notEqual(await canvas.evaluate(node => getComputedStyle(node).touchAction), 'none');
  const before = await timeline(page);
  await canvas.tap({ position: { x: 56 + (await canvas.boundingBox()).width * 0.2, y: 100 } });
  assert.deepEqual(await timeline(page), before);
  assert.equal(await page.locator('#inspection').isVisible(), true);
  assert.match(await page.locator('#inspection').innerText(), /Jitter:/);
  await page.evaluate(() => window.dispatchEvent(new Event('scroll')));
  assert.equal(await page.locator('#inspection').isVisible(), false, 'Scrolling dismisses a fixed touch tooltip');
  await historyAction(page, () => page.locator('#zoomIn').tap());
  assert.ok((await timeline(page)).duration < before.duration);
});

// Real timestamps stay fixed as query windows change; a recent session must not slide out of view.
for (const input of ['button', 'wheel']) {
  test(`${input} zoom keeps recently started monitoring in view`, async t => {
    scenario = 'recent';
    const page = await dashboard(t);
    assert.equal(await page.evaluate(() => visiblePoints().length), 120);
    for (let index = 0; index < 3; index++) {
      const previousBucketEnd = await page.evaluate(() => Math.min(timeline.to, chartData.from + (Math.floor((chartData.points.at(-1).time-chartData.from)/chartData.bucketMs)+1)*chartData.bucketMs));
      await historyAction(page, async () => {
        if (input === 'button') await page.locator('#zoomIn').click();
        else {
          const canvas = page.locator(index % 2 ? '#jitterChart' : '#chart');
          const bounds = await canvas.boundingBox();
          await page.mouse.move(bounds.x + 120, bounds.y + 40);
          await page.mouse.wheel(0, -200);
        }
      });
      assert.equal(await page.evaluate(() => visiblePoints().length), 120, 'Zoom must retain the available five minutes while the window is wider');
      const latest = await page.evaluate(() => ({ ...timeline }));
      assert.ok(latest.to >= previousBucketEnd - 1, 'Keep the whole newest aggregate bucket, not only its first timestamp');
      assert.ok(latest.to >= fixedHistoryEnd);
      assert.ok(latest.from <= fixedHistoryEnd - 119 * 2500);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      for (const [id, color] of [['chart', '#c9d9ca'], ['jitterChart', '#e7a55d']]) {
        const path = trace(await draw(page, id), color);
        assert.ok(path?.length, 'Both panels retain visible measurements');
      }
    }
  });
}
