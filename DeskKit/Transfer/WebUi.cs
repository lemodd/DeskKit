using System;

namespace DeskKit.Transfer
{
    /// <summary>
    /// 浏览器端单页（2026-11-10 网页改版：微信式聊天界面）。
    /// 纯内嵌 HTML/CSS/JS，零外部资源（断网手机也能加载）。
    /// 交互流：
    ///   1. localStorage 存 deviceId/设备名/kind → 没有则 /api/register 领；
    ///   2. 每 1s 轮询 /api/state → 心跳 + 设备列表 + 会话摘要（未读/最后一条）；
    ///   3. 点会话 → /api/chat 拉历史（打开期间 1s 增量刷新，sig 去重防图片闪烁），打开即 /api/read 已读；
    ///   4. 发文字 → /api/msg；发文件/图片 → /api/upload（multipart，带 from）；
    ///   5. 图片气泡内嵌 <img>（/api/file/{id}），文件气泡带下载链接，文字点击复制。
    /// 布局：桌面双栏（侧栏 250px + 聊天区）；手机（≤640px）单栏，进会话隐藏侧栏 + 返回键。
    /// </summary>
    public static class WebUi
    {
        public const string Html = /* lang=html */ @"<!DOCTYPE html>
<html lang='zh-CN'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<!-- favicon：2026-11-12 改为服务端 /favicon.ico（内嵌 DeskKit.ico，与程序图标一致） -->
<link rel='icon' href='/favicon.ico'>
<title>DeskKit 传输</title>
<style>
  * { box-sizing:border-box; margin:0; padding:0; }
  html,body { height:100%; }
  body { font-family:'Microsoft YaHei UI','PingFang SC',system-ui,sans-serif;
         background:#D9D9D9; color:#191919; font-size:15px; overflow:hidden; }
  /* 电脑网页不全屏：限宽居中卡片（2026-11-11 反馈），手机 <=640px 仍铺满 */
  #app { display:flex; height:100%; max-width:900px; margin:0 auto;
         background:#EDEDED; box-shadow:0 0 24px rgba(0,0,0,.18); }
  /* ---- 侧栏（会话列表） ---- */
  #side { width:250px; flex:none; background:#F7F7F7; border-right:1px solid #DCDCDC;
          display:flex; flex-direction:column; }
  .shdr { padding:16px 14px 10px; font-size:17px; font-weight:600; }
  #convList { flex:1; overflow-y:auto; }
  .conv { display:flex; gap:10px; padding:11px 12px; cursor:pointer; align-items:center; }
  .conv:hover, .conv.sel { background:#E8E8E8; }
  .ava { width:40px; height:40px; border-radius:8px; color:#fff; flex:none;
         display:flex; align-items:center; justify-content:center; font-size:17px; }
  .meta { flex:1; min-width:0; }
  .nm { font-size:14px; display:flex; justify-content:space-between; align-items:center; }
  .nm b { font-weight:500; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .pv { font-size:12px; color:#9A9A9A; margin-top:2px;
        white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .badge { background:#FA5151; color:#fff; border-radius:9px; min-width:17px; height:17px;
           padding:0 5px; font-size:11px; line-height:17px; text-align:center; flex:none; }
  .on-dot { width:8px; height:8px; border-radius:50%; background:#C4C4C4; flex:none; }
  .on-dot.on { background:#34C759; }
  /* ---- 图片当前标签预览（2026-11-11 反馈：不开新标签） ---- */
  #lbox { position:fixed; left:0; top:0; right:0; bottom:0; background:rgba(0,0,0,.88);
          display:none; align-items:center; justify-content:center; flex-direction:column;
          z-index:98; cursor:zoom-out; }
  #lbox.show { display:flex; }
  #lbox img { max-width:94vw; max-height:84vh; }
  #lbox .lt { color:#DDD; font-size:12px; margin-top:10px; }
  /* 复制结果提示 */
  #toast { position:fixed; left:50%; top:50%; transform:translate(-50%,-50%);
           background:rgba(0,0,0,.75); color:#fff; font-size:13px; padding:9px 18px;
           border-radius:8px; display:none; z-index:100; pointer-events:none; }
  #toast.show { display:block; }
  /* ---- 改名弹层（2026-11-11：prompt() 被内置浏览器禁用，改用页内弹层） ---- */
  #mask { position:fixed; left:0; top:0; right:0; bottom:0; background:rgba(0,0,0,.45);
          display:none; align-items:center; justify-content:center; z-index:99; }
  #mask.show { display:flex; }
  #dlg { background:#fff; border-radius:12px; padding:18px 16px 14px; width:270px; max-width:86vw; }
  #dlg .dt { font-size:14px; font-weight:600; color:#191919; }
  #nameInput { width:100%; box-sizing:border-box; padding:9px 10px; border:1px solid #DCDCDC;
               border-radius:8px; font-size:14px; margin:12px 0 16px; outline:none; }
  #nameInput:focus { border-color:#07C160; }
  #dlg .drow { display:flex; gap:10px; justify-content:flex-end; }
  #dlg .drow button { border-radius:8px; font-size:14px; padding:7px 16px; cursor:pointer; }
  #btnCancel { background:#F2F2F2; border:0; color:#191919; }
  #btnOk { background:#07C160; border:0; color:#fff; }
  /* 自己条目 + 分割线（2026-11-11 反馈：自己固定第一位） */
  .sep { height:1px; background:#E4E4E4; margin:5px 8px; flex:none; }
  .me-row .pv { color:#B2B2B2; }
  /* ---- 聊天区 ---- */
  #chat { flex:1; display:flex; flex-direction:column; min-width:0; }
  .chead { padding:13px 16px; background:#F7F7F7; border-bottom:1px solid #DCDCDC;
           font-size:15px; font-weight:600; display:flex; align-items:center; gap:8px; }
  #back { display:none; border:0; background:none; font-size:22px; color:#191919;
          cursor:pointer; line-height:1; padding:0 6px 2px; }
  #msgs { flex:1; overflow-y:auto; padding:16px; }
  .empty { color:#9A9A9A; text-align:center; margin-top:40px; font-size:13px; }
  .mrow { display:flex; gap:9px; margin-bottom:14px; align-items:flex-start; }
  .mrow.me { flex-direction:row-reverse; }
  .mava { width:34px; height:34px; border-radius:6px; color:#fff; flex:none; margin-top:2px;
          display:flex; align-items:center; justify-content:center; font-size:14px; }
  .mcol { max-width:72%; min-width:0; }
  .bub { padding:9px 12px; border-radius:8px; font-size:14.5px; line-height:1.5;
         word-break:break-word; white-space:pre-wrap; background:#fff; box-shadow:0 1px 1px rgba(0,0,0,.04); }
  .mrow.me .bub { background:#95EC69; }
  .bub img { max-width:220px; max-height:220px; border-radius:6px; display:block; cursor:pointer; }
  .bub .fn { font-weight:600; font-size:14px; }
  .bub .fs { font-size:12px; color:#888; margin-top:3px; }
  .bub a.dl { color:#576B95; text-decoration:none; }
  .tm { font-size:10.5px; color:#AAA; margin-top:3px; }
  .mrow.me .tm { text-align:right; }
  /* ---- 输入区（微信式：输入框+工具条同容器） ---- */
  .cbox { border:1px solid #DCDCDC; border-radius:8px; background:#fff; padding:8px 10px;
          margin:0 12px 10px; }
  .cbox:focus-within { border-color:#07C160; }
  #msgText { display:block; width:100%; border:0; padding:2px 4px;
             font-size:14.5px; font-family:inherit; background:transparent; outline:none;
             resize:none; min-height:38px; max-height:220px; line-height:1.4;
             box-sizing:border-box; }
  /* 输入框高度拖拽把手（2026-11-11 反馈：不用浏览器右下角拖拽角，与桌面端同款） */
  #grip { height:12px; display:flex; align-items:center; justify-content:center;
          cursor:ns-resize; touch-action:none; }
  #grip i { width:44px; height:4px; border-radius:2px; background:#C8C8C8; }
  .tools { display:flex; align-items:center; margin-top:6px; }
  .tools .sp { flex:1; }
  .cbtn { border:0; border-radius:6px; font-size:14px; cursor:pointer; flex:none; }
  #btnPick { background:none; color:#606060; font-size:18px; padding:2px 8px; }
  #btnSend { background:#07C160; color:#fff; padding:5px 16px; }
  #btnSend:disabled { opacity:.5; }
  .prog { height:3px; background:#E4E4E4; overflow:hidden; display:none; flex:none; }
  .prog i { display:block; height:100%; background:#07C160; width:0; transition:width .2s; }
  .hint { padding:6px 12px; font-size:11px; color:#B0B0B0; background:#F7F7F7; flex:none; }
  /* ---- 手机单栏 ---- */
  @media (max-width:640px) {
    #side { width:100%; border-right:0; }
    #chat { display:none; }
    body.in-chat #side { display:none; }
    body.in-chat #chat { display:flex; }
    #back { display:block; }
  }
</style>
</head>
<body>
<div id='app'>
  <div id='side'>
    <div class='shdr'>DeskKit 传输</div>
    <div id='convList'><div class='empty'>连接中…</div></div>
  </div>
  <div id='chat'>
    <div class='chead'><button id='back' onclick='goBack()'>‹</button><span id='peerName'></span></div>
    <div id='msgs'><div class='empty'>选择一个会话开始</div></div>
    <div class='prog' id='prog'><i id='progBar'></i></div>
    <div class='hint'>支持文字 / 图片 / 文件 · 图片点击看原图 · 聊天区可直接拖文件发送</div>
    <div id='grip' title='拖动调整输入框高度'><i></i></div>
    <div class='cbox'>
      <textarea id='msgText' rows='1' placeholder='输入消息…'
                onkeydown='if(event.key===&quot;Enter&quot;&amp;&amp;!event.shiftKey){event.preventDefault();sendText();}'></textarea>
      <div class='tools'>
        <input type='file' id='filePick' multiple style='display:none' onchange='sendFiles(this.files)'>
        <button class='cbtn' id='btnPick' title='发送图片或文件（聊天区可直接拖文件，Ctrl+V 粘贴截图）' onclick='document.getElementById(&quot;filePick&quot;).click()'>＋</button>
        <span class='sp'></span>
        <button class='cbtn' id='btnSend' onclick='sendText()'>发送</button>
      </div>
    </div>
  </div>
</div>

<!-- 图片当前标签预览层（替代 window.open 新标签） -->
<div id='lbox'><img id='limg' alt='预览'><div class='lt'>点击空白处关闭 · 长按图片可保存</div></div>

<!-- 复制结果提示 -->
<div id='toast'></div>

<!-- 改名弹层（替代被内置浏览器禁用的 prompt） -->
<div id='mask'>
  <div id='dlg'>
    <div class='dt'>设备显示名</div>
    <input id='nameInput' maxlength='60' autocomplete='off'>
    <div class='drow'>
      <button id='btnCancel'>取消</button>
      <button id='btnOk'>确定</button>
    </div>
  </div>
</div>

<script>
var S = { id: localStorage.dk_id || '', name: localStorage.dk_name || '',
          kind: /Mobi|Android|iPhone|iPad/i.test(navigator.userAgent) ? 'phone' : 'pc' };
var devices = [], convs = [], peer = null, chat = [], chatSig = '';

function $(x){ return document.getElementById(x); }
function esc(s){ var d=document.createElement('div'); d.textContent=s==null?'':String(s); return d.innerHTML; }
function enc(s){ return encodeURIComponent(s); }
function fmtSize(n){ if(n>=1073741824) return (n/1073741824).toFixed(1)+' GB'; if(n>=1048576) return (n/1048576).toFixed(1)+' MB'; if(n>=1024) return (n/1024).toFixed(0)+' KB'; return n+' B'; }
function fmtTime(ts){ try { var d=new Date(ts); return (d.getHours()<10?'0':'')+d.getHours()+':'+(d.getMinutes()<10?'0':'')+d.getMinutes(); } catch(e){ return ''; } }
var AVA_COLOR = { deskkit:'#07C160', pc:'#576B95', phone:'#FA9D3B' };
function avaChar(n){ return (n||'?').charAt(0).toUpperCase(); }

async function register(){
  if (!S.name) S.name = '我的' + (S.kind==='phone' ? '手机' : '电脑');
  var r = await fetch('/api/register?id=' + enc(S.id), { method:'POST', body: JSON.stringify({ name: S.name, kind: S.kind }) });
  var j = await r.json();
  S.id = j.id; S.name = j.name;
  localStorage.dk_id = S.id; localStorage.dk_name = S.name;
}
function editName(){
  // prompt() 在微信等内置浏览器里被禁（静默返回 null），改用页内弹层
  $('nameInput').value = S.name;
  $('mask').classList.add('show');
  setTimeout(function(){ try { $('nameInput').focus(); $('nameInput').select(); } catch(e){} }, 60);
}
$('btnCancel').onclick = function(){ $('mask').classList.remove('show'); };
$('btnOk').onclick = function(){
  var v = ($('nameInput').value || '').trim();
  $('mask').classList.remove('show');
  if (!v || v === S.name) return;
  S.name = v;
  localStorage.dk_name = S.name;
  register().then(poll);
};
$('nameInput').onkeydown = function(e){ if (e.key === 'Enter') $('btnOk').click(); if (e.key === 'Escape') $('btnCancel').click(); };

// 输入框自动增高：只在内容超出当前高度时长大；手动拖拽设置的高度不会被覆盖
function autoGrow(){
  var ta = $('msgText');
  if (ta.scrollHeight > ta.clientHeight) ta.style.height = ta.scrollHeight + 'px';
}
$('msgText').addEventListener('input', autoGrow);

// 把手拖拽调高（与桌面端一致）：pointer 事件兼容鼠标和触屏
(function(){
  var grip = $('grip'), ta = $('msgText'), on = false, y0 = 0, h0 = 0;
  grip.addEventListener('pointerdown', function(e){
    on = true; y0 = e.clientY; h0 = ta.clientHeight;
    grip.setPointerCapture(e.pointerId); e.preventDefault();
  });
  grip.addEventListener('pointermove', function(e){
    if (!on) return;
    var h = Math.min(220, Math.max(38, h0 - (e.clientY - y0)));  // 上拖=加高
    ta.style.height = h + 'px';
  });
  grip.addEventListener('pointerup', function(){ on = false; });
  grip.addEventListener('pointercancel', function(){ on = false; });
})();

function devOf(id){ return devices.filter(function(d){ return d.id===id; })[0]; }
function nameOf(id){ if (id===S.id) return S.name; var d=devOf(id); return d ? d.name : id.slice(0,6); }

function renderSide(){
  var cmap = {};
  convs.forEach(function(c){ cmap[c.peer]=c; });
  // 自己从设备列表抽出，固定显示在第一位（2026-11-11 反馈），其余设备照旧排序
  var rows = devices.filter(function(d){ return d.id !== S.id; }).map(function(d){
    var c = cmap[d.id] || { peer:d.id, last:'', unread:0 };
    return { id:d.id, name:d.name, type:d.type, online:d.online, last:c.last, unread:c.unread };
  });
  rows.sort(function(a,b){
    if (a.id==='deskkit') return -1; if (b.id==='deskkit') return 1;
    if ((b.unread>0)-(a.unread>0)) return (b.unread>0)-(a.unread>0);
    return (b.online?1:0)-(a.online?1:0);
  });
  var myType = S.kind || 'phone';
  var h = '';
  // 自己：点击改名字（与底部按钮同效）
  h += '<div class=' + q('conv me-row') + ' onclick=' + q('editName()') + '>'
     + '<div class=' + q('ava') + ' style=' + q('background:'+(AVA_COLOR[myType]||'#576B95')) + '>'+avaChar(S.name)+'</div>'
     + '<div class=' + q('meta') + '><div class=' + q('nm') + '><b>'+esc(S.name)+'</b>'
     + '<span class=' + q('on-dot on') + '></span></div>'
     + '<div class=' + q('pv') + '>本设备，点击改名</div></div>'
     + '</div>';
  h += '<div class=' + q('sep') + '></div>';
  rows.forEach(function(r){
    h += '<div class=' + q('conv'+(peer===r.id?' sel':'')) + ' data-id=' + q(r.id) + '>'
       + '<div class=' + q('ava') + ' style=' + q('background:'+(AVA_COLOR[r.type]||'#576B95')) + '>'+avaChar(r.name)+'</div>'
       + '<div class=' + q('meta') + '><div class=' + q('nm') + '><b>'+esc(r.name)+'</b>'
       + '<span class=' + q('on-dot'+(r.online?' on':'')) + '></span></div>'
       + '<div class=' + q('pv') + '>'+esc(r.last||'')+'</div></div>'
       + (r.unread>0?'<span class=' + q('badge') + '>'+r.unread+'</span>':'')
       + '</div>';
  });
  $('convList').innerHTML = h || '<div class='+E('empty')+'></div>';
}
// HTML 属性值加引号的小工具（避免在 JS 字符串里写死双引号——C# 逐字字符串里双引号必须成对）
var DQ = String.fromCharCode(34); // 双引号字符
function q(s){ return DQ+String(s).replace(/\u0022/g,'&quot;')+DQ; }
function E(c){ return DQ+c+DQ; }

// 会话列表点击（事件委托，免去内联 onclick 的引号转义）
document.getElementById('convList').addEventListener('click', function(e){
  var el = e.target.closest ? e.target.closest('.conv') : null;
  if (el && el.dataset.id) openConv(el.dataset.id);
});

function openConv(pid){
  peer = pid; chatSig = '';
  document.body.classList.add('in-chat');
  $('peerName').textContent = nameOf(pid);
  renderSide();
  loadChat(true);
}
function goBack(){
  peer = null;
  document.body.classList.remove('in-chat');
  renderSide();
  $('msgs').innerHTML = '<div class='+E('empty')+'></div>';
}

async function loadChat(force){
  if (!peer) return;
  try {
    var r = await fetch('/api/chat?id='+enc(S.id)+'&peer='+enc(peer), { cache:'no-store' });
    if (!r.ok) return;
    var j = await r.json();
    var entries = j.entries || [];
    var sig = entries.map(function(e){ return e.id; }).join(',');
    if (!force && sig === chatSig) return;
    chatSig = sig; chat = entries;
    renderChat();
    fetch('/api/read?id='+enc(S.id)+'&peer='+enc(peer), { method:'POST' }); // 打开着就算已读
  } catch(e){}
}

function renderChat(){
  var m = $('msgs');
  var nearBottom = m.scrollHeight - m.scrollTop - m.clientHeight < 140;
  var h = '';
  chat.forEach(function(e){
    var inner = '';
    if (e.kind === 'image') {
      inner += '<img class=' + q('zimg') + ' src=' + q('/api/file/'+e.id+'?id='+S.id) + ' loading=' + q('lazy') + '>';
    } else if (e.kind === 'file') {
      inner += '<div class=' + q('fn') + '>📄 '+esc(e.name||'文件')+'</div>'
             + '<div class=' + q('fs') + '>'+fmtSize(e.size)+' · <a class=' + q('dl') + ' href=' + q('/api/file/'+e.id+'?id='+S.id) + ' download>下载</a></div>';
    } else {
      inner += esc(e.text||'');
    }
    var avaType = e.from==='deskkit' ? 'deskkit' : (devOf(e.from) ? devOf(e.from).type : 'pc');
    h += '<div class=' + q('mrow'+(e.mine?' me':'')) + '>'
       + '<div class=' + q('mava') + ' style=' + q('background:'+(AVA_COLOR[avaType]||'#576B95')) + '>'+avaChar(e.fromName||nameOf(e.from))+'</div>'
       + '<div class=' + q('mcol') + '><div class=' + q('bub') + '>'+inner+'</div><div class=' + q('tm') + '>'+esc(e.fromName||'')+' · '+fmtTime(e.ts)+'</div></div>'
       + '</div>';
  });
  m.innerHTML = h || '<div class='+E('empty')+'></div>';
  if (nearBottom) m.scrollTop = m.scrollHeight;
}

// 聊天区点击（事件委托）：点图片当前标签内预览（不开新标签）、点文字气泡复制
$('msgs').addEventListener('click', function(e){
  if (e.target.tagName === 'IMG' && e.target.classList.contains('zimg')) {
    $('limg').src = e.target.src;
    $('lbox').classList.add('show');
    return;
  }
  var bub = e.target.closest ? e.target.closest('.bub') : null;
  if (!bub || bub.querySelector('a') || bub.querySelector('img')) return;
  var t = bub.textContent || '';
  if (!t) return;
  copyText(t);
});
// 点预览层任意处关闭（点图片本身也关，点空白提示区也一样）
$('lbox').addEventListener('click', function(){ this.classList.remove('show'); });

var toastTimer = null;
function toast(msg){
  var t = $('toast');
  t.textContent = msg;
  t.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(function(){ t.classList.remove('show'); }, 1400);
}
function copyText(t){
  // http 非 https 时 navigator.clipboard 多半不存在/被拒，先试再兜底 execCommand
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(t).then(function(){ toast('已复制'); },
                                          function(){ doFallbackCopy(t); });
  } else doFallbackCopy(t);
}
function doFallbackCopy(t){
  var ta = document.createElement('textarea');
  ta.value = t;
  ta.style.position = 'fixed';
  ta.style.opacity = '0';
  ta.style.left = '0'; ta.style.top = '0';
  ta.setAttribute('readonly', 'readonly'); // 防手机弹输入法
  document.body.appendChild(ta);
  ta.focus();
  ta.select();
  ta.setSelectionRange(0, t.length);       // iOS 需要
  var ok = false;
  try { ok = document.execCommand('copy'); } catch(e){}
  document.body.removeChild(ta);
  toast(ok ? '已复制' : '复制失败，请长按消息手动复制');
}

async function poll(){
  try {
    var r = await fetch('/api/state?id=' + enc(S.id), { cache:'no-store' });
    if (!r.ok) return;
    var j = await r.json();
    if (j.rebind) { register().then(poll); return; }
    if (j.you && j.you !== S.id) { S.id = j.you; localStorage.dk_id = S.id; }
    devices = j.devices || []; convs = j.chats || [];
    renderSide();
    if (peer) await loadChat(false);
  } catch(e){ /* 网络闪断，下轮再试 */ }
}

function sendText(){
  var t = $('msgText').value.trim();
  if (!t || !peer) return;
  $('msgText').value = '';
  autoGrow();                          // 清空后收回自动增高
  chat.push({ id:'local'+Date.now(), mine:true, from:S.id, fromName:S.name,
              kind:'text', text:t, ts:new Date().toISOString() });
  chatSig = '';                       // 下一轮轮询会用服务端条目替换本地乐观条目
  renderChat();
  fetch('/api/msg', { method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({ from:S.id, target:peer, text:t }) });
}

function sendFiles(files){
  if (!files || !files.length || !peer) return;
  if (typeof XMLHttpRequest === 'undefined') return;
  var fd = new FormData();
  fd.append('target', peer);
  fd.append('from', S.id);
  for (var i = 0; i < files.length; i++) fd.append('files', files[i], files[i].name);
  var xhr = new XMLHttpRequest();
  var prog = $('prog'), bar = $('progBar');
  prog.style.display = 'block'; bar.style.width = '0%';
  $('btnPick').disabled = true; $('btnSend').disabled = true;
  xhr.upload.onprogress = function(e){ if (e.lengthComputable) bar.style.width = (e.loaded/e.total*100).toFixed(0) + '%'; };
  xhr.onload = function(){
    prog.style.display = 'none'; $('btnPick').disabled = false; $('btnSend').disabled = false;
    $('filePick').value = '';
    chatSig = ''; loadChat(true);      // 上传完强制刷一次会话
  };
  xhr.onerror = function(){ prog.style.display = 'none'; $('btnPick').disabled = false; $('btnSend').disabled = false; };
  xhr.open('POST', '/api/upload');
  xhr.send(fd);
}

// 聊天区拖文件直接发送
var msgsEl = $('msgs');
msgsEl.addEventListener('dragover', function(e){ e.preventDefault(); });msgsEl.addEventListener('drop', function(e){
  e.preventDefault();
  if (e.dataTransfer && e.dataTransfer.files && peer) sendFiles(e.dataTransfer.files);
});

register().then(poll);
setInterval(poll, 1000);
</script>
</body>
</html>";
    }
}
