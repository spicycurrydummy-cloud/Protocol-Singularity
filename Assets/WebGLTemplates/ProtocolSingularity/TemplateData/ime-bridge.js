// Protocol Singularity — WebGL IME bridge.
//
// Unity WebGL の UI Toolkit TextField は Chromium / Firefox の composition
// イベント (日本語 IME の確定前イベント) を受け取れないため、ページ側で
// 隠し <textarea> にフォーカスを流し、compositionend と input イベントで
// 確定後の文字列を Unity へ送る。
//
// ゲーム側には ImeBridge シーンオブジェクトに "ReceiveImeText(string)" メソッド
// を用意する想定。SendMessage 経由で 1 文字〜複数文字まとめて届く。
//
// 有効化は canvas に "data-ime-bridge" 属性が付く or 明示的に
// window.__imeBridgeEnable() を呼んだとき。
(function () {
  var bridge = document.getElementById("ime-bridge");
  var canvas = document.getElementById("unity-canvas");
  if (!bridge || !canvas) return;

  var unityInstance = null;
  var targetObject = "ImeBridge";
  var targetMethod = "ReceiveImeText";
  var composing = false;

  function send(text) {
    if (!unityInstance || !text) return;
    try {
      unityInstance.SendMessage(targetObject, targetMethod, text);
    } catch (e) {
      // シーンに ImeBridge が存在しないビルドでは無視 (メインメニュー等)
    }
  }

  // 日本語入力開始を検知: ブラウザが IME 確定前の composition を起こそうとするとき、
  // フォーカスが canvas 上にある状態では文字が拾えない。キー押下時点で bridge を focus へ。
  function ensureBridgeFocus() {
    if (document.activeElement !== bridge) {
      bridge.style.pointerEvents = "auto";
      bridge.focus({ preventScroll: true });
    }
  }

  // 英数字 1 文字は KeyDown 経由で Unity に届くため bridge にフォーカスを移さない。
  // IME が作動しうる条件 (composition / CJK 文字列が来そうなとき) のみ切り替える。
  canvas.addEventListener("keydown", function (e) {
    if (e.isComposing || e.keyCode === 229) {
      ensureBridgeFocus();
    }
  });

  bridge.addEventListener("compositionstart", function () {
    composing = true;
  });

  bridge.addEventListener("compositionend", function (e) {
    composing = false;
    var text = e.data || bridge.value;
    bridge.value = "";
    if (text) send(text);
    canvas.focus();
  });

  // 直接入力 (CJK 以外) が bridge に入ったケース
  bridge.addEventListener("input", function () {
    if (composing) return;
    var text = bridge.value;
    bridge.value = "";
    if (text) send(text);
  });

  bridge.addEventListener("blur", function () {
    composing = false;
  });

  window.__imeBridgeAttach = function (instance) {
    unityInstance = instance;
  };

  window.__imeBridgeEnable = function () {
    ensureBridgeFocus();
  };
})();
