using System.IO;
using System.Text;

namespace KeyTrace.Analysis;

/// <summary>
/// 生成单文件练习页：一屏一个字，打它的小鹤双拼两键，错了当场标红，练完给成绩。
///
/// 为什么不用输入法：这里练的是"这个字该按哪两个键"，不是选字。让输入法参与，
/// 键位信息就被它吞掉了，成绩也没法归因到具体的键。
/// 练完把结果贴回来，才有"下次出什么"的依据——动态调整的前提是成绩能回流。
/// </summary>
internal static class DrillPage
{
    public static void Write(string outputPath, string itemsJson, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        string html = Template
            .Replace("/*__ITEMS__*/", itemsJson, StringComparison.Ordinal)
            .Replace("__TITLE__", title, StringComparison.Ordinal);

        File.WriteAllText(outputPath, html, new UTF8Encoding(false));
    }

    internal static string Build(string itemsJson, string title) => Template
        .Replace("/*__ITEMS__*/", itemsJson, StringComparison.Ordinal)
        .Replace("__TITLE__", title, StringComparison.Ordinal);

    private const string Template = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__</title>
<style>
:root{--bg:#161616;--panel:#1f1f1f;--line:#2e2e2e;--text:#e8e6e3;--dim:#9a968f;--faint:#6b675f;
  --red:#d4503a;--green:#5ba36f;--blue:#4a7fb5}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);min-height:100vh;
  font-family:-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;
  display:flex;flex-direction:column;align-items:center;justify-content:flex-start;
  padding:28px 20px 40px;user-select:none}
.top{width:100%;max-width:760px;display:flex;justify-content:space-between;
  align-items:baseline;color:var(--dim);font-size:13px;margin-bottom:10px}
.progress{width:100%;max-width:760px;height:3px;background:#262626;border-radius:2px;overflow:hidden}
.progress > i{display:block;height:100%;width:0;background:var(--blue);transition:width .15s}
.stage{flex:1;width:100%;max-width:760px;display:flex;flex-direction:column;
  align-items:center;justify-content:center;gap:6px;padding:40px 0 20px}
.hz{font-size:104px;line-height:1.1;font-weight:400;letter-spacing:.04em;
  font-family:"Songti SC","SimSun",serif;transition:color .12s}
.hz.ok{color:var(--green)}
.hz.bad{color:var(--red)}
.py{color:var(--dim);font-size:15px;letter-spacing:.08em}
.code{font-size:22px;letter-spacing:.35em;color:var(--blue);font-variant-numeric:tabular-nums;
  min-height:30px;font-family:ui-monospace,Consolas,monospace}
.code.bad{color:var(--red)}
.code.hidden{visibility:hidden}
.slots{display:flex;gap:10px;margin-top:6px}
.slot{width:44px;height:44px;border-radius:8px;border:1px solid var(--line);
  display:flex;align-items:center;justify-content:center;font-size:20px;
  font-family:ui-monospace,Consolas,monospace;color:var(--dim)}
.slot.filled{background:#243027;color:var(--text);border-color:#3a5343}
.slot.wrong{background:#3a201b;border-color:#7a3a2e;color:#f1a898}
.hint{color:var(--faint);font-size:12px;margin-top:18px;text-align:center;line-height:1.8}
button{background:#262626;color:var(--text);border:1px solid var(--line);border-radius:8px;
  padding:7px 14px;font-size:13px;cursor:pointer;font-family:inherit}
button:hover{background:#2e2e2e}
.controls{display:flex;gap:10px;margin-top:18px}
.summary{display:none;width:100%;max-width:760px}
.summary.show{display:block}
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:10px;margin:16px 0}
.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
.card .k{color:var(--dim);font-size:12px}
.card .v{font-size:22px;font-weight:500;font-variant-numeric:tabular-nums}
table{width:100%;border-collapse:collapse;font-size:13px;margin-top:8px}
th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line)}
th{color:var(--dim);font-weight:400}
td.num{text-align:right;font-variant-numeric:tabular-nums}
textarea{width:100%;height:120px;background:#1a1a1a;color:var(--dim);border:1px solid var(--line);
  border-radius:8px;padding:10px;font-family:ui-monospace,Consolas,monospace;font-size:12px;margin-top:10px}
.badge{display:inline-block;background:#243027;color:#8fbf9f;border-radius:4px;padding:1px 6px;font-size:11px}
</style>
</head>
<body>
<div class="top">
  <span id="title"></span>
  <span id="counter"></span>
</div>
<div class="progress"><i id="bar"></i></div>

<div class="stage" id="stage">
  <div class="hz" id="hz">·</div>
  <div class="py" id="py"></div>
  <div class="code" id="code"></div>
  <div class="slots"><div class="slot" id="s0"></div><div class="slot" id="s1"></div></div>
  <div class="hint" id="hint">点一下页面，然后按键盘上对应的两个字母<br>Esc 跳过这个字 · H 显示/隐藏双拼码</div>
  <div class="controls">
    <button id="toggle">隐藏双拼码</button>
    <button id="reset">重来</button>
  </div>
</div>

<div class="summary" id="summary">
  <h2 style="font-size:16px;font-weight:500;margin:26px 0 0">练完了</h2>
  <div class="cards" id="cards"></div>
  <div id="keytable"></div>
  <div style="margin-top:14px;color:var(--dim);font-size:12px">
    把下面这段贴回给我，下次就能按这次的表现出题：
  </div>
  <textarea id="result" readonly></textarea>
</div>

<script>
const ITEMS = /*__ITEMS__*/;
const $ = (id) => document.getElementById(id);
const mode = location.hash === '#noshow' ? 'hidden' : 'show';

let index = 0, buf = '', showAt = 0, lastAt = 0, startedAt = 0;
let correct = 0, wrongChars = [];
let hideCode = false;
const keyStats = {};
const records = [];

function stat(k) {
  if (!keyStats[k]) keyStats[k] = { hit: 0, miss: 0 };
  return keyStats[k];
}

function current() { return ITEMS[index]; }

function render() {
  const it = current();
  if (!it) return finish();
  $('hz').className = 'hz';
  $('hz').textContent = it.c;
  $('py').textContent = it.p;
  $('code').textContent = it.k[0] + '  ' + it.k[1];
  $('code').className = 'code' + (hideCode ? ' hidden' : '');
  $('s0').textContent = ''; $('s1').textContent = '';
  $('s0').className = 'slot'; $('s1').className = 'slot';
  $('counter').textContent = (index + 1) + ' / ' + ITEMS.length;
  $('bar').style.width = (index / ITEMS.length * 100) + '%';
  buf = '';
  showAt = performance.now();
}

function advance() {
  index++;
  render();
}

function pressSlot(i, ch, ok) {
  const el = $('s' + i);
  el.textContent = ch;
  el.className = 'slot ' + (ok ? 'filled' : 'wrong');
}

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') { e.preventDefault(); advance(); return; }
  if (e.key === 'h' || e.key === 'H') { hideCode = !hideCode; $('code').className = 'code' + (hideCode ? ' hidden' : ''); return; }
  if (e.ctrlKey || e.altKey || e.metaKey) return;

  const ch = e.key.toLowerCase();
  if (!/^[a-z]$/.test(ch)) return;
  e.preventDefault();

  const it = current();
  if (!it) return;
  if (!startedAt) startedAt = performance.now();

  if (buf.length === 0) {
    buf = ch;
    lastAt = performance.now();
    pressSlot(0, ch, ch === it.k[0]);
    if (ch !== it.k[0]) stat(ch).miss++;
    return;
  }

  buf += ch;
  const ok = buf === it.k;
  pressSlot(1, ch, ch === it.k[1]);
  const elapsed = Math.round(performance.now() - showAt);

  if (ok) {
    correct++;
    $('hz').className = 'hz ok';
    stat(it.k[0]).hit++;
    stat(it.k[1]).hit++;
    records.push({ c: it.c, k: it.k, ms: elapsed, ok: true });
    setTimeout(advance, 90);
  } else {
    if (ch !== it.k[1]) stat(ch).miss++;
    $('hz').className = 'hz bad';
    $('code').className = 'code bad';
    wrongChars.push(it.c);
    records.push({ c: it.c, k: it.k, got: buf, ms: elapsed, ok: false });
    setTimeout(advance, 420);
  }
});

function keyName(k) { return k.toUpperCase(); }

function finish() {
  $('stage').style.display = 'none';
  $('bar').style.width = '100%';
  $('counter').textContent = ITEMS.length + ' / ' + ITEMS.length;
  const total = ITEMS.length;
  const elapsed = startedAt ? Math.round(performance.now() - startedAt) : 0;
  const accuracy = total ? (correct / total * 100) : 0;
  const cpm = elapsed > 0 ? (correct * 2 / (elapsed / 60000)) : 0;

  $('cards').innerHTML = [
    ['正确', correct + ' / ' + total, ''],
    ['正确率', accuracy.toFixed(0) + '%', ''],
    ['用时', (elapsed / 1000).toFixed(0), '秒'],
    ['速度', cpm.toFixed(0), '键/分'],
  ].map(([k, v]) => `<div class="card"><div class="k">${k}</div><div class="v">${v}</div></div>`).join('');

  const rows = Object.entries(keyStats)
    .filter(([, s]) => s.miss > 0)
    .sort((a, b) => b[1].miss - a[1].miss);

  $('keytable').innerHTML = rows.length === 0
    ? '<div style="color:#8fbf9f">这次没有按错任何键。</div>'
    : '<table><tr><th>键</th><th class="num">按对</th><th class="num">按错</th></tr>'
      + rows.map(([k, s]) => `<tr><td>${keyName(k)}</td><td class="num">${s.hit}</td>`
        + `<td class="num" style="color:#e08a78">${s.miss}</td></tr>`).join('')
      + '</table>';

  const payload = {
    v: 1,
    finishedAt: new Date().toISOString(),
    total: total,
    correct: correct,
    elapsedMs: elapsed,
    accuracy: Number(accuracy.toFixed(1)),
    keys: Object.entries(keyStats).map(([k, s]) => ({ key: k, hit: s.hit, miss: s.miss })),
    wrongChars: wrongChars,
  };
  $('result').value = JSON.stringify(payload, null, 0);
  $('summary').classList.add('show');
}

$('toggle').onclick = () => {
  hideCode = !hideCode;
  $('code').className = 'code' + (hideCode ? ' hidden' : '');
  $('toggle').textContent = hideCode ? '显示双拼码' : '隐藏双拼码';
};
$('reset').onclick = () => location.reload();

const weak = [...new Set(ITEMS.flatMap(i => i.k.split('')))].sort();
$('title').textContent = `${ITEMS.length} 个字 · 覆盖 ${weak.map(k=>k.toUpperCase()).join(' ')} · 小鹤双拼`;
render();
</script>
</body>
</html>
""";
}
