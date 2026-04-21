// Protocol Singularity — WebGL IME bridge (v3: HTML input overlay).
//
// <canvas> は DOM の IME / 日本語入力を受け取れないため、画面下部に常時表示される
// HTML <input> を用意し、そこで入力された文字を Unity へ都度転送する。
// Unity 側 (UI Toolkit TextField) はそのまま文字列をミラー表示する想定。
//
// C# から呼ばれる window.__imeBridge* API:
//   Show / Hide     : overlay の表示切替 (フォーカスも伴う)
//   SetValue(s)     : overlay の value を外部から置き換え (送信後クリア等)
//   Insert(s)       : キャレット位置に文字列を挿入 + フォーカス (mention chip から)
//   Attach(inst)    : createUnityInstance 完了後に呼ぶ

(function () {
  var unityInstance = null;
  var target = "ImeBridge";
  var methodText = "ReceiveImeText";       // value 全体を送る (replace semantics)
  var methodSubmit = "ReceiveImeSubmit";   // Enter で呼ばれる
  var inputEl = null;

  function send(method, payload) {
    if (!unityInstance) return;
    try { unityInstance.SendMessage(target, method, payload); } catch (e) {}
  }

  function ensureInput() {
    if (inputEl) return inputEl;
    inputEl = document.createElement("input");
    inputEl.type = "text";
    inputEl.id = "ime-overlay-input";
    inputEl.maxLength = 120;
    inputEl.autocomplete = "off";
    inputEl.autocapitalize = "off";
    inputEl.spellcheck = false;
    inputEl.placeholder = "ここに入力... (日本語 IME 対応)";

    // Unity chat-input に合わせたターミナル風スタイル。
    var s = inputEl.style;
    s.position = "fixed";
    s.right = "68px";           // terminal-root padding 56 + chat-panel margin
    s.bottom = "72px";          // status-line の上
    s.width = "600px";
    s.height = "40px";
    s.padding = "6px 12px";
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
    inputEl.addEventListener("input", function () {
      send(methodText, inputEl.value);
    });

    // Enter で送信。変換中 (composition) は無視。
    inputEl.addEventListener("keydown", function (e) {
      if (e.isComposing) return;
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        send(methodSubmit, "");
      }
    });

    inputEl.addEventListener("focus", function () {
      s.borderColor = "#50FFAA";
    });
    inputEl.addEventListener("blur", function () {
      s.borderColor = "#3CC878";
    });

    document.body.appendChild(inputEl);
    return inputEl;
  }

  window.__imeBridgeAttach = function (instance) {
    unityInstance = instance;
    // 接続確立直後に overlay を出現させる (ゲーム内で常に入力できる)。
    ensureInput().style.display = "block";
  };

  window.__imeBridgeShow = function () {
    var el = ensureInput();
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
    try { el.setSelectionRange(newPos, newPos); } catch (e) {}
    try { el.focus({ preventScroll: true }); } catch (e) { el.focus(); }
    send(methodText, el.value);
  };

  window.__imeBridgeIsAvailable = function () { return true; };
})();
