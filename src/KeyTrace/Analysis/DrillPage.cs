using System.IO;
using System.Text;

namespace KeyTrace.Analysis;

/// <summary>
/// 生成单文件练习页，界面按 keybr 的布局来：
/// 顶部实时速度/准确率 → 一屏一行字、逐字上色 → 底部键盘高亮下一个该按的键。
///
/// 只借布局与交互，不搬它的代码：keybr.com 是 AGPL-3.0，把代码拿进来整个项目
/// 都得跟着换许可；而且它是 Node 全栈，做不到"双击就开"。
///
/// 练的是键不是选字：不让输入法参与，否则按键被 IME 吞掉，成绩没法归因到具体键。
/// </summary>
internal static class DrillPage
{
    public static void Write(string outputPath, string itemsJson, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, Build(itemsJson, title), new UTF8Encoding(false));
    }

    internal static string Build(string itemsJson, string title) => Template
        .Replace("/*__ITEMS__*/", itemsJson, StringComparison.Ordinal)
        .Replace("__TITLE__", EscapeHtml(title), StringComparison.Ordinal);

    private static string EscapeHtml(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private const string Template = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__</title>
<style>
:root{--bg:#111111;--panel:#1a1a1a;--line:#2a2a2a;--text:#e8e6e3;--dim:#8c8880;--faint:#5d5a54;
  --red:#d4503a;--green:#5ba36f;--blue:#4a7fb5;--cur:#2b2b2b}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);min-height:100vh;
  font-family:-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;user-select:none;
  display:flex;flex-direction:column;align-items:center}
.bar{width:100%;max-width:900px;display:flex;justify-content:space-between;align-items:center;
  padding:16px 24px;font-size:12px;color:var(--dim);letter-spacing:.02em}
.bar b{color:var(--text);font-weight:500;font-size:15px;font-variant-numeric:tabular-nums}
.sep{color:var(--faint);margin:0 10px}
.progress{width:100%;height:2px;background:#202020}
.progress > i{display:block;height:100%;width:0;background:var(--blue);transition:width .12s}
.stage{width:100%;max-width:900px;padding:56px 24px 0;min-height:260px}
.line{font-size:44px;line-height:1.5;letter-spacing:.02em;font-family:"Songti SC","SimSun",serif;
  display:flex;flex-wrap:wrap;align-items:baseline}
.c{position:relative;padding:2px 1px;color:var(--faint);transition:color .1s}
.c.done{color:#6f9a7c}
.c.err{color:var(--red)}
.c.cur{color:var(--text);background:var(--cur);border-radius:4px}
.c.cur::after{content:"";position:absolute;left:2px;right:2px;bottom:-2px;height:2px;background:var(--blue)}
.sub{margin-top:10px;height:24px;font-size:14px;color:var(--dim);letter-spacing:.06em}
.sub em{color:var(--blue);font-style:normal;font-family:ui-monospace,Consolas,monospace;letter-spacing:.25em}
.sub.bad em{color:var(--red)}
.kbd{width:100%;max-width:900px;padding:26px 24px 8px}
.krow{display:flex;gap:5px;margin-bottom:5px;justify-content:center}
.krow .k{width:38px;height:38px;border-radius:6px;background:#1d1d1d;border:1px solid var(--line);
  display:flex;align-items:center;justify-content:center;font-size:12px;color:var(--dim);
  font-family:ui-monospace,Consolas,monospace;transition:background .08s,border-color .08s}
.krow .k.next{background:#1d3040;border-color:var(--blue);color:#a8cbe8}
.krow .k.hit{background:#243027;border-color:var(--green);color:#a9d3b8}
.krow .k.miss{background:#3a201b;border-color:var(--red);color:#f0a898}
.krow .k.blank{background:transparent;border-color:transparent}
.controls{display:flex;gap:10px;align-items:center;margin:6px 0 30px;color:var(--dim);font-size:12px}
button{background:#202020;color:var(--text);border:1px solid var(--line);border-radius:7px;
  padding:6px 12px;font-size:12px;cursor:pointer;font-family:inherit}
button:hover{background:#282828}
.stage.done{display:none}
.summary{display:none;width:100%;max-width:900px;padding:40px 24px 60px}
.summary.show{display:block}
h2{font-size:16px;font-weight:500;margin:0 0 14px}
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:10px;margin-bottom:18px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
.card .k{color:var(--dim);font-size:12px}
.card .v{font-size:22px;font-weight:500;font-variant-numeric:tabular-nums}
.card .u{font-size:12px;color:var(--faint);margin-left:3px}
table{width:100%;border-collapse:collapse;font-size:13px}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line)}
th{color:var(--dim);font-weight:400}
td.num{text-align:right;font-variant-numeric:tabular-nums}
textarea{width:100%;height:110px;background:#161616;color:var(--dim);border:1px solid var(--line);
  border-radius:8px;padding:10px;font-family:ui-monospace,Consolas,monospace;font-size:12px;margin-top:8px}
.note{color:var(--faint);font-size:12px;margin-top:10px}
</style>
</head>
<body>
<div class="bar">
  <span>__TITLE__</span>
  <span><b id="wpm">0</b> 字/分<span class="sep">·</span><b id="acc">100</b>% 准确<span class="sep">·</span><span id="counter">0 / 0</span></span>
</div>
<div class="progress"><i id="bar"></i></div>

<div class="stage" id="stage">
  <div class="line" id="line"></div>
  <div class="sub" id="sub"></div>
  <div class="kbd"><div id="kbd"></div></div>
  <div class="controls">
    <button id="toggle" type="button">隐藏双拼码</button>
    <button id="reset" type="button">重来</button>
    <span>点一下页面再按键 · Tab 切换提示 · Esc 跳过</span>
  </div>
</div>

<div class="summary" id="summary">
  <h2>练完了</h2>
  <div class="cards" id="cards"></div>
  <div id="keytable"></div>
  <div class="note" style="display:flex;justify-content:space-between;align-items:center;gap:10px">
    <span>把下面这段贴回给我（或跑 <code>keytrace drill --record -</code>），下次就能按这次的表现出题：</span>
    <button id="copy" type="button">复制结果</button>
  </div>
  <textarea id="result" readonly></textarea>
</div>

<script>
const ITEMS = /*__ITEMS__*/;
const $ = (id) => document.getElementById(id);

let idx = 0, step = 0, showAt = 0, startedAt = 0, hideCode = false;
let attempts = 0, correct = 0;
const status = [];          // 每个字: 'todo' | 'done' | 'err'
const keyStats = {};        // 键 -> {hit, miss}
const wrongChars = [];

const rows = ['qwertyuiop', 'asdfghjkl', 'zxcvbnm'];
const kbdEls = {};
const flashTimers = {};
let subTimer = null;

function stat(k) { return keyStats[k] || (keyStats[k] = { hit: 0, miss: 0 }); }
function current() { return ITEMS[idx]; }

function buildKeyboard() {
  for (const r of rows) {
    const row = document.createElement('div');
    row.className = 'krow';
    for (const ch of r) {
      const d = document.createElement('div');
      d.className = 'k';
      d.textContent = ch.toUpperCase();
      kbdEls[ch] = d;
      row.appendChild(d);
    }
    $('kbd').appendChild(row);
  }
}

function clearNext() {
  for (const el of Object.values(kbdEls)) el.classList.remove('next');
}

function renderLine() {
  const from = Math.max(0, idx - 4);
  const to = Math.min(ITEMS.length, from + 14);
  const html = [];
  for (let i = from; i < to; i++) {
    let cls = 'c';
    if (i === idx) cls += ' cur';
    else if (status[i] === 'done') cls += ' done';
    else if (status[i] === 'err') cls += ' err';
    html.push(`<span class="${cls}">${ITEMS[i].c}</span>`);
  }
  $('line').innerHTML = html.join('');
}

function renderHint() {
  const it = current();
  if (!it) return;
  const code = it.k[0] + '  ' + it.k[1];
  $('sub').className = 'sub';
  $('sub').innerHTML = `${it.p} · <em>${hideCode ? '?&nbsp;&nbsp;?' : code}</em>`;
}

function toggleHint() {
  hideCode = !hideCode;
  $('toggle').textContent = hideCode ? '显示双拼码' : '隐藏双拼码';
  renderHint();
}

function markNext() {
  clearNext();
  const it = current();
  if (!it) return;
  const ch = it.k[step];
  if (kbdEls[ch]) kbdEls[ch].classList.add('next');
}

function flash(ch, cls) {
  const el = kbdEls[ch];
  if (!el) return;
  if (flashTimers[ch]) clearTimeout(flashTimers[ch]);
  el.classList.remove('hit', 'miss');
  el.classList.add(cls);
  flashTimers[ch] = setTimeout(() => {
    el.classList.remove(cls);
    delete flashTimers[ch];
  }, 160);
}

function render() {
  const it = current();
  if (!it) return finish();
  status[idx] = status[idx] || 'todo';
  renderLine();
  renderHint();
  markNext();
  $('counter').textContent = (idx + 1) + ' / ' + ITEMS.length;
  $('bar').style.width = (idx / ITEMS.length * 100) + '%';
  step = 0;
  showAt = performance.now();
}

function advance(ok) {
  status[idx] = ok ? 'done' : 'err';
  idx++;
  render();
  updateMetrics();
}

function updateMetrics() {
  const used = startedAt ? (performance.now() - startedAt) : 0;
  $('wpm').textContent = used > 800 ? Math.round(correct / (used / 60000)) : 0;
  $('acc').textContent = Math.round(accuracy() * 100);
}

// 准确率按"按键"算，不按"字数"算：一个字按错三次再按对，准确率就该难看，
// 否则每次错都白错了。
function accuracy() {
  let hit = 0, miss = 0;
  for (const s of Object.values(keyStats)) { hit += s.hit; miss += s.miss; }
  return hit + miss === 0 ? 1 : hit / (hit + miss);
}

document.addEventListener('keydown', (e) => {
  const it = current();
  if (!it) return;
  if (e.ctrlKey || e.altKey || e.metaKey) return;
  if (e.key === 'Escape') { e.preventDefault(); attempts++; advance(false); return; }
  if (e.key === 'Tab') { e.preventDefault(); toggleHint(); return; }

  const ch = e.key.toLowerCase();
  if (!/^[a-z]$/.test(ch)) return;
  e.preventDefault();

  if (!startedAt) startedAt = performance.now();

  const want = it.k[step];
  if (ch === want) {
    if (subTimer) { clearTimeout(subTimer); subTimer = null; }
    $('sub').className = 'sub';
    stat(ch).hit++;
    flash(ch, 'hit');
    step++;
    showAt = performance.now();
    if (step >= 2) {
      attempts++; correct++;
      advance(true);
    } else {
      markNext();
    }
  } else {
    stat(want).miss++;
    flash(ch, 'miss');
    if (subTimer) clearTimeout(subTimer);
    $('sub').className = 'sub bad';
    // 按错就重来这个字的第一个键，不往前走——keybr 也是这个规矩
    step = 0;
    showAt = performance.now();
    markNext();
    subTimer = setTimeout(() => { $('sub').className = 'sub'; subTimer = null; }, 260);
    if (!wrongChars.includes(it.c)) wrongChars.push(it.c);
  }
  updateMetrics();
});

function finish() {
  clearNext();
  $('stage').classList.add('done');
  $('bar').style.width = '100%';
  const total = ITEMS.length;
  const elapsed = startedAt ? Math.round(performance.now() - startedAt) : 0;
  const acc = accuracy() * 100;
  const speed = elapsed > 0 ? Math.round(correct / (elapsed / 60000)) : 0;

  $('cards').innerHTML = [
    ['完成', correct + ' / ' + total, '字'],
    ['准确率', acc.toFixed(0) + '%', ''],
    ['用时', (elapsed / 1000).toFixed(0), '秒'],
    ['速度', String(speed), '字/分'],
  ].map(([k, v, u]) => `<div class="card"><div class="k">${k}</div><div class="v">${v}${u ? '<span class="u">' + u + '</span>' : ''}</div></div>`).join('');

  const bad = Object.entries(keyStats).filter(([, s]) => s.miss > 0)
    .sort((a, b) => b[1].miss - a[1].miss);

  $('keytable').innerHTML = bad.length === 0
    ? '<div style="color:#6f9a7c">这次没有按错任何键。</div>'
    : '<table><tr><th>键</th><th class="num">按对</th><th class="num">按错</th><th class="num">错误率</th></tr>'
      + bad.map(([k, s]) => {
          const t = s.hit + s.miss;
          return `<tr><td>${k.toUpperCase()}</td><td class="num">${s.hit}</td>`
            + `<td class="num" style="color:#e08a78">${s.miss}</td>`
            + `<td class="num">${(s.miss / t * 100).toFixed(0)}%</td></tr>`;
        }).join('') + '</table>';

  $('result').value = JSON.stringify({
    v: 1,
    finishedAt: new Date().toISOString(),
    total: total,
    correct: correct,
    attempts: attempts,
    elapsedMs: elapsed,
    accuracy: Number(acc.toFixed(1)),
    keys: Object.entries(keyStats).map(([k, s]) => ({ key: k, hit: s.hit, miss: s.miss })),
    wrongChars: wrongChars,
  });
  $('summary').classList.add('show');
}

$('toggle').onclick = toggleHint;
$('reset').onclick = () => location.reload();
$('copy').onclick = () => {
  const text = $('result').value;
  const btn = $('copy');
  const done = () => { btn.textContent = '已复制'; setTimeout(() => { btn.textContent = '复制结果'; }, 1200); };
  function fallback() {
    $('result').select();
    try { document.execCommand('copy'); } catch {}
    done();
  }
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(text).then(done, fallback);
  } else {
    fallback();
  }
};

buildKeyboard();
render();
</script>
</body>
</html>
""";
}
