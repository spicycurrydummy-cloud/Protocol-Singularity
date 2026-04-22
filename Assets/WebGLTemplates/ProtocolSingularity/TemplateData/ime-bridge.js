// Protocol Singularity — WebGL IME bridge (v4: field-overlay mode).
//
// Unity の UI Toolkit TextField は WebGL で IME / 日本語入力を取りこぼすため、
// HTML <input> を「フォーカス中の TextField の真上」に動的に配置して
// あたかも TextField をそのまま使っているかのように見せる。
//
// C# から呼ばれる window.__imeBridge* API:
//   Place(nx, ny, nw, nh, fontPx)  — 正規化 (0..1) の canvas 相対座標で位置決め & 表示
//   Show                            — 位置はそのままで表示
//   Hide                            — 隠す
//   SetValue(s)                     — value を外部から上書き
//   Insert(s)                       — キャレット位置に挿入 + フォーカス
//   Attach(inst)                    — createUnityInstance 完了後に呼ぶ
//   IsAvailable                     — 常に 1

(function () {
  var unityInstance = null;
  var target = "ImeBridge";
  var methodText = "ReceiveImeText";
  var methodSubmit = "ReceiveImeSubmit";
  var inputEl = null;
  var lastPlace = null;

  function send(method, payload) {
    if (!unityInstance) return;
    try { unityInstance.SendMessage(target, method, payload); } catch (e) {}
  }

  function canvasEl() {
    return document.querySelector('#unity-canvas') ||
           document.getElementsByTagName('canvas')[0];
  }

  function ensureInput() {
    if (inputEl) return inputEl;
    inputEl = document.createElement("input");
    inputEl.type = "text";
    inputEl.id = "ime-overlay-input";
    inputEl.maxLength = 200;
    inputEl.autocomplete = "off";
    inputEl.autocapitalize = "off";
    inputEl.spellcheck = false;

    // TextField と見分けがつかない程度のターミナル調スタイル。位置は Place で上書き。
    var s = inputEl.style;
    s.position = "fixed";
    s.left = "-9999px";
    s.top = "-9999px";
    s.width = "0px";
    s.height = "0px";
    s.margin = "0";
    s.padding = "0 8px";
    s.boxSizing = "border-box";
    s.backgroundColor = "#141C14";
    s.color = "#C8E6C8";
    s.border = "1px solid #3CC878";
    s.borderRadius = "0";
    s.fontFamily = '"Consolas","Menlo","Courier New",monospace';
    s.fontSize = "16px";
    s.letterSpacing = "0";
    s.outline = "none";
    s.zIndex = "1000";
    s.display = "none";

    // 入力のたび Unity へ全体 value を送る。
    inputEl.addEventListener("input", function (e) {
      e.stopPropagation();
      send(methodText, inputEl.value);
    });

    // キーイベントは Unity の window リスナーに渡さない (念のための二重防御)。
    // captureAllKeyboardInput=false と併用して Unity にキーが漏れないようにする。
    function stop(e) { e.stopPropagation(); }
    inputEl.addEventListener("keydown", function (e) {
      e.stopPropagation();
      if (e.isComposing) return;
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        send(methodSubmit, "");
      }
    });
    inputEl.addEventListener("keyup", stop);
    inputEl.addEventListener("keypress", stop);

    inputEl.addEventListener("focus", function () {
      s.borderColor = "#50FFAA";
    });
    inputEl.addEventListener("blur", function () {
      s.borderColor = "#3CC878";
      // blur 直後に Unity 側から別 TextField がフォーカスされて再度 Place → focus()
      // が来るケースがあるため、遅延してから本当に非アクティブなら隠す。
      setTimeout(function () {
        if (document.activeElement !== inputEl) {
          inputEl.style.display = "none";
        }
      }, 120);
    });

    document.body.appendChild(inputEl);

    // キャンバスリサイズ時に追従する (表示中のときのみ)。
    window.addEventListener("resize", function () {
      if (inputEl && inputEl.style.display !== "none") applyPlace();
    });

    return inputEl;
  }

  function applyPlace() {
    if (!lastPlace || !inputEl) return;
    var canvas = canvasEl();
    if (!canvas) return;
    var rect = canvas.getBoundingClientRect();
    var s = inputEl.style;
    s.left = (rect.left + lastPlace.nx * rect.width) + "px";
    s.top  = (rect.top  + lastPlace.ny * rect.height) + "px";
    s.width  = (lastPlace.nw * rect.width) + "px";
    s.height = (lastPlace.nh * rect.height) + "px";
    // fontRatio は panel 高さに対する比率 (C# 側で除算済)。canvas 高さに乗算。
    if (lastPlace.fontPx && lastPlace.fontPx > 0) {
      var fs = lastPlace.fontPx * rect.height;
      if (fs < 10) fs = 10;
      if (fs > 40) fs = 40;
      s.fontSize = fs + "px";
    }
  }

  window.__imeBridgeAttach = function (instance) {
    unityInstance = instance;
    // 接続直後は input は非表示。TextField フォーカス時に Place で出す。
    ensureInput();
  };

  window.__imeBridgePlace = function (nx, ny, nw, nh, fontPx) {
    var el = ensureInput();
    lastPlace = { nx: nx, ny: ny, nw: nw, nh: nh, fontPx: fontPx };
    applyPlace();
    el.style.display = "block";
    try { el.focus({ preventScroll: true }); } catch (e) { el.focus(); }
  };

  window.__imeBridgeShow = function () {
    var el = ensureInput();
    if (lastPlace) applyPlace();
    el.style.display = "block";
    try { el.focus({ preventScroll: true }); } catch (e) { el.focus(); }
  };

  window.__imeBridgeHide = function () {
    if (inputEl) inputEl.style.display = "none";
  };

  window.__imeBridgeSetValue = function (v) {
    var el = ensureInput();
    el.value = v || "";
  };

  window.__imeBridgeInsert = function (v) {
    var el = ensureInput();
    if (!v) return;
    var pos = el.selectionStart !== null && el.selectionStart !== undefined ? el.selectionStart : el.value.length;
    var before = el.value.substring(0, pos);
    var after = el.value.substring(pos);
    el.value = before + v + after;
    var newPos = pos + v.length;
    // 挿入前に非表示だった場合も表示する (mention chip 経由)
    if (lastPlace) applyPlace();
    el.style.display = "block";
    try { el.setSelectionRange(newPos, newPos); } catch (e) {}
    try { el.focus({ preventScroll: true }); } catch (e) { el.focus(); }
    send(methodText, el.value);
  };

  window.__imeBridgeIsAvailable = function () { return true; };
})();
