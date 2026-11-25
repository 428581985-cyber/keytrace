using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KeyTrace.Analysis;

internal sealed record DashboardGenerationResult(
    bool Success,
    string? ReportPath,
    int EventCount,
    bool UsedAllHistory,
    string? ErrorMessage);

/// <summary>
/// 生成单文件看板：数据内嵌、样式内联、不联网、不发请求。
///
/// 为什么不是起个本地服务：普通人会双击一个文件，不会开终端跑服务器。
/// 同一份文件还能直接丢到 GitHub Pages 当在线 demo。
/// </summary>
internal static class Dashboard
{
    public const int DefaultLookbackDays = 7;

    public static string ReportFilePath(string baseDirectory) => Path.Combine(baseDirectory, "report.html");

    public static void Write(string outputPath, string statsJson)
    {
        string fullPath = Path.GetFullPath(outputPath);
        string dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        string tmp = Path.Combine(dir, $"report.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp");
        try
        {
            File.WriteAllText(tmp, Build(statsJson), new UTF8Encoding(false));
            File.Move(tmp, fullPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // 清理临时文件失败不应掩盖原始异常
            }

            throw;
        }
    }

    /// <summary>
    /// 直接从本地事件目录归因并生成离线 HTML 统计看板，供托盘/桌面端一键打开。
    /// 默认汇总近 7 天；若近 7 天为空则自动回退全部历史；完全无事件时仍生成合法空看板，永不崩溃。
    /// </summary>
    public static DashboardGenerationResult GenerateFromEvents(
        string eventsDirectory,
        string baseDirectory,
        int days = DefaultLookbackDays,
        DateOnly? todayOverride = null)
    {
        try
        {
            DateOnly today = todayOverride ?? DateOnly.FromDateTime(DateTime.Now);
            DateOnly? since = days > 0 ? today.AddDays(1 - days) : null;
            bool usedAllHistory = since is null;

            (List<string> files, IEnumerable<EventRecord> stream, int rejected) = EventLog.Read(eventsDirectory, since, today);
            List<EventRecord> events = [.. stream];

            if (events.Count == 0 && since is not null)
            {
                (files, stream, rejected) = EventLog.Read(eventsDirectory, since: null, today);
                events = [.. stream];
                usedAllHistory = events.Count > 0;
            }

            AttributorOptions options = new();
            AttributionResult attribution = new Attributor(options).Run(events, rejected);
            DateOnly from = usedAllHistory
                ? (EarliestFileDate(files) ?? today)
                : (events.Count > 0 ? (since ?? EarliestFileDate(files) ?? today) : today);

            StatsContext context = new()
            {
                Directory = eventsDirectory,
                Files = files,
                Events = events.Count,
                From = from,
                To = today,
                PauseFactor = options.PauseFactor,
                TypoMaxBackspaces = options.TypoMaxBackspaces,
                MinKeySamples = options.MinKeySamples,
            };

            string statsJson = StatsBuilder.BuildJson(attribution, context);
            string reportPath = ReportFilePath(baseDirectory);
            Write(reportPath, statsJson);

            return new DashboardGenerationResult(
                Success: true,
                ReportPath: reportPath,
                EventCount: events.Count,
                UsedAllHistory: usedAllHistory,
                ErrorMessage: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new DashboardGenerationResult(
                Success: false,
                ReportPath: null,
                EventCount: 0,
                UsedAllHistory: false,
                ErrorMessage: ex.Message);
        }
    }

    internal static string Build(string statsJson) => Template.Replace("/*__DATA__*/", statsJson, StringComparison.Ordinal);

    private static DateOnly? EarliestFileDate(List<string> files)
    {
        DateOnly? earliest = null;
        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            string stem = name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(name))
                : Path.GetFileNameWithoutExtension(name);

            if (DateOnly.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
                && (earliest is null || day < earliest))
            {
                earliest = day;
            }
        }

        return earliest;
    }

    /// <summary>键盘布局：每一行是 [显示名, vk, 宽度权重]。宽度按真实键帽比例。</summary>
    private const string Template = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>KeyTrace · 打字画像</title>
<style>
:root{
  --bg:#161616; --panel:#1f1f1f; --line:#2e2e2e;
  --text:#e8e6e3; --dim:#9a968f; --faint:#6b675f;
  --red:#d4503a; --red-dim:#8a3527; --green:#5ba36f; --blue:#4a7fb5; --amber:#c9963f;
}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);
  font-family:-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;
  font-size:14px;line-height:1.6;padding:32px 20px 64px}
.wrap{max-width:1040px;margin:0 auto}
h1{font-size:20px;font-weight:500;margin:0 0 4px}
.sub{color:var(--dim);font-size:13px;margin-bottom:28px}
h2{font-size:14px;font-weight:500;color:var(--dim);margin:36px 0 14px;
  letter-spacing:.02em}
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px 16px}
.card .k{color:var(--dim);font-size:12px}
.card .v{font-size:24px;font-weight:500;margin-top:2px;font-variant-numeric:tabular-nums}
.card .u{font-size:12px;color:var(--faint);margin-left:3px}
.kb{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:18px;overflow-x:auto}
.row{display:flex;gap:6px;margin-bottom:6px;min-width:640px}
.key{border-radius:6px;height:46px;display:flex;flex-direction:column;
  align-items:center;justify-content:center;border:1px solid #00000000;
  font-size:12px;color:var(--text);flex:0 0 auto;position:relative;cursor:default}
.key small{font-size:9px;color:#ffffff85;line-height:1}
.key.dim{color:var(--faint)}
.legend{display:flex;gap:14px;align-items:center;margin-top:14px;font-size:12px;color:var(--dim);flex-wrap:wrap}
.sw{width:14px;height:10px;border-radius:2px;display:inline-block;margin-right:5px;vertical-align:middle}
.bars{display:grid;gap:8px}
.bar{display:grid;grid-template-columns:120px 1fr 60px;gap:10px;align-items:center;font-size:13px}
.bar .track{background:#242424;border-radius:4px;height:18px;overflow:hidden}
.bar .fill{height:100%;border-radius:4px}
.bar .n{text-align:right;color:var(--dim);font-variant-numeric:tabular-nums;font-size:12px}
.advice{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:16px 18px}
.advice ol{margin:8px 0 0;padding-left:20px}
.advice li{margin:6px 0}
.advice code{background:#2a2a2a;border-radius:4px;padding:1px 5px;font-size:12px}
.guard{background:#292118;border:1px solid #6b4d25;color:#ecd09b;border-radius:10px;
  padding:12px 16px;margin:28px 0 0;font-size:13px;display:none}
.guard.show{display:block}
.note{color:var(--faint);font-size:12px;margin-top:10px}
.foot{margin-top:44px;color:var(--faint);font-size:12px;border-top:1px solid var(--line);padding-top:16px}
#tip{position:fixed;pointer-events:none;background:#000;border:1px solid var(--line);
  border-radius:6px;padding:6px 9px;font-size:12px;display:none;z-index:9;white-space:pre;line-height:1.5}
</style>
</head>
<body>
<div class="wrap">
  <h1>打字画像</h1>
  <div class="sub" id="scope"></div>
  <div class="cards" id="cards"></div>

  <h2>键盘热力图<small style="color:var(--faint)"> · 越红越该练，灰色是样本不够或从没错过</small></h2>
  <div class="kb" id="kb"></div>
  <div class="legend">
    <span><i class="sw" style="background:#2f2f2f"></i>无数据</span>
    <span><i class="sw" style="background:#4a3b39"></i>轻微</span>
    <span><i class="sw" style="background:#7a3a2e"></i>偏弱</span>
    <span><i class="sw" style="background:#a8402f"></i>弱</span>
    <span><i class="sw" style="background:#d4503a"></i>明显弱</span>
  </div>

  <h2>退格都花在哪了</h2>
  <div class="bars" id="errors"></div>
  <div class="note" id="identity"></div>

  <h2>在哪些程序里打字</h2>
  <div class="bars" id="apps"></div>

  <div class="guard" id="guard"></div>

  <h2>本周该练</h2>
  <div class="advice" id="advice"></div>
  <div class="note" id="caveat"></div>

  <div class="foot" id="foot"></div>
</div>
<div id="tip"></div>
<script>
const D = /*__DATA__*/;
const $ = (id) => document.getElementById(id);
const num = (n, d=0) => Number(n).toLocaleString('zh-CN', {maximumFractionDigits:d});

const layout = [
  [["`",192,1],["1",49,1],["2",50,1],["3",51,1],["4",52,1],["5",53,1],["6",54,1],["7",55,1],["8",56,1],["9",57,1],["0",48,1],["-",189,1],["=",187,1],["退格",8,2.1]],
  [["Tab",9,1.6],["Q",81,1],["W",87,1],["E",69,1],["R",82,1],["T",84,1],["Y",89,1],["U",85,1],["I",73,1],["O",79,1],["P",80,1],["[",219,1],["]",221,1],["\\",220,1.5]],
  [["Caps",20,1.9],["A",65,1],["S",83,1],["D",68,1],["F",70,1],["G",71,1],["H",72,1],["J",74,1],["K",75,1],["L",76,1],[";",186,1],["'",222,1],["回车",13,2.2]],
  [["Shift",16,2.4],["Z",90,1],["X",88,1],["C",67,1],["V",86,1],["B",66,1],["N",78,1],["M",77,1],[",",188,1],[".",190,1],["/",191,1],["Shift",160,2.4]],
  [["Ctrl",17,1.3],["Win",91,1.2],["Alt",18,1.2],["空格",32,6.4],["Alt",164,1.2],["Win",92,1.2],["Menu",93,1.2],["Ctrl",162,1.3]]
];

const keyByVk = {};
for (const k of D.keys) keyByVk[k.vk] = k;

function tone(score){
  if (score === null || score === undefined) return '#2f2f2f';
  if (score <= 0) return '#2f2f2f';
  if (score < 0.15) return '#4a3b39';
  if (score < 0.3) return '#7a3a2e';
  if (score < 0.5) return '#a8402f';
  return '#d4503a';
}

const W = 46;
const rows = layout.map(row => {
  const el = document.createElement('div');
  el.className = 'row';
  for (const [label, vk, w] of row) {
    const k = keyByVk[vk];
    const d = document.createElement('div');
    d.className = 'key' + (k ? '' : ' dim');
    d.style.width = (W * w + (w - 1) * 6) + 'px';
    d.style.background = k ? tone(k.weakScore) : '#242424';
    d.innerHTML = `<span>${label}</span>` + (k ? `<small>${num(k.presses)} 次</small>` : '');
    if (k) {
      d.onmousemove = (ev) => {
        const t = $('tip');
        const rate = (k.typoRate * 100).toFixed(1);
        t.textContent = `${label}  (vk ${k.vk} · ${k.role})\n`
          + `按下 ${num(k.presses)} 次，其中 ${num(k.typos)} 次确认打错（${rate}%）\n`
          + `删了没补 ${num(k.typosUnreplaced)} 次\n`
          + `中位间隔 ${num(k.ikiP50Ms,0)}ms（全局的 ${k.ikiRatio.toFixed(2)} 倍）\n`
          + `弱键分 ${k.weakScore === null ? '样本不足/零错误，不排名' : k.weakScore.toFixed(2)}`;
        t.style.display = 'block';
        t.style.left = (ev.clientX + 14) + 'px';
        t.style.top = (ev.clientY + 14) + 'px';
      };
      d.onmouseleave = () => { $('tip').style.display = 'none'; };
    }
    el.appendChild(d);
  }
  return el;
});
for (const r of rows) $('kb').appendChild(r);

const T = D.totals, B = D.baseline, S = D.window.skipped;
$('scope').textContent = `${D.window.from} 至 ${D.window.to} · ${num(D.window.days)} 天 · `
  + `${num(D.window.events)} 条事件 · 输入法 ${D.config.ime}`;

const cards = [
  ['击键', num(T.keystrokes), '次'],
  ['上屏', num(T.chars), '字'],
  ['真实速度', num(B.cpm, 1), '字/分'],
  ['打错', num(T.typos), '次'],
  ['改措辞', num(T.rewording), '次'],
  ['会话', num(T.sessions), '段'],
];
$('cards').innerHTML = cards.map(([k, v, u]) =>
  `<div class="card"><div class="k">${k}</div><div class="v">${v}<span class="u">${u}</span></div></div>`).join('');

const errRows = [
  ['确认打错', T.typos, '#d4503a'],
  ['删了没补', T.typosUnreplaced, '#a8402f'],
  ['改措辞', T.rewording, '#6b675f'],
  ['连带删除', T.collateral, '#4a4a44'],
  ['读不到文字', T.blindDeletions, '#3c4a52'],
  ['选字错', T.selectionErrors, '#c9963f'],
];
const maxErr = Math.max(...errRows.map(r => r[1]), 1);
$('errors').innerHTML = errRows.map(([name, n, color]) =>
  `<div class="bar"><span>${name}</span>
   <div class="track"><div class="fill" style="width:${(n / maxErr * 100).toFixed(1)}%;background:${color}"></div></div>
   <span class="n">${num(n)}</span></div>`).join('');

const identity = T.backspaces;
const sum = T.typos + T.typosUnreplaced + T.rewording + T.collateral + T.blindDeletions + T.selectionErrors;
$('identity').textContent = `${num(identity)} 次退格 = 上面六项之和 ${num(sum)}`
  + (identity === sum ? '（对得上）' : '（对不上，数据可能读到一半）');

const maxKeys = Math.max(...D.apps.map(a => a.keystrokes), 1);
$('apps').innerHTML = D.apps.slice(0, 8).map(a =>
  `<div class="bar"><span>${a.exe.replace(/\.exe$/i, '')}</span>
   <div class="track"><div class="fill" style="width:${(a.keystrokes / maxKeys * 100).toFixed(1)}%;background:${a.textAvailable ? '#4a7fb5' : '#33414a'}"></div></div>
   <span class="n">${num(a.keystrokes)}</span></div>`).join('')
  + `<div class="note">蓝色 = 能读到上屏文字；深色 = 读不到（只统计击键，不判对错）</div>`;

const accPct = (1 - (B.typoRate || 0)) * 100;
if ((B.typoRate || 0) > 0.05 && T.keystrokes > 0) {
  const g = $('guard');
  g.className = 'guard show';
  g.textContent = `准确率护栏提醒：这段时间整体准确率 ${accPct.toFixed(1)}%（低于 95% 阈值），建议先放慢节奏保准，再加新键。`;
}

const ranked = D.keys.filter(k => k.weakScore !== null && k.weakScore > 0)
  .sort((a, b) => b.weakScore - a.weakScore);
if (ranked.length === 0) {
  $('advice').innerHTML = '还没有足够的数据排弱键。再多打几天。';
} else {
  const top = ranked.slice(0, 3);
  const items = top.map(k => {
    const rate = (k.typoRate * 100).toFixed(1);
    const slow = k.ikiRatio.toFixed(1);
    const speedNote = k.ikiRatio < 1.05 ? `速度正常（${slow}x）` : `速度是平均的 ${slow} 倍慢`;
    const fragile = k.presses < 60 ? '（样本只有 ' + k.presses + ' 次，先当参考）' : '';
    return `<li><code>${k.name}</code> 按了 ${num(k.presses)} 次，确认打错 ${num(k.typos)} 次（${rate}%），`
      + `${speedNote} ${fragile}</li>`;
  }).join('');
  $('advice').innerHTML = `<div>下一步练这三个键：</div><ol>${items}</ol>`
    + `<div class="note">中文可以先生成练习串：<code>keytrace words</code>；英文导出给 keybr：<code>keytrace export --format keybr</code></div>`;
}

const C = D.chinese;
$('caveat').textContent = C.commits > 0
  ? `中文：${num(C.commits)} 条上屏，其中 ${num(Math.round(C.compCoverage * 100))}% 能分清拼音层和选字层；`
    + `剩下那些读不到组字过程，只统计击键。`
  : '这段时间没有中文上屏记录。';

$('foot').innerHTML = `共 ${num(S.paste)} 条粘贴、${num(S.shortcut)} 条快捷键、${num(S.glyph)} 条图标字符被排除；`
  + `${num(S.bulkCommits)} 条自动补全（${num(S.bulkChars)} 字）不算你打的。<br>`
  + `数据只在本机，这份 HTML 不联网、不上传。`;
</script>
</body>
</html>
""";
}
