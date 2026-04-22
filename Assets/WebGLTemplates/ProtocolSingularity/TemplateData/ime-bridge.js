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
  // ---------------------------------------------------------------
  // Unity WebGL のキーボードイベント横取り対策 (capture-phase blocker)
  // ---------------------------------------------------------------
  // Unity ランタイムは window / document に capture:true で keydown 等を仕掛け、
  // preventDefault を当てるので HTML input にフォーカスが乗っていても
  // 英数字や Backspace, 矢印キーが届かない。captureAllKeyboardInput = false
  // だけでは不十分なので、Unity より先に登録した listener で HTML input
  // フォーカス時は stopImmediatePropagation する。
  //
  // この IIFE は Unity の createUnityInstance より前 (つまり Unity が
  // listener を登録する前) に実行されるため、同じ capture フェーズでも
  // こちらが先に発火する。
  function isEditable(el) {
    if (!el) return false;
    var t = el.tagName;
    return t === "INPUT" || t === "TEXTAREA" || el.isContentEditable === true;
  }
  function captureBlocker(e) {
    if (isEditable(document.activeElement)) {
      e.stopImmediatePropagation();
    }
  }
  ["keydown", "keyup", "keypress"].forEach(function (type) {
    window.addEventListener(type, captureBlocker, true);
    document.addEventListener(type, captureBlocker, true);
  });

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

    // Unity 側の TextField (.input > .unity-base-text-field__input) の styling と
     // padding / font-family / border を一致させて、overlay 非表示時にテキスト位置・
    // グリフ幅が変わって UI がガタつかないようにする。
    var s = inputEl.style;
    s.position = "fixed";
    s.left = "-9999px";
    s.top = "-9999px";
    s.width = "0px";
    s.height = "0px";
    s.margin = "0";
    s.padding = "12px 14px";     // Unity USS に合わせる
    s.boxSizing = "border-box";
    s.backgroundColor = "#141C14";
    s.color = "#C8E6C8";
    s.border = "1px solid #3CC878";
    s.borderRadius = "0";
    // Unity 側と完全に同じ Noto Sans JP を使う (style.css で @font-face 登録済)。
    s.fontFamily = '"ProtocolSingularityJP","Hiragino Kaku Gothic ProN","Yu Gothic","Meiryo",sans-serif';
    s.fontSize = "20px";
    s.lineHeight = "1.1";
    s.letterSpacing = "0";
    s.outline = "none";
    s.zIndex = "1000";
    s.display = "none";

    // 入力のたび Unity へ全体 value を送る。
    inputEl.addEventListener("input", function (e) {
      e.stopPropagation();
      if (window.__imeBridgeDebug) console.log("[IME] input:", JSON.stringify(inputEl.value));
      send(methodText, inputEl.value);
    });

    // IME 変換確定 (composition commit) 時に明示的に最新 value を送る保険。
    // 一部ブラウザでは compositionend 後に input event が発火しないケースあり。
    inputEl.addEventListener("compositionend", function (e) {
      e.stopPropagation();
      if (window.__imeBridgeDebug) console.log("[IME] compositionend:", JSON.stringify(inputEl.value));
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
    var fieldH = lastPlace.nh * rect.height;
    s.left = (rect.left + lastPlace.nx * rect.width) + "px";
    s.top  = (rect.top  + lastPlace.ny * rect.height) + "px";
    s.width  = (lastPlace.nw * rect.width) + "px";
    s.height = fieldH + "px";
    // lastPlace.fontPx は font-size / field-height の無次元比率 (C# 側で計算済)。
    // field の CSS 高さに乗算すれば、パネル scale や DPR に依らず Unity と一致する
    // CSS px 値が得られる。
    if (lastPlace.fontPx && lastPlace.fontPx > 0) {
      var fs = lastPlace.fontPx * fieldH;
      if (fs < 8) fs = 8;
      if (fs > 60) fs = 60;
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
